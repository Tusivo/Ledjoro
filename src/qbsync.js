'use strict';
/**
 * What Ledjoro asks QuickBooks Desktop for (through the QuickBooks Web Connector) and how the answers become Ledjoro records.
 *
 * Everything is READ-ONLY: only "query" requests are ever sent, nothing is written to QuickBooks.
 * The answers are collected into the same in-memory model the file importers produce, shown to the user as a preview,
 * and only imported when the user confirms (and the import can be undone like any other).
 *
 * Transactions arrive as double-entry journal entries: QuickBooks' own debits and credits are rebuilt from each transaction's
 * accounts, so the books balance exactly as they did in QuickBooks. Every entry is tagged with QuickBooks' transaction id,
 * so a later sync never imports the same transaction twice.
 */
const { newModel, money, parseDate } = require('./importers/model');
const { importQbxml, parseXml, find, val } = require('./importers/qbxml');

const QBXML_VERSION = '13.0';
const PAGE = 100;   // records per request; QuickBooks hands out the rest through an "iterator"

// ---------- the questions ----------
// Each step: what to ask for, whether it can be limited to "changed since the last sync", and whether line items are wanted.
const LISTS = [
  // QuickBooks does not accept the iterator attribute on account queries (checked against QuickBooks itself), so accounts are asked for in one go
  { id: 'accounts', label: 'chart of accounts', rq: 'AccountQueryRq', rs: 'AccountQueryRs', plain: true },
  { id: 'customers', label: 'customers', rq: 'CustomerQueryRq', rs: 'CustomerQueryRs' },
  { id: 'vendors', label: 'vendors', rq: 'VendorQueryRq', rs: 'VendorQueryRs' },
  { id: 'items', label: 'products and services', rq: 'ItemQueryRq', rs: 'ItemQueryRs' },
];
const TXNS = [
  { id: 'invoices', label: 'invoices', rq: 'InvoiceQueryRq', rs: 'InvoiceQueryRs', ret: 'InvoiceRet', lines: true },
  { id: 'bills', label: 'bills', rq: 'BillQueryRq', rs: 'BillQueryRs', ret: 'BillRet', lines: true },
  { id: 'payments', label: 'customer payments', rq: 'ReceivePaymentQueryRq', rs: 'ReceivePaymentQueryRs', ret: 'ReceivePaymentRet' },
  { id: 'billpayments', label: 'bill payments (checks)', rq: 'BillPaymentCheckQueryRq', rs: 'BillPaymentCheckQueryRs', ret: 'BillPaymentCheckRet' },
  { id: 'billpaymentscc', label: 'bill payments (credit card)', rq: 'BillPaymentCreditCardQueryRq', rs: 'BillPaymentCreditCardQueryRs', ret: 'BillPaymentCreditCardRet' },
  { id: 'deposits', label: 'deposits', rq: 'DepositQueryRq', rs: 'DepositQueryRs', ret: 'DepositRet', lines: true },
  { id: 'checks', label: 'checks', rq: 'CheckQueryRq', rs: 'CheckQueryRs', ret: 'CheckRet', lines: true },
  { id: 'cardcharges', label: 'credit card charges', rq: 'CreditCardChargeQueryRq', rs: 'CreditCardChargeQueryRs', ret: 'CreditCardChargeRet', lines: true },
  { id: 'cardcredits', label: 'credit card credits', rq: 'CreditCardCreditQueryRq', rs: 'CreditCardCreditQueryRs', ret: 'CreditCardCreditRet', lines: true },
  { id: 'salesreceipts', label: 'sales receipts', rq: 'SalesReceiptQueryRq', rs: 'SalesReceiptQueryRs', ret: 'SalesReceiptRet', lines: true },
  { id: 'creditmemos', label: 'credit memos', rq: 'CreditMemoQueryRq', rs: 'CreditMemoQueryRs', ret: 'CreditMemoRet', lines: true },
  { id: 'vendorcredits', label: 'vendor credits', rq: 'VendorCreditQueryRq', rs: 'VendorCreditQueryRs', ret: 'VendorCreditRet', lines: true },
  { id: 'journal', label: 'journal entries', rq: 'JournalEntryQueryRq', rs: 'JournalEntryQueryRs', ret: 'JournalEntryRet', lines: true },
  { id: 'transfers', label: 'transfers', rq: 'TransferQueryRq', rs: 'TransferQueryRs', ret: 'TransferRet' },
];
const STEPS = [...LISTS.map((s) => ({ ...s, kind: 'list' })), ...TXNS.map((s) => ({ ...s, kind: 'txn' }))];

const esc = (s) => String(s).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
const stamp = (ms) => new Date(ms).toISOString().replace(/\.\d+Z$/, '');   // 2026-01-31T12:00:00

/** The qbXML for one request. `iteratorId` set = "give me the next page of the same question". */
function buildRequest(step, n, { since = null, iteratorId = null, version = QBXML_VERSION } = {}) {
  if (step.plain) return `<?xml version="1.0"?><?qbxml version="${version}"?><QBXML><QBXMLMsgsRq onError="stopOnError"><${step.rq} requestID="${n}"><ActiveStatus>All</ActiveStatus></${step.rq}></QBXMLMsgsRq></QBXML>`;
  const attrs = iteratorId ? ` requestID="${n}" iterator="Continue" iteratorID="${esc(iteratorId)}"` : ` requestID="${n}" iterator="Start"`;
  let body = `<MaxReturned>${PAGE}</MaxReturned>`;
  if (!iteratorId) {
    if (step.kind === 'list') body += '<ActiveStatus>All</ActiveStatus>';
    else {
      if (since) body += `<ModifiedDateRangeFilter><FromModifiedDate>${stamp(since)}</FromModifiedDate></ModifiedDateRangeFilter>`;
      if (step.lines) body += '<IncludeLineItems>true</IncludeLineItems>';
    }
  } else if (step.lines) body += '<IncludeLineItems>true</IncludeLineItems>';   // QuickBooks remembers the filters but not this: without it, later pages arrive without their lines
  // written exactly the way the Web Connector writes its own requests: no encoding in the declaration (QuickBooks is handed text, not bytes) and no line breaks between the parts
  return `<?xml version="1.0"?><?qbxml version="${version}"?><QBXML><QBXMLMsgsRq onError="stopOnError"><${step.rq}${attrs}>${body}</${step.rq}></QBXMLMsgsRq></QBXML>`;
}

// ---------- reading the answers ----------
/** @returns {{ok:boolean, none:boolean, code:string, message:string, severity:string, more:number, iteratorId:string, node:object|null}} */
function readResponse(step, xml) {
  let root;
  try { root = parseXml(xml); } catch (e) { return { ok: false, none: false, code: 'xml', message: 'QuickBooks sent an answer Ledjoro could not read as XML.', severity: 'Error', more: 0, iteratorId: '', node: null }; }
  const rs = find(root, step.rs)[0];
  if (!rs) return { ok: false, none: false, code: 'missing', message: 'QuickBooks sent an answer that was not the one Ledjoro asked for.', severity: 'Error', more: 0, iteratorId: '', node: null };
  const a = rs.attrs || {}, code = String(a.statusCode ?? '');
  return {
    ok: code === '0' || code === '1' || code === '', none: code === '1', code, message: a.statusMessage || '', severity: a.statusSeverity || '',
    more: Number(a.iteratorRemainingCount || 0) || 0, iteratorId: a.iteratorID || '', node: rs, root,
  };
}

/** A fresh collector for one sync run. */
function newRun() {
  return {
    model: newModel('qbxml'), items: new Map(), counts: {}, unsupported: new Set(), adjusted: 0, adjustedBy: {}, zero: 0, failed: [],
    accountNames: new Set(), contactNames: new Set(),
  };
}

const name = (n, ...p) => val(n, ...p, 'FullName') || val(n, ...p, 'Name');
const cents = (t) => { const c = money(t); return c === null ? 0 : c; };

/** Take one page of an answer and add it to the run. */
function absorb(run, step, res) {
  if (!res.node) return;
  const xml = res.root;
  if (step.kind === 'list') {
    // the existing qbXML reader already knows the lists; hand it just this page
    const piece = importQbxml(serialize(res.node));
    const m = run.model;
    for (const a of piece.accounts) { m.accounts.push(a); run.accountNames.add(a.name); }
    for (const c of piece.contacts) { m.contacts.push(c); run.contactNames.add(c.kind + '|' + c.name); }
    for (const i of piece.items) m.items.push(i);
    for (const note of piece.notes) if (!m.notes.includes(note)) m.notes.push(note);
    if (step.id === 'items') for (const it of find(res.node, 'ItemServiceRet').concat(find(res.node, 'ItemInventoryRet'), find(res.node, 'ItemNonInventoryRet'), find(res.node, 'ItemOtherChargeRet'), find(res.node, 'ItemDiscountRet'), find(res.node, 'ItemSalesTaxRet'), find(res.node, 'ItemSubtotalRet'), find(res.node, 'ItemPaymentRet'))) learnItem(run, it);
    run.counts[step.id] = (run.counts[step.id] || 0) + (step.id === 'accounts' ? piece.accounts.length : step.id === 'items' ? piece.items.length : piece.contacts.length);
    return;
  }
  void xml;
  const rets = find(res.node, step.ret).filter((n, i, all) => !all.some((o) => o !== n && contains(o, n)));
  for (const r of rets) { try { convert(run, step, r); } catch (e) { run.failed.push(`${step.label}: ${e.message}`); } }
  run.counts[step.id] = (run.counts[step.id] || 0) + rets.length;
}
const contains = (outer, inner) => outer.children.some((c) => c === inner || contains(c, inner));
// the list reader takes text, so a parsed node is written back out as simple XML for it
function serialize(n) {
  const at = Object.entries(n.attrs || {}).map(([k, v]) => ` ${k}="${esc(v)}"`).join('');
  return `<${n.name}${at}>${n.children.length ? n.children.map(serialize).join('') : esc(n.text)}</${n.name}>`;
}

// ---------- items: which account does each product or service post to? ----------
function learnItem(run, it) {
  const nm = name(it); if (!nm) return;
  const t = it.name;
  const income = name(it, 'SalesOrPurchase', 'AccountRef') || name(it, 'SalesAndPurchase', 'IncomeAccountRef') || name(it, 'IncomeAccountRef') || name(it, 'AccountRef') || '';
  const expense = name(it, 'SalesAndPurchase', 'ExpenseAccountRef') || name(it, 'COGSAccountRef') || name(it, 'PurchaseAccountRef') || name(it, 'SalesOrPurchase', 'AccountRef') || name(it, 'AssetAccountRef') || '';
  run.items.set(nm, { kind: t === 'ItemSubtotalRet' ? 'subtotal' : t === 'ItemSalesTaxRet' ? 'tax' : t === 'ItemPaymentRet' ? 'payment' : 'item', income: t === 'ItemPaymentRet' ? (name(it, 'DepositToAccountRef') || '') : income, expense });
}

// ---------- turning transactions into journal entries ----------
const FALLBACK = { income: 'Uncategorized Income', expense: 'Uncategorized Expense', ar: 'Accounts Receivable', ap: 'Accounts Payable', tax: 'Sales Tax Payable', undeposited: 'Undeposited Funds', bank: 'Uncategorized Bank' };
const dr = (account, c, extra = {}) => (c >= 0 ? { account, debit_cents: c, credit_cents: 0, ...extra } : { account, debit_cents: 0, credit_cents: -c, ...extra });
const cr = (account, c, extra = {}) => dr(account, -c, extra);

function addEntry(run, kind, step, r, { date, ref, memo, lines, id }) {
  lines = lines.filter((l) => l.debit_cents || l.credit_cents);
  const d = lines.reduce((s, l) => s + l.debit_cents, 0), c = lines.reduce((s, l) => s + l.credit_cents, 0);
  if (d !== c) {   // keep the books balanced exactly as QuickBooks had them: put the difference where it is easy to find and say so
    const acct = d > c ? (kind === 'sale' ? FALLBACK.income : FALLBACK.expense) : (kind === 'sale' ? FALLBACK.income : FALLBACK.expense);
    lines.push(d > c ? { account: acct, debit_cents: 0, credit_cents: d - c, memo: 'Difference when converting from QuickBooks' } : { account: acct, debit_cents: c - d, credit_cents: 0, memo: 'Difference when converting from QuickBooks' });
    run.adjusted++; (run.adjustedBy[step.id] = run.adjustedBy[step.id] || []).push({ id, d, c, ref });
    needFallback(run, acct);
  }
  if (!lines.length) { run.zero++; return; }   // nothing in it (a voided check, say)
  const when = parseDate(date);
  if (!when || lines.length < 2) { run.failed.push(`${step.label}: ${ref || id} has no usable date or amount, so it was left out`); return; }
  for (const l of lines) needFallback(run, l.account);
  run.model.journal_entries.push({ date: when, ref: String(ref || '').slice(0, 50), memo: String(memo || '').slice(0, 300), lines, external_id: `qbwc-${step.id}-${id}` });
}
// accounts that QuickBooks did not list (for example a fixed name like "Undeposited Funds") are created so nothing is lost
function needFallback(run, account) {
  if (!account || run.accountNames.has(account)) return;
  const kind = account === FALLBACK.income ? 'income' : account === FALLBACK.expense ? 'expense' : account === FALLBACK.ar ? 'ar' : account === FALLBACK.ap ? 'ap' : account === FALLBACK.tax ? 'other_current_liability'
    : account === FALLBACK.undeposited ? 'other_current_asset' : account === FALLBACK.bank ? 'bank' : null;
  if (!kind) return;   // a real account that is simply not in the list: the import preview will say so
  run.accountNames.add(account); run.model.accounts.push({ number: '', name: account, subtype: kind, description: 'Created by the QuickBooks sync' });
}

const lineAccount = (run, l, side) => {
  const direct = name(l, 'AccountRef');
  if (direct) return direct;
  const item = run.items.get(name(l, 'ItemRef'));
  if (item) { const a = side === 'sale' ? item.income : item.expense; if (a) return a; }
  return side === 'sale' ? FALLBACK.income : FALLBACK.expense;
};

function convert(run, step, r) {
  const id = val(r, 'TxnID'); if (!id) return;
  const date = val(r, 'TxnDate'), ref = val(r, 'RefNumber');
  const who = name(r, 'CustomerRef') || name(r, 'VendorRef') || name(r, 'PayeeEntityRef') || name(r, 'EntityRef');
  const ent = { id, date, ref };
  switch (step.id) {
    case 'invoices': case 'creditmemos': case 'salesreceipts': {
      const credit = step.id === 'creditmemos';
      const control = step.id === 'salesreceipts' ? (name(r, 'DepositToAccountRef') || FALLBACK.undeposited) : (name(r, 'ARAccountRef') || FALLBACK.ar);
      const body = [], payments = [];
      for (const l of find(r, step.id === 'invoices' ? 'InvoiceLineRet' : step.id === 'creditmemos' ? 'CreditMemoLineRet' : 'SalesReceiptLineRet')) {
        const it = run.items.get(name(l, 'ItemRef'));
        if (it && it.kind === 'subtotal') continue;   // subtotal lines repeat amounts already counted
        const amount = cents(val(l, 'Amount')); if (!amount) continue;
        if (it && it.kind === 'payment') { payments.push({ account: it.income || FALLBACK.undeposited, amount: -amount }); continue; }   // a payment taken on the invoice itself
        const acct = it && it.kind === 'tax' ? FALLBACK.tax : lineAccount(run, l, 'sale');
        body.push({ account: acct, amount, memo: val(l, 'Desc') });
      }
      const tax = cents(val(r, 'SalesTaxTotal'));
      let sumBody = body.reduce((s, l) => s + l.amount, 0);
      const taxIncluded = body.some((l) => l.account === FALLBACK.tax);
      if (tax && !taxIncluded) { body.push({ account: FALLBACK.tax, amount: tax, memo: 'Sales tax' }); sumBody += tax; }
      const total = val(r, 'Subtotal') !== '' ? cents(val(r, 'Subtotal')) + tax : sumBody;
      const lines = [];
      lines.push(credit ? cr(control, total, { contact: who }) : dr(control, total, { contact: who }));
      for (const l of body) lines.push(credit ? dr(l.account, l.amount, { memo: l.memo }) : cr(l.account, l.amount, { memo: l.memo }));
      if (!credit) for (const p of payments) lines.push(dr(p.account, p.amount, { memo: 'Payment taken on the invoice' }));
      return addEntry(run, 'sale', step, r, { ...ent, memo: `${step.id === 'invoices' ? 'Invoice' : step.id === 'creditmemos' ? 'Credit memo' : 'Sales receipt'} ${ref}${who ? ' - ' + who : ''}`.trim(), lines });
    }
    case 'bills': case 'vendorcredits': {
      const credit = step.id === 'vendorcredits';
      const control = name(r, 'APAccountRef') || FALLBACK.ap;
      const body = [];
      for (const tag of credit ? ['ExpenseLineRet', 'ItemLineRet'] : ['ExpenseLineRet', 'ItemLineRet']) {
        for (const l of find(r, tag)) { const amount = cents(val(l, 'Amount')); if (!amount) continue; body.push({ account: lineAccount(run, l, 'buy'), amount, memo: val(l, 'Memo') || val(l, 'Desc') }); }
      }
      const total = body.reduce((s, l) => s + l.amount, 0) || cents(val(r, 'AmountDue'));
      const lines = [credit ? dr(control, total, { contact: who }) : cr(control, total, { contact: who })];
      for (const l of body) lines.push(credit ? cr(l.account, l.amount, { memo: l.memo }) : dr(l.account, l.amount, { memo: l.memo }));
      return addEntry(run, 'buy', step, r, { ...ent, memo: `${credit ? 'Vendor credit' : 'Bill'} ${ref}${who ? ' - ' + who : ''}`.trim(), lines });
    }
    case 'payments': {
      const total = cents(val(r, 'TotalAmount'));
      return addEntry(run, 'sale', step, r, { ...ent, memo: `Payment received${who ? ' - ' + who : ''}`, lines: [dr(name(r, 'DepositToAccountRef') || FALLBACK.undeposited, total), cr(name(r, 'ARAccountRef') || FALLBACK.ar, total, { contact: who })] });
    }
    case 'billpayments': case 'billpaymentscc': {
      const total = cents(val(r, 'Amount')), bank = name(r, 'BankAccountRef') || FALLBACK.bank;
      const ap = name(r, 'APAccountRef') || FALLBACK.ap;
      return addEntry(run, 'buy', step, r, { ...ent, memo: `Bill payment${who ? ' - ' + who : ''}`, lines: [dr(ap, total, { contact: who }), cr(bank, total)] });
    }
    case 'deposits': {
      const to = name(r, 'DepositToAccountRef') || FALLBACK.bank, lines = [];
      let total = 0;
      for (const l of find(r, 'DepositLineRet')) { const a = cents(val(l, 'Amount')); if (!a) continue; total += a; lines.push(cr(name(l, 'AccountRef') || FALLBACK.undeposited, a, { memo: val(l, 'Memo'), contact: name(l, 'EntityRef') })); }
      const stated = cents(val(r, 'DepositTotal')); if (stated) total = stated;
      lines.unshift(dr(to, total));
      return addEntry(run, 'sale', step, r, { ...ent, memo: val(r, 'Memo') || 'Deposit', lines });
    }
    case 'checks': case 'cardcharges': case 'cardcredits': {
      const out = name(r, 'AccountRef') || FALLBACK.bank, back = step.id === 'cardcredits';
      const body = [];
      for (const tag of ['ExpenseLineRet', 'ItemLineRet']) for (const l of find(r, tag)) { const a = cents(val(l, 'Amount')); if (!a) continue; body.push({ account: lineAccount(run, l, 'buy'), amount: a, memo: val(l, 'Memo') || val(l, 'Desc') }); }
      const total = body.reduce((s, l) => s + l.amount, 0) || cents(val(r, 'Amount'));
      const lines = [back ? dr(out, total) : cr(out, total)];
      for (const l of body) lines.push(back ? cr(l.account, l.amount, { memo: l.memo, contact: who }) : dr(l.account, l.amount, { memo: l.memo, contact: who }));
      return addEntry(run, 'buy', step, r, { ...ent, memo: `${step.id === 'checks' ? 'Check' : back ? 'Card credit' : 'Card charge'}${who ? ' - ' + who : ''}`, lines });
    }
    case 'journal': {
      const lines = [];
      for (const l of find(r, 'JournalDebitLine')) lines.push(dr(name(l, 'AccountRef'), cents(val(l, 'Amount')), { memo: val(l, 'Memo'), contact: name(l, 'EntityRef') }));
      for (const l of find(r, 'JournalCreditLine')) lines.push(cr(name(l, 'AccountRef'), cents(val(l, 'Amount')), { memo: val(l, 'Memo'), contact: name(l, 'EntityRef') }));
      return addEntry(run, 'journal', step, r, { ...ent, memo: `Journal entry ${ref}`.trim(), lines });
    }
    case 'transfers': {
      const a = cents(val(r, 'Amount'));
      return addEntry(run, 'transfer', step, r, { ...ent, memo: val(r, 'Memo') || 'Transfer', lines: [dr(name(r, 'TransferToAccountRef') || FALLBACK.bank, a), cr(name(r, 'TransferFromAccountRef') || FALLBACK.bank, a)] });
    }
    default: run.unsupported.add(step.id);
  }
}

/** Finish a run: the model to preview and import, and a plain-language summary. */
function finishRun(run, startedMs) {
  const m = run.model;
  // the same account or person can arrive in several pages; keep each once
  const seen = new Set(); m.accounts = m.accounts.filter((a) => (seen.has(a.name) ? false : seen.add(a.name)));
  const sc = new Set(); m.contacts = m.contacts.filter((c) => { const k = c.kind + '|' + c.name; return sc.has(k) ? false : sc.add(k); });
  const si = new Set(); m.items = m.items.filter((i) => (si.has(i.name) ? false : si.add(i.name)));
  if (run.adjusted) m.notes.push(`${run.adjusted} transaction${run.adjusted === 1 ? '' : 's'} did not add up exactly when rebuilt from QuickBooks' data (for example a line Ledjoro could not place). The small difference was put in "Uncategorized Income" or "Uncategorized Expense" so the books still balance; look for it there.`);
  if (run.zero) m.notes.push(`${run.zero} QuickBooks transaction${run.zero === 1 ? ' has' : 's have'} no amount (voided checks, for example) and ${run.zero === 1 ? 'was' : 'were'} left out.`);
  for (const f of run.failed.slice(0, 10)) m.warnings.push(f);
  if (run.failed.length > 10) m.warnings.push(`...and ${run.failed.length - 10} more items QuickBooks sent that were left out.`);
  m.notes.push('Changes made later in QuickBooks to a transaction that was already imported are not copied (it is recognised as already imported). Transaction types not listed in the preview, such as estimates, purchase orders, payroll and inventory adjustments, are not brought across.');
  return { model: m, started: startedMs, counts: run.counts };
}

module.exports = { STEPS, QBXML_VERSION, buildRequest, readResponse, newRun, absorb, finishRun, PAGE };
