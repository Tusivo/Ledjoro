'use strict';
/**
 * qbXML (the documented QuickBooks Desktop SDK XML) and QuickBooks-Online-API-style JSON importers.
 * The XML reader is a minimal, hardened tokenizer: DOCTYPE/entities are stripped (never expanded), nesting and node
 * counts are capped, and only the five predefined XML entities are decoded.
 */
const { bad } = require('../util');
const { skippedNote, newModel, mapAccountType, termsToDays, parseDate, money, phone } = require('./model');

const ENT = { '&amp;': '&', '&lt;': '<', '&gt;': '>', '&quot;': '"', '&apos;': "'" };
const decode = (s) => s.replace(/&(amp|lt|gt|quot|apos);/g, (x) => ENT[x]);
const MAX_DEPTH = 64, MAX_NODES = 2_000_000;

/** Attributes of a tag, such as requestID="1" statusCode="0" (values decoded, names kept as written). */
function attrsOf(str) {
  const a = {}; if (!str) return a;
  const re = /([A-Za-z_][\w.:-]*)\s*=\s*(?:"([^"]*)"|'([^']*)')/g; let m;
  while ((m = re.exec(str))) a[m[1]] = decode(m[2] ?? m[3] ?? '');
  return a;
}

function parseXml(text) {
  let s = text.replace(/^﻿/, '')
    .replace(/<!DOCTYPE[^>[]*(\[[\s\S]*?\])?\s*>/gi, '') // never process DTDs
    .replace(/<!--[\s\S]*?-->/g, '').replace(/<\?[\s\S]*?\?>/g, '');
  const root = { name: '#root', children: [], text: '' };
  const stack = [root];
  const re = /<!\[CDATA\[([\s\S]*?)\]\]>|<\/([A-Za-z_][\w.:-]*)\s*>|<([A-Za-z_][\w.:-]*)((?:\s+[^<>]*?)?)(\/?)>|([^<]+)/g;
  let nodes = 0, m;
  while ((m = re.exec(s))) {
    if (m[1] !== undefined) stack[stack.length - 1].text += m[1];
    else if (m[2]) { if (stack.length > 1 && stack[stack.length - 1].name === m[2]) stack.pop(); else throw bad('This XML file is damaged: a tag is closed that was never opened, or in the wrong order. It was probably cut off or edited by hand. Export it again from QuickBooks.'); }
    else if (m[3]) {
      if (++nodes > MAX_NODES) throw bad('This XML file is larger or more complicated than Ledjoro will read safely. Split the export into smaller parts.');
      const node = { name: m[3], children: [], text: '', attrs: attrsOf(m[4]) };
      stack[stack.length - 1].children.push(node);
      if (!m[5]) { stack.push(node); if (stack.length > MAX_DEPTH) throw bad('This XML file is nested far deeper than a real QuickBooks export ever is, so Ledjoro will not read it. Check that it really came from QuickBooks.'); }
    } else if (m[6] && stack.length > 1) stack[stack.length - 1].text += decode(m[6]);
  }
  if (stack.length !== 1) throw bad('This XML file is damaged: some tags are opened but never closed. It was probably cut off while saving. Export it again.');
  return root;
}
const find = (n, name, out = []) => { for (const c of n.children) { if (c.name === name) out.push(c); find(c, name, out); } return out; };
const val = (n, ...path) => { let c = n; for (const p of path) { c = c?.children.find((x) => x.name === p); if (!c) return ''; } return c.text.trim(); };

const QBXML_TYPES = { Bank: 'bank', AccountsReceivable: 'ar', AccountsPayable: 'ap', CreditCard: 'credit_card', Equity: 'equity', Income: 'income', CostOfGoodsSold: 'cogs', Expense: 'expense', OtherIncome: 'other_income', OtherExpense: 'other_expense', FixedAsset: 'fixed_asset', OtherAsset: 'other_asset', OtherCurrentAsset: 'other_current_asset', OtherCurrentLiability: 'other_current_liability', LongTermLiability: 'long_term_liability' };

function importQbxml(text) {
  const m = newModel('qbxml');
  const root = parseXml(text);
  const skipped = [];
  for (const a of find(root, 'AccountRet')) {
    const name = val(a, 'FullName') || val(a, 'Name');
    const subtype = QBXML_TYPES[val(a, 'AccountType')];
    if (!name) continue;
    if (!subtype) { skipped.push({ name, type: val(a, 'AccountType') }); continue; }
    if (val(a, 'IsActive') === 'false') continue;
    m.accounts.push({ number: val(a, 'AccountNumber'), name, subtype, description: val(a, 'Desc') });
  }
  { const sn = skippedNote(skipped); if (sn) m.notes.push(sn); }
  for (const kind of ['Customer', 'Vendor']) {
    for (const c of find(root, `${kind}Ret`)) {
      const name = val(c, 'FullName') || val(c, 'Name');
      if (!name) continue;
      const addr = ['Addr1', 'Addr2', 'City', 'State', 'PostalCode'].map((k) => val(c, 'BillAddress', k) || (kind === 'Vendor' ? val(c, 'VendorAddress', k) : '')).filter(Boolean).join(', ');
      m.contacts.push({ kind: kind.toLowerCase(), name, email: val(c, 'Email'), phone: phone(val(c, 'Phone')), address: addr, terms_days: termsToDays(val(c, 'TermsRef', 'FullName')), is_1099: val(c, 'IsVendorEligibleFor1099') === 'true' });
    }
  }
  for (const [tag, kind] of [['ItemServiceRet', 'service'], ['ItemInventoryRet', 'product'], ['ItemNonInventoryRet', 'nonstock']]) {
    for (const it of find(root, tag)) {
      const name = val(it, 'FullName') || val(it, 'Name');
      if (!name) continue;
      const price = money(val(it, 'SalesOrPurchase', 'Price') || val(it, 'SalesAndPurchase', 'SalesPrice') || val(it, 'SalesPrice'));
      m.items.push({ name, kind, description: val(it, 'SalesOrPurchase', 'Desc') || val(it, 'SalesAndPurchase', 'SalesDesc') || val(it, 'SalesDesc'), unit_price_cents: Math.max(0, price ?? 0), income_account: val(it, 'SalesOrPurchase', 'AccountRef', 'FullName') || val(it, 'IncomeAccountRef', 'FullName') });
    }
  }
  const txCount = ['InvoiceRet', 'BillRet', 'CheckRet', 'DepositRet', 'JournalEntryRet', 'ReceivePaymentRet', 'CreditMemoRet', 'SalesReceiptRet'].reduce((s, t) => s + find(root, t).length, 0);
  if (txCount) m.notes.push(`${txCount} transaction record(s) in this qbXML were not converted. Export a Trial Balance (CSV) for opening balances, or an IIF file for full transactions.`);
  return m;
}

function importQboJson(text) {
  const m = newModel('qbo-json');
  let j;
  try { j = JSON.parse(text); } catch { throw bad('That file should be JSON but could not be read as JSON: it has a formatting mistake or was cut off. Export it again.'); }
  const bucket = j?.QueryResponse ?? j?.IntuitResponse?.QueryResponse ?? j;
  const arr = (k) => (Array.isArray(bucket?.[k]) ? bucket[k] : []);
  const skipped2 = [];
  for (const a of arr('Account')) {
    const name = a.FullyQualifiedName || a.Name;
    const subtype = mapAccountType(a.AccountType === 'Accounts Receivable' ? 'accountsreceivable' : a.AccountType);
    if (!name || a.Active === false) continue;
    if (!subtype) { skipped2.push({ name, type: a.AccountType }); continue; }
    m.accounts.push({ number: a.AcctNum || '', name, subtype, description: a.Description || '' });
  }
  { const sn = skippedNote(skipped2); if (sn) m.notes.push(sn); }
  for (const [key, kind] of [['Customer', 'customer'], ['Vendor', 'vendor']]) {
    for (const c of arr(key)) {
      const name = c.FullyQualifiedName || c.DisplayName;
      if (!name || c.Active === false) continue;
      const b = c.BillAddr || {};
      m.contacts.push({ kind, name, email: c.PrimaryEmailAddr?.Address || '', phone: phone(c.PrimaryPhone?.FreeFormNumber), address: [b.Line1, b.City, b.CountrySubDivisionCode, b.PostalCode].filter(Boolean).join(', '), terms_days: termsToDays(c.SalesTermRef?.name || c.TermRef?.name), is_1099: !!c.Vendor1099 });
    }
  }
  for (const it of arr('Item')) {
    if (!it.Name || it.Active === false) continue;
    m.items.push({ name: it.FullyQualifiedName || it.Name, kind: it.Type === 'Service' ? 'service' : it.Type === 'Inventory' ? 'product' : 'nonstock', description: it.Description || '', unit_price_cents: Math.max(0, money(it.UnitPrice) ?? 0), income_account: it.IncomeAccountRef?.name || '', expense_account: it.ExpenseAccountRef?.name || '' });
  }
  for (const je of arr('JournalEntry')) {
    const date = parseDate(je.TxnDate);
    const lines = [];
    for (const l of je.Line || []) {
      const d = l.JournalEntryLineDetail;
      const amt = money(l.Amount);
      if (!d || amt === null) continue;
      lines.push({ account: d.AccountRef?.name || d.AccountRef?.value, debit_cents: d.PostingType === 'Debit' ? amt : 0, credit_cents: d.PostingType === 'Credit' ? amt : 0, memo: l.Description || '', contact: d.Entity?.EntityRef?.name || '' });
    }
    const dr = lines.reduce((s, l) => s + l.debit_cents, 0), cr = lines.reduce((s, l) => s + l.credit_cents, 0);
    if (!date || lines.length < 2 || dr !== cr) { m.errors.push(`Journal entry ${je.DocNumber || je.Id || ''}: it has a missing date, fewer than two lines, or debits that do not equal credits, so it was left out.`); continue; }
    m.journal_entries.push({ date, ref: je.DocNumber || '', memo: je.PrivateNote || `Journal entry ${je.DocNumber || je.Id || ''}`.trim(), lines, external_id: `qbo-je-${je.Id ?? ''}-${je.DocNumber ?? ''}-${date}-${dr}` });
  }
  const other = ['Invoice', 'Bill', 'Payment', 'Purchase', 'Deposit', 'SalesReceipt'].reduce((s, k) => s + arr(k).length, 0);
  if (other) m.notes.push(`${other} invoice/bill/payment-type record(s) were not converted. Use a Trial Balance CSV for opening balances.`);
  return m;
}

module.exports = { importQbxml, importQboJson, parseXml, find, val };
