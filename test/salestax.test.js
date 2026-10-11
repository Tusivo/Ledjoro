'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const { startServer, setupAdmin, loginAs, PASSWORD } = require('./helpers');
const ST = require('../src/salestax');
const { openDb, migrate8 } = require('../src/db');
const { checkLedgerIntegrity } = require('../src/ledger');

const acct = (s, name) => s.db.prepare('SELECT id FROM accounts WHERE name=? COLLATE NOCASE').get(name).id;
const balanceOf = (s, name) => Number(s.db.prepare('SELECT COALESCE(SUM(credit_cents-debit_cents),0) AS b FROM journal_lines WHERE account_id=?').get(acct(s, name)).b);
const ok = (r, what) => { assert.equal(r.status, 200, `${what}: ${r.text}`); return r.json; };

test('rates are typed as percentages and kept as exact thousandths; tax rounds half up', () => {
  assert.equal(ST.pctToMilli('6.25'), 6250);
  assert.equal(ST.pctToMilli('7.375%'), 7375);
  assert.equal(ST.pctToMilli(0), 0);
  assert.equal(ST.pctToMilli('100'), 100000);
  for (const bad of ['', 'six', '101', '-1', '1.2345', '7,5']) assert.throws(() => ST.pctToMilli(bad), /percentage/, bad);
  assert.equal(ST.mulRate(1999, 6250), 125, '6.25% of 19.99 is 1.249375, rounded to 1.25');
  assert.equal(ST.mulRate(10000, 7375), 738, '7.375% of 100.00 is 7.375, rounded up to 7.38');
  assert.equal(ST.mulRate(1, 6250), 0);
  assert.equal(ST.mulRate(1e12, 100000), 1e12, 'huge amounts stay exact');
});

test('rates and combined rates: create, duplicate names, group rules, default', async () => {
  const s = await startServer();
  try {
    const admin = await setupAdmin(s);
    const state = ok(await admin.post('/api/salestax/rates', { name: 'State', rate_pct: '4', agency: 'State Revenue Dept' }), 'state').id;
    const county = ok(await admin.post('/api/salestax/rates', { name: 'County', rate_pct: '2.5', agency: 'County Treasurer' }), 'county').id;
    assert.equal((await admin.post('/api/salestax/rates', { name: 'state', rate_pct: '1' })).status, 400, 'names are unique, ignoring capitals');
    assert.equal((await admin.post('/api/salestax/rates', { name: 'Bad', rate_pct: 'x' })).status, 400);
    const group = ok(await admin.post('/api/salestax/rates', { name: 'Springfield', kind: 'group', parts: [state, county], is_default: true }), 'group').id;
    assert.equal((await admin.post('/api/salestax/rates', { name: 'Nested', kind: 'group', parts: [group] })).status, 400, 'a group cannot contain a group');
    const list = ok(await admin.get('/api/salestax/rates'), 'list');
    assert.equal(list.default_id, group);
    const g = list.rates.find((r) => r.id === group);
    assert.equal(g.rate_milli, 6500, 'a combined rate shows the sum of its parts');
    assert.deepEqual(g.parts.sort(), [state, county].sort());
    assert.equal(list.rates.find((r) => r.id === state).used, 1, 'a rate inside a group counts as in use');
    // the liability account must be a liability
    assert.equal((await admin.post('/api/salestax/rates', { name: 'Odd', rate_pct: '1', account_id: acct(s, 'Checking') })).status, 400);
    ok(await admin.patch(`/api/salestax/rates/${county}`, { rate_pct: '3' }), 'edit');
    assert.equal(ok(await admin.get('/api/salestax/rates'), 'again').rates.find((r) => r.id === group).rate_milli, 7000);
    ok(await admin.patch(`/api/salestax/rates/${group}`, { is_default: false }), 'unset default');
    assert.equal(ok(await admin.get('/api/salestax/rates'), 'again').default_id, null);
  } finally { await s.close(); }
});

test('an invoice charges tax per jurisdiction on the taxable lines only and posts it to the tax account', async () => {
  const s = await startServer();
  try {
    const admin = await setupAdmin(s);
    const state = ok(await admin.post('/api/salestax/rates', { name: 'State', rate_pct: '4', agency: 'State' }), 'r1').id;
    const county = ok(await admin.post('/api/salestax/rates', { name: 'County', rate_pct: '2.5', agency: 'County' }), 'r2').id;
    const combo = ok(await admin.post('/api/salestax/rates', { name: 'Combined', kind: 'group', parts: [state, county] }), 'g').id;
    const cust = ok(await admin.post('/api/contacts', { kind: 'customer', name: 'Taxed Co' }), 'c').id;
    const inc = acct(s, 'Sales');
    const d = ok(await admin.post('/api/documents', { kind: 'invoice', contact_id: cust, doc_date: '2026-03-05', tax_rate_id: combo, lines: [
      { description: 'Widgets', qty_milli: 1000, unit_price_cents: 10000, account_id: inc, taxable: true },
      { description: 'Delivery (not taxable)', qty_milli: 1000, unit_price_cents: 5000, account_id: inc, taxable: false }] }), 'invoice').id;
    const doc = ok(await admin.get(`/api/documents/${d}`), 'get');
    assert.equal(doc.subtotal_cents, 15000);
    assert.equal(doc.tax_cents, 650, '4% of 100.00 plus 2.5% of 100.00');
    assert.equal(doc.total_cents, 15650);
    assert.deepEqual(doc.tax.map((t) => [t.name, t.taxable_cents, t.tax_cents]), [['County', 10000, 250], ['State', 10000, 400]]);
    assert.deepEqual(doc.lines.map((l) => l.taxable), [1, 0]);
    assert.equal(balanceOf(s, 'Sales Tax Payable'), 650, 'the tax is owed to the agencies');
    assert.ok(checkLedgerIntegrity(s.db).ok);
    // the entry balances to the total
    const lines = s.db.prepare('SELECT SUM(debit_cents) d, SUM(credit_cents) c FROM journal_lines WHERE entry_id=?').get(doc.entry_id);
    assert.equal(lines.d, 15650); assert.equal(lines.c, 15650);
    // no rate chosen: no tax, even on lines marked taxable
    const d2 = ok(await admin.post('/api/documents', { kind: 'invoice', contact_id: cust, doc_date: '2026-03-06', lines: [{ description: 'x', qty_milli: 1000, unit_price_cents: 1000, account_id: inc, taxable: true }] }), 'plain').id;
    assert.equal(ok(await admin.get(`/api/documents/${d2}`), 'g2').tax_cents, 0);
    // a switched-off rate cannot be used on a new invoice
    ok(await admin.patch(`/api/salestax/rates/${county}`, { active: false }), 'off');
    const refused = await admin.post('/api/documents', { kind: 'invoice', contact_id: cust, doc_date: '2026-03-07', tax_rate_id: combo, lines: [{ description: 'x', qty_milli: 1000, unit_price_cents: 1000, account_id: inc }] });
    assert.equal(refused.status, 400); assert.match(refused.json.error, /switched off/);
    // bills never carry sales tax
    const vend = ok(await admin.post('/api/contacts', { kind: 'vendor', name: 'Supplier' }), 'v').id;
    const bill = ok(await admin.post('/api/documents', { kind: 'bill', contact_id: vend, doc_date: '2026-03-08', tax_rate_id: combo, lines: [{ description: 'x', qty_milli: 1000, unit_price_cents: 1000, account_id: acct(s, 'Office Supplies') }] }), 'bill').id;
    assert.equal(ok(await admin.get(`/api/documents/${bill}`), 'gb').tax_cents, 0);
  } finally { await s.close(); }
});

test('a tax-exempt customer is never charged; an items taxable flag is the default for its lines', async () => {
  const s = await startServer();
  try {
    const admin = await setupAdmin(s);
    const rate = ok(await admin.post('/api/salestax/rates', { name: 'State', rate_pct: '5' }), 'rate').id;
    const exempt = ok(await admin.post('/api/contacts', { kind: 'customer', name: 'School District', tax_exempt: true, tax_exempt_note: 'Cert 12-345' }), 'c').id;
    assert.equal(ok(await admin.get('/api/contacts?kind=customer'), 'l').find((c) => c.id === exempt).tax_exempt, 1);
    const inc = acct(s, 'Sales');
    const d = ok(await admin.post('/api/documents', { kind: 'invoice', contact_id: exempt, doc_date: '2026-04-01', tax_rate_id: rate, lines: [{ description: 'x', qty_milli: 1000, unit_price_cents: 20000, account_id: inc, taxable: true }] }), 'inv').id;
    const doc = ok(await admin.get(`/api/documents/${d}`), 'g');
    assert.equal(doc.tax_cents, 0); assert.equal(doc.tax_exempt, 1); assert.equal(doc.total_cents, 20000);
    // items: a non-taxable item leaves its lines unticked
    const food = ok(await admin.post('/api/items', { name: 'Groceries', kind: 'product', taxable: false, income_account_id: inc, unit_price_cents: 1000 }), 'item').id;
    const buyer = ok(await admin.post('/api/contacts', { kind: 'customer', name: 'Buyer' }), 'b').id;
    const d2 = ok(await admin.post('/api/documents', { kind: 'invoice', contact_id: buyer, doc_date: '2026-04-02', tax_rate_id: rate, lines: [{ item_id: food, qty_milli: 1000 }, { description: 'Other', qty_milli: 1000, unit_price_cents: 1000, account_id: inc }] }), 'inv2').id;
    const doc2 = ok(await admin.get(`/api/documents/${d2}`), 'g2');
    assert.deepEqual(doc2.lines.map((l) => l.taxable), [0, 1]);
    assert.equal(doc2.tax_cents, 50);
    // exempting a customer later changes new invoices only
    ok(await admin.patch(`/api/contacts/${buyer}`, { tax_exempt: true }), 'exempt buyer');
    assert.equal(ok(await admin.get(`/api/documents/${d2}`), 'still').tax_cents, 50);
  } finally { await s.close(); }
});

test('the return worksheet splits sales, finds the tax per jurisdiction, ignores voided invoices and ties out to the ledger', async () => {
  const s = await startServer();
  try {
    const admin = await setupAdmin(s);
    const state = ok(await admin.post('/api/salestax/rates', { name: 'State', rate_pct: '4', agency: 'State Revenue' }), 'r').id;
    const county = ok(await admin.post('/api/salestax/rates', { name: 'County', rate_pct: '1', agency: 'County' }), 'r2').id;
    const combo = ok(await admin.post('/api/salestax/rates', { name: 'Combined', kind: 'group', parts: [state, county] }), 'g').id;
    const inc = acct(s, 'Sales');
    const shop = ok(await admin.post('/api/contacts', { kind: 'customer', name: 'Shop' }), 'c1').id;
    const school = ok(await admin.post('/api/contacts', { kind: 'customer', name: 'School', tax_exempt: true, tax_exempt_note: 'ST-9' }), 'c2').id;
    const inv = async (contact, date, lines, rate = combo) => ok(await admin.post('/api/documents', { kind: 'invoice', contact_id: contact, doc_date: date, tax_rate_id: rate, lines }), 'inv');
    const L = (cents, taxable) => ({ description: 'l', qty_milli: 1000, unit_price_cents: cents, account_id: inc, taxable });
    const a = (await inv(shop, '2026-01-10', [L(10000, true), L(2000, false)])).id;   // taxable 100.00, non-taxable 20.00, tax 5.00
    const b = (await inv(school, '2026-01-20', [L(30000, true)])).id;                  // exempt 300.00
    const c = (await inv(shop, '2026-01-25', [L(5000, true)])).id;                     // taxable 50.00 tax 2.50 (voided below)
    const outside = (await inv(shop, '2026-02-02', [L(9900, true)])).id;               // another month
    ok(await admin.post(`/api/documents/${c}/void`), 'void');
    const w = ok(await admin.get('/api/salestax/worksheet?from=2026-01-01&to=2026-01-31'), 'worksheet');
    assert.deepEqual(w.sales, { total_cents: 10000 + 2000 + 30000, taxable_cents: 10000, exempt_cents: 30000, nontaxable_cents: 2000 });
    assert.equal(w.tax_collected_cents, 500);
    assert.deepEqual(w.jurisdictions.map((j) => [j.name, j.agency, j.rate_milli, j.taxable_cents, j.tax_cents]), [['County', 'County', 1000, 10000, 100], ['State', 'State Revenue', 4000, 10000, 400]]);
    assert.deepEqual(w.exempt_customers, [{ customer: 'School', note: 'ST-9', sales_cents: 30000 }]);
    assert.deepEqual(w.invoices.map((i) => i.id), [a, b], 'voided and other-month invoices are not in the period');
    assert.equal(w.ledger.other_entries_cents, 0);
    assert.equal(w.ledger.closing_cents - w.ledger.opening_cents, 500 + 250, 'the ledger shows the voided invoice until its reversal, which is dated when it was voided');
    // February picks up the other invoice and the opening balance carries
    const f = ok(await admin.get('/api/salestax/worksheet?from=2026-02-01&to=2026-02-28'), 'feb');
    assert.equal(f.tax_collected_cents, 495); assert.equal(f.ledger.opening_cents, 750, 'the voided invoice is still in the ledger until its reversal date');
    assert.ok(outside);
    // tax recorded by something other than an invoice (an import, a manual entry) shows up as its own line
    ok(await admin.post('/api/journal', { date: '2026-01-31', memo: 'Tax from an old system', lines: [{ account_id: acct(s, 'Checking'), debit_cents: 700 }, { account_id: acct(s, 'Sales Tax Payable'), credit_cents: 700 }] }), 'journal');
    assert.equal(ok(await admin.get('/api/salestax/worksheet?from=2026-01-01&to=2026-01-31'), 'again').ledger.other_entries_cents, 700);
    assert.equal((await admin.get('/api/salestax/worksheet?from=2026-02-01&to=2026-01-01')).status, 400);
    assert.equal((await admin.get('/api/salestax/worksheet?from=nope&to=2026-01-01')).status, 400);
  } finally { await s.close(); }
});

test('paying the tax agency posts to the books, shows on the worksheet and can be voided', async () => {
  const s = await startServer();
  try {
    const admin = await setupAdmin(s);
    const rate = ok(await admin.post('/api/salestax/rates', { name: 'State', rate_pct: '10', agency: 'State Revenue' }), 'rate').id;
    const cust = ok(await admin.post('/api/contacts', { kind: 'customer', name: 'C' }), 'c').id;
    ok(await admin.post('/api/documents', { kind: 'invoice', contact_id: cust, doc_date: '2026-05-10', tax_rate_id: rate, lines: [{ description: 'x', qty_milli: 1000, unit_price_cents: 100000, account_id: acct(s, 'Sales') }] }), 'inv');
    assert.equal(balanceOf(s, 'Sales Tax Payable'), 10000);
    const pid = ok(await admin.post('/api/salestax/payments', { agency: 'State Revenue', pay_date: '2026-06-15', amount_cents: 10000, bank_account_id: acct(s, 'Checking'), period_from: '2026-05-01', period_to: '2026-05-31' }), 'pay').id;
    assert.equal(balanceOf(s, 'Sales Tax Payable'), 0);
    const list = ok(await admin.get('/api/salestax/payments'), 'list');
    assert.equal(list.length, 1); assert.equal(list[0].tax_account_name, 'Sales Tax Payable'); assert.match(list[0].memo, /State Revenue/);
    const w = ok(await admin.get('/api/salestax/worksheet?from=2026-06-01&to=2026-06-30'), 'w');
    assert.equal(w.ledger.remitted_cents, 10000); assert.deepEqual(w.remittances, [{ agency: 'State Revenue', amount_cents: 10000 }]);
    assert.equal(w.ledger.other_entries_cents, 0, 'the payment is not an unexplained entry');
    // wrong accounts are refused
    assert.equal((await admin.post('/api/salestax/payments', { pay_date: '2026-06-16', amount_cents: 1, bank_account_id: acct(s, 'Sales') })).status, 400);
    assert.equal((await admin.post('/api/salestax/payments', { pay_date: '2026-06-16', amount_cents: 1, bank_account_id: acct(s, 'Checking'), tax_account_id: acct(s, 'Rent') })).status, 400);
    ok(await admin.post(`/api/salestax/payments/${pid}/void`), 'void');
    assert.equal(balanceOf(s, 'Sales Tax Payable'), 10000, 'voiding puts the liability back');
    assert.equal((await admin.post(`/api/salestax/payments/${pid}/void`)).status, 400);
    assert.equal(ok(await admin.get('/api/salestax/worksheet?from=2026-06-01&to=2026-06-30'), 'w2').ledger.remitted_cents, 0);
    assert.ok(checkLedgerIntegrity(s.db).ok);
  } finally { await s.close(); }
});

test('who may change sales tax: view-only people can look but not change', async () => {
  const s = await startServer();
  try {
    const admin = await setupAdmin(s);
    const role = s.db.prepare("SELECT id FROM roles WHERE name='View only'").get().id;
    ok(await admin.post('/api/users', { username: 'looker', display_name: 'Looker', password: PASSWORD, role_id: role }), 'user');
    s.db.prepare("UPDATE users SET must_change_password=0 WHERE username='looker'").run();
    const v = await loginAs(s, 'looker');
    assert.equal((await v.get('/api/salestax/rates')).status, 200);
    assert.equal((await v.get('/api/salestax/worksheet?from=2026-01-01&to=2026-01-31')).status, 200);
    assert.equal((await v.post('/api/salestax/rates', { name: 'X', rate_pct: '1' })).status, 403);
    assert.equal((await v.post('/api/salestax/payments', { pay_date: '2026-01-01', amount_cents: 1, bank_account_id: 1 })).status, 403);
  } finally { await s.close(); }
});

test('the upgrade to sales tax keeps existing data and can be run twice', () => {
  const db = openDb(':memory:');
  db.prepare("INSERT INTO contacts(kind,name,created_at) VALUES ('customer','Old Customer',0)").run();
  migrate8(db); migrate8(db);
  const c = db.prepare("SELECT tax_exempt, tax_rate_id, tax_exempt_note FROM contacts WHERE name='Old Customer'").get();
  assert.deepEqual({ ...c }, { tax_exempt: 0, tax_rate_id: null, tax_exempt_note: '' });
  assert.equal(db.prepare('PRAGMA user_version').get().user_version, 8);
});
