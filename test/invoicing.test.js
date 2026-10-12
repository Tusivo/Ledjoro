'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const { startServer, setupAdmin } = require('./helpers');
const { checkLedgerIntegrity } = require('../src/ledger');

const acct = (s, name) => s.db.prepare('SELECT id FROM accounts WHERE name=? COLLATE NOCASE').get(name).id;
const ok = (r, what) => { assert.equal(r.status, 200, `${what}: ${r.text}`); return r.json; };

test('items can be changed and switched off, and names stay unique', async () => {
  const s = await startServer();
  try {
    const admin = await setupAdmin(s);
    const inc = acct(s, 'Sales');
    const a = ok(await admin.post('/api/items', { name: 'Widget', kind: 'product', unit_price_cents: 1500, income_account_id: inc }), 'a').id;
    const b = ok(await admin.post('/api/items', { name: 'Gadget', kind: 'product', unit_price_cents: 900, income_account_id: inc }), 'b').id;
    ok(await admin.patch(`/api/items/${a}`, { name: 'Widget Pro', unit_price_cents: 1999, taxable: false, description: 'Better widget' }), 'edit');
    const row = ok(await admin.get('/api/items'), 'list').find((i) => i.id === a);
    assert.deepEqual([row.name, row.unit_price_cents, row.taxable, row.description], ['Widget Pro', 1999, 0, 'Better widget']);
    assert.equal((await admin.patch(`/api/items/${b}`, { name: 'widget pro' })).status, 400, 'names are unique, ignoring capitals');
    ok(await admin.patch(`/api/items/${b}`, { active: false }), 'off');
    assert.equal(ok(await admin.get('/api/items'), 'list2').find((i) => i.id === b).active, 0);
    assert.equal((await admin.patch('/api/items/9999', { name: 'x' })).status, 404);
    assert.equal((await admin.patch(`/api/items/${a}`, { unit_price_cents: -1 })).status, 400);
  } finally { await s.close(); }
});

test('a corrected invoice replaces the original in one step, and only when nothing has been paid on it', async () => {
  const s = await startServer();
  try {
    const admin = await setupAdmin(s);
    const cust = ok(await admin.post('/api/contacts', { kind: 'customer', name: 'Replace Me Co' }), 'c').id;
    const inc = acct(s, 'Sales');
    const mk = async (cents, extra = {}) => ok(await admin.post('/api/documents', { kind: 'invoice', contact_id: cust, doc_date: '2026-05-01', lines: [{ description: 'Work', qty_milli: 1000, unit_price_cents: cents, account_id: inc }], ...extra }), 'make').id;
    const first = await mk(10000);
    const second = await mk(12500, { replaces_id: first });
    const oldDoc = ok(await admin.get(`/api/documents/${first}`), 'old'), newDoc = ok(await admin.get(`/api/documents/${second}`), 'new');
    assert.equal(oldDoc.status, 'void'); assert.equal(newDoc.status, 'open'); assert.equal(newDoc.total_cents, 12500);
    assert.match(newDoc.memo, new RegExp(`^Replaces ${oldDoc.number}`));
    assert.ok(checkLedgerIntegrity(s.db).ok);
    // the receivable ends at the new amount only
    const ar = s.db.prepare("SELECT COALESCE(SUM(debit_cents-credit_cents),0) b FROM journal_lines l JOIN accounts a ON a.id=l.account_id WHERE a.subtype='ar'").get().b;
    assert.equal(Number(ar), 12500);
    // a paid invoice cannot be replaced, and the failed attempt leaves nothing behind
    const paid = await mk(5000);
    ok(await admin.post('/api/payments', { kind: 'customer', contact_id: cust, pay_date: '2026-05-02', amount_cents: 1000, account_id: acct(s, 'Checking'), applications: [{ document_id: paid, amount_cents: 1000 }] }), 'pay');
    const before = s.db.prepare("SELECT COUNT(*) n FROM documents WHERE kind='invoice'").get().n;
    const refused = await admin.post('/api/documents', { kind: 'invoice', contact_id: cust, doc_date: '2026-05-03', replaces_id: paid, lines: [{ description: 'x', qty_milli: 1000, unit_price_cents: 100, account_id: inc }] });
    assert.equal(refused.status, 400);
    assert.equal(s.db.prepare("SELECT COUNT(*) n FROM documents WHERE kind='invoice'").get().n, before, 'nothing was half-done');
    // the original number can be reused? no: numbers stay unique
    assert.notEqual(oldDoc.number, newDoc.number);
  } finally { await s.close(); }
});

test('the company profile printed on invoices: address, contact details and a JPEG logo, with checks', async () => {
  const s = await startServer();
  try {
    const admin = await setupAdmin(s);
    const empty = ok(await admin.get('/api/profile'), 'empty');
    assert.equal(empty.name, 'Test Co'); assert.equal(empty.logo, '');
    ok(await admin.put('/api/profile', { street: '12 Main St', city: 'Concord', state: 'nh', zip: '03301', phone: '603-555-0100', email: 'billing@example.com', note: 'Please pay within 30 days.' }), 'save');
    const p = ok(await admin.get('/api/profile'), 'p');
    assert.deepEqual([p.street, p.city, p.state, p.zip, p.note], ['12 Main St', 'Concord', 'NH', '03301', 'Please pay within 30 days.']);
    assert.equal((await admin.put('/api/profile', { state: 'New Hampshire' })).status, 400);
    assert.equal((await admin.put('/api/profile', { zip: '123' })).status, 400);
    const jpeg = Buffer.concat([Buffer.from([0xff, 0xd8, 0xff, 0xe0]), Buffer.alloc(400, 1)]).toString('base64');
    ok(await admin.put('/api/profile', { logo: jpeg }), 'logo');
    assert.equal(ok(await admin.get('/api/profile'), 'p2').logo, jpeg);
    assert.equal((await admin.put('/api/profile', { logo: Buffer.from('not a picture at all, just words '.repeat(20)).toString('base64') })).status, 400);
    assert.equal((await admin.put('/api/profile', { logo: Buffer.concat([Buffer.from([0xff, 0xd8]), Buffer.alloc(400 * 1024)]).toString('base64') })).status, 400);
    ok(await admin.put('/api/profile', { logo: null }), 'clear');
    assert.equal(ok(await admin.get('/api/profile'), 'p3').logo, '');
    assert.equal(ok(await admin.get('/api/profile'), 'p4').street, '12 Main St', 'clearing the logo leaves the rest');
  } finally { await s.close(); }
});
