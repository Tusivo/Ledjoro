'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const { startServer, Client, setupAdmin, loginAs, PASSWORD } = require('./helpers');
const U = require('../src/util');
const C = require('../src/convert');
const { openDb } = require('../src/db');

const limits = { maxRows: 100000 };

test('phone numbers are normalised to xxx-xxx-xxxx', () => {
  for (const raw of ['(603) 555-0142', '603.555.0142', '603 555 0142', '+1 603 555 0142', '1-603-555-0142', '6035550142']) assert.equal(U.normalizePhone(raw), '603-555-0142', raw);
  for (const raw of ['555-0142', '603-555-014', '603-555-0142 x12', 'call me', '']) assert.equal(U.normalizePhone(raw), null, raw);
  assert.equal(U.vPhone(''), '');
  assert.throws(() => U.vPhone('555-0142'), /10-digit/);
});

test('API stores phones as xxx-xxx-xxxx and rejects malformed ones; legacy values stay editable', async () => {
  const s = await startServer();
  try {
    const admin = await setupAdmin(s);
    const ok = await admin.post('/api/contacts', { kind: 'customer', name: 'Phone Co', phone: '(603) 555 0199' });
    assert.equal(ok.status, 200);
    assert.equal((await admin.get('/api/contacts?kind=customer')).json[0].phone, '603-555-0199');
    assert.equal((await admin.post('/api/contacts', { kind: 'customer', name: 'Bad Co', phone: '12345' })).status, 400);
    // a legacy, non-conforming number already in the database must not block editing other fields
    s.db.prepare("UPDATE contacts SET phone='555-0100' WHERE name='Phone Co'").run();
    const id = (await admin.get('/api/contacts?kind=customer')).json[0].id;
    assert.equal((await admin.patch(`/api/contacts/${id}`, { email: 'x@y.example' })).status, 200);
    assert.equal((await admin.get('/api/contacts?kind=customer')).json[0].phone, '555-0100');
    assert.equal((await admin.patch(`/api/contacts/${id}`, { phone: '603.555.0111' })).status, 200);
    assert.equal((await admin.get('/api/contacts?kind=customer')).json[0].phone, '603-555-0111');
  } finally { await s.close(); }
});

test('importers normalise phone numbers and warn about ones that cannot be', () => {
  const T = (...c) => c.join('\t');
  const iif = [T('!CUST', 'NAME', 'PHONE1'), T('CUST', 'A Co', '(603) 555-0142'), T('CUST', 'B Co', '555-0100')].join('\n');
  const m = C.parse('iif', iif, {}, limits);
  assert.deepEqual(m.contacts.map((c) => c.phone), ['603-555-0142', '555-0100']);
  const db = openDb(':memory:');
  const p = C.plan(db, m);
  assert.ok(p.notes.some((w) => /not 10 digits/.test(w)));
  const csv = C.parse('csv-customers', 'Customer,Phone Numbers\r\nC Co,603.555.0177\r\n', {}, limits);
  assert.equal(csv.contacts[0].phone, '603-555-0177');
});

test('setup shows a recovery key once; the key resets the password, is single-use, and revokes sessions', async () => {
  const s = await startServer();
  try {
    const c = new Client(s.port);
    const r = await c.post('/api/setup', { token: s.state.setupToken, username: 'owner', password: PASSWORD, company_name: 'Co' });
    assert.equal(r.status, 200);
    const key = r.json.recovery_key;
    assert.match(key, /^[0-9A-HJKMNP-TV-Z]{5}(-[0-9A-HJKMNP-TV-Z]{5}){3}$/);
    assert.ok(!s.db.prepare("SELECT value FROM settings WHERE key LIKE 'recovery:%'").get().value.includes(key.replace(/-/g, '')), 'only a hash is stored');
    await c.refreshSession();

    const anon = new Client(s.port);
    const NEW = 'a-completely-new-passphrase-55';
    assert.equal((await anon.post('/api/recover', { username: 'owner', recovery_key: 'AAAAA-AAAAA-AAAAA-AAAAA', new_password: NEW })).status, 401);
    assert.equal((await anon.post('/api/recover', { username: 'nobody', recovery_key: key, new_password: NEW })).status, 401);
    assert.equal((await anon.post('/api/recover', { username: 'owner', recovery_key: key, new_password: 'short' })).status, 400, 'password policy still applies');
    const lowercaseAndSloppy = key.toLowerCase().replace(/-/g, ' ');
    const good = await anon.post('/api/recover', { username: 'owner', recovery_key: lowercaseAndSloppy, new_password: NEW });
    assert.equal(good.status, 200);
    assert.notEqual(good.json.recovery_key, key, 'a fresh key is issued');
    // the old session was revoked, the old key is dead, the new password works
    assert.equal((await c.get('/api/accounts')).status, 401);
    assert.equal((await new Client(s.port).post('/api/recover', { username: 'owner', recovery_key: key, new_password: NEW + 'x' })).status, 401);
    await loginAs(s, 'owner', NEW);
    assert.equal((await new Client(s.port).post('/api/login', { username: 'owner', password: PASSWORD })).status, 401);
    assert.ok(s.db.prepare("SELECT 1 FROM audit_log WHERE action='password.recover'").get());
  } finally { await s.close(); }
});

test('wrong recovery keys trigger the same escalating lockout as wrong passwords', async () => {
  const s = await startServer();
  try {
    const admin = new Client(s.port);
    const r = await admin.post('/api/setup', { token: s.state.setupToken, username: 'owner', password: PASSWORD });
    const anon = new Client(s.port);
    for (let i = 0; i < 5; i++) await anon.post('/api/recover', { username: 'owner', recovery_key: 'ZZZZZ-ZZZZZ-ZZZZZ-ZZZZZ', new_password: 'whatever-long-enough-1' });
    const locked = await anon.post('/api/recover', { username: 'owner', recovery_key: r.json.recovery_key, new_password: 'whatever-long-enough-1' });
    assert.equal(locked.status, 429, 'even the right key is refused while the account is locked');
  } finally { await s.close(); }
});

test('signed-in users can replace their recovery key (needs the password); the old key stops working', async () => {
  const s = await startServer();
  try {
    const c = new Client(s.port);
    const first = (await c.post('/api/setup', { token: s.state.setupToken, username: 'owner', password: PASSWORD })).json.recovery_key;
    await c.refreshSession();
    assert.equal((await c.post('/api/recovery-key', { password: 'wrong-password-123' })).status, 403);
    const second = (await c.post('/api/recovery-key', { password: PASSWORD })).json.recovery_key;
    assert.notEqual(first, second);
    const anon = new Client(s.port);
    assert.equal((await anon.post('/api/recover', { username: 'owner', recovery_key: first, new_password: 'another-long-passphrase-9' })).status, 401);
    assert.equal((await new Client(s.port).post('/api/recover', { username: 'owner', recovery_key: second, new_password: 'another-long-passphrase-9' })).status, 200);
  } finally { await s.close(); }
});

test('automatic invoice numbers skip numbers that are already used', async () => {
  const s = await startServer();
  try {
    const admin = await setupAdmin(s);
    const cust = (await admin.post('/api/contacts', { kind: 'customer', name: 'Number Co' })).json.id;
    const sales = (await admin.get('/api/accounts')).json.find((a) => a.name === 'Sales').id;
    const mk = (extra) => admin.post('/api/documents', { kind: 'invoice', contact_id: cust, doc_date: '2026-01-01', lines: [{ description: 'x', qty_milli: 1000, unit_price_cents: 100, account_id: sales }], ...extra });
    assert.equal((await mk({ number: 'INV-0001' })).status, 200);
    assert.equal((await mk({ number: 'INV-0002' })).status, 200);
    const auto = await mk({});
    assert.equal(auto.status, 200, auto.text);
    assert.equal((await admin.get(`/api/documents/${auto.json.id}`)).json.number, 'INV-0003');
    assert.equal((await mk({ number: 'INV-0004' })).status, 200);
    assert.equal((await admin.get(`/api/documents/${(await mk({})).json.id}`)).json.number, 'INV-0005');
  } finally { await s.close(); }
});
