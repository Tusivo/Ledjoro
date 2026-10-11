'use strict';
const { daysBetween, today, vDate, bad, addDays } = require('./util');

const BASE = `FROM journal_lines l JOIN journal_entries e ON e.id=l.entry_id JOIN accounts a ON a.id=l.account_id`;

function accountTotals(db, { from, to }) {
  return db.prepare(`SELECT a.id, a.number, a.name, a.type, a.subtype, SUM(l.debit_cents) AS d, SUM(l.credit_cents) AS c ${BASE}
    WHERE (? IS NULL OR e.entry_date >= ?) AND (? IS NULL OR e.entry_date <= ?) GROUP BY a.id ORDER BY COALESCE(NULLIF(a.number,''),'zzzz'), a.name`)
    .all(from ?? null, from ?? null, to ?? null, to ?? null);
}

function trialBalance(db, { asof }) {
  const rows = accountTotals(db, { to: asof }).map((r) => {
    const net = r.d - r.c;
    return { account_id: r.id, number: r.number, name: r.name, debit_cents: net > 0 ? net : 0, credit_cents: net < 0 ? -net : 0 };
  }).filter((r) => r.debit_cents || r.credit_cents);
  const totals = rows.reduce((t, r) => ({ debit_cents: t.debit_cents + r.debit_cents, credit_cents: t.credit_cents + r.credit_cents }), { debit_cents: 0, credit_cents: 0 });
  return { asof, rows, totals, balanced: totals.debit_cents === totals.credit_cents };
}

const section = (rows, types, sign) => {
  const items = rows.filter((r) => types.includes(r.type)).map((r) => ({ account_id: r.id, number: r.number, name: r.name, amount_cents: sign * (r.d - r.c) })).filter((r) => r.amount_cents !== 0);
  return { items, total_cents: items.reduce((s, r) => s + r.amount_cents, 0) };
};

function profitLoss(db, { from, to }) {
  const rows = accountTotals(db, { from, to });
  const income = section(rows, ['income'], -1), cogs = section(rows, ['cogs'], 1), expenses = section(rows, ['expense'], 1);
  const otherIncome = section(rows, ['other_income'], -1), otherExpense = section(rows, ['other_expense'], 1);
  const gross = income.total_cents - cogs.total_cents;
  const operating = gross - expenses.total_cents;
  return { from, to, income, cogs, gross_profit_cents: gross, expenses, operating_income_cents: operating, other_income: otherIncome, other_expense: otherExpense, net_income_cents: operating + otherIncome.total_cents - otherExpense.total_cents };
}

function balanceSheet(db, { asof }) {
  const rows = accountTotals(db, { to: asof });
  const assets = section(rows, ['asset'], 1), liabilities = section(rows, ['liability'], -1), equity = section(rows, ['equity'], -1);
  const earnings = profitLoss(db, { from: null, to: asof }).net_income_cents;
  const totalEquity = equity.total_cents + earnings;
  return { asof, assets, liabilities, equity: { ...equity, current_earnings_cents: earnings, total_cents: totalEquity },
    total_liabilities_and_equity_cents: liabilities.total_cents + totalEquity, balanced: assets.total_cents === liabilities.total_cents + totalEquity };
}

function generalLedger(db, { from, to, accountId }) {
  const accounts = accountTotals(db, {}).filter((a) => !accountId || a.id === accountId);
  const out = [];
  for (const a of accounts) {
    const opening = db.prepare(`SELECT COALESCE(SUM(l.debit_cents - l.credit_cents),0) AS n ${BASE} WHERE l.account_id=? AND (? IS NOT NULL AND e.entry_date < ?)`).get(a.id, from ?? null, from ?? null).n;
    const lines = db.prepare(`SELECT e.id AS entry_id, e.entry_date, e.memo, e.ref, l.debit_cents, l.credit_cents, c.name AS contact_name
      FROM journal_lines l JOIN journal_entries e ON e.id=l.entry_id LEFT JOIN contacts c ON c.id=l.contact_id
      WHERE l.account_id=? AND (? IS NULL OR e.entry_date>=?) AND (? IS NULL OR e.entry_date<=?) ORDER BY e.entry_date, e.id, l.id LIMIT 5000`)
      .all(a.id, from ?? null, from ?? null, to ?? null, to ?? null);
    if (!lines.length && !opening) continue;
    let run = opening;
    for (const l of lines) { run += l.debit_cents - l.credit_cents; l.balance_cents = run; }
    out.push({ account_id: a.id, number: a.number, name: a.name, opening_cents: opening, lines });
  }
  return { from, to, accounts: out };
}

const BUCKETS = ['current', 'd1_30', 'd31_60', 'd61_90', 'd90_plus'];
/** Open-item aging built from the ledger (works for native and imported data): credits are applied oldest-first. */
function aging(db, { kind, asof }) {
  const sub = kind === 'ar' ? 'ar' : 'ap';
  const rows = db.prepare(`SELECT l.contact_id, c.name, c.terms_days, e.entry_date, l.debit_cents, l.credit_cents ${BASE.replace('JOIN accounts a', 'JOIN accounts a')}
    LEFT JOIN contacts c ON c.id=l.contact_id WHERE a.subtype=? AND e.entry_date<=? ORDER BY e.entry_date, e.id, l.id`).all(sub, asof);
  const byContact = new Map();
  for (const r of rows) {
    const k = r.contact_id ?? 0;
    if (!byContact.has(k)) byContact.set(k, { contact_id: r.contact_id, name: r.name || '(no contact)', terms: r.terms_days ?? 30, charges: [], credit: 0 });
    const g = byContact.get(k);
    const charge = kind === 'ar' ? r.debit_cents : r.credit_cents;
    const pay = kind === 'ar' ? r.credit_cents : r.debit_cents;
    if (charge) g.charges.push({ date: r.entry_date, amount: charge });
    g.credit += pay;
  }
  const result = [];
  for (const g of byContact.values()) {
    const b = Object.fromEntries(BUCKETS.map((k) => [k, 0]));
    let credit = g.credit;
    for (const ch of g.charges) {
      const used = Math.min(credit, ch.amount); credit -= used;
      const open = ch.amount - used;
      if (!open) continue;
      const late = daysBetween(addDays(ch.date, g.terms), asof);
      b[late <= 0 ? 'current' : late <= 30 ? 'd1_30' : late <= 60 ? 'd31_60' : late <= 90 ? 'd61_90' : 'd90_plus'] += open;
    }
    if (credit) b.current -= credit; // unapplied credit / prepayment
    const total = BUCKETS.reduce((s, k) => s + b[k], 0);
    if (total) result.push({ contact_id: g.contact_id, name: g.name, ...b, total_cents: total });
  }
  result.sort((x, y) => y.total_cents - x.total_cents);
  const totals = Object.fromEntries([...BUCKETS, 'total_cents'].map((k) => [k, result.reduce((s, r) => s + r[k], 0)]));
  return { kind, asof, rows: result, totals };
}

function salesByCustomer(db, { from, to }) {
  const rows = db.prepare(`SELECT c.id, c.name, SUM(l.credit_cents - l.debit_cents) AS total ${BASE} JOIN contacts c ON c.id=l.contact_id
    WHERE a.type='income' AND e.entry_date>=? AND e.entry_date<=? GROUP BY c.id HAVING total<>0 ORDER BY total DESC`).all(from, to);
  return { from, to, rows: rows.map((r) => ({ contact_id: r.id, name: r.name, total_cents: r.total })), total_cents: rows.reduce((s, r) => s + r.total, 0) };
}

function dashboard(db) {
  const t = today();
  const yearStart = t.slice(0, 4) + '-01-01';
  const sum = (sub) => db.prepare(`SELECT COALESCE(SUM(l.debit_cents-l.credit_cents),0) AS n ${BASE} WHERE a.subtype=?`).get(sub).n;
  const pl = profitLoss(db, { from: yearStart, to: t });
  const last30 = profitLoss(db, { from: addDays(t, -30), to: t });
  return {
    cash_cents: sum('bank'), receivable_cents: sum('ar'), payable_cents: -sum('ap'), credit_card_cents: -sum('credit_card'),
    ytd: { income_cents: pl.income.total_cents, expenses_cents: pl.cogs.total_cents + pl.expenses.total_cents, net_cents: pl.net_income_cents },
    last30: { income_cents: last30.income.total_cents, net_cents: last30.net_income_cents },
    overdue_invoices: db.prepare("SELECT COUNT(*) AS n FROM documents WHERE kind='invoice' AND status='open' AND due_date<?").get(t).n,
    bills_due_soon: db.prepare("SELECT COUNT(*) AS n FROM documents WHERE kind='bill' AND status='open' AND due_date<=?").get(addDays(t, 7)).n,
    last_backup: db.prepare("SELECT value FROM settings WHERE key='last_backup'").get()?.value || '',
    bank_to_review: db.prepare("SELECT COUNT(*) AS n FROM bank_transactions WHERE status='new'").get().n,
  };
}

function run(db, name, q) {
  const asof = vDate(q.asof, 'as-of date', false) || today();
  const to = vDate(q.to, 'to date', false) || today();
  const from = vDate(q.from, 'from date', false) || to.slice(0, 4) + '-01-01';
  if (from > to) throw bad('The "from" date is later than the "to" date, so there is nothing in between. Swap them or fix one.');
  switch (name) {
    case 'trial-balance': return trialBalance(db, { asof });
    case 'profit-loss': return profitLoss(db, { from, to });
    case 'balance-sheet': return balanceSheet(db, { asof });
    case 'general-ledger': return generalLedger(db, { from, to, accountId: q.account_id ? Number(q.account_id) : undefined });
    case 'ar-aging': return aging(db, { kind: 'ar', asof });
    case 'ap-aging': return aging(db, { kind: 'ap', asof });
    case 'sales-by-customer': return salesByCustomer(db, { from, to });
    case 'cash-forecast': return require('./forecast').cashForecast(db, { asof, weeks: Number(q.weeks) || 13 });
    default: throw bad('That report does not exist. Pick a report from the row at the top of the Reports page.');
  }
}

module.exports = { run, dashboard, trialBalance, profitLoss, balanceSheet, aging };
