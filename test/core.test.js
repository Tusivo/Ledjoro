'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const { openDb } = require('../src/db');
const U = require('../src/util');
const L = require('../src/ledger');
const R = require('../src/reports');
const C = require('../src/convert');
const { audit, verifyAudit } = require('../src/audit');
const { parseXml } = require('../src/importers/qbxml');
const { fixture } = require('./helpers');

const limits = { maxRows: 100000 };
const acct = (db, name) => db.prepare('SELECT id FROM accounts WHERE name=?').get(name).id;
const ctx = (db) => { db.prepare("INSERT OR IGNORE INTO users(id,username,display_name,password_hash,role_id,created_at) VALUES (1,'t','t','x',1,0)").run(); return { user: { id: 1, username: 't' }, ip: '' }; };

test('money parsing is exact and strict', () => {
  assert.equal(U.parseMoney('1,234.56'), 123456);
  assert.equal(U.parseMoney('($12.30)'), -1230);
  assert.equal(U.parseMoney('-5'), -500);
  assert.equal(U.parseMoney('0.1') + U.parseMoney('0.2'), 30); // no float drift
  assert.equal(U.parseMoney('1.005'), 101);
  assert.equal(U.parseMoney('abc'), null);
  assert.equal(U.parseMoney('1e3'), null);
  assert.equal(U.formatMoney(-5), '-0.05');
});

test('CSV export neutralises formula injection', () => {
  assert.equal(U.csvCell('=HYPERLINK("http://evil")'), `"'=HYPERLINK(""http://evil"")"`);
  assert.equal(U.csvCell('+cmd'), "'+cmd");
  assert.equal(U.csvCell('-12.50'), '-12.50'); // plain numbers untouched
  assert.equal(U.csvCell('a,b'), '"a,b"');
});

test('ledger rejects unbalanced / malformed entries and honours the closing date', () => {
  const db = openDb(':memory:');
  const a = acct(db, 'Checking'), b = acct(db, 'Sales');
  assert.throws(() => L.postEntry(db, { date: '2026-01-01', lines: [{ account_id: a, debit_cents: 100 }, { account_id: b, credit_cents: 99 }] }), /does not balance/);
  assert.throws(() => L.postEntry(db, { date: '2026-01-01', lines: [{ account_id: a, debit_cents: 100, credit_cents: 100 }, { account_id: b, credit_cents: 0 }] }), /both the debit and credit columns, or in neither/);
  assert.throws(() => L.postEntry(db, { date: '2026-02-30', lines: [{ account_id: a, debit_cents: 1 }, { account_id: b, credit_cents: 1 }] }), /date/);
  assert.throws(() => L.postEntry(db, { date: '2026-01-01', lines: [{ account_id: a, debit_cents: 1.5 }, { account_id: b, credit_cents: 1.5 }] }), /not a valid money amount/);
  db.prepare("UPDATE settings SET value='2026-01-31' WHERE key='closing_date'").run();
  assert.throws(() => L.postEntry(db, { date: '2026-01-15', lines: [{ account_id: a, debit_cents: 1 }, { account_id: b, credit_cents: 1 }] }), /closed through/);
  assert.ok(L.postEntry(db, { date: '2026-02-01', lines: [{ account_id: a, debit_cents: 1 }, { account_id: b, credit_cents: 1 }] }));
});

test('posted entries and audit rows are immutable at the database level', () => {
  const db = openDb(':memory:');
  const a = acct(db, 'Checking'), b = acct(db, 'Sales');
  const id = L.postEntry(db, { date: '2026-01-01', lines: [{ account_id: a, debit_cents: 100 }, { account_id: b, credit_cents: 100 }] });
  assert.throws(() => db.prepare('UPDATE journal_lines SET debit_cents=999 WHERE entry_id=?').run(id), /immutable/);
  assert.throws(() => db.prepare('DELETE FROM journal_entries WHERE id=?').run(id), /immutable/);
  audit(db, null, 'x');
  assert.throws(() => db.prepare('UPDATE audit_log SET action=?').run('y'), /append-only/);
  assert.throws(() => db.prepare('DELETE FROM audit_log').run(), /append-only/);
});

test('reversal restores the original balance and cannot be repeated', () => {
  const db = openDb(':memory:');
  const a = acct(db, 'Checking'), b = acct(db, 'Sales');
  const id = L.postEntry(db, { date: '2026-01-01', lines: [{ account_id: a, debit_cents: 100 }, { account_id: b, credit_cents: 100 }] });
  L.reverseEntry(db, id, {});
  assert.equal(R.trialBalance(db, { asof: '2026-12-31' }).rows.length, 0);
  assert.throws(() => L.reverseEntry(db, id, {}), /already been reversed/);
});

test('audit chain verifies and detects a broken link', () => {
  const db = openDb(':memory:');
  for (let i = 0; i < 5; i++) audit(db, { user: { id: null, username: 'u' }, ip: '1.1.1.1' }, 'act' + i);
  assert.deepEqual(verifyAudit(db), { ok: true, checked: 5 });
  db.exec('DROP TRIGGER audit_no_update'); // simulate an attacker with raw DB access
  db.prepare("UPDATE audit_log SET detail='tampered' WHERE id=3").run();
  assert.equal(verifyAudit(db).ok, false);
});

test('IIF: converts, balances to zero, and re-import is idempotent', () => {
  const db = openDb(':memory:');
  const m = C.parse('iif', fixture('sample.iif'), {}, limits);
  assert.equal(m.accounts.length, 5);
  assert.equal(m.journal_entries.length, 3);
  assert.equal(m.errors.length, 0);
  assert.equal(m.contacts.find((c) => c.name === 'Acme Widgets').terms_days, 30);
  assert.equal(m.contacts.find((c) => c.name === 'Landlord LLC').is_1099, true);
  assert.ok(!JSON.stringify(m).includes('12-3456789'), 'tax IDs must not be imported');
  const x = ctx(db);
  const r = C.commit(db, x, m);
  assert.equal(r.journal_entries, 3);
  const tb = R.trialBalance(db, { asof: '2026-12-31' });
  assert.ok(tb.balanced);
  const rec = tb.rows.find((r) => r.name === 'Accounts Receivable');
  assert.equal(rec.debit_cents, 75000); // 1250 - 500
  const r2 = C.commit(db, x, C.parse('iif', fixture('sample.iif'), {}, limits));
  assert.equal(r2.journal_entries, 0);
  assert.equal(r2.skipped_duplicates, 3);
  const ar = R.aging(db, { kind: 'ar', asof: '2026-03-31' });
  assert.equal(ar.totals.total_cents, 75000);
  assert.equal(ar.rows[0].name, 'Acme Widgets');
});

test('IIF: unbalanced and bad-date transactions are reported, not imported', () => {
  const T = (...c) => c.join('\t');
  const text = [T('!TRNS', 'TRNSTYPE', 'DATE', 'ACCNT', 'AMOUNT'), T('!SPL', 'TRNSTYPE', 'DATE', 'ACCNT', 'AMOUNT'), T('!ENDTRNS'),
    T('TRNS', 'GENERAL JOURNAL', '01/01/2026', 'Checking', '100.00'), T('SPL', 'GENERAL JOURNAL', '01/01/2026', 'Sales', '-99.00'), T('ENDTRNS'),
    T('TRNS', 'GENERAL JOURNAL', '13/45/2026', 'Checking', '5.00'), T('SPL', 'GENERAL JOURNAL', '13/45/2026', 'Sales', '-5.00'), T('ENDTRNS')].join('\n');
  const m = C.parse('iif', text, {}, limits);
  assert.equal(m.journal_entries.length, 0);
  assert.equal(m.errors.length, 2);
  assert.match(m.errors[0], /must balance/);
  const db = openDb(':memory:');
  assert.throws(() => C.commit(db, ctx(db), m), /have problems/);
});

test('IIF: dmy date order option', () => {
  const T = (...c) => c.join('\t');
  const text = [T('!TRNS', 'DATE', 'ACCNT', 'AMOUNT'), T('!SPL', 'DATE', 'ACCNT', 'AMOUNT'), T('TRNS', '25/12/2026', 'Checking', '5.00'), T('SPL', '25/12/2026', 'Sales', '-5.00'), T('ENDTRNS')].join('\n');
  assert.equal(C.parse('iif', text, { dateOrder: 'dmy' }, limits).journal_entries[0].date, '2026-12-25');
  assert.equal(C.parse('iif', text, {}, limits).journal_entries.length, 0);
});

test('OFX/QBO: parses SGML bank file, entities, comma amounts; de-duplicates on re-import', () => {
  const db = openDb(':memory:');
  const m = C.parse('ofx', fixture('sample.qbo'), {}, limits);
  assert.equal(m.bank_transactions.length, 3);
  assert.deepEqual(m.bank_transactions.map((t) => t.amount_cents), [-4567, 120000, -1999]);
  assert.equal(m.bank_transactions[2].description, 'CLOUD SOFTWARE & HOSTING');
  assert.equal(m.bank_meta.account_id_tail, '2333');
  const chk = acct(db, 'Checking');
  const r = C.commit(db, ctx(db), m, { bank_account_id: chk });
  assert.equal(r.bank_added, 3);
  const r2 = C.commit(db, ctx(db), C.parse('ofx', fixture('sample.qbo'), {}, limits), { bank_account_id: chk });
  assert.equal(r2.bank_added, 0); assert.equal(r2.bank_duplicates, 3);
  assert.throws(() => C.commit(db, ctx(db), m, {}), /which account they belong to/);
});

test('CSV: trial balance becomes a balanced opening entry; accounts map QuickBooks type names', () => {
  const db = openDb(':memory:');
  const tb = C.parse('csv-trialbalance', fixture('sample-trial-balance.csv'), { asof: '2026-01-01' }, limits);
  assert.equal(tb.journal_entries[0].lines.length, 4);
  C.commit(db, ctx(db), tb);
  assert.equal(R.trialBalance(db, { asof: '2026-12-31' }).totals.debit_cents, 1250000);
  const ac = C.parse('csv-accounts', fixture('sample-accounts.csv'), {}, limits);
  assert.deepEqual(ac.accounts.map((a) => [a.number, a.name, a.subtype]), [['7100', 'Cleaning', 'expense'], ['1050', 'Petty Cash', 'bank']]);
  assert.equal(C.detectFormat('x.csv', fixture('sample-trial-balance.csv')), 'csv-trialbalance');
  assert.equal(C.detectFormat('x.iif', fixture('sample.iif')), 'iif');
  assert.equal(C.detectFormat('x.qbo', fixture('sample.qbo')), 'ofx');
});

test('CSV: quoted fields, embedded newlines and unterminated quotes', () => {
  const { parseCsv } = require('../src/importers/csv');
  assert.deepEqual(parseCsv('a,b\r\n"x, ""y""","line1\nline2"\r\n', limits), [['a', 'b'], ['x, "y"', 'line1\nline2']]);
  assert.throws(() => parseCsv('a,"b', limits), /never closed/);
  assert.throws(() => parseCsv('a\n'.repeat(11), { maxRows: 10 }), /more rows than/);
});

test('qbXML: converts accounts/contacts and never expands entities (XXE-safe)', () => {
  const xml = `<?xml version="1.0"?><!DOCTYPE foo [<!ENTITY xxe SYSTEM "file:///etc/passwd">]><QBXML><QBXMLMsgsRs>
  <AccountQueryRs><AccountRet><FullName>Checking &xxe;</FullName><AccountType>Bank</AccountType><AccountNumber>1000</AccountNumber></AccountRet></AccountQueryRs>
  <CustomerQueryRs><CustomerRet><FullName>Acme &amp; Sons</FullName><Email>a@b.example</Email><TermsRef><FullName>Net 45</FullName></TermsRef></CustomerRet></CustomerQueryRs>
  <InvoiceQueryRs><InvoiceRet/></InvoiceQueryRs></QBXMLMsgsRs></QBXML>`;
  const m = C.parse('qbxml', xml, {}, limits);
  assert.equal(m.accounts[0].name, 'Checking &xxe;'); // literal, not expanded
  assert.ok(!JSON.stringify(m).includes('root:'));
  assert.equal(m.contacts[0].name, 'Acme & Sons');
  assert.equal(m.contacts[0].terms_days, 45);
  assert.ok(m.notes.some((w) => /transaction record/.test(w)));
  assert.throws(() => parseXml('<a><b></a>'), /XML file is damaged/);
  assert.throws(() => parseXml('<a>'.repeat(100)), /nested far deeper/);
});

test('QBO API JSON: accounts, customers and journal entries convert', () => {
  const j = JSON.stringify({ QueryResponse: {
    Account: [{ Name: 'Checking', AccountType: 'Bank', AcctNum: '1000' }, { Name: 'Sales', AccountType: 'Income' }, { Name: 'AR', AccountType: 'Accounts Receivable' }],
    Customer: [{ DisplayName: 'Zed Co', PrimaryEmailAddr: { Address: 'z@z.example' } }],
    JournalEntry: [{ Id: '9', TxnDate: '2026-03-01', DocNumber: 'J1', Line: [
      { Amount: 10, Description: 'x', JournalEntryLineDetail: { PostingType: 'Debit', AccountRef: { name: 'Checking' } } },
      { Amount: 10, JournalEntryLineDetail: { PostingType: 'Credit', AccountRef: { name: 'Sales' } } }] }] } });
  const m = C.parse('qbo-json', j, {}, limits);
  assert.equal(m.accounts.length, 3); assert.equal(m.accounts[2].subtype, 'ar');
  assert.equal(m.journal_entries[0].lines[0].debit_cents, 1000);
  const db = openDb(':memory:');
  assert.equal(C.commit(db, ctx(db), m).journal_entries, 1);
});

test('OBX round trip: export then import into a fresh database reproduces balances', () => {
  const a = openDb(':memory:');
  C.commit(a, ctx(a), C.parse('iif', fixture('sample.iif'), {}, limits));
  const obx = C.exportObx(a);
  const b = openDb(':memory:');
  C.commit(b, ctx(b), C.parse('obx', obx, {}, limits));
  assert.deepEqual(R.trialBalance(b, { asof: '2030-01-01' }).rows.map((r) => [r.name, r.debit_cents, r.credit_cents]),
    R.trialBalance(a, { asof: '2030-01-01' }).rows.map((r) => [r.name, r.debit_cents, r.credit_cents]));
  assert.throws(() => C.parse('obx', '{"format":"other"}', {}, limits), /not made by Ledjoro/);
});

test('plan() reports missing accounts without writing anything', () => {
  const db = openDb(':memory:');
  const m = C.parse('iif', fixture('sample.iif'), {}, limits);
  m.journal_entries[0].lines[0].account = 'No Such Account';
  const p = C.plan(db, m);
  assert.equal(p.canCommit, false);
  assert.ok(p.errors.some((e) => /does not exist/.test(e)));
  assert.equal(db.prepare('SELECT COUNT(*) n FROM journal_entries').get().n, 0);
});
