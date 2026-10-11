'use strict';
/**
 * 13-week cash forecast built only from your own history (no outside data, no AI).
 *  - "Already committed": open invoices expected when each customer usually pays, open bills on their due dates.
 *  - "Typical activity continues": the above, plus new invoices and bills at the recent monthly rate (collected/paid after the usual lag),
 *    plus the recurring cash that is not tied to invoices or bills (payroll, loan payments, owner draws, card spending...).
 */
const { addDays, daysBetween, today, formatMoney } = require('./util');

const WEEKS_PER_MONTH = 30.4375 / 7;
const clamp = (v, lo, hi) => Math.max(lo, Math.min(hi, v));
const $ = (c) => (c < 0 ? '-' : '') + '$' + formatMoney(Math.abs(c)).replace(/\B(?=(\d{3})+(?!\d))/g, ',');

function monthStart(date, delta) { // first day of the month `delta` months from `date`'s month
  const y = Number(date.slice(0, 4)), m = Number(date.slice(5, 7)) - 1 + delta;
  return `${y + Math.floor(m / 12)}-${String(((m % 12) + 12) % 12 + 1).padStart(2, '0')}-01`;
}

function cashForecast(db, { asof = today(), weeks = 13 } = {}) {
  weeks = clamp(Math.round(Number(weeks) || 13), 4, 52);
  const week = (date) => { const k = Math.floor(daysBetween(asof, date) / 7); return k < 0 ? 0 : k >= weeks ? null : k; };

  const cashNow = db.prepare(`SELECT COALESCE(SUM(l.debit_cents-l.credit_cents),0) n FROM journal_lines l JOIN journal_entries e ON e.id=l.entry_id JOIN accounts a ON a.id=l.account_id WHERE a.subtype='bank' AND e.entry_date<=?`).get(asof).n;

  // how late (days after due date) each customer usually pays, from the last 12 months
  const lateRows = db.prepare(`SELECT d.contact_id, AVG(julianday(p.pay_date)-julianday(d.due_date)) late FROM payment_applications pa
    JOIN payments p ON p.id=pa.payment_id AND p.voided=0 JOIN documents d ON d.id=pa.document_id AND d.kind='invoice' WHERE p.pay_date>=? GROUP BY d.contact_id`).all(addDays(asof, -365));
  const lateBy = new Map(lateRows.map((r) => [r.contact_id, clamp(Math.round(r.late), 0, 60)]));
  const overallLate = lateRows.length ? clamp(Math.round(lateRows.reduce((s, r) => s + r.late, 0) / lateRows.length), 0, 60) : 10;

  const mk = () => Array.from({ length: weeks }, (_, k) => ({ week: k, start: addDays(asof, k * 7), end: addDays(asof, k * 7 + 6), open_receipts: 0, open_payments: 0, new_receipts: 0, new_payments: 0, other_in: 0, other_out: 0 }));
  const rows = mk();

  let openInv = 0, openBills = 0;
  for (const d of db.prepare("SELECT * FROM documents WHERE status='open'").all()) {
    const bal = d.total_cents - d.paid_cents;
    if (d.kind === 'invoice') {
      const k = week(addDays(d.due_date, lateBy.get(d.contact_id) ?? overallLate));
      openInv += bal; if (k !== null) rows[k].open_receipts += bal;
    } else { const k = week(d.due_date); openBills += bal; if (k !== null) rows[k].open_payments += bal; }
  }

  // recent run-rate: the three full calendar months before this one
  const from = monthStart(asof, -3), to = addDays(monthStart(asof, 0), -1);
  const sumDocs = (kind) => db.prepare("SELECT COALESCE(SUM(total_cents),0) n FROM documents WHERE kind=? AND status<>'void' AND doc_date BETWEEN ? AND ?").get(kind, from, to).n / 3;
  const avgInvoiced = sumDocs('invoice'), avgBilled = sumDocs('bill');
  const lag = (kind, fallback) => {
    const r = db.prepare(`SELECT AVG(julianday(p.pay_date)-julianday(d.doc_date)) lag FROM payment_applications pa JOIN payments p ON p.id=pa.payment_id AND p.voided=0 JOIN documents d ON d.id=pa.document_id AND d.kind=? WHERE p.pay_date>=?`).get(kind, addDays(asof, -365)).lag;
    return r === null ? fallback : clamp(Math.round(r), 0, 120);
  };
  const invLag = lag('invoice', 30), billLag = lag('bill', 20);
  const other = db.prepare(`SELECT COALESCE(SUM(l.debit_cents),0) inflow, COALESCE(SUM(l.credit_cents),0) outflow FROM journal_lines l JOIN journal_entries e ON e.id=l.entry_id JOIN accounts a ON a.id=l.account_id
    WHERE a.subtype='bank' AND e.entry_date BETWEEN ? AND ? AND e.source_type NOT IN ('payment','invoice','bill','reversal') AND e.memo NOT LIKE 'Owner distribution%'
    AND e.id NOT IN (SELECT entry_id FROM entry_reversals UNION SELECT reversal_id FROM entry_reversals)`).get(from, to);
  const weekly = { newIn: Math.round(avgInvoiced / WEEKS_PER_MONTH), newOut: Math.round(avgBilled / WEEKS_PER_MONTH), otherIn: Math.round(other.inflow / 3 / WEEKS_PER_MONTH), otherOut: Math.round(other.outflow / 3 / WEEKS_PER_MONTH) };
  for (const r of rows) {
    if (r.week >= Math.ceil(invLag / 7)) r.new_receipts = weekly.newIn;
    if (r.week >= Math.ceil(billLag / 7)) r.new_payments = weekly.newOut;
    r.other_in = weekly.otherIn; r.other_out = weekly.otherOut;
  }

  const run = (typical) => {
    let bal = cashNow, min = { closing_cents: Infinity, week: 0 }, negative = 0;
    const out = rows.map((r) => {
      const receipts = r.open_receipts + (typical ? r.new_receipts + r.other_in : 0);
      const payments = r.open_payments + (typical ? r.new_payments + r.other_out : 0);
      bal += receipts - payments;
      if (bal < min.closing_cents) min = { closing_cents: bal, week: r.week };
      if (bal < 0) negative++;
      return { week: r.week, start: r.start, end: r.end, receipts_cents: receipts, payments_cents: payments, net_cents: receipts - payments, closing_cents: bal,
        detail: typical ? { open_invoices: r.open_receipts, new_invoices: r.new_receipts, other_in: r.other_in, open_bills: r.open_payments, new_bills: r.new_payments, other_out: r.other_out } : { open_invoices: r.open_receipts, open_bills: r.open_payments } };
    });
    return { weeks: out, lowest: min, negative_weeks: negative, ending_cents: bal };
  };

  return {
    asof, weeks, cash_now_cents: cashNow, open_invoices_cents: openInv, open_bills_cents: openBills,
    assumptions: [
      `Starting cash is the ledger balance of your bank accounts: ${$(cashNow)}.`,
      `Open invoices (${$(openInv)}) are expected on the due date plus how late each customer usually pays (${overallLate} days on average, from the last 12 months).`,
      `Open bills (${$(openBills)}) are paid on their due dates.`,
      `"Typical activity" adds new invoices at ${$(Math.round(avgInvoiced))} a month, collected after ${invLag} days, and new bills at ${$(Math.round(avgBilled))} a month, paid after ${billLag} days (averages of ${from} to ${to}).`,
      `It also adds recurring cash not tied to invoices or bills (payroll, loan payments, owner draws, card spending): about ${$(Math.round(other.inflow / 3))} in and ${$(Math.round(other.outflow / 3))} out a month. One-off owner distributions are left out.`,
      'This is an estimate from your own history. It cannot know about a new customer, a late supplier, or a big purchase you have not entered yet.',
    ],
    scenarios: { committed: run(false), typical: run(true) },
  };
}

module.exports = { cashForecast };
