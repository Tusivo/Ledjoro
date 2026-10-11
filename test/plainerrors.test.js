'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { startServer, Client, setupAdmin, PASSWORD } = require('./helpers');
const { explainDbError, unexpected } = require('../src/explain');

// every message the engine can show a person comes from one of these calls
function messages() {
  const out = [];
  const files = [];
  const walk = (d) => { for (const f of fs.readdirSync(d, { withFileTypes: true })) { const p = path.join(d, f.name); if (f.isDirectory()) walk(p); else if (p.endsWith('.js')) files.push(p); } };
  walk(path.join(__dirname, '..', 'src'));
  const re = /(?:\bbad\(\s*|HttpError\(\s*\d+\s*,\s*)(['`])((?:\\.|(?!\1)[^\\])*)\1/g;
  for (const f of files) {
    const src = fs.readFileSync(f, 'utf8');
    let m; while ((m = re.exec(src))) out.push({ file: path.relative(path.join(__dirname, '..'), f), text: m[2].replace(/\\'/g, "'") });
  }
  return out;
}

test('every message the engine can show says enough to act on, and reads like a sentence', () => {
  const all = messages();
  assert.ok(all.length > 150, `found ${all.length} messages`);
  const problems = [];
  for (const { file, text } of all) {
    const t = text.replace(/\$\{OUT_OF_STEP\}/g, 'Close Ledjoro and open it again.').replace(/\$\{[^}]*\}/g, 'X').trim();
    if (t.length < 16) problems.push(`${file}: too short to explain anything: "${text}"`);
    if (!/[.!?)"'`]$/.test(t)) problems.push(`${file}: does not end like a sentence: "${text}"`);
    if (!/^[A-Z"'`(0-9]/.test(t) && !/^\$\{/.test(text)) problems.push(`${file}: should start with a capital letter: "${text}"`);
    if (/\b(undefined|null|NaN|exception|stack trace|SQLITE_|ERR_|errno|0x[0-9A-F]{6,})\b/i.test(t)) problems.push(`${file}: contains jargon: "${text}"`);
    if (/^(Invalid|Bad|Unknown|Forbidden|Not found|Error)\b/i.test(t) && t.length < 60) problems.push(`${file}: terse, no reason or next step: "${text}"`);
  }
  assert.deepEqual(problems, [], '\n' + problems.join('\n'));
});

test('database-level errors are explained with a reason and a next step', () => {
  const cases = [
    ['UNIQUE constraint failed: accounts.name', /duplicate: another account already has the same name/],
    ['UNIQUE constraint failed: contacts.name', /another customer or vendor already has the same name/],
    ['FOREIGN KEY constraint failed', /points at a record that does not exist|depend on/],
    ['CHECK constraint failed: debit_cents >= 0', /not allowed here/],
    ['NOT NULL constraint failed: accounts.name', /required value is missing/],
    ['journal entries are immutable', /locked.*reversing entry/],
    ['database is locked', /busy/],
    ['attempt to write a readonly database', /read-only/],
    ['disk I/O error', /could not read or write the books file/],
    ['database or disk is full', /disk is full/],
    ['database disk image is malformed', /damaged.*restore your most recent backup/],
  ];
  for (const [raw, want] of cases) {
    const r = explainDbError(new Error(raw)); assert.ok(r, raw); assert.match(r.error, want, raw);
    assert.ok(!/SQLITE|constraint failed/i.test(r.error), 'no raw database words leak through: ' + r.error);
  }
  assert.equal(explainDbError(new Error('something else entirely')), null);
  const u = unexpected(); assert.match(u.error, /all-or-nothing/); assert.match(u.error, /ledjoro\.log/);
});

test('what people see over the API: an unknown address, a duplicate, a wrong password and a bad number each explain themselves', async () => {
  const s = await startServer();
  try {
    const admin = await setupAdmin(s);
    const lost = await admin.get('/api/nothing-here'); assert.equal(lost.status, 404); assert.match(lost.json.error, /does not recognise that request/);
    await admin.post('/api/accounts', { number: '7777', name: 'Dup Account', type: 'expense', subtype: 'expense' });
    const dup = await admin.post('/api/accounts', { number: '7778', name: 'Dup Account', type: 'expense', subtype: 'expense' });
    assert.equal(dup.status, 400); assert.match(dup.json.error, /Another account already uses that name or number/);
    const anon = new Client(s.port);
    const wrong = await anon.post('/api/login', { username: 'admin', password: 'definitely-not-it-1' });
    assert.equal(wrong.status, 401); assert.match(wrong.json.error, /Caps Lock/); assert.match(wrong.json.error, /locked for a minute/);
    const bad = await admin.post('/api/contacts', { kind: 'customer', name: 'x'.repeat(500) });
    assert.equal(bad.status, 400); assert.match(bad.json.error, /too long.*most Ledjoro keeps is 150 characters.*has 500/);
    const noName = await admin.post('/api/contacts', { kind: 'customer' }); assert.match(noName.json.error, /Please fill in the name/);
    const cust = (await admin.post('/api/contacts', { kind: 'customer', name: 'Date Test Co' })).json.id;
    const date = await admin.post('/api/documents', { kind: 'invoice', contact_id: cust, doc_date: '31/12/2026', lines: [] }); assert.match(date.json.error, /not a real date.*year-month-day/);
    const lock = await anon.post('/api/passkeys/login/finish', { challengeId: 'nope', credentialId: 'x' }); assert.match(lock.json.error, /expired/);
  } finally { await s.close(); }
});

test('importing a chart of accounts or vendor list does not bury the person in yellow messages: information is a quiet note, real problems stay warnings', () => {
  const C = require('../src/convert');
  const { openDb } = require('../src/db');
  const limits = { maxRows: 1000 };
  // a QuickBooks chart of accounts: many "non-posting" accounts that have no effect on the books
  const rows = ['Account,Type,Number'];
  for (let i = 0; i < 40; i++) rows.push(`Purchase Order ${i},Non-Posting Accounts,${9000 + i}`);
  rows.push('Checking,Bank,1000', 'Sales,Income,4000');
  const m = C.parse('csv-accounts', rows.join('\n'), {}, limits);
  assert.deepEqual(m.warnings, [], 'skipping non-posting accounts is not a warning');
  assert.equal(m.notes.length, 1, 'one summary line, not one per account');
  assert.match(m.notes[0], /40 accounts were left out/); assert.match(m.notes[0], /no effect on your reports/);
  const p = C.plan(openDb(':memory:'), m);
  assert.deepEqual(p.warnings, []); assert.equal(p.notes.length, 1);
  // vendors with an odd phone number: information only
  const v = C.parse('csv-vendors', 'Vendor,Phone\nA Co,(603) 555-0142\nB Co,555-0100\n', {}, limits);
  const pv = C.plan(openDb(':memory:'), v);
  assert.deepEqual(pv.warnings, []); assert.ok(pv.notes.some((n) => /not 10 digits/.test(n)));
  // a trial balance that does not balance IS something to look at
  const tb = C.parse('csv-trialbalance', 'Account,Debit,Credit\nChecking,100.00,\nSales,,90.00\n', { asof: '2026-01-01' }, limits);
  assert.ok(tb.warnings.some((w) => /out by 10\.00/.test(w)), 'a real imbalance stays a warning');
});
