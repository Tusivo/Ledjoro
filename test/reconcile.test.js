'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { startServer, setupAdmin, PASSWORD } = require('./helpers');

async function books() {
  const s = await startServer(); const c = await setupAdmin(s);
  const accts = (await c.get('/api/accounts')).json;
  const bank = accts.find((a) => a.subtype === 'bank'), income = accts.find((a) => a.subtype === 'income'), expense = accts.find((a) => a.subtype === 'expense');
  const card = (await c.post('/api/accounts', { name: 'Test Card', subtype: 'credit_card' })).json;
  const entry = (date, memo, debit, credit, acct = bank) => c.post('/api/journal', { date, memo, lines: [{ account_id: acct.id, debit_cents: debit, credit_cents: credit }, { account_id: (debit ? income : expense).id, debit_cents: credit, credit_cents: debit }] });
  return { s, c, bank, income, expense, card, entry };
}
const lineIds = async (c, bankId, date) => (await c.get(`/api/reconcile/start?account_id=${bankId}&statement_date=${date}`)).json;

test('reconciling: the statement must agree to the cent, then entries are cleared and cannot be cleared twice', async () => {
  const { s, c, bank, entry } = await books();
  await entry('2026-01-05', 'Deposit A', 100000, 0); await entry('2026-01-10', 'Deposit B', 25000, 0); await entry('2026-01-20', 'Rent', 0, 40000); await entry('2026-02-02', 'February deposit', 5000, 0);
  const st = await lineIds(c, bank.id, '2026-01-31');
  assert.equal(st.lines.length, 3, 'entries after the statement date are not offered');
  assert.equal(st.cleared_cents, 0);
  const ids = st.lines.map((l) => l.id);
  let r = await c.post('/api/reconcile', { account_id: bank.id, statement_date: '2026-01-31', statement_cents: 85000, line_ids: ids.slice(0, 2) });
  assert.equal(r.status, 400); assert.match(r.json.error, /difference of 400\.00|difference of 600\.00/);
  r = await c.post('/api/reconcile', { account_id: bank.id, statement_date: '2026-01-31', statement_cents: 85000, line_ids: ids });
  assert.equal(r.status, 200, r.text); assert.equal(r.json.cleared, 3);
  const again = await lineIds(c, bank.id, '2026-03-01');
  assert.deepEqual(again.lines.map((l) => l.memo), ['February deposit']);
  assert.equal(again.cleared_cents, 85000); assert.equal(again.last.statement_cents, 85000);
  r = await c.post('/api/reconcile', { account_id: bank.id, statement_date: '2026-03-01', statement_cents: 90000, line_ids: ids.slice(0, 1).concat(again.lines.map((l) => l.id)) });
  assert.equal(r.status, 400); assert.match(r.json.error, /already reconciled/);
  await s.close();
});

test('reconciling: only the newest reconciliation can be undone, and undoing frees the entries', async () => {
  const { s, c, bank, entry } = await books();
  await entry('2026-01-05', 'A', 1000, 0); await entry('2026-02-05', 'B', 2000, 0);
  const a = (await lineIds(c, bank.id, '2026-01-31')).lines;
  const r1 = await c.post('/api/reconcile', { account_id: bank.id, statement_date: '2026-01-31', statement_cents: 1000, line_ids: a.map((l) => l.id) });
  const b = (await lineIds(c, bank.id, '2026-02-28')).lines;
  const r2 = await c.post('/api/reconcile', { account_id: bank.id, statement_date: '2026-02-28', statement_cents: 3000, line_ids: b.map((l) => l.id) });
  assert.equal((await c.post(`/api/reconcile/${r1.json.id}/undo`)).status, 400);
  assert.equal((await c.post(`/api/reconcile/${r2.json.id}/undo`)).status, 200);
  assert.equal((await lineIds(c, bank.id, '2026-02-28')).lines.length, 1);
  assert.equal((await c.post(`/api/reconcile/${r1.json.id}/undo`)).status, 200);
  assert.equal((await lineIds(c, bank.id, '2026-02-28')).lines.length, 2);
  assert.equal((await c.get('/api/reconcile/history')).json.filter((h) => !h.undone).length, 0);
  await s.close();
});

test('reconciling a credit card counts charges as positive', async () => {
  const { s, c, card, expense } = await books();
  await c.post('/api/journal', { date: '2026-01-05', memo: 'Supplies', lines: [{ account_id: expense.id, debit_cents: 7000 }, { account_id: card.id, credit_cents: 7000 }] });
  const st = await lineIds(c, card.id, '2026-01-31');
  assert.equal(st.lines[0].amount_cents, 7000);
  const r = await c.post('/api/reconcile', { account_id: card.id, statement_date: '2026-01-31', statement_cents: 7000, line_ids: [st.lines[0].id] });
  assert.equal(r.status, 200, r.text);
  await s.close();
});

test('matching: a bank row that is already in the books is linked, not posted twice', async () => {
  const { s, c, bank, entry, db = s.db } = await books();
  await entry('2026-03-03', 'Payment from Acme', 50000, 0); await entry('2026-03-04', 'Payment from Beta', 50000, 0); await entry('2026-03-04', 'Other amount', 123, 0);
  const rows = [['2026-03-03', 50000, 'ACME PAYMENT', 'f1'], ['2026-03-20', 777, 'Unknown', 'f2']];
  for (const [d, a, t, f] of rows) db.prepare('INSERT INTO bank_transactions(account_id,tx_date,amount_cents,description,fitid) VALUES (?,?,?,?,?)').run(bank.id, d, a, t, f);
  const [t1, t2] = db.prepare('SELECT id FROM bank_transactions ORDER BY id').all().map((r) => r.id);
  const m = (await c.get(`/api/bank/transactions/${t1}/matches`)).json;
  assert.equal(m.length, 2, 'two entries of the same amount are both offered, closest date first'); assert.equal(m[0].memo, 'Payment from Acme');
  assert.deepEqual((await c.get(`/api/bank/transactions/${t2}/matches`)).json, []);
  const before = db.prepare('SELECT COUNT(*) n FROM journal_entries').get().n;
  const r = await c.post(`/api/bank/transactions/${t1}/match`, { line_id: m[0].line_id });
  assert.equal(r.status, 200, r.text);
  assert.equal(db.prepare('SELECT COUNT(*) n FROM journal_entries').get().n, before, 'no new journal entry');
  assert.equal(db.prepare('SELECT status FROM bank_transactions WHERE id=?').get(t1).status, 'categorized');
  assert.equal((await c.post(`/api/bank/transactions/${t1}/match`, { line_id: m[0].line_id })).status, 400);
  assert.equal((await c.post('/api/bank/auto-match')).json.matched, 0);
  await s.close();
});

test('automatic matching only takes the unambiguous ones', async () => {
  const { s, c, bank, entry } = await books(); const db = s.db;
  await entry('2026-04-01', 'Only one', 9900, 0); await entry('2026-04-05', 'Twin one', 4400, 0); await entry('2026-04-06', 'Twin two', 4400, 0);
  for (const [d, a, f] of [['2026-04-01', 9900, 'a'], ['2026-04-02', 4400, 'b']]) db.prepare('INSERT INTO bank_transactions(account_id,tx_date,amount_cents,description,fitid) VALUES (?,?,?,?,?)').run(bank.id, d, a, 'x', f);
  assert.equal((await c.post('/api/bank/auto-match')).json.matched, 1);
  assert.equal(db.prepare("SELECT COUNT(*) n FROM bank_transactions WHERE status='new'").get().n, 1);
  await s.close();
});

test('rules can be removed', async () => {
  const { s, c, expense } = await books();
  const r = await c.post('/api/bank/rules', { name: 'Fuel', match_text: 'shell', account_id: expense.id });
  assert.equal((await c.get('/api/bank/rules')).json.length, 1);
  assert.equal((await c.post(`/api/bank/rules/${r.json.id}/remove`)).status, 200);
  assert.equal((await c.get('/api/bank/rules')).json.length, 0);
  assert.equal((await c.post(`/api/bank/rules/${r.json.id}/remove`)).status, 404);
  await s.close();
});

test('search finds customers, invoices by number or amount, accounts and journal memos', async () => {
  const { s, c, entry } = await books();
  await c.post('/api/contacts', { kind: 'customer', name: 'Zebra Crossing Ltd', email: 'hi@zebra.test' });
  await entry('2026-01-05', 'Quarterly zebra grant', 100, 0);
  const types = (await c.get('/api/search?q=zebra')).json.map((r) => r.type);
  assert.ok(types.includes('Customer') && types.includes('Journal'), types.join());
  assert.deepEqual((await c.get('/api/search?q=z')).json, [], 'one letter searches nothing');
  assert.ok((await c.get('/api/search?q=100%25')).json.every((r) => typeof r.hash === 'string'), 'wildcard characters are treated as plain text');
  await s.close();
});

test('automatic backups: written when due, old ones pruned, hand-made ones kept, problems explained', async () => {
  const { s, c } = await books(); const dir = path.join(s.cfg.dataDir, 'AutoBackups');
  s.db.prepare("DELETE FROM settings WHERE key LIKE 'recovery_wrap:%'").run();
  let r = await c.post('/api/backup/auto', { enabled: true, folder: 'AutoBackups', every_days: 1, keep: 2, mode: 'recovery' });
  assert.equal(r.status, 400); assert.match(r.json.error, /recovery key/i, 'locked mode needs the key to be linked');
  r = await c.post('/api/backup/auto', { enabled: true, folder: 'AutoBackups', every_days: 1, keep: 2, mode: 'none' });
  assert.equal(r.status, 200, r.text);
  fs.writeFileSync(path.join(dir, 'my-own.obbak'), 'x');
  for (let i = 0; i < 3; i++) { r = await c.post('/api/backup/auto/run'); assert.equal(r.status, 200, r.text); await new Promise((x) => setTimeout(x, 1100)); }
  const files = fs.readdirSync(dir);
  assert.equal(files.filter((f) => f.startsWith('ledjoro-auto-')).length, 2, files.join());
  assert.ok(files.includes('my-own.obbak'));
  const st = (await c.get('/api/backup/auto')).json; assert.equal(st.enabled, true); assert.ok(st.last_auto);
  assert.equal((await c.get('/api/session')).json.backup.auto, true);
  const file = fs.readFileSync(path.join(dir, files.filter((f) => f.startsWith('ledjoro-auto-'))[0]));
  assert.equal(file.subarray(0, 5).toString(), 'OBBK2'); assert.equal(file[5], 0, 'unlocked mode byte');
  const AB = require('../src/autobackup');
  assert.equal(AB.isDue(s.db), false);
  s.db.prepare("UPDATE settings SET value='2000-01-01T00:00:00.000Z' WHERE key='last_auto_backup'").run();
  assert.equal(AB.isDue(s.db), true);
  fs.writeFileSync(path.join(s.cfg.dataDir, 'afile'), 'x');
  r = await c.post('/api/backup/auto', { enabled: true, folder: 'afile/inside', every_days: 1, keep: 2, mode: 'none' });
  assert.equal(r.status, 400); assert.match(r.json.error, /cannot save backups/);
  assert.equal((await c.post('/api/backup/auto', { enabled: true, folder: 'x', every_days: 3, keep: 2, mode: 'none' })).status, 400);
  await s.close();
});

test('customers: first/middle/last name, two phones and a US address are stored, composed and checked', async () => {
  const { s, c } = await books();
  let r = await c.post('/api/contacts', { kind: 'customer', first_name: 'Ada', middle_name: 'M', last_name: 'Lovelace', phone: '603 555 0142', phone2: '(603) 555-0199', addr_street: '12 Main St', addr_street2: 'Suite 4', addr_city: 'Concord', addr_state: 'nh', addr_zip: '03301' });
  assert.equal(r.status, 200, r.text);
  let got = (await c.get('/api/contacts?kind=customer')).json.find((x) => x.id === r.json.id);
  assert.equal(got.name, 'Ada M Lovelace'); assert.equal(got.phone, '603-555-0142'); assert.equal(got.phone2, '603-555-0199');
  assert.equal(got.address, '12 Main St\nSuite 4\nConcord, NH 03301'); assert.equal(got.addr_state, 'NH');
  r = await c.post('/api/contacts', { kind: 'customer', name: 'Lovelace Analytics', first_name: 'Ada', last_name: 'Lovelace' });
  assert.equal((await c.get('/api/contacts?kind=customer')).json.find((x) => x.id === r.json.id).name, 'Lovelace Analytics', 'a business name wins as the display name');
  assert.equal((await c.post('/api/contacts', { kind: 'customer', addr_state: 'ZZ', first_name: 'Bad' })).status, 400);
  assert.match((await c.post('/api/contacts', { kind: 'customer', addr_zip: '123', first_name: 'Bad2' })).json.error, /ZIP code/);
  assert.match((await c.post('/api/contacts', { kind: 'customer' })).json.error, /fill in the name/);
  assert.equal((await c.post('/api/contacts', { kind: 'customer', name: 'Old Style', address: '1 Free Text Way' })).status, 200);
  const old = (await c.get('/api/contacts?kind=customer')).json.find((x) => x.name === 'Old Style'); assert.equal(old.address, '1 Free Text Way');
  const u = await c.patch('/api/contacts/' + old.id, { addr_street: '9 New Rd', addr_city: 'Derry', addr_state: 'NH', addr_zip: '03038-1234', phone2: '603-555-0111' });
  assert.equal(u.status, 200, u.text);
  const upd = (await c.get('/api/contacts?kind=customer')).json.find((x) => x.id === old.id);
  assert.equal(upd.address, '9 New Rd\nDerry, NH 03038-1234'); assert.equal(upd.name, 'Old Style'); assert.equal(upd.phone2, '603-555-0111');
  await s.close();
});
