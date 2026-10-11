'use strict';
/**
 * Books health check: explainable, rule-based review of the books (no AI, nothing leaves the machine).
 * Every finding says what was found, why it matters, and links to the records involved.
 */
const { addDays, today, formatMoney } = require('./util');
const { checkLedgerIntegrity } = require('./ledger');
const { verifyAudit } = require('./audit');

const $ = (c) => (c < 0 ? '-' : '') + '$' + formatMoney(Math.abs(c)).replace(/\B(?=(\d{3})+(?!\d))/g, ',');
const SEVERITY_ORDER = { high: 0, medium: 1, low: 2, info: 3 };
// A passing check is worded as a statement of fact; a failing one is worded as the problem.
const PROBLEM = {
  'ledger-balanced': 'Some journal entries do not balance', 'audit-chain': 'The audit log has been altered', overdrawn: 'A bank account is overdrawn',
  duplicates: 'Possible duplicate invoices or bills', 'old-receivables': 'Invoices are more than 60 days past due', 'overdue-bills': 'Bills are overdue',
  'stale-bank': 'Bank transactions have been waiting more than 2 weeks', 'odd-balances': 'Accounts have a balance on the unexpected side', 'opening-equity': 'Opening Balance Equity still has a balance',
  'inactive-with-balance': 'Inactive accounts still hold money', 'future-dated': 'Entries are dated far in the future', 'unusual-amounts': 'Unusually large invoices or bills',
  'number-gaps': 'Invoice numbers have gaps', 'missing-contact': 'Receivable/payable lines have no customer or vendor', 'customer-credits': 'Customers have unapplied credits',
  backup: 'No recent backup', 'period-lock': 'Old periods are not locked',
};

function booksHealth(db, { asof = today() } = {}) {
  const findings = [];
  const passed = [];
  const check = (id, title, run) => {
    const f = run();
    if (f) findings.push({ id, title: PROBLEM[id] || title, count: f.items?.length ?? 0, ...f, items: (f.items || []).slice(0, 12) });
    else passed.push(title);
  };
  const docLink = (d) => ({ label: `${d.kind === 'invoice' ? 'Invoice' : 'Bill'} ${d.number} - ${d.name || d.contact_name} - ${$(d.total_cents - (d.paid_cents || 0))}`, hash: `#/document/${d.id}` });

  check('ledger-balanced', 'Every journal entry balances', () => {
    const r = checkLedgerIntegrity(db);
    return r.ok ? null : { severity: 'high', detail: `${r.unbalanced.length} entries do not balance and ${r.empty.length} are empty. This should be impossible; restore from a backup and report it.`, items: r.unbalanced.map((u) => ({ label: `Entry #${u.entry_id}`, hash: '#/journal' })) };
  });
  check('audit-chain', 'Audit log is intact', () => {
    const r = verifyAudit(db);
    return r.ok ? null : { severity: 'high', detail: `The tamper-evident audit chain is broken at record ${r.brokenAtId}. Someone may have edited the database file directly.`, items: [] };
  });
  check('overdrawn', 'No bank account is overdrawn', () => {
    const rows = db.prepare(`SELECT a.id, a.name, SUM(l.debit_cents-l.credit_cents) net FROM accounts a JOIN journal_lines l ON l.account_id=a.id JOIN journal_entries e ON e.id=l.entry_id WHERE a.subtype='bank' AND e.entry_date<=? GROUP BY a.id HAVING net<0`).all(asof);
    return rows.length ? { severity: 'high', detail: 'A bank account shows a negative balance. Usually a deposit or transfer is missing, or a payment was entered twice.', items: rows.map((r) => ({ label: `${r.name}: ${$(r.net)}`, hash: '#/accounts' })) } : null;
  });
  check('duplicates', 'No likely duplicate invoices or bills', () => {
    const rows = db.prepare(`SELECT a.id a_id, b.id b_id, a.kind, a.number an, b.number bn, c.name, a.total_cents, a.doc_date ad, b.doc_date bd
      FROM documents a JOIN documents b ON a.kind=b.kind AND a.contact_id=b.contact_id AND a.total_cents=b.total_cents AND a.id<b.id JOIN contacts c ON c.id=a.contact_id
      WHERE a.status<>'void' AND b.status<>'void' AND abs(julianday(a.doc_date)-julianday(b.doc_date))<=7 ORDER BY a.doc_date DESC LIMIT 50`).all();
    return rows.length ? { severity: 'medium', detail: 'Same customer or vendor, same amount, within a week. If one is a mistake, void it so you are not double-counting income or expenses.', items: rows.map((r) => ({ label: `${r.name}: ${r.kind} ${r.an} and ${r.bn}, ${$(r.total_cents)} (${r.ad} / ${r.bd})`, hash: `#/document/${r.a_id}` })) } : null;
  });
  check('old-receivables', 'No invoices more than 60 days past due', () => {
    const rows = db.prepare(`SELECT d.*, c.name FROM documents d JOIN contacts c ON c.id=d.contact_id WHERE d.kind='invoice' AND d.status='open' AND d.due_date < ? ORDER BY d.due_date LIMIT 100`).all(addDays(asof, -60));
    return rows.length ? { severity: 'medium', detail: `${rows.length} invoice(s) totalling ${$(rows.reduce((s, r) => s + r.total_cents - r.paid_cents, 0))} are well past due. Follow up, or consider writing them off.`, items: rows.map(docLink) } : null;
  });
  check('overdue-bills', 'No overdue bills', () => {
    const rows = db.prepare(`SELECT d.*, c.name FROM documents d JOIN contacts c ON c.id=d.contact_id WHERE d.kind='bill' AND d.status='open' AND d.due_date < ? ORDER BY d.due_date LIMIT 100`).all(asof);
    return rows.length ? { severity: 'medium', detail: 'Bills past their due date can mean late fees or strained supplier relationships.', items: rows.map(docLink) } : null;
  });
  check('stale-bank', 'No bank transactions waiting more than 2 weeks', () => {
    const rows = db.prepare(`SELECT t.id, t.tx_date, t.description, t.amount_cents FROM bank_transactions t WHERE t.status='new' AND t.tx_date < ? ORDER BY t.tx_date LIMIT 100`).all(addDays(asof, -14));
    return rows.length ? { severity: 'medium', detail: 'Uncategorised bank activity means your reports are missing real income and expenses.', items: rows.map((r) => ({ label: `${r.tx_date} ${r.description} ${$(r.amount_cents)}`, hash: '#/banking' })) } : null;
  });
  check('odd-balances', 'No account has a balance on the unexpected side', () => {
    const rows = db.prepare(`SELECT a.id, a.number, a.name, a.type, SUM(l.debit_cents-l.credit_cents) net FROM accounts a JOIN journal_lines l ON l.account_id=a.id JOIN journal_entries e ON e.id=l.entry_id WHERE e.entry_date<=? GROUP BY a.id`).all(asof);
    const debitNormal = new Set(['asset', 'cogs', 'expense', 'other_expense']);
    const odd = rows.filter((r) => r.type !== 'equity' && !/accumulated|depreciation|allowance|contra|draw|distribution|discount|refund|return/i.test(r.name) && (debitNormal.has(r.type) ? r.net < -100 : r.net > 100));
    return odd.length ? { severity: 'medium', detail: 'For example a liability with a debit balance, or an expense that is negative. This often points to a payment posted to the wrong account.', items: odd.map((r) => ({ label: `${r.number ? r.number + ' ' : ''}${r.name}: ${$(Math.abs(r.net))} ${debitNormal.has(r.type) ? 'credit' : 'debit'} balance`, hash: '#/accounts' })) } : null;
  });
  check('opening-equity', 'Opening Balance Equity is cleared', () => {
    const r = db.prepare(`SELECT SUM(l.debit_cents-l.credit_cents) net FROM journal_lines l JOIN accounts a ON a.id=l.account_id WHERE a.name='Opening Balance Equity'`).get();
    return r.net ? { severity: 'medium', detail: 'This holding account should be zero once opening balances are settled. Move the balance to owner\'s equity or retained earnings with a journal entry.', items: [{ label: `Opening Balance Equity: ${$(Math.abs(r.net))}`, hash: '#/accounts' }] } : null;
  });
  check('inactive-with-balance', 'No inactive account holds a balance', () => {
    const rows = db.prepare(`SELECT a.name, SUM(l.debit_cents-l.credit_cents) net FROM accounts a JOIN journal_lines l ON l.account_id=a.id WHERE a.active=0 GROUP BY a.id HAVING net<>0`).all();
    return rows.length ? { severity: 'medium', detail: 'Money is parked in an account you have switched off, so it is easy to forget.', items: rows.map((r) => ({ label: `${r.name}: ${$(Math.abs(r.net))}`, hash: '#/accounts' })) } : null;
  });
  check('future-dated', 'Nothing is dated far in the future', () => {
    const rows = db.prepare('SELECT id, entry_date, memo FROM journal_entries WHERE entry_date > ? ORDER BY entry_date LIMIT 50').all(addDays(asof, 30));
    return rows.length ? { severity: 'medium', detail: 'A typo in the year is the usual cause. These entries will not show in current reports.', items: rows.map((r) => ({ label: `#${r.id} ${r.entry_date} ${r.memo}`, hash: '#/journal' })) } : null;
  });
  check('unusual-amounts', 'No unusually large invoices or bills', () => {
    const rows = db.prepare(`SELECT d.*, c.name FROM documents d JOIN contacts c ON c.id=d.contact_id
      JOIN (SELECT contact_id, kind, AVG(total_cents) avg_total, COUNT(*) n FROM documents WHERE status<>'void' GROUP BY contact_id, kind HAVING n>=8) s ON s.contact_id=d.contact_id AND s.kind=d.kind
      WHERE d.status<>'void' AND d.doc_date >= ? AND d.total_cents > 3*s.avg_total AND d.total_cents > 100000 ORDER BY d.total_cents DESC LIMIT 20`).all(addDays(asof, -365));
    return rows.length ? { severity: 'low', detail: 'More than three times that customer\'s or vendor\'s average over the last year. Usually legitimate (a big project), but worth a glance for typos like an extra zero.', items: rows.map((d) => ({ ...docLink(d), label: `${d.kind} ${d.number} - ${d.name}: ${$(d.total_cents)}` })) } : null;
  });
  check('number-gaps', 'Invoice numbers have no gaps', () => {
    const nums = db.prepare("SELECT number FROM documents WHERE kind='invoice'").all().map((r) => /^INV-(\d+)$/.exec(r.number)).filter(Boolean).map((m) => Number(m[1])).sort((a, b) => a - b);
    const missing = []; for (let i = 1; i < nums.length; i++) for (let n = nums[i - 1] + 1; n < nums[i] && missing.length < 200; n++) missing.push(n);
    return missing.length ? { severity: 'low', detail: 'Auditors and tax authorities expect invoice numbers to be unbroken. Voided invoices keep their number, so gaps usually mean a number was typed manually or a record was lost.', items: missing.slice(0, 12).map((n) => ({ label: `INV-${String(n).padStart(4, '0')} is missing`, hash: '#/invoices' })) } : null;
  });
  check('missing-contact', 'Receivable and payable lines all name a customer or vendor', () => {
    const r = db.prepare(`SELECT COUNT(*) n FROM journal_lines l JOIN accounts a ON a.id=l.account_id WHERE a.subtype IN ('ar','ap') AND l.contact_id IS NULL`).get();
    return r.n ? { severity: 'low', detail: `${r.n} receivable/payable line(s) have no customer or vendor, so aging reports cannot attribute them to anyone.`, items: [{ label: 'Review in the journal', hash: '#/journal' }] } : null;
  });
  check('customer-credits', 'No customer has an unapplied credit', () => {
    const rows = db.prepare(`SELECT c.id, c.name, SUM(l.debit_cents-l.credit_cents) net FROM journal_lines l JOIN accounts a ON a.id=l.account_id JOIN contacts c ON c.id=l.contact_id WHERE a.subtype='ar' GROUP BY c.id HAVING net<-100`).all();
    return rows.length ? { severity: 'info', detail: 'These customers have paid more than they were billed (an overpayment or an unapplied deposit). Refund it or apply it to the next invoice.', items: rows.map((r) => ({ label: `${r.name}: ${$(-r.net)} credit`, hash: '#/customers' })) } : null;
  });
  check('backup', 'A backup was made in the last 30 days', () => {
    const last = db.prepare("SELECT value FROM settings WHERE key='last_backup'").get()?.value;
    const age = last ? Math.floor((Date.parse(asof + 'T00:00:00Z') - Date.parse(last)) / 86400000) : null;
    return age === null || age > 30 ? { severity: 'medium', detail: age === null ? 'No encrypted backup has ever been made from this app. A damaged disk would lose everything.' : `The last backup was ${age} days ago.`, items: [{ label: 'Make a backup now', hash: '#/settings/data' }] } : null;
  });
  check('period-lock', 'Old periods are locked', () => {
    const closing = db.prepare("SELECT value FROM settings WHERE key='closing_date'").get()?.value;
    const first = db.prepare('SELECT MIN(entry_date) d FROM journal_entries').get().d;
    return !closing && first && first < addDays(asof, -400) ? { severity: 'info', detail: 'Setting a "books closed through" date stops accidental changes to years you have already filed taxes for.', items: [{ label: 'Set the closing date', hash: '#/settings/company' }] } : null;
  });

  findings.sort((a, b) => SEVERITY_ORDER[a.severity] - SEVERITY_ORDER[b.severity]);
  const counts = { high: 0, medium: 0, low: 0, info: 0 };
  for (const f of findings) counts[f.severity]++;
  const score = Math.max(0, 100 - counts.high * 25 - counts.medium * 8 - counts.low * 3);
  return { asof, score, counts, findings, passed };
}

module.exports = { booksHealth };
