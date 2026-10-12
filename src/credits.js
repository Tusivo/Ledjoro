'use strict';
/**
 * Credit memos: money a customer is owed back (a returned item, an overcharge, a goodwill discount). A credit memo is posted like an invoice in
 * reverse (debit income and sales tax, credit receivables), and can then be applied to open invoices of the same customer or refunded.
 * Applying a credit changes no ledger entry: the receivable was already reduced when the memo was made, so only the invoice's own balance moves.
 */
const { tx, getSetting, setSetting } = require('./db');
const { bad, HttpError, vStr, vInt, vDate, vBool, vEnum, vObj, vArr, today } = require('./util');
const { postEntry, reverseEntry, systemAccount } = require('./ledger');
const { audit } = require('./audit');
const ST = require('./salestax');

const remaining = (c) => c.total_cents - c.applied_cents - c.refunded_cents;
const lineAmount = (qtyMilli, unit) => {
  const v = Math.round((qtyMilli * unit) / 1000);
  if (!Number.isSafeInteger(v)) throw bad('A line amount is too large to calculate. Check the quantity and price for a misplaced decimal point or an extra zero.');
  return v;
};

function nextNumber(db) {
  let n = Number(getSetting(db, 'next_credit') || 1);
  const used = db.prepare('SELECT 1 FROM credit_memos WHERE number=?');
  while (used.get('CM-' + String(n).padStart(4, '0'))) n++;
  setSetting(db, 'next_credit', n + 1);
  return 'CM-' + String(n).padStart(4, '0');
}

function createCredit(db, ctx, b) {
  const contactId = vInt(b.contact_id, 'contact_id');
  const contact = db.prepare('SELECT * FROM contacts WHERE id=?').get(contactId);
  if (!contact || contact.kind !== 'customer') throw bad('Choose a customer for this credit memo. None is chosen, or the one chosen is not a customer.');
  const date = vDate(b.memo_date ?? b.doc_date, 'date');
  const raw = vArr(b.lines, 'lines', { min: 1, max: 200 });
  const lines = raw.map((l, i) => {
    vObj(l, `line ${i + 1}`);
    const item = l.item_id ? db.prepare('SELECT * FROM items WHERE id=?').get(vInt(l.item_id, 'item')) : null;
    const qty = vInt(l.qty_milli ?? 1000, `line ${i + 1} quantity`, { min: 1, max: 1e9 });
    const unit = vInt(l.unit_price_cents ?? item?.unit_price_cents ?? 0, `line ${i + 1} price`, { min: 0, max: 1e12 });
    const account = vInt(l.account_id ?? item?.income_account_id, `line ${i + 1} account`);
    const taxable = l.taxable === undefined ? (item ? item.taxable : 1) : vBool(l.taxable);
    return { item_id: item?.id ?? null, description: vStr(l.description ?? item?.name, `line ${i + 1} description`, { max: 300 }), qty_milli: qty, unit_price_cents: unit, amount_cents: lineAmount(qty, unit), account_id: account, taxable };
  });
  const subtotal = lines.reduce((s, l) => s + l.amount_cents, 0);
  if (subtotal <= 0) throw bad('The total is zero or less. Add at least one line with an amount above zero.');
  const taxExempt = contact.tax_exempt ? 1 : 0;
  const taxRateId = !taxExempt && b.tax_rate_id ? vInt(b.tax_rate_id, 'tax rate') : null;
  const taxed = taxRateId ? ST.taxFor(db, taxRateId, lines.filter((l) => l.taxable).reduce((s, l) => s + l.amount_cents, 0)) : { parts: [], total_cents: 0 };
  const total = subtotal + taxed.total_cents;
  const control = systemAccount(db, 'ar');

  return tx(db, () => {
    const number = vStr(b.number, 'number', { max: 30 }) || nextNumber(db);
    if (db.prepare('SELECT 1 FROM credit_memos WHERE number=?').get(number)) throw bad(`The credit memo number ${number} is already used. Leave the number empty to let Ledjoro pick the next free one.`);
    const entry = postEntry(db, {
      date, memo: `Credit memo ${number}`, ref: number, source_type: 'credit_memo', user_id: ctx.user.id,
      lines: [
        ...lines.map((l) => ({ account_id: l.account_id, contact_id: contactId, debit_cents: l.amount_cents, memo: l.description })),
        ...taxed.parts.filter((p) => p.tax_cents > 0).map((p) => ({ account_id: p.account_id, contact_id: contactId, debit_cents: p.tax_cents, memo: `Sales tax - ${p.name}` })),
        { account_id: control, contact_id: contactId, credit_cents: total },
      ],
    });
    const id = Number(db.prepare('INSERT INTO credit_memos(number,contact_id,memo_date,memo,total_cents,tax_cents,tax_rate_id,tax_exempt,status,entry_id,created_by,created_at) VALUES (?,?,?,?,?,?,?,?,?,?,?,?)')
      .run(number, contactId, date, vStr(b.memo, 'memo', { max: 500 }), total, taxed.total_cents, taxRateId, taxExempt, 'open', entry, ctx.user.id, Date.now()).lastInsertRowid);
    const insL = db.prepare('INSERT INTO credit_memo_lines(credit_id,item_id,description,qty_milli,unit_price_cents,amount_cents,account_id,taxable) VALUES (?,?,?,?,?,?,?,?)');
    for (const l of lines) insL.run(id, l.item_id, l.description, l.qty_milli, l.unit_price_cents, l.amount_cents, l.account_id, l.taxable);
    const insT = db.prepare('INSERT INTO credit_memo_tax(credit_id,rate_id,rate_milli,taxable_cents,tax_cents) VALUES (?,?,?,?,?)');
    for (const p of taxed.parts) insT.run(id, p.rate_id, p.rate_milli, p.taxable_cents, p.tax_cents);
    audit(db, ctx, 'credit.create', 'credit_memo', id, `${number} total ${total}`);
    return id;
  });
}

function getCredit(db, id) {
  const c = db.prepare('SELECT c.*, k.name AS contact_name FROM credit_memos c JOIN contacts k ON k.id=c.contact_id WHERE c.id=?').get(id);
  if (!c) throw new HttpError(404, 'That credit memo could not be found. It may have been removed; go back to the list and refresh.');
  c.lines = db.prepare('SELECT l.*, a.name AS account_name FROM credit_memo_lines l JOIN accounts a ON a.id=l.account_id WHERE credit_id=?').all(id);
  c.tax = db.prepare('SELECT t.rate_id, r.name, r.agency, t.rate_milli, t.taxable_cents, t.tax_cents FROM credit_memo_tax t JOIN tax_rates r ON r.id=t.rate_id WHERE credit_id=? ORDER BY r.name').all(id);
  c.applications = db.prepare('SELECT a.id, a.amount_cents, a.document_id, d.number AS document_number, a.applied_on FROM credit_applications a JOIN documents d ON d.id=a.document_id WHERE a.credit_id=? ORDER BY a.id').all(id);
  c.refunds = db.prepare('SELECT r.*, b.name AS bank_name FROM credit_refunds r JOIN accounts b ON b.id=r.account_id WHERE r.credit_id=? ORDER BY r.id').all(id);
  c.subtotal_cents = c.total_cents - c.tax_cents;
  c.remaining_cents = remaining(c);
  return c;
}

const listCredits = (db, status) => db.prepare(`SELECT c.*, k.name AS contact_name, c.total_cents - c.applied_cents - c.refunded_cents AS remaining_cents
  FROM credit_memos c JOIN contacts k ON k.id=c.contact_id WHERE (? IS NULL OR c.status=?) ORDER BY c.memo_date DESC, c.id DESC LIMIT 20000`).all(status ?? null, status ?? null);

function applyCredit(db, ctx, creditId, b) {
  const c = db.prepare('SELECT * FROM credit_memos WHERE id=?').get(creditId);
  if (!c) throw new HttpError(404, 'That credit memo could not be found. It may have been removed; refresh the list.');
  if (c.status !== 'open') throw bad(`${c.number} has nothing left to use, or has been voided.`);
  const amount = vInt(b.amount_cents, 'amount', { min: 1, max: 1e13 });
  const d = db.prepare('SELECT * FROM documents WHERE id=?').get(vInt(b.document_id, 'invoice'));
  if (!d || d.kind !== 'invoice' || d.contact_id !== c.contact_id) throw bad('A credit memo can only be applied to an invoice of the same customer. Choose one of their open invoices.');
  if (d.status !== 'open') throw bad(`${d.number} is not open, so a credit cannot be applied to it.`);
  if (amount > remaining(c)) throw bad(`Only ${(remaining(c) / 100).toFixed(2)} is left on ${c.number}. Lower the amount.`);
  if (amount > d.total_cents - d.paid_cents) throw bad(`Only ${((d.total_cents - d.paid_cents) / 100).toFixed(2)} is still owed on ${d.number}. Lower the amount, or apply the rest to another invoice.`);
  return tx(db, () => {
    const id = Number(db.prepare('INSERT INTO credit_applications(credit_id,document_id,amount_cents,applied_on,created_by,created_at) VALUES (?,?,?,?,?,?)').run(creditId, d.id, amount, today(), ctx.user.id, Date.now()).lastInsertRowid);
    db.prepare("UPDATE documents SET paid_cents=paid_cents+?, status=CASE WHEN paid_cents+?=total_cents THEN 'paid' ELSE status END WHERE id=?").run(amount, amount, d.id);
    db.prepare("UPDATE credit_memos SET applied_cents=applied_cents+?, status=CASE WHEN total_cents-applied_cents-refunded_cents-?=0 THEN 'used' ELSE status END WHERE id=?").run(amount, amount, creditId);
    audit(db, ctx, 'credit.apply', 'credit_memo', creditId, `${c.number} to ${d.number} ${amount}`);
    return id;
  });
}

function removeApplication(db, ctx, appId) {
  const a = db.prepare('SELECT * FROM credit_applications WHERE id=?').get(appId);
  if (!a) throw new HttpError(404, 'That credit application could not be found. It may already have been removed; refresh the page.');
  const d = db.prepare('SELECT * FROM documents WHERE id=?').get(a.document_id);
  if (d.status === 'void') throw bad(`${d.number} has been voided, so its credit cannot be taken back off it.`);
  tx(db, () => {
    db.prepare('DELETE FROM credit_applications WHERE id=?').run(appId);
    db.prepare("UPDATE documents SET paid_cents=paid_cents-?, status=CASE WHEN status='paid' THEN 'open' ELSE status END WHERE id=?").run(a.amount_cents, a.document_id);
    db.prepare("UPDATE credit_memos SET applied_cents=applied_cents-?, status=CASE WHEN status='used' THEN 'open' ELSE status END WHERE id=?").run(a.amount_cents, a.credit_id);
    audit(db, ctx, 'credit.unapply', 'credit_memo', a.credit_id, `${d.number} ${a.amount_cents}`);
  });
}

function refundCredit(db, ctx, creditId, b) {
  const c = db.prepare('SELECT * FROM credit_memos WHERE id=?').get(creditId);
  if (!c) throw new HttpError(404, 'That credit memo could not be found. It may have been removed; refresh the list.');
  if (c.status !== 'open') throw bad(`${c.number} has nothing left to refund, or has been voided.`);
  const amount = vInt(b.amount_cents, 'amount', { min: 1, max: 1e13 });
  if (amount > remaining(c)) throw bad(`Only ${(remaining(c) / 100).toFixed(2)} is left on ${c.number}. Lower the amount.`);
  const bankId = vInt(b.account_id, 'account you paid from');
  const bank = db.prepare('SELECT subtype FROM accounts WHERE id=?').get(bankId);
  if (!bank || !['bank', 'credit_card', 'other_current_asset'].includes(bank.subtype)) throw bad('Choose where the money came from: a bank, credit card or cash account. The account chosen is a different kind of account.');
  const date = vDate(b.pay_date, 'date');
  const control = systemAccount(db, 'ar');
  return tx(db, () => {
    const memo = `Refund of credit memo ${c.number}`;
    const entry = postEntry(db, { date, memo, ref: vStr(b.ref, 'reference', { max: 50 }), source_type: 'credit_refund', user_id: ctx.user.id,
      lines: [{ account_id: control, contact_id: c.contact_id, debit_cents: amount, memo }, { account_id: bankId, credit_cents: amount, memo }] });
    const id = Number(db.prepare('INSERT INTO credit_refunds(credit_id,pay_date,amount_cents,account_id,method,ref,entry_id,created_by,created_at) VALUES (?,?,?,?,?,?,?,?,?)')
      .run(creditId, date, amount, bankId, vStr(b.method, 'method', { max: 30 }), vStr(b.ref, 'reference', { max: 50 }), entry, ctx.user.id, Date.now()).lastInsertRowid);
    db.prepare("UPDATE credit_memos SET refunded_cents=refunded_cents+?, status=CASE WHEN total_cents-applied_cents-refunded_cents-?=0 THEN 'used' ELSE status END WHERE id=?").run(amount, amount, creditId);
    audit(db, ctx, 'credit.refund', 'credit_memo', creditId, `${c.number} ${amount}`);
    return id;
  });
}

function voidRefund(db, ctx, refundId) {
  const r = db.prepare('SELECT * FROM credit_refunds WHERE id=?').get(refundId);
  if (!r) throw new HttpError(404, 'That refund could not be found. It may have been removed; refresh the page.');
  if (r.voided) throw bad('This refund has already been voided.');
  tx(db, () => {
    reverseEntry(db, r.entry_id, { date: today() > r.pay_date ? today() : r.pay_date, userId: ctx.user.id, memo: `Void refund #${r.id}` });
    db.prepare('UPDATE credit_refunds SET voided=1 WHERE id=?').run(refundId);
    db.prepare("UPDATE credit_memos SET refunded_cents=refunded_cents-?, status=CASE WHEN status='used' THEN 'open' ELSE status END WHERE id=?").run(r.amount_cents, r.credit_id);
    audit(db, ctx, 'credit.refund.void', 'credit_memo', r.credit_id, String(r.amount_cents));
  });
}

function voidCredit(db, ctx, id) {
  const c = db.prepare('SELECT * FROM credit_memos WHERE id=?').get(id);
  if (!c) throw new HttpError(404, 'That credit memo could not be found. It may have been removed; refresh the list.');
  if (c.status === 'void') throw bad('This credit memo has already been voided.');
  if (c.applied_cents > 0 || c.refunded_cents > 0) throw bad(`${c.number} has been applied to an invoice or refunded, so it cannot be voided yet. Remove the applications and void the refunds first.`);
  tx(db, () => {
    reverseEntry(db, c.entry_id, { date: today() > c.memo_date ? today() : c.memo_date, userId: ctx.user.id, memo: `Void credit memo ${c.number}` });
    db.prepare("UPDATE credit_memos SET status='void' WHERE id=?").run(id);
    audit(db, ctx, 'credit.void', 'credit_memo', id, c.number);
  });
}

module.exports = { createCredit, getCredit, listCredits, applyCredit, removeApplication, refundCredit, voidRefund, voidCredit, remaining };
