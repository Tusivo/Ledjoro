'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const { startServer, setupAdmin } = require('./helpers');
const { checkLedgerIntegrity } = require('../src/ledger');

const acct = (s, name) => s.db.prepare('SELECT id FROM accounts WHERE name=? COLLATE NOCASE').get(name).id;
const ok = (r, what) => { assert.equal(r.status, 200, `${what}: ${r.text}`); return r.json; };
const balance = (s, sub) => Number(s.db.prepare('SELECT COALESCE(SUM(debit_cents-credit_cents),0) b FROM journal_lines l JOIN accounts a ON a.id=l.account_id WHERE a.subtype=?').get(sub).b);

async function setup() {
  const s = await startServer(); const admin = await setupAdmin(s);
  const cust = ok(await admin.post('/api/contacts', { kind: 'customer', name: 'Returns Co' }), 'c').id;
  const rate = ok(await admin.post('/api/salestax/rates', { name: 'State', rate_pct: '10', agency: 'State Revenue' }), 'r').id;
  const inc = acct(s, 'Sales');
  const invoice = async (cents, date = '2026-06-01') => ok(await admin.post('/api/documents', { kind: 'invoice', contact_id: cust, doc_date: date, tax_rate_id: rate, lines: [{ description: 'Goods', qty_milli: 1000, unit_price_cents: cents, account_id: inc, taxable: true }] }), 'inv').id;
  const credit = async (cents, extra = {}) => ok(await admin.post('/api/credits', { contact_id: cust, memo_date: '2026-06-10', tax_rate_id: rate, lines: [{ description: 'Returned goods', qty_milli: 1000, unit_price_cents: cents, account_id: inc, taxable: true }], ...extra }), 'credit').id;
  return { s, admin, cust, rate, inc, invoice, credit };
}

test('a credit memo reduces what the customer owes, takes the tax back, and can be applied to an invoice', async () => {
  const { s, admin, invoice, credit } = await setup();
  try {
    const inv = await invoice(10000);                        // 100.00 + 10.00 tax = 110.00 owed
    assert.equal(balance(s, 'ar'), 11000);
    const cm = await credit(4000);                           // 40.00 + 4.00 tax = 44.00 credit
    const c = ok(await admin.get(`/api/credits/${cm}`), 'get');
    assert.equal(c.total_cents, 4400); assert.equal(c.tax_cents, 400); assert.equal(c.remaining_cents, 4400); assert.equal(c.status, 'open');
    assert.match(c.number, /^CM-\d{4}$/);
    assert.equal(balance(s, 'ar'), 11000 - 4400, 'receivables go down by the credit, tax included');
    assert.ok(checkLedgerIntegrity(s.db).ok);
    // apply 30.00 now, the rest later
    ok(await admin.post(`/api/credits/${cm}/apply`, { document_id: inv, amount_cents: 3000 }), 'apply');
    let doc = ok(await admin.get(`/api/documents/${inv}`), 'doc');
    assert.equal(doc.paid_cents, 3000); assert.equal(doc.status, 'open'); assert.equal(doc.credits.length, 1);
    assert.equal(ok(await admin.get(`/api/credits/${cm}`), 'c2').remaining_cents, 1400);
    assert.equal(balance(s, 'ar'), 6600, 'applying a credit moves no money');
    // too much, wrong customer
    assert.equal((await admin.post(`/api/credits/${cm}/apply`, { document_id: inv, amount_cents: 1401 })).status, 400);
    const other = ok(await admin.post('/api/contacts', { kind: 'customer', name: 'Other Co' }), 'o').id;
    const foreign = ok(await admin.post('/api/documents', { kind: 'invoice', contact_id: other, doc_date: '2026-06-02', lines: [{ description: 'x', qty_milli: 1000, unit_price_cents: 500, account_id: acct(s, 'Sales') }] }), 'f').id;
    assert.equal((await admin.post(`/api/credits/${cm}/apply`, { document_id: foreign, amount_cents: 100 })).status, 400);
    // use the rest: the credit is used up
    ok(await admin.post(`/api/credits/${cm}/apply`, { document_id: inv, amount_cents: 1400 }), 'apply rest');
    assert.equal(ok(await admin.get(`/api/credits/${cm}`), 'c3').status, 'used');
    doc = ok(await admin.get(`/api/documents/${inv}`), 'doc2'); assert.equal(doc.total_cents - doc.paid_cents, 11000 - 4400);
    // taking an application back reopens both
    const appId = ok(await admin.get(`/api/credits/${cm}`), 'c4').applications[1].id;
    ok(await admin.post(`/api/credits/applications/${appId}/remove`), 'remove');
    assert.equal(ok(await admin.get(`/api/credits/${cm}`), 'c5').status, 'open');
    assert.equal(ok(await admin.get(`/api/documents/${inv}`), 'doc3').paid_cents, 3000);
    // a credit that has been applied cannot be voided
    assert.equal((await admin.post(`/api/credits/${cm}/void`)).status, 400);
  } finally { await s.close(); }
});

test('a credit can be refunded in cash, the refund voided, and an unused credit voided', async () => {
  const { s, admin, credit } = await setup();
  try {
    const cm = await credit(10000);                              // 100 + 10 tax = 110 credit
    const bank = acct(s, 'Checking');
    assert.equal(balance(s, 'ar'), -11000, 'an unapplied credit shows as a negative receivable');
    const rf = ok(await admin.post(`/api/credits/${cm}/refund`, { pay_date: '2026-06-12', amount_cents: 5000, account_id: bank, method: 'Check', ref: '1001' }), 'refund').id;
    let c = ok(await admin.get(`/api/credits/${cm}`), 'c');
    assert.equal(c.refunded_cents, 5000); assert.equal(c.remaining_cents, 6000); assert.equal(c.refunds.length, 1);
    assert.equal(balance(s, 'ar'), -6000);
    assert.equal((await admin.post(`/api/credits/${cm}/refund`, { pay_date: '2026-06-12', amount_cents: 6001, account_id: bank })).status, 400);
    assert.equal((await admin.post(`/api/credits/${cm}/refund`, { pay_date: '2026-06-12', amount_cents: 100, account_id: acct(s, 'Sales') })).status, 400);
    assert.equal((await admin.post(`/api/credits/${cm}/void`)).status, 400, 'a refunded credit cannot be voided until the refund is');
    ok(await admin.post(`/api/credits/refunds/${rf}/void`), 'void refund');
    c = ok(await admin.get(`/api/credits/${cm}`), 'c2'); assert.equal(c.refunded_cents, 0); assert.equal(c.remaining_cents, 11000);
    assert.equal((await admin.post(`/api/credits/refunds/${rf}/void`)).status, 400);
    ok(await admin.post(`/api/credits/${cm}/void`), 'void');
    assert.equal(ok(await admin.get(`/api/credits/${cm}`), 'c3').status, 'void');
    assert.equal(balance(s, 'ar'), 0);
    assert.ok(checkLedgerIntegrity(s.db).ok);
  } finally { await s.close(); }
});

test('credit memos come off the sales tax return worksheet', async () => {
  const { s, admin, invoice, credit } = await setup();
  try {
    await invoice(20000, '2026-07-05');                       // tax 20.00
    const cm = await credit(5000, { memo_date: '2026-07-20' });  // tax 5.00 back
    const w = ok(await admin.get('/api/salestax/worksheet?from=2026-07-01&to=2026-07-31'), 'w');
    assert.equal(w.sales.total_cents, 15000); assert.equal(w.sales.taxable_cents, 15000);
    assert.equal(w.tax_collected_cents, 1500);
    assert.deepEqual(w.jurisdictions.map((j) => [j.name, j.taxable_cents, j.tax_cents]), [['State', 15000, 1500]]);
    assert.equal(w.credit_memos.count, 1); assert.equal(w.credit_memos.subtotal_cents, 5000);
    const row = w.invoices.find((i) => i.kind === 'credit');
    assert.equal(row.subtotal_cents, -5000); assert.equal(row.tax_cents, -500);
    assert.equal(w.ledger.other_entries_cents, 0, 'the credit memo is not an unexplained entry');
    assert.equal(w.ledger.closing_cents - w.ledger.opening_cents, 1500);
    // voiding the credit puts the tax back in the period
    ok(await admin.post(`/api/credits/${cm}/void`), 'void');
    assert.equal(ok(await admin.get('/api/salestax/worksheet?from=2026-07-01&to=2026-07-31'), 'w2').tax_collected_cents, 2000);
  } finally { await s.close(); }
});

test('tax-exempt customers are not charged tax on credit memos either, and bad requests are refused', async () => {
  const { s, admin, rate, inc } = await setup();
  try {
    const school = ok(await admin.post('/api/contacts', { kind: 'customer', name: 'School', tax_exempt: true }), 'c').id;
    const cm = ok(await admin.post('/api/credits', { contact_id: school, memo_date: '2026-08-01', tax_rate_id: rate, lines: [{ description: 'x', qty_milli: 1000, unit_price_cents: 2000, account_id: inc, taxable: true }] }), 'cm').id;
    const c = ok(await admin.get(`/api/credits/${cm}`), 'g');
    assert.equal(c.tax_cents, 0); assert.equal(c.tax_exempt, 1); assert.equal(c.total_cents, 2000);
    assert.equal((await admin.post('/api/credits', { contact_id: 99999, memo_date: '2026-08-01', lines: [{ description: 'x', qty_milli: 1000, unit_price_cents: 100, account_id: inc }] })).status, 400);
    assert.equal((await admin.get('/api/credits/9999')).status, 404);
    assert.equal(ok(await admin.get('/api/credits?status=open'), 'list').length, 1);
  } finally { await s.close(); }
});

test('entries made by credit memos, refunds and tax payments cannot be reversed from the journal', async () => {
  const { s, admin, credit } = await setup();
  try {
    const cm = await credit(1000);
    const bank = acct(s, 'Checking');
    ok(await admin.post(`/api/credits/${cm}/refund`, { pay_date: '2026-06-12', amount_cents: 500, account_id: bank }), 'refund');
    ok(await admin.post('/api/salestax/payments', { pay_date: '2026-06-13', amount_cents: 100, bank_account_id: bank }), 'tax payment');
    const entries = s.db.prepare("SELECT id, source_type FROM journal_entries WHERE source_type IN ('credit_memo','credit_refund','salestax')").all();
    assert.deepEqual(entries.map((e) => e.source_type).sort(), ['credit_memo', 'credit_refund', 'salestax']);
    for (const e of entries) {
      const r = await admin.post(`/api/journal/${e.id}/reverse`, {});
      assert.equal(r.status, 400, e.source_type);
      assert.match(r.json.error, /cannot be reversed on its own/);
    }
  } finally { await s.close(); }
});
