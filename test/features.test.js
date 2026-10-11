'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { DatabaseSync } = require('node:sqlite');
const { startServer, Client, setupAdmin, loginAs, PASSWORD, fixture } = require('./helpers');
const { openDb } = require('../src/db');
const Backup = require('../src/backup');
const C = require('../src/convert');
const R = require('../src/reports');
const D = require('../src/domain');
const Assets = require('../src/fixedassets');
const { booksHealth } = require('../src/health');
const { cashForecast } = require('../src/forecast');
const U = require('../src/util');

const limits = { maxRows: 100000 };
const ctx = (db) => { db.prepare("INSERT OR IGNORE INTO users(id,username,display_name,password_hash,role_id,created_at) VALUES (1,'t','t','x',1,0)").run(); return { user: { id: 1, username: 't' }, ip: '' }; };
const acct = (db, name) => db.prepare('SELECT id FROM accounts WHERE name=?').get(name).id;
const tmpFile = (n) => path.join(fs.mkdtempSync(path.join(os.tmpdir(), 'obt-')), n);

// ---------------- backup ----------------
test('backup: needs the password, round-trips through decrypt/restore, and rejects wrong passphrases and tampering', async () => {
  const s = await startServer();
  try {
    const admin = await setupAdmin(s);
    await admin.post('/api/contacts', { kind: 'customer', name: 'Backup Co' });
    assert.equal((await admin.post('/api/backup', { password: 'nope-nope-nope-1', passphrase: 'a-long-backup-passphrase' })).status, 403);
    assert.equal((await admin.post('/api/backup', { password: PASSWORD, passphrase: 'short' })).status, 400);
    const r = await admin.request('POST', '/api/backup', { password: PASSWORD, passphrase: 'a-long-backup-passphrase' });
    assert.equal(r.status, 200);
    assert.match(r.headers['content-disposition'], /ledjoro-\d{12}\.obbak/);
    assert.ok(s.db.prepare("SELECT value FROM settings WHERE key='last_backup'").get().value);

    // fetch raw bytes (the Client helper decodes as utf8, so re-run via createBackup for byte-exact checks)
    const bytes = await Backup.createBackup(s.db, 'a-long-backup-passphrase');
    assert.ok(bytes.subarray(0, 5).equals(Buffer.from('OBBK2')));
    assert.ok(!bytes.includes(Buffer.from('Backup Co')), 'contents are encrypted');
    const f = tmpFile('b.obbak'); fs.writeFileSync(f, bytes);
    const target = tmpFile('restored.db');
    Backup.restoreBackup(f, 'a-long-backup-passphrase', target);
    const restored = new DatabaseSync(target, { readOnly: true });
    assert.equal(restored.prepare("SELECT COUNT(*) n FROM contacts WHERE name='Backup Co'").get().n, 1);
    restored.close();
    assert.throws(() => Backup.restoreBackup(f, 'a-long-backup-passphrase', target), /overwrite/);
    assert.throws(() => Backup.restoreBackup(f, 'wrong-passphrase-here', tmpFile('x.db')), /not the right passphrase or recovery key/);
    const bad = Buffer.from(bytes); bad[bad.length - 5] ^= 0xff; fs.writeFileSync(f, bad);
    assert.throws(() => Backup.restoreBackup(f, 'a-long-backup-passphrase', tmpFile('y.db')), /safety check|damaged/);
    fs.writeFileSync(f, 'not a backup at all, definitely not');
    assert.throws(() => Backup.restoreBackup(f, 'a-long-backup-passphrase', tmpFile('z.db')), /not a Ledjoro backup/);

    // only settings.manage may back up
    const roles = (await admin.get('/api/roles')).json;
    await admin.post('/api/users', { username: 'viewer', password: PASSWORD, role_id: roles.find((x) => x.name === 'View only').id });
    s.db.prepare("UPDATE users SET must_change_password=0 WHERE username='viewer'").run();
    const v = await loginAs(s, 'viewer');
    assert.equal((await v.post('/api/backup', { password: PASSWORD, passphrase: 'a-long-backup-passphrase' })).status, 403);
  } finally { await s.close(); }
});

// ---------------- undo import ----------------
test('undo import: reverses every entry, leaves history intact, and the file can be imported again', async () => {
  const db = openDb(':memory:');
  const x = ctx(db);
  const r1 = C.commit(db, x, C.parse('iif', fixture('sample.iif'), {}, limits), { filename: 'sample.iif' });
  assert.equal(r1.journal_entries, 3);
  assert.equal(R.trialBalance(db, { asof: '2030-01-01' }).rows.length > 0, true);
  const batches = C.listBatches(db);
  assert.equal(batches[0].live_entries, 3);

  const u = C.undoBatch(db, x, r1.batchId);
  assert.equal(u.reversed, 3);
  assert.equal(R.trialBalance(db, { asof: '2030-01-01' }).rows.length, 0, 'every period nets to zero');
  assert.ok(R.trialBalance(db, { asof: '2030-01-01' }).balanced);
  assert.equal(db.prepare('SELECT COUNT(*) n FROM journal_entries').get().n, 6, 'originals are kept; reversals are added');
  assert.throws(() => C.undoBatch(db, x, r1.batchId), /already been undone/);
  assert.equal(C.listBatches(db)[0].undone, true);

  // re-import after undo works (the duplicate check ignores reversed entries) ...
  const p = C.plan(db, C.parse('iif', fixture('sample.iif'), {}, limits));
  assert.equal(p.summary.journal_entries.new, 3);
  const r2 = C.commit(db, x, C.parse('iif', fixture('sample.iif'), {}, limits), { filename: 'again.iif' });
  assert.equal(r2.journal_entries, 3);
  assert.equal(R.trialBalance(db, { asof: '2030-01-01' }).rows.find((r) => r.name === 'Accounts Receivable').debit_cents, 75000);
  // ... and importing it a third time is again recognised as duplicate
  assert.equal(C.commit(db, x, C.parse('iif', fixture('sample.iif'), {}, limits)).skipped_duplicates, 3);
});

test('undo import removes uncategorised bank rows so the statement can be re-imported, keeps categorised ones', () => {
  const db = openDb(':memory:');
  const x = ctx(db);
  const chk = acct(db, 'Checking');
  const r = C.commit(db, x, C.parse('ofx', fixture('sample.qbo'), {}, limits), { bank_account_id: chk });
  const first = db.prepare("SELECT id FROM bank_transactions ORDER BY id").get().id;
  D.categorizeBank(db, x, first, { account_id: acct(db, 'Office Supplies') });
  const u = C.undoBatch(db, x, r.batchId);
  assert.equal(u.bank_removed, 2); assert.equal(u.bank_kept, 1);
  assert.equal(db.prepare('SELECT COUNT(*) n FROM bank_transactions').get().n, 1);
  assert.equal(C.commit(db, x, C.parse('ofx', fixture('sample.qbo'), {}, limits), { bank_account_id: chk }).bank_added, 2);
});

test('undo import respects the closing date and rolls back completely', () => {
  const db = openDb(':memory:');
  const x = ctx(db);
  const r = C.commit(db, x, C.parse('iif', fixture('sample.iif'), {}, limits));
  db.prepare("UPDATE settings SET value='2026-02-02' WHERE key='closing_date'").run(); // closes January and the 1 Feb payment
  const before = db.prepare('SELECT COUNT(*) n FROM journal_entries').get().n;
  assert.throws(() => C.undoBatch(db, x, r.batchId), /closed/);
  assert.equal(db.prepare('SELECT COUNT(*) n FROM journal_entries').get().n, before, 'nothing half-undone');
  assert.equal(C.listBatches(db)[0].undone, false);
});

// ---------------- fixed assets ----------------
test('depreciation: straight-line with exact cumulative rounding; posting is idempotent and ends at cost - salvage', () => {
  const db = openDb(':memory:');
  const x = ctx(db);
  const id = Assets.createAsset(db, x, { name: 'Laptop', cost_cents: 100000, salvage_cents: 1000, in_service: '2026-01-15', life_months: 7 });
  const a = db.prepare('SELECT * FROM fixed_assets WHERE id=?').get(id);
  const sched = Assets.schedule(a);
  assert.equal(sched.length, 7);
  assert.equal(sched.reduce((s, r) => s + r.amount_cents, 0), 99000, 'total is exactly cost minus salvage');
  assert.equal(sched[0].date, '2026-01-31'); assert.equal(sched[1].date, '2026-02-28'); assert.equal(sched[6].date, '2026-07-31');

  const r = Assets.postDepreciation(db, x, '2026-03-31');
  assert.equal(r.entries, 3);
  assert.equal(Assets.postDepreciation(db, x, '2026-03-31').entries, 0, 'running again posts nothing');
  assert.equal(Assets.listAssets(db)[0].accumulated_cents, sched[2].accumulated_cents);
  Assets.postDepreciation(db, x, '2027-12-31');
  const done = Assets.listAssets(db)[0];
  assert.equal(done.accumulated_cents, 99000); assert.equal(done.book_value_cents, 1000); assert.equal(done.status, 'fully depreciated');
  assert.equal(db.prepare("SELECT COUNT(*) n FROM journal_entries WHERE source_type='depreciation'").get().n, 7);
  assert.ok(R.trialBalance(db, { asof: '2030-01-01' }).balanced);
});

test('assets already depreciated in the books are registered without double-posting; closing date is honoured', () => {
  const db = openDb(':memory:');
  const x = ctx(db);
  Assets.createAsset(db, x, { name: 'Old press', cost_cents: 120000, in_service: '2026-01-01', life_months: 12, depreciated_through: '2026-06-30' });
  assert.equal(Assets.listAssets(db)[0].accumulated_cents, 60000);
  assert.equal(Assets.postDepreciation(db, x, '2026-06-30').entries, 0);
  assert.equal(Assets.postDepreciation(db, x, '2026-08-31').entries, 2);
  db.prepare("UPDATE settings SET value='2026-10-31' WHERE key='closing_date'").run();
  assert.throws(() => Assets.postDepreciation(db, x, '2026-12-31'), /closed/);
  assert.throws(() => Assets.createAsset(db, x, { name: 'x', cost_cents: 100, in_service: '2026-01-01', life_months: 12, depreciated_through: '2026-06-15' }), /last day of a month/);
});

test('disposal books proceeds and the gain or loss, and refuses until depreciation is current', () => {
  const db = openDb(':memory:');
  const x = ctx(db);
  const asset = acct(db, 'Equipment');
  D.createJournal(db, x, { date: '2026-01-01', lines: [{ account_id: asset, debit_cents: 120000 }, { account_id: acct(db, "Owner's Equity"), credit_cents: 120000 }] });
  const id = Assets.createAsset(db, x, { name: 'Printer', cost_cents: 120000, in_service: '2026-01-01', life_months: 12 });
  assert.throws(() => Assets.disposeAsset(db, x, id, { date: '2026-07-10', proceeds_cents: 0 }), /Post depreciation through 2026-06-30/);
  Assets.postDepreciation(db, x, '2026-06-30'); // 6 months = 60,000
  const r = Assets.disposeAsset(db, x, id, { date: '2026-07-10', proceeds_cents: 70000, deposit_account_id: acct(db, 'Checking') });
  assert.equal(r.gain_cents, 10000); // 70,000 proceeds + 60,000 accumulated - 120,000 cost
  const tb = R.trialBalance(db, { asof: '2026-12-31' });
  assert.ok(tb.balanced);
  assert.equal(tb.rows.find((t) => t.name === 'Equipment'), undefined, 'cost is removed from the asset account');
  assert.equal(tb.rows.find((t) => t.name === 'Checking').debit_cents, 70000);
  assert.equal(tb.rows.find((t) => t.name === 'Gain or Loss on Disposal').credit_cents, 10000);
  assert.equal(Assets.listAssets(db)[0].status, 'disposed');
  assert.throws(() => Assets.disposeAsset(db, x, id, { date: '2026-07-11' }), /already/);
  assert.equal(Assets.postDepreciation(db, x, '2026-12-31').entries, 0, 'disposed assets stop depreciating');
});

test('asset API: permissions and validation', async () => {
  const s = await startServer();
  try {
    const admin = await setupAdmin(s);
    assert.equal((await admin.post('/api/assets', { name: 'Bad', cost_cents: 0, in_service: '2026-01-01', life_months: 12 })).status, 400);
    assert.equal((await admin.post('/api/assets', { name: 'Bad', cost_cents: 100, salvage_cents: 100, in_service: '2026-01-01', life_months: 12 })).status, 400);
    const ok = await admin.post('/api/assets', { name: 'Desk', cost_cents: 60000, in_service: '2026-01-01', life_months: 60 });
    assert.equal(ok.status, 200);
    assert.equal((await admin.post('/api/assets/depreciate', { through: '2026-03-31' })).json.entries, 3);
    const sc = (await admin.get(`/api/assets/${ok.json.id}/schedule`)).json;
    assert.equal(sc.schedule.filter((r) => r.posted).length, 3);
    const roles = (await admin.get('/api/roles')).json;
    await admin.post('/api/users', { username: 'viewer', password: PASSWORD, role_id: roles.find((x) => x.name === 'View only').id });
    s.db.prepare("UPDATE users SET must_change_password=0 WHERE username='viewer'").run();
    const v = await loginAs(s, 'viewer');
    assert.equal((await v.get('/api/assets')).status, 200);
    assert.equal((await v.post('/api/assets/depreciate', { through: '2026-12-31' })).status, 403);
  } finally { await s.close(); }
});

test('opening a version-1 database upgrades it in place (adds the asset register, keeps data)', () => {
  const f = tmpFile('v1.db');
  const db = openDb(f);
  db.exec('DROP TABLE fixed_assets; DROP TABLE passkeys; DROP TABLE reconciled_lines; DROP TABLE reconciliations; DROP TABLE watched_files; PRAGMA user_version = 1');
  db.prepare("INSERT INTO contacts(kind,name,created_at) VALUES ('customer','Keep Me',0)").run();
  db.close();
  const up = openDb(f);
  assert.equal(up.prepare('PRAGMA user_version').get().user_version, 7);
  assert.equal(up.prepare("SELECT COUNT(*) n FROM contacts WHERE name='Keep Me'").get().n, 1);
  assert.equal(up.prepare('SELECT COUNT(*) n FROM fixed_assets').get().n, 0);
  up.close();
});

// ---------------- books health ----------------
test('books health: a clean book scores 100; real problems are found with links', () => {
  const db = openDb(':memory:');
  const x = ctx(db);
  const clean = booksHealth(db, { asof: '2026-10-08' });
  assert.equal(clean.findings.filter((f) => f.severity === 'high' || f.severity === 'medium').length, 1, 'only the no-backup nudge on an empty book');
  assert.equal(clean.findings[0].id, 'backup');

  const cust = D.createContact(db, x, { kind: 'customer', name: 'Dup Co' });
  const sales = acct(db, 'Sales'), chk = acct(db, 'Checking');
  const mk = (date) => D.createDocument(db, x, { kind: 'invoice', contact_id: cust, doc_date: date, lines: [{ description: 'x', qty_milli: 1000, unit_price_cents: 50000, account_id: sales }] });
  mk('2026-01-01'); mk('2026-01-03'); // same customer, same amount, two days apart
  D.createJournal(db, x, { date: '2026-02-01', lines: [{ account_id: chk, credit_cents: 5000 }, { account_id: acct(db, 'Opening Balance Equity'), debit_cents: 5000 }] }); // overdraws checking, leaves OBE non-zero
  D.createJournal(db, x, { date: '2030-01-01', memo: 'typo year', lines: [{ account_id: chk, debit_cents: 1 }, { account_id: sales, credit_cents: 1 }] });
  db.prepare("UPDATE bank_transactions SET status='new'").run();
  D.addBankTransactions(db, chk, [{ date: '2026-01-02', amount_cents: -999, description: 'OLD ITEM', fitid: 'h1' }]);

  const r = booksHealth(db, { asof: '2026-10-08' });
  const ids = r.findings.map((f) => f.id);
  for (const id of ['overdrawn', 'duplicates', 'old-receivables', 'stale-bank', 'opening-equity', 'future-dated', 'backup']) assert.ok(ids.includes(id), `expected finding ${id}, got ${ids}`);
  assert.equal(r.findings[0].severity, 'high');
  assert.ok(r.findings.find((f) => f.id === 'duplicates').items[0].hash.startsWith('#/document/'));
  assert.ok(r.score < 60);
  assert.ok(r.passed.includes('Every journal entry balances'));
});

test('books health: invoice-number gaps and unusual amounts', () => {
  const db = openDb(':memory:');
  const x = ctx(db);
  const cust = D.createContact(db, x, { kind: 'customer', name: 'Gap Co' });
  const sales = acct(db, 'Sales');
  const mk = (n, date, amt) => D.createDocument(db, x, { kind: 'invoice', contact_id: cust, number: n, doc_date: date, lines: [{ description: 'x', qty_milli: 1000, unit_price_cents: amt, account_id: sales }] });
  mk('INV-0001', '2026-02-01', 60000); mk('INV-0002', '2026-03-01', 61000); mk('INV-0005', '2026-04-01', 62000);
  for (let i = 0; i < 6; i++) mk(`INV-01${i}0`, `2026-0${3 + (i % 3)}-1${i}`, 60000 + i * 500);
  mk('INV-0200', '2026-06-20', 2500000); // 40x the usual
  const r = booksHealth(db, { asof: '2026-10-08' });
  assert.deepEqual(r.findings.find((f) => f.id === 'number-gaps').items.slice(0, 2).map((i) => i.label), ['INV-0003 is missing', 'INV-0004 is missing']);
  assert.ok(r.findings.find((f) => f.id === 'unusual-amounts').items[0].label.includes('25,000.00'));
});

// ---------------- cash forecast ----------------
test('cash forecast: open items land in the right weeks, customer lateness is applied, scenarios differ', () => {
  const db = openDb(':memory:');
  const x = ctx(db);
  const chk = acct(db, 'Checking'), sales = acct(db, 'Sales'), rent = acct(db, 'Rent');
  D.createJournal(db, x, { date: '2026-01-01', lines: [{ account_id: chk, debit_cents: 1000000 }, { account_id: acct(db, "Owner's Equity"), credit_cents: 1000000 }] });
  const cust = D.createContact(db, x, { kind: 'customer', name: 'Late Payer', terms_days: 30 });
  const vend = D.createContact(db, x, { kind: 'vendor', name: 'Landlord' });
  // history: this customer always pays 14 days after the due date
  for (const [inv, due] of [['2026-03-01', '2026-03-31'], ['2026-04-01', '2026-05-01']]) {
    const id = D.createDocument(db, x, { kind: 'invoice', contact_id: cust, doc_date: inv, due_date: due, lines: [{ description: 'x', qty_milli: 1000, unit_price_cents: 100000, account_id: sales }] });
    D.createPayment(db, x, { kind: 'customer', contact_id: cust, pay_date: U.addDays(due, 14), amount_cents: 100000, account_id: chk, applications: [{ document_id: id, amount_cents: 100000 }] });
  }
  const asof = '2026-10-08';
  D.createDocument(db, x, { kind: 'invoice', contact_id: cust, doc_date: '2026-10-01', due_date: '2026-10-15', lines: [{ description: 'open', qty_milli: 1000, unit_price_cents: 250000, account_id: sales }] }); // expected 10-29 -> week 2
  D.createDocument(db, x, { kind: 'bill', contact_id: vend, doc_date: '2026-10-01', due_date: '2026-10-20', lines: [{ description: 'rent', qty_milli: 1000, unit_price_cents: 90000, account_id: rent }] }); // week 1

  const f = cashForecast(db, { asof, weeks: 13 });
  assert.equal(f.cash_now_cents, 1200000, 'cash = opening 10,000.00 + the two 1,000.00 payments received');
  const c = f.scenarios.committed.weeks;
  // week k covers asof+7k .. asof+7k+6, so week 3 is 10-29..11-04
  assert.equal(c[3].receipts_cents, 250000, 'due 10-15 + this customer usually pays 14 days late = 10-29, which is week 3');
  assert.equal(c[2].receipts_cents, 0);
  assert.equal(c[1].payments_cents, 90000);
  assert.equal(c.reduce((s, w) => s + w.receipts_cents, 0), 250000);
  assert.equal(f.scenarios.committed.ending_cents, f.cash_now_cents + 250000 - 90000);
  assert.equal(f.scenarios.typical.weeks.length, 13);
  assert.equal(f.assumptions.length, 6);
  assert.equal(cashForecast(db, { asof, weeks: 2 }).weeks, 4, 'weeks are clamped to a sensible range');
});

test('cash forecast: "typical" adds recurring spending and warns when cash would run out', () => {
  const db = openDb(':memory:');
  const x = ctx(db);
  const chk = acct(db, 'Checking');
  D.createJournal(db, x, { date: '2026-01-01', lines: [{ account_id: chk, debit_cents: 200000 }, { account_id: acct(db, "Owner's Equity"), credit_cents: 200000 }] });
  for (const m of ['07', '08', '09']) D.createJournal(db, x, { date: `2026-${m}-28`, memo: 'Payroll', lines: [{ account_id: acct(db, 'Wages'), debit_cents: 300000 }, { account_id: chk, credit_cents: 300000 }] });
  const f = cashForecast(db, { asof: '2026-10-08', weeks: 13 });
  assert.equal(f.cash_now_cents, 200000 - 900000);
  assert.ok(f.scenarios.typical.ending_cents < f.scenarios.committed.ending_cents, 'recurring payroll lowers typical cash');
  assert.equal(f.scenarios.typical.weeks[0].detail.other_out, Math.round(900000 / 3 / (30.4375 / 7)));
  assert.ok(f.scenarios.typical.negative_weeks > 0);
});

test('forecast report is reachable through the reports API', async () => {
  const s = await startServer();
  try {
    const admin = await setupAdmin(s);
    const r = await admin.get('/api/reports/cash-forecast?asof=2026-10-08&weeks=8');
    assert.equal(r.status, 200); assert.equal(r.json.weeks, 8);
    assert.equal((await admin.get('/api/health-check')).json.score <= 100, true);
  } finally { await s.close(); }
});

test('account register CSV: detected, balanced, cross-checked against its own balance column, idempotent, undoable', async () => {
  const REG = [
    '"Date","Number","Payee","Account","Memo","Payment","C","Deposit","Balance"',
    '"12/31/2009",,,"Opening Balance","Deposit",,"X","1,000.00","1,000.00"',
    '"01/05/2010","1001","Town of Essex","Property Tax",,"250.50","X",,"749.50"',
    '"01/06/2010",,"Client A","Consulting Income","Invoice 7",,"X","400.00","1,149.50"',
    '"01/07/2010","1002","Acme Supply","Accounts Payable","bill","75.00","X",,"1,074.50"',
    '"01/08/2010","OXfr",,"Savings",,"100.00","X",,"974.50"',
    '"01/08/2010","OXfr",,"Savings",,"100.00","X",,"874.50"',
  ].join('\r\n');
  assert.equal(C.detectFormat('Personal.CSV', REG), 'csv-register');
  assert.throws(() => C.parse('csv-register', REG, {}, { maxRows: 1000 }), /name of the account this register belongs to/);
  const m = C.parse('csv-register', REG, { register: 'Personal Checking' }, { maxRows: 1000 });
  assert.equal(m.journal_entries.length, 4, 'rows that are not bills/invoices stay journal entries; identical-looking rows stay separate');
  assert.deepEqual(m.documents.map((d) => d.kind + ':' + d.contact), ['invoice:Client A', 'bill:Acme Supply'], 'income deposits with a payer become invoices, Accounts Payable payments become bills');
  assert.equal(C.parse('csv-register', REG, { register: 'Personal Checking', documents: false }, { maxRows: 1000 }).documents.length, 0, 'documents can be switched off');
  for (const e of m.journal_entries) assert.equal(e.lines.reduce((s, l) => s + l.debit_cents - l.credit_cents, 0), 0);
  assert.ok(m.notes.some((w) => /Cross-check passed/.test(w) && /874\.50/.test(w)));
  const types = Object.fromEntries(m.accounts.map((a) => [a.name, a.subtype]));
  assert.equal(types['Personal Checking'], 'bank'); assert.equal(types['Property Tax'], 'expense'); assert.equal(types['Consulting Income'], 'income'); assert.equal(types['Opening Balance Equity'], 'equity');
  // a corrupted balance column is reported, not hidden
  const broken = C.parse('csv-register', REG.replace('"749.50"', '"700.00"'), { register: 'Personal Checking' }, { maxRows: 1000 });
  assert.ok(broken.warnings.some((w) => /Balance column disagrees/.test(w)));

  const s = await startServer();
  try {
    const admin = await setupAdmin(s);
    const body = { filename: 'Personal.CSV', content: REG, options: { register: 'Personal Checking' } };
    const pv = await admin.post('/api/import/preview', body);
    assert.equal(pv.status, 200, pv.text); assert.equal(pv.json.format, 'csv-register'); assert.equal(pv.json.canCommit, true);
    assert.equal((await admin.post('/api/import/preview', { filename: 'Personal.CSV', content: REG })).status, 400, 'register name is required');
    const c1 = await admin.post('/api/import/commit', body); assert.equal(c1.status, 200, c1.text); assert.equal(c1.json.journal_entries, 4); assert.equal(c1.json.documents, 2);
    const bal = s.db.prepare("SELECT SUM(l.debit_cents-l.credit_cents) n FROM journal_lines l JOIN accounts a ON a.id=l.account_id WHERE a.name='Personal Checking'").get().n;
    assert.equal(bal, 87450, 'the bank account ends at the register\'s final balance');
    assert.equal(s.db.prepare("SELECT COUNT(*) n FROM contacts WHERE kind='vendor' AND name='Acme Supply'").get().n, 1, 'payees on Accounts Payable lines become vendors');
    assert.equal(s.db.prepare("SELECT COUNT(*) n FROM contacts WHERE kind='customer' AND name='Client A'").get().n, 1);
    const docs = (await admin.get('/api/documents?kind=bill')).json, inv = (await admin.get('/api/documents?kind=invoice')).json;
    assert.equal(docs.length, 1); assert.equal(docs[0].status, 'paid'); assert.equal(docs[0].total_cents, 7500); assert.equal(docs[0].contact_name, 'Acme Supply');
    assert.equal(inv.length, 1); assert.equal(inv[0].status, 'paid'); assert.equal(inv[0].total_cents, 40000);
    const ap = s.db.prepare("SELECT COALESCE(SUM(l.credit_cents-l.debit_cents),0) n FROM journal_lines l JOIN accounts a ON a.id=l.account_id WHERE a.subtype IN ('ap','ar')").get().n;
    assert.equal(ap, 0, 'bills and invoices are fully paid, so A/P and A/R net to zero');
    assert.equal(c1.json.bank_history, 6, 'every register row is also a categorized bank transaction');
    const hist = (await admin.get('/api/bank/transactions?status=categorized')).json;
    assert.equal(hist.length, 6); assert.ok(hist.every((t) => t.account_name === 'Personal Checking' && t.entry_id));
    assert.equal(hist.reduce((n, t) => n + t.amount_cents, 0), 87450, 'their amounts add up to the account balance');
    const c2 = await admin.post('/api/import/commit', body); assert.equal(c2.json.journal_entries, 0); assert.equal(c2.json.documents, 0); assert.equal(c2.json.skipped_duplicates, 6);
    assert.equal((await admin.post(`/api/import/batches/${c1.json.batchId}/undo`)).status, 200);
    const after = s.db.prepare("SELECT SUM(l.debit_cents-l.credit_cents) n FROM journal_lines l JOIN accounts a ON a.id=l.account_id WHERE a.name='Personal Checking'").get().n;
    assert.equal(after, 0, 'undo nets the register account back to zero');
    assert.equal((await admin.get('/api/documents?kind=bill')).json.length, 0, 'undo removes the bills it made (no voided clutter)');
    assert.equal((await admin.get('/api/documents?kind=invoice')).json.length, 0);
    assert.equal(s.db.prepare('SELECT COUNT(*) n FROM bank_transactions').get().n, 0, 'undo removes the bank history it wrote');
    const again = await admin.post('/api/import/commit', body); assert.equal(again.json.documents, 2, 're-importing after an undo works');
    assert.equal((await admin.get('/api/documents?status=void&kind=bill')).json.length, 0, 'and leaves nothing in Void');
  } finally { await s.close(); }
});

test('CSV column mapping: sniffing suggests columns; a mapped register equals the built-in register format; a mapped bank statement waits for review', async () => {
  const REG = ['"Date","Number","Payee","Account","Memo","Payment","C","Deposit","Balance"',
    '"12/31/2009",,,"Opening Balance","Deposit",,"X","1,000.00","1,000.00"',
    '"01/05/2010","1001","Town of Essex","Property Tax",,"250.50","X",,"749.50"',
    '"01/07/2010","1002","Acme Supply","Accounts Payable","bill","75.00","X",,"674.50"'].join('\r\n');
  const sn = C.sniff(REG, { maxRows: 1000 });
  assert.equal(sn.hasHeader, true); assert.equal(sn.columns.length, 9); assert.equal(sn.rowCount, 3);
  assert.deepEqual(sn.suggested, { date: 0, number: 1, payee: 2, account: 3, memo: 4, out: 5, in: 7, balance: 8 }, 'headers are recognised');
  const CARD = ['"Date","Ref.","Payee","Account","Memo","Charge","C","Payment","Balance"', '"03/17/2013","1282","pizza","FOOD","x","17.94","X",,"17.94"'].join('\n');
  assert.deepEqual(C.sniff(CARD, { maxRows: 100 }).suggested, { date: 0, number: 1, payee: 2, account: 3, memo: 4, out: 5, in: 7, balance: 8 }, 'on a card statement, "Charge" is money out and "Payment" is money in');
  const full = { date: 0, number: 1, payee: 2, account: 3, memo: 4, out: 5, in: 7, balance: 8 };
  const viaMap = C.parse('csv-mapped', REG, { mapping: full, register: 'Personal Checking' }, { maxRows: 1000 });
  const viaReg = C.parse('csv-register', REG, { register: 'Personal Checking' }, { maxRows: 1000 });
  const strip = (m) => JSON.stringify({ e: m.journal_entries, d: m.documents, a: m.accounts });
  assert.equal(strip(viaMap), strip(viaReg), 'mapping the register columns gives the same books as the built-in register importer');
  assert.throws(() => C.parse('csv-mapped', REG, { mapping: { out: 5 } }, { maxRows: 1000 }), /holds the date/);
  assert.throws(() => C.parse('csv-mapped', REG, { mapping: { date: 0 } }, { maxRows: 1000 }), /holds the money/);
  assert.throws(() => C.parse('csv-mapped', REG, { mapping: full }, { maxRows: 1000 }), /name of the account this register belongs to/);

  // a bank statement with other column names, a signed amount, and no account column
  const BANK = 'Posted,Details,Amt,Running\n2026-03-02,COFFEE SHOP,-4.50,95.50\n2026-03-03,PAYROLL,1000.00,1095.50\n2026-03-03,PAYROLL,1000.00,2095.50\n';
  const s2 = C.sniff(BANK, { maxRows: 1000 });
  assert.deepEqual(s2.suggested, { date: 0, payee: 1, balance: 3, amount: 2 });
  const m = C.parse('csv-mapped', BANK, { mapping: { date: 0, payee: 1, amount: 2, balance: 3 } }, { maxRows: 1000 });
  assert.deepEqual(m.bank_transactions.map((t) => [t.date, t.amount_cents, t.description]), [['2026-03-02', -450, 'COFFEE SHOP'], ['2026-03-03', 100000, 'PAYROLL'], ['2026-03-03', 100000, 'PAYROLL']]);
  assert.equal(new Set(m.bank_transactions.map((t) => t.fitid)).size, 3, 'identical-looking lines stay separate');
  const flipped = C.parse('csv-mapped', BANK, { mapping: { date: 0, payee: 1, amount: 2 }, invert: true }, { maxRows: 1000 });
  assert.equal(flipped.bank_transactions[0].amount_cents, 450, '"positive means money out" flips the sign');
  const dmy = C.parse('csv-mapped', 'd,t,a\n05/03/2026,X,1.00\n', { mapping: { date: 0, payee: 1, amount: 2 }, dateOrder: 'dmy' }, { maxRows: 1000 });
  assert.equal(dmy.bank_transactions[0].date, '2026-03-05');
  const badDate = C.parse('csv-mapped', 'd,t,a\nnot a date,X,1.00\n', { mapping: { date: 0, payee: 1, amount: 2 } }, { maxRows: 1000 });
  assert.equal(badDate.errors.length, 1);

  const s = await startServer();
  try {
    const admin = await setupAdmin(s);
    const sniffed = await admin.post('/api/import/sniff', { content: BANK });
    assert.equal(sniffed.status, 200); assert.equal(sniffed.json.columns[2].header, 'Amt'); assert.equal(sniffed.json.columns[2].samples[0], '-4.50');
    const checking = (await admin.get('/api/accounts')).json.find((a) => a.name === 'Checking').id;
    const body = { filename: 'bank.csv', content: BANK, format: 'csv-mapped', options: { mapping: { date: 0, payee: 1, amount: 2, balance: 3, evil: 5, payeeX: -1 } }, bank_account_id: checking };
    const pv = await admin.post('/api/import/preview', body); assert.equal(pv.status, 200, pv.text); assert.equal(pv.json.summary.bank_transactions.count, 3);
    assert.equal((await admin.post('/api/import/commit', { ...body, bank_account_id: undefined })).status, 400, 'a bank account must be chosen');
    const c1 = await admin.post('/api/import/commit', body); assert.equal(c1.status, 200, c1.text); assert.equal(c1.json.bank_added, 3);
    assert.equal((await admin.get('/api/bank/transactions?status=new')).json.length, 3, 'lines wait under To review');
    assert.equal((await admin.post('/api/import/commit', body)).json.bank_duplicates, 3, 're-importing the same statement adds nothing');
    assert.equal((await admin.post('/api/import/sniff', { content: '' })).status, 400);
  } finally { await s.close(); }
});

test('backups: the recovery key opens them (so a forgotten passphrase is not fatal), a passphrase is optional, and encryption can be switched off', async () => {
  const s = await startServer();
  try {
    const admin = await setupAdmin(s);
    const KEY = admin.recoveryKey; assert.match(KEY, /^[0-9A-Z]{5}(-[0-9A-Z]{5}){3}$/);
    await admin.post('/api/contacts', { kind: 'customer', name: 'Key Backup Co' });
    const restoreWith = (bytes, secret) => { const f = tmpFile('m.obbak'); fs.writeFileSync(f, bytes); const t = tmpFile('m.db'); Backup.restoreBackup(f, secret, t); const d = new DatabaseSync(t, { readOnly: true }); const n = d.prepare("SELECT COUNT(*) n FROM contacts WHERE name='Key Backup Co'").get().n; d.close(); return n; };

    // 1) protected by the recovery key alone
    const rec = await Backup.createBackup(s.db, { mode: 'recovery' });
    assert.deepEqual(Backup.inspect(rec), { kind: 'encrypted', version: 2, hasPassphrase: false, hasRecovery: true });
    assert.ok(!rec.includes(Buffer.from('Key Backup Co')), 'contents are encrypted');
    assert.equal(restoreWith(rec, KEY), 1, 'opens with the recovery key');
    assert.equal(restoreWith(rec, KEY.toLowerCase().replace(/-/g, ' ')), 1, 'typing style does not matter');
    assert.throws(() => restoreWith(rec, 'AAAAA-AAAAA-AAAAA-AAAAA'), /not the right passphrase or recovery key/);
    assert.throws(() => restoreWith(rec, ''), /backup is locked/);

    // 2) a passphrase of the person's own; the recovery key still opens it
    const both = await Backup.createBackup(s.db, { mode: 'passphrase', passphrase: 'my-own-backup-phrase' });
    assert.deepEqual(Backup.inspect(both), { kind: 'encrypted', version: 2, hasPassphrase: true, hasRecovery: true });
    assert.equal(restoreWith(both, 'my-own-backup-phrase'), 1);
    assert.equal(restoreWith(both, KEY), 1, 'forgot the passphrase: the recovery key opens it');
    await assert.rejects(Backup.createBackup(s.db, { mode: 'passphrase', passphrase: 'short' }), /at least 12 characters/);

    // 3) not encrypted at all
    const plain = await Backup.createBackup(s.db, { mode: 'none' });
    assert.deepEqual(Backup.inspect(plain), { kind: 'plain', version: 2, hasPassphrase: false, hasRecovery: false });
    assert.ok(plain.includes(Buffer.from('Key Backup Co')), 'an unencrypted backup really is readable');
    assert.equal(restoreWith(plain, ''), 1, 'and needs no secret');

    // 4) tampering with the header or the data is noticed
    const hdrFlip = Buffer.from(both); hdrFlip[20] ^= 1;
    assert.throws(() => restoreWith(hdrFlip, KEY), /damaged|safety check|not the right/);
    const dataFlip = Buffer.from(rec); dataFlip[dataFlip.length - 3] ^= 0xff;
    assert.throws(() => restoreWith(dataFlip, KEY), /safety check/);
    assert.throws(() => Backup.inspect(Buffer.from('definitely not a backup')), /not a Ledjoro backup/);

    // 5) through the app: modes, linking an older recovery key, and the status route
    const st = (await admin.get('/api/backup/status')).json; assert.equal(st.recovery_linked, true); assert.equal(st.recovery_linked_me, true);
    assert.equal((await admin.request('POST', '/api/backup', { password: PASSWORD, mode: 'recovery' })).status, 200);
    assert.equal((await admin.request('POST', '/api/backup', { password: PASSWORD, mode: 'none' })).status, 200);
    s.db.prepare("DELETE FROM settings WHERE key LIKE 'recovery_wrap:%'").run(); // books made before backups used the recovery key
    assert.equal((await admin.get('/api/backup/status')).json.recovery_linked, false);
    const unlinked = await admin.post('/api/backup', { password: PASSWORD, mode: 'recovery' }); assert.equal(unlinked.status, 400); assert.match(unlinked.json.error, /not linked to backups yet/);
    assert.match((await admin.post('/api/backup', { password: PASSWORD, mode: 'recovery', recovery_key: 'AAAAA-AAAAA-AAAAA-AAAAA' })).json.error, /not your current recovery key/);
    assert.equal((await admin.request('POST', '/api/backup', { password: PASSWORD, mode: 'recovery', recovery_key: KEY })).status, 200, 'typing the current key links it');
    assert.equal((await admin.get('/api/backup/status')).json.recovery_linked, true);
    assert.equal((await admin.request('POST', '/api/backup', { password: 'wrong-wrong-wrong-1', mode: 'none' })).status, 403);
    const insp = await admin.post('/api/backup/inspect', { file_b64: rec.toString('base64') }); assert.equal(insp.json.hasRecovery, true); assert.equal(insp.json.kind, 'encrypted');

    // 6) after a password reset the recovery key is replaced; the new key opens new backups and the old key still opens old ones
    const reset = await new Client(s.port).post('/api/recover', { username: 'admin', recovery_key: KEY, new_password: 'a-completely-new-passphrase-55' });
    assert.equal(reset.status, 200); const KEY2 = reset.json.recovery_key; assert.notEqual(KEY2, KEY);
    const rec2 = await Backup.createBackup(s.db, { mode: 'recovery' });
    assert.equal(restoreWith(rec2, KEY2), 1, 'the new key opens a new backup');
    assert.equal(restoreWith(rec, KEY), 1, 'the old key still opens the old backup');
    assert.throws(() => restoreWith(rec2, KEY), /not the right passphrase or recovery key/, 'but the old key does not open a backup made after it was replaced');
  } finally { await s.close(); }
});
