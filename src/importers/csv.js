'use strict';
const crypto = require('node:crypto');
const { bad } = require('../util');
const { skippedNote, newModel, mapAccountType, termsToDays, parseDate, money, phone, norm, checkLimit } = require('./model');

/** RFC 4180 CSV parser (quotes, escaped quotes, CRLF, embedded newlines). Auto-detects comma/tab/semicolon. */
function parseCsv(text, limits) {
  if (text.charCodeAt(0) === 0xfeff) text = text.slice(1);
  const firstLine = text.split(/\r\n|\n|\r/, 1)[0];
  const counts = { ',': 0, '\t': 0, ';': 0 };
  let q = false;
  for (const ch of firstLine) { if (ch === '"') q = !q; else if (!q && ch in counts) counts[ch]++; }
  const delim = Object.entries(counts).sort((a, b) => b[1] - a[1])[0][0];
  const rows = [];
  let row = [], field = '', inQ = false, i = 0;
  const n = text.length;
  while (i < n) {
    const ch = text[i];
    if (inQ) {
      if (ch === '"') { if (text[i + 1] === '"') { field += '"'; i++; } else inQ = false; } else field += ch;
    } else if (ch === '"' && field === '') inQ = true;
    else if (ch === delim) { row.push(field); field = ''; }
    else if (ch === '\n' || ch === '\r') {
      if (ch === '\r' && text[i + 1] === '\n') i++;
      row.push(field); field = '';
      if (row.length > 1 || row[0] !== '') rows.push(row);
      row = [];
      checkLimit(rows.length, limits);
    } else field += ch;
    i++;
  }
  if (inQ) throw bad('This CSV file has a quotation mark that is opened but never closed, so Ledjoro cannot tell where one value ends. The file was probably cut short or edited by hand. Export it again.');
  if (field !== '' || row.length) { row.push(field); rows.push(row); }
  return rows;
}

/** Turn rows into objects keyed by normalised header; first row containing a recognised header wins. */
const hnorm = (c) => norm(String(c).replace(/#/g, ' number ')); // "Account #" -> accountnumber (distinct from "Account")
function table(rows, required) {
  const idx = rows.findIndex((r) => required.every((alts) => r.some((c) => alts.includes(hnorm(c)))));
  if (idx < 0) return null;
  const heads = rows[idx].map(hnorm);
  return rows.slice(idx + 1).map((r) => ({ get: (...alts) => { for (const a of alts) { const i = heads.indexOf(a); if (i >= 0 && r[i] !== undefined) return r[i].trim(); } return ''; }, raw: r }));
}

function importAccountsCsv(text, limits) {
  const m = newModel('csv-accounts');
  const t = table(parseCsv(text, limits), [['account', 'accountname', 'fullname', 'name'], ['type', 'accounttype']]);
  if (!t) throw bad('This does not look like a chart-of-accounts export: Ledjoro looked for columns called "Account" and "Type" and did not find both. Check you picked the right file, or choose a different format in the Format list.');
  const skipped = [];
  for (const r of t) {
    const name = r.get('fullname', 'accountname', 'account', 'name');
    if (!name || /^total\b/i.test(name)) continue;
    const subtype = mapAccountType(r.get('type', 'accounttype'));
    if (!subtype) { skipped.push({ name, type: r.get('type', 'accounttype') }); continue; }
    m.accounts.push({ number: r.get('accountnumber', 'number', 'acctnumber', 'accountno', 'no'), name, subtype, description: r.get('description', 'desc') });
  }
  const sn = skippedNote(skipped); if (sn) m.notes.push(sn);
  return m;
}

function importContactsCsv(text, kind, limits) {
  const m = newModel(`csv-${kind}s`);
  const t = table(parseCsv(text, limits), [kind === 'customer' ? ['customer', 'customername', 'displayname', 'fullname', 'name', 'company'] : ['vendor', 'vendorname', 'displayname', 'fullname', 'name', 'company']]);
  if (!t) throw bad(`This does not look like a ${kind} list: Ledjoro could not find a column holding the ${kind}'s name. Check you picked the right file, or choose a different format in the Format list.`);
  for (const r of t) {
    const name = r.get(kind, `${kind}name`, 'displayname', 'fullname', 'name', 'company');
    if (!name || /^total\b/i.test(name)) continue;
    const addr = r.get('billingaddress', 'address', 'streetaddress') || [r.get('street', 'billstreet', 'billaddr1'), r.get('city', 'billcity'), r.get('state', 'billstate'), r.get('zip', 'zipcode', 'postalcode', 'billzip')].filter(Boolean).join(', ');
    m.contacts.push({ kind, name, email: r.get('email', `${kind}email`, 'emailaddress'), phone: phone(r.get('phone', 'phonenumbers', 'phonenumber', 'mobile')), address: addr, terms_days: termsToDays(r.get('terms')), is_1099: /^(y|yes|true|1)$/i.test(r.get('track1099', 'eligiblefor1099', 'is1099')) });
  }
  return m;
}

function importItemsCsv(text, limits) {
  const m = newModel('csv-items');
  const t = table(parseCsv(text, limits), [['productservice', 'productservicename', 'name', 'item', 'itemname']]);
  if (!t) throw bad('This does not look like a products and services list: there is no "Product/Service" (or "Name" / "Item") column. Check you picked the right file, or choose a different format in the Format list.');
  for (const r of t) {
    const name = r.get('productservice', 'productservicename', 'name', 'item', 'itemname');
    if (!name || /^total\b/i.test(name)) continue;
    const ty = norm(r.get('type', 'itemtype'));
    m.items.push({ name, kind: /inventory$|^inventory|product/.test(ty) && !/non/.test(ty) ? 'product' : /non/.test(ty) ? 'nonstock' : 'service', description: r.get('salesdescription', 'description', 'desc'), unit_price_cents: Math.max(0, money(r.get('salesprice', 'rate', 'price', 'unitprice')) ?? 0), income_account: r.get('incomeaccount'), expense_account: r.get('expenseaccount') });
  }
  return m;
}

/** Trial-balance CSV -> one opening-balance journal entry (the most common migration path from any bookkeeping product). */
function importTrialBalanceCsv(text, asof, limits) {
  const m = newModel('csv-trialbalance');
  if (!asof) throw bad('A trial balance needs the date it is "as of". Fill in "Opening-balance date" above, then preview again.');
  const t = table(parseCsv(text, limits), [['account', 'accountname', 'name'], ['debit', 'debits'], ['credit', 'credits']]);
  if (!t) throw bad('This does not look like a trial balance: Ledjoro looked for columns called "Account", "Debit" and "Credit" and did not find all three. Check you picked the right file, or choose a different format in the Format list.');
  const lines = [];
  for (const r of t) {
    const name = r.get('account', 'accountname', 'name');
    if (!name || /^total\b/i.test(name)) continue;
    const d = money(r.get('debit', 'debits')) ?? 0, c = money(r.get('credit', 'credits')) ?? 0;
    if (!d && !c) continue;
    const net = d - c;
    lines.push({ account: name, debit_cents: net > 0 ? net : 0, credit_cents: net < 0 ? -net : 0 });
  }
  const dr = lines.reduce((s, l) => s + l.debit_cents, 0), cr = lines.reduce((s, l) => s + l.credit_cents, 0);
  if (dr !== cr) { const diff = dr - cr; m.warnings.push(`Trial balance is out by ${(Math.abs(diff) / 100).toFixed(2)}; the difference was posted to Opening Balance Equity`); lines.push({ account: 'Opening Balance Equity', debit_cents: diff < 0 ? -diff : 0, credit_cents: diff > 0 ? diff : 0 }); }
  if (lines.length >= 2) m.journal_entries.push({ date: asof, ref: 'OPENING', memo: 'Opening balances (imported trial balance)', lines });
  return m;
}

/**
 * Account-register CSV (QuickBooks Desktop "Register" export): Date, Number, Payee, Account, Memo, Payment, C, Deposit, Balance.
 * The file is the history of ONE account; the "Account" column is the other side of each line. Every row becomes a balanced entry
 * between the register's account (named by the user) and that other account. Accounts that do not exist yet are created, with a
 * type guessed from how they are used (listed in a warning so they can be corrected afterwards). The file's own running balance
 * is re-computed as a cross-check.
 */
function importRegisterCsv(text, register, opts, limits) {
  const m = newModel('csv-register');
  const regName = String(register ?? '').trim();
  if (!regName) throw bad('Enter the name of the account this register belongs to (for example "Checking"). Registers do not say which account they are from.');
  const t = table(parseCsv(text, limits), [['date'], ['payee', 'account'], ['payment', 'deposit']]);
  if (!t) throw bad('This does not look like an account register: Ledjoro looked for columns called "Date", "Payee" or "Account", and "Payment" or "Deposit", and did not find them. Check you picked the right file, or use "Match columns myself".');
  const order = opts?.dateOrder === 'dmy' ? 'dmy' : 'mdy';
  const rows = [], usage = new Map();
  let line = 1, badDates = 0, runCents = 0, balMismatch = 0, firstMismatch = '';
  for (const r of t) {
    line++;
    const dateTxt = r.get('date');
    if (!dateTxt && !r.get('payment') && !r.get('deposit')) continue;
    if (/^total\b|^beginning balance/i.test(dateTxt)) continue;
    const date = parseDate(dateTxt, order);
    const pay = money(r.get('payment')) ?? 0, dep = money(r.get('deposit')) ?? 0;
    if (!date) { badDates++; if (badDates <= 5) m.errors.push(`Row ${line}: "${dateTxt}" is not a date (month/day/year is assumed; change "Date order" if yours is day/month/year)`); continue; }
    const amount = dep - pay;
    if (!amount) continue;
    const other = r.get('account') || 'Uncategorized';
    const u = usage.get(other) ?? { pay: 0, dep: 0 }; if (pay) u.pay++; if (dep) u.dep++; usage.set(other, u);
    runCents += amount;
    const bal = money(r.get('balance'));
    if (bal !== null && bal !== undefined && bal !== runCents) { balMismatch++; if (!firstMismatch) firstMismatch = `row ${line} (${dateTxt})`; }
    rows.push({ date, ref: r.get('number'), payee: r.get('payee'), other, memo: r.get('memo'), amount, bal: r.get('balance') });
  }
  const nameFor = (o) => (/^opening bal/i.test(o) ? 'Opening Balance Equity' : o === '-split-' ? 'Uncategorized Split Transactions' : o);
  const made = [];
  m.accounts.push({ number: '', name: regName, subtype: 'bank', description: 'Created from a register import' });
  for (const [o, u] of usage) {
    const name = nameFor(o);
    if (name === regName || m.accounts.some((a) => a.name === name)) continue;
    // Best guess from the name and from how the account is used (QuickBooks registers do not carry account types).
    const subtype = /payable/i.test(o) ? 'ap' : /receivable/i.test(o) ? 'ar' : name === 'Opening Balance Equity' ? 'equity'
      : name === 'Uncategorized Split Transactions' ? 'other_current_asset' : /\b(visa|mastercard|master card|amex|american express|discover|credit card)\b/i.test(o) ? 'credit_card'
      : u.dep > u.pay ? 'income' : 'expense';
    m.accounts.push({ number: '', name, subtype, description: 'Created from a register import' });
    if (name !== 'Opening Balance Equity') made.push(`${name} (${subtype.replace(/_/g, ' ')})`);
  }
  const seen = new Map();
  const makeDocs = opts?.documents !== false;
  let nBills = 0, nInvoices = 0, needExp = false, needInc = false;
  for (const r of rows) {
    const other = nameFor(r.other);
    const kind = m.accounts.find((a) => a.name === other)?.subtype;
    const memo = [r.payee, r.memo].filter((x) => x && x.toLowerCase() !== 'deposit').join(' - ') || 'Register entry';
    const key = [r.date, r.ref, r.payee, r.other, r.amount, r.bal].join('|');
    const idx = seen.get(key) ?? 0; seen.set(key, idx + 1);
    const cents = Math.abs(r.amount), dep = r.amount > 0;
    const contact = (kind === 'ap' || kind === 'ar') && r.payee ? r.payee : '';
    const extId = crypto.createHash('sha256').update(`reg|${regName}|${key}|${idx}`).digest('hex');
    // every register row is also a line of the account's bank history (already categorized: its entry exists), so it shows under Bank transactions > Categorized
    m.bank_history.push({ external_id: extId, account: regName, date: r.date, amount_cents: r.amount, description: [r.payee, r.memo].filter(Boolean).join(' - ') || 'Register entry', fitid: 'reg-' + extId.slice(0, 32) });
    // A payment to Accounts Payable with a payee is a paid bill; a deposit into an income / receivable account with a payee is a paid invoice.
    // (A register has no bill or invoice detail, so these are one-line documents dated the day the money moved.)
    const asBill = makeDocs && r.payee && !dep && kind === 'ap';
    // bank descriptions ("deposit from ...", "mobile dep", "venmo cashout", "transfer ...") are not customers
    const notACustomer = /\b(deposit|dep|transfer|xfr|mobile|cash\s?out|venmo|paypal|zelle|refund|interest|bank|bkofamerica|adjust\w*|income)\b|\d{4,}/i.test(r.payee);
    const asInvoice = makeDocs && r.payee && dep && !notACustomer && (kind === 'ar' || (kind === 'income' && /income|sales|revenue/i.test(r.other)));
    if (asBill || asInvoice) {
      const line = { description: [r.payee, r.memo].filter(Boolean).join(' - '), amount_cents: cents, account: asBill ? 'Uncategorized Expense' : kind === 'ar' ? 'Uncategorized Income' : other };
      if (asBill) { nBills++; needExp = true; } else { nInvoices++; if (kind === 'ar') needInc = true; }
      m.documents.push({ kind: asBill ? 'bill' : 'invoice', contact: r.payee, date: r.date, memo, line, pay: { account: regName, ref: r.ref }, external_id: extId });
      continue;
    }
    m.journal_entries.push({
      date: r.date, ref: r.ref, memo, external_id: extId,
      lines: [
        { account: regName, debit_cents: dep ? cents : 0, credit_cents: dep ? 0 : cents, memo: r.memo },
        { account: other, debit_cents: dep ? 0 : cents, credit_cents: dep ? cents : 0, memo: r.memo, contact },
      ],
    });
  }
  if (needExp) m.accounts.push({ number: '', name: 'Uncategorized Expense', subtype: 'expense', description: 'Created from a register import: bills made from payments, whose real expense category is not in a register' });
  if (needInc) m.accounts.push({ number: '', name: 'Uncategorized Income', subtype: 'income', description: 'Created from a register import' });
  if (nBills) m.notes.push(`${nBills} payment(s) to Accounts Payable became paid bills (vendor = payee). A register does not say what each bill was for, so they are booked to "Uncategorized Expense"; re-categorise them there, or import your bills from a transaction export instead.`);
  if (nInvoices) m.notes.push(`${nInvoices} deposit(s) from named payers became paid invoices (customer = payer), booked to the income account shown in the register.`);
  const splits = usage.get('-split-');
  if (splits) m.notes.push(`${splits.pay + splits.dep} row(s) are QuickBooks "-split-" transactions: the register export shows only the total, not the categories it was split across. They are posted to "Uncategorized Split Transactions"; re-categorise them there, or export a transaction report if you need the detail.`);
  if (badDates > 5) m.errors.push(`${badDates - 5} more row(s) have dates that could not be read`);
  if (made.length) m.notes.push(`These accounts did not exist and will be created with a guessed type; check them in the chart of accounts afterwards: ${made.slice(0, 25).join('; ')}${made.length > 25 ? `; and ${made.length - 25} more` : ''}.`);
  if (balMismatch) m.warnings.push(`The register's own Balance column disagrees with the totals of the rows in ${balMismatch} place(s), first at ${firstMismatch}. Some rows may be missing from the export (for example transactions that were later deleted or filtered).`);
  else if (rows.length) m.notes.push(`Cross-check passed: the register's running balance matches the imported rows all the way down (ending at ${(runCents / 100).toFixed(2)}).`);
  return m;
}

// ---------- "match the columns yourself" ----------
const MAP_FIELDS = ['date', 'number', 'payee', 'account', 'memo', 'out', 'in', 'amount', 'balance'];
const MAP_GUESS = {
  date: /^(date|posted|postdate|transactiondate|transdate|postingdate)/,
  number: /^(number|num|no|check|checknumber|checkno|chk|ref|reference|refnumber|id)$/,
  payee: /^(payee|description|desc|name|merchant|details|narrative)/,
  account: /^(account|category|accountname|class|split)$/,
  memo: /^(memo|note|notes|remarks)/,
  out: /^(payment|payments|withdrawal|withdrawals|debit|debits|paidout|moneyout|charge|charges|spent)/,
  in: /^(deposit|deposits|credit|credits|paidin|moneyin|received)/,
  amount: /^(amount|amt|value|transactionamount)$/,
  balance: /^(balance|runningbalance|running|runbal|bal)/,
};

/** Look at a CSV and describe its columns, so a person can say what each one means. */
function sniffCsv(text, limits) {
  if (typeof text !== 'string' || !text.trim()) throw bad('The file is empty (there is no text in it), so there is nothing to import. Check you chose the right file and that it finished exporting or downloading.');
  const rows = parseCsv(text, limits);
  if (rows.length < 1) throw bad('The file has no rows, so there is nothing to import. Check you chose the right file.');
  const width = Math.min(60, Math.max(...rows.slice(0, 200).map((r) => r.length)));
  const first = rows[0].map((c) => c.trim());
  // a header row is one whose cells are mostly words, not dates or amounts
  const wordy = first.filter((c) => c && !parseDate(c) && money(c) === null).length;
  const hasHeader = wordy >= Math.max(1, Math.ceil(first.filter(Boolean).length * 0.6));
  const body = rows.slice(hasHeader ? 1 : 0);
  const columns = [];
  for (let i = 0; i < width; i++) {
    const samples = [];
    for (const r of body) { const v = (r[i] ?? '').trim(); if (v) samples.push(v.slice(0, 40)); if (samples.length === 3) break; }
    columns.push({ index: i, header: hasHeader ? (first[i] ?? '') : '', samples });
  }
  const suggested = {};
  const taken = new Set();
  for (const f of MAP_FIELDS) {
    const hit = columns.find((c) => !taken.has(c.index) && c.header && MAP_GUESS[f].test(norm(c.header)));
    if (hit) { suggested[f] = hit.index; taken.add(hit.index); }
  }
  if (suggested.in === undefined && suggested.out !== undefined) { // a card statement: "Charge" is money out, so a separate "Payment" column is money in
    const pay = columns.find((c) => !taken.has(c.index) && /^(payments?|paymentsandcredits|credits?)$/.test(norm(c.header)));
    if (pay) { suggested.in = pay.index; taken.add(pay.index); }
  }
  if (suggested.date === undefined) { // no header words: pick the first column whose samples all look like dates
    const d = columns.find((c) => c.samples.length && c.samples.every((s) => parseDate(s)));
    if (d) suggested.date = d.index;
  }
  return { columns, hasHeader, rowCount: body.length, suggested, preview: rows.slice(0, 8).map((r) => r.slice(0, width)) };
}

const csvCell = (s) => `"${String(s ?? '').replace(/"/g, '""')}"`;

/**
 * CSV with the columns chosen by the user: {date, number, payee, account, memo, out, in, amount, balance} = column index.
 * With an Account column it is treated as an account register (needs the register's account name, like csv-register);
 * without one it is a bank statement: every line waits under Banking > To review for the bank account chosen at import.
 */
function importMappedCsv(text, opts, limits) {
  const mp = opts?.mapping ?? {};
  const idx = (k) => (Number.isInteger(mp[k]) && mp[k] >= 0 ? mp[k] : -1);
  if (idx('date') < 0) throw bad('Choose which column holds the date: open that column\'s list and pick "Date".');
  if (idx('out') < 0 && idx('in') < 0 && idx('amount') < 0) throw bad('Choose which column holds the money: set one column to "Money out" and/or one to "Money in", or a single column to "Amount (+ or -)".');
  const order = opts?.dateOrder === 'dmy' ? 'dmy' : 'mdy';
  const headerRows = Number.isInteger(opts?.headerRows) ? Math.max(0, Math.min(20, opts.headerRows)) : 1;
  const rows = parseCsv(text, limits).slice(headerRows);
  const cell = (r, k) => (idx(k) >= 0 ? String(r[idx(k)] ?? '').trim() : '');
  const lines = [];
  let n = headerRows;
  for (const r of rows) {
    n++;
    if (!r.some((c) => String(c).trim())) continue;
    let out = 0, inn = 0;
    if (idx('amount') >= 0) { let v = money(cell(r, 'amount')) ?? 0; if (opts?.invert) v = -v; if (v > 0) inn = v; else out = -v; }
    else { out = Math.abs(money(cell(r, 'out')) ?? 0); inn = Math.abs(money(cell(r, 'in')) ?? 0); if (opts?.invert) [out, inn] = [inn, out]; }
    lines.push({ n, date: cell(r, 'date'), number: cell(r, 'number'), payee: cell(r, 'payee'), account: cell(r, 'account'), memo: cell(r, 'memo'), out, inn, balance: cell(r, 'balance') });
  }
  const fmt = (c) => (c ? (c / 100).toFixed(2) : '');
  if (idx('account') >= 0) {                   // an account register: rebuild it in the canonical layout and reuse that importer
    const csvText = ['Date,Number,Payee,Account,Memo,Payment,C,Deposit,Balance', ...lines.map((l) => [l.date, l.number, l.payee, l.account, l.memo, fmt(l.out), '', fmt(l.inn), l.balance].map(csvCell).join(','))].join('\r\n');
    const m = importRegisterCsv(csvText, opts?.register, opts, limits);
    m.source.format = 'csv-mapped';
    return m;
  }
  const m = newModel('csv-mapped');
  const seen = new Map();
  let bad1 = 0;
  for (const l of lines) {
    const date = parseDate(l.date, order);
    if (!date) { bad1++; if (bad1 <= 5) m.errors.push(`Row ${l.n}: "${l.date}" is not a date (month/day/year is assumed; change "Date order" if yours is day/month/year)`); continue; }
    const cents = l.inn - l.out;
    if (!cents) continue;
    const description = [l.payee, l.memo].filter(Boolean).join(' - ') || '(no description)';
    const key = [date, cents, description, l.balance, l.number].join('|');
    const k = seen.get(key) ?? 0; seen.set(key, k + 1);
    m.bank_transactions.push({ date, amount_cents: cents, description, fitid: crypto.createHash('sha256').update(`map|${key}|${k}`).digest('hex').slice(0, 40) });
  }
  if (bad1 > 5) m.errors.push(`${bad1 - 5} more row(s) have dates that could not be read`);
  if (m.bank_transactions.length) m.notes.push(`${m.bank_transactions.length} line(s) will wait under Banking > To review for the bank account you choose below. Map an "Account" column instead if the file already says where each line belongs.`);
  return m;
}

module.exports = { sniffCsv, importMappedCsv, importRegisterCsv, parseCsv, importAccountsCsv, importContactsCsv, importItemsCsv, importTrialBalanceCsv, parseDate };
