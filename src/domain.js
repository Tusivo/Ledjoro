'use strict';
const { tx, getSetting, setSetting, ACCOUNT_TYPES, SUBTYPES } = require('./db');
const { bad, HttpError, vStr, vInt, vDate, vEnum, vBool, vObj, vArr, vPhone, addDays, today } = require('./util');
const { postEntry, reverseEntry, systemAccount } = require('./ledger');
const { audit } = require('./audit');
const ST = require('./salestax');

const TYPE_FOR_SUBTYPE = {
  bank: 'asset', ar: 'asset', other_current_asset: 'asset', fixed_asset: 'asset', other_asset: 'asset',
  ap: 'liability', credit_card: 'liability', other_current_liability: 'liability', long_term_liability: 'liability',
  equity: 'equity', retained_earnings: 'equity', income: 'income', other_income: 'other_income',
  cogs: 'cogs', expense: 'expense', other_expense: 'other_expense',
};

// ---------------- accounts ----------------
const listAccounts = (db) => db.prepare(`SELECT a.*, COALESCE((SELECT SUM(debit_cents-credit_cents) FROM journal_lines WHERE account_id=a.id),0) AS balance_cents
  FROM accounts a ORDER BY COALESCE(NULLIF(number,''),'zzzz'), name`).all();

function createAccount(db, ctx, b) {
  const subtype = vEnum(b.subtype, 'subtype', SUBTYPES);
  const type = TYPE_FOR_SUBTYPE[subtype];
  const number = vStr(b.number, 'number', { max: 20 });
  const name = vStr(b.name, 'name', { required: true, max: 120 });
  try {
    const r = db.prepare('INSERT INTO accounts(number,name,type,subtype,description,created_at) VALUES (?,?,?,?,?,?)')
      .run(number || null, name, type, subtype, vStr(b.description, 'description', { max: 300 }), Date.now());
    audit(db, ctx, 'account.create', 'account', r.lastInsertRowid, name);
    return Number(r.lastInsertRowid);
  } catch (e) {
    if (/UNIQUE/.test(e.message)) throw bad('Another account already uses that name or number. Every account needs its own name and number; choose different ones.');
    throw e;
  }
}
function updateAccount(db, ctx, id, b) {
  const a = db.prepare('SELECT * FROM accounts WHERE id=?').get(id);
  if (!a) throw new HttpError(404, 'That account could not be found. It may have been deleted; refresh the list.');
  const name = b.name !== undefined ? vStr(b.name, 'name', { required: true, max: 120 }) : a.name;
  const number = b.number !== undefined ? vStr(b.number, 'number', { max: 20 }) : a.number;
  const active = b.active !== undefined ? vBool(b.active) : a.active;
  if (a.system && !active) throw bad('This is a built-in account that Ledjoro needs (for invoices, bills or opening balances), so it cannot be made inactive.');
  const used = db.prepare('SELECT 1 FROM journal_lines WHERE account_id=? LIMIT 1').get(id);
  let { type, subtype } = a;
  if (b.subtype !== undefined && b.subtype !== a.subtype) {
    if (used) throw bad('This account already has transactions, so its type cannot be changed (it would rewrite your old reports). Make a new account of the right type and use it from now on.');
    subtype = vEnum(b.subtype, 'subtype', SUBTYPES); type = TYPE_FOR_SUBTYPE[subtype];
  }
  try {
    db.prepare('UPDATE accounts SET name=?, number=?, active=?, type=?, subtype=?, description=? WHERE id=?')
      .run(name, number || null, active, type, subtype, b.description !== undefined ? vStr(b.description, 'description', { max: 300 }) : a.description, id);
  } catch (e) {
    if (/UNIQUE/.test(e.message)) throw bad('Another account already uses that name or number. Every account needs its own name and number; choose different ones.');
    throw e;
  }
  audit(db, ctx, 'account.update', 'account', id, name);
}

// ---------------- contacts ----------------
const listContacts = (db, kind) => db.prepare(`SELECT c.*, COALESCE((SELECT SUM(debit_cents-credit_cents) FROM journal_lines l JOIN accounts a ON a.id=l.account_id
  WHERE l.contact_id=c.id AND a.subtype IN ('ar','ap')),0) AS ledger_balance_cents FROM contacts c WHERE (? IS NULL OR c.kind=?) ORDER BY c.name`).all(kind ?? null, kind ?? null);

const US_STATES = new Set('AL AK AZ AR CA CO CT DE DC FL GA HI ID IL IN IA KS KY LA ME MD MA MI MN MS MO MT NE NV NH NJ NM NY NC ND OH OK OR PA RI SC SD TN TX UT VT VA WA WV WI WY AS GU MP PR VI AA AE AP'.split(' '));
const ADDRESS_PARTS = ['addr_street', 'addr_street2', 'addr_city', 'addr_state', 'addr_zip'];
/** A US mailing address as it would be written on an envelope: street, second line, then "City, ST 12345". */
function composeAddress(f) {
  const last = [f.addr_city, [f.addr_state, f.addr_zip].filter(Boolean).join(' ')].filter(Boolean).join(', ');
  return [f.addr_street, f.addr_street2, last].filter(Boolean).join('\n');
}
function contactFields(b) {
  const f = {
    email: vStr(b.email, 'email', { max: 200 }), phone: vPhone(b.phone), phone2: vPhone(b.phone2, 'second phone number'),
    first_name: vStr(b.first_name, 'first name', { max: 60 }), middle_name: vStr(b.middle_name, 'middle name', { max: 60 }), last_name: vStr(b.last_name, 'last name', { max: 60 }),
    addr_street: vStr(b.addr_street, 'street address', { max: 120 }), addr_street2: vStr(b.addr_street2, 'apartment or suite', { max: 120 }), addr_city: vStr(b.addr_city, 'city', { max: 80 }),
    addr_state: vStr(b.addr_state, 'state', { max: 2 }).toUpperCase(), addr_zip: vStr(b.addr_zip, 'ZIP code', { max: 10 }),
    terms_days: vInt(b.terms_days ?? 30, 'terms_days', { min: 0, max: 365 }), is_1099: vBool(b.is_1099),
    tax_exempt: vBool(b.tax_exempt), tax_exempt_note: vStr(b.tax_exempt_note, 'exemption note', { max: 120 }),
    tax_rate_id: b.tax_rate_id ? vInt(b.tax_rate_id, 'tax rate') : null,
  };
  if (f.addr_state && !US_STATES.has(f.addr_state)) throw bad(`"${f.addr_state}" is not a US state abbreviation. Use the two letters the post office uses, such as NH, CA or TX.`);
  if (f.addr_zip && !/^\d{5}(-\d{4})?$/.test(f.addr_zip)) throw bad('The ZIP code must be 5 digits (such as 03301) or 5+4 digits (such as 03301-1234).');
  f.address = ADDRESS_PARTS.some((k) => f[k]) ? composeAddress(f) : vStr(b.address, 'address', { max: 500 });   // older callers send one free-text address
  return f;
}
/** The name shown in lists: the business name if given, otherwise "First Middle Last". */
function contactName(b, f) {
  const given = vStr(b.name, 'name', { max: 150 });
  const person = [f.first_name, f.middle_name, f.last_name].filter(Boolean).join(' ');
  const n = given || person;
  if (!n) throw bad('Please fill in the name: a first and last name, a business name, or both.');
  return n;
}
function createContact(db, ctx, b) {
  const kind = vEnum(b.kind, 'kind', ['customer', 'vendor']);
  const f = contactFields(b);
  const name = contactName(b, f);
  try {
    const r = db.prepare('INSERT INTO contacts(kind,name,email,phone,phone2,address,first_name,middle_name,last_name,addr_street,addr_street2,addr_city,addr_state,addr_zip,terms_days,is_1099,tax_exempt,tax_exempt_note,tax_rate_id,created_at) VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)')
      .run(kind, name, f.email, f.phone, f.phone2, f.address, f.first_name, f.middle_name, f.last_name, f.addr_street, f.addr_street2, f.addr_city, f.addr_state, f.addr_zip, f.terms_days, f.is_1099, f.tax_exempt, f.tax_exempt_note, f.tax_rate_id, Date.now());
    audit(db, ctx, 'contact.create', kind, r.lastInsertRowid, name);
    return Number(r.lastInsertRowid);
  } catch (e) {
    if (/UNIQUE/.test(e.message)) throw bad(`You already have a ${kind} called "${name}". Use a different name, or open the existing one. Adding a detail such as a city tells similar names apart.`);
    throw e;
  }
}
function updateContact(db, ctx, id, b) {
  const c = db.prepare('SELECT * FROM contacts WHERE id=?').get(id);
  if (!c) throw new HttpError(404, 'That customer or vendor could not be found. It may have been deleted; refresh the list.');
  const merged = { ...c, ...b };
  const f = contactFields({ ...merged, phone: b.phone !== undefined && b.phone !== c.phone ? b.phone : '', phone2: b.phone2 !== undefined && b.phone2 !== c.phone2 ? b.phone2 : '' });
  if (b.phone === undefined || b.phone === c.phone) f.phone = c.phone; // unchanged (possibly legacy-format) numbers are left alone
  if (b.phone2 === undefined || b.phone2 === c.phone2) f.phone2 = c.phone2;
  const name = b.name !== undefined || b.first_name !== undefined || b.last_name !== undefined ? contactName(merged, f) : c.name;
  const active = b.active !== undefined ? vBool(b.active) : c.active;
  try {
    db.prepare('UPDATE contacts SET name=?,email=?,phone=?,phone2=?,address=?,first_name=?,middle_name=?,last_name=?,addr_street=?,addr_street2=?,addr_city=?,addr_state=?,addr_zip=?,terms_days=?,is_1099=?,tax_exempt=?,tax_exempt_note=?,tax_rate_id=?,active=? WHERE id=?').run(name, f.email, f.phone, f.phone2, f.address, f.first_name, f.middle_name, f.last_name, f.addr_street, f.addr_street2, f.addr_city, f.addr_state, f.addr_zip, f.terms_days, f.is_1099, f.tax_exempt, f.tax_exempt_note, f.tax_rate_id, active, id);
  } catch (e) {
    if (/UNIQUE/.test(e.message)) throw bad(`You already have a ${c.kind} called "${name}". Use a different name, or open the existing one.`);
    throw e;
  }
  audit(db, ctx, 'contact.update', c.kind, id, name);
}

// ---------------- items ----------------
const listItems = (db) => db.prepare('SELECT * FROM items ORDER BY name').all();
function createItem(db, ctx, b) {
  const name = vStr(b.name, 'name', { required: true, max: 150 });
  const kind = vEnum(b.kind || 'service', 'kind', ['service', 'product', 'nonstock']);
  const inc = vInt(b.income_account_id, 'income_account_id', { required: false });
  const exp = vInt(b.expense_account_id, 'expense_account_id', { required: false });
  for (const a of [inc, exp]) if (a && !db.prepare('SELECT 1 FROM accounts WHERE id=?').get(a)) throw bad('One of the accounts chosen does not exist any more. Pick the account again from the list.');
  try {
    const r = db.prepare('INSERT INTO items(name,kind,description,unit_price_cents,income_account_id,expense_account_id,taxable) VALUES (?,?,?,?,?,?,?)')
      .run(name, kind, vStr(b.description, 'description', { max: 300 }), vInt(b.unit_price_cents ?? 0, 'unit_price_cents', { min: 0, max: 1e12 }), inc, exp, b.taxable === undefined ? 1 : vBool(b.taxable));
    audit(db, ctx, 'item.create', 'item', r.lastInsertRowid, name);
    return Number(r.lastInsertRowid);
  } catch (e) {
    if (/UNIQUE/.test(e.message)) throw bad('You already have a product or service with that name. Choose a different name.');
    throw e;
  }
}

function updateItem(db, ctx, id, b) {
  const it = db.prepare('SELECT * FROM items WHERE id=?').get(id);
  if (!it) throw new HttpError(404, 'That product or service could not be found. It may have been removed; refresh the list.');
  const name = b.name !== undefined ? vStr(b.name, 'name', { required: true, max: 150 }) : it.name;
  const kind = b.kind !== undefined ? vEnum(b.kind, 'kind', ['service', 'product', 'nonstock']) : it.kind;
  const inc = b.income_account_id !== undefined ? (b.income_account_id ? vInt(b.income_account_id, 'income_account_id') : null) : it.income_account_id;
  const exp = b.expense_account_id !== undefined ? (b.expense_account_id ? vInt(b.expense_account_id, 'expense_account_id') : null) : it.expense_account_id;
  for (const a of [inc, exp]) if (a && !db.prepare('SELECT 1 FROM accounts WHERE id=?').get(a)) throw bad('One of the accounts chosen does not exist any more. Pick the account again from the list.');
  const price = b.unit_price_cents !== undefined ? vInt(b.unit_price_cents, 'unit_price_cents', { min: 0, max: 1e12 }) : it.unit_price_cents;
  const desc = b.description !== undefined ? vStr(b.description, 'description', { max: 300 }) : it.description;
  const taxable = b.taxable !== undefined ? vBool(b.taxable) : it.taxable;
  const active = b.active !== undefined ? vBool(b.active) : it.active;
  try {
    db.prepare('UPDATE items SET name=?, kind=?, description=?, unit_price_cents=?, income_account_id=?, expense_account_id=?, taxable=?, active=? WHERE id=?')
      .run(name, kind, desc, price, inc, exp, taxable, active, id);
  } catch (e) {
    if (/UNIQUE/.test(e.message)) throw bad('You already have a product or service with that name. Choose a different name.');
    throw e;
  }
  audit(db, ctx, 'item.update', 'item', id, name);
}

// ---------------- documents (invoices & bills) ----------------
const lineAmount = (qtyMilli, unit) => {
  const v = Math.round((qtyMilli * unit) / 1000);
  if (!Number.isSafeInteger(v)) throw bad('A line amount is too large to calculate. Check the quantity and price for a misplaced decimal point or an extra zero.');
  return v;
};

function nextNumber(db, kind) {
  // Automatic numbers skip any number already in use (e.g. after importing history or typing numbers by hand).
  const key = kind === 'invoice' ? 'next_invoice' : 'next_bill';
  const prefix = kind === 'invoice' ? 'INV-' : 'BILL-';
  let n = Number(getSetting(db, key) || 1);
  const used = db.prepare('SELECT 1 FROM documents WHERE kind=? AND number=?');
  while (used.get(kind, prefix + String(n).padStart(4, '0'))) n++;
  setSetting(db, key, n + 1);
  return prefix + String(n).padStart(4, '0');
}

function createDocument(db, ctx, b) {
  const kind = vEnum(b.kind, 'kind', ['invoice', 'bill']);
  const contactId = vInt(b.contact_id, 'contact_id');
  const contact = db.prepare('SELECT * FROM contacts WHERE id=?').get(contactId);
  if (!contact || contact.kind !== (kind === 'invoice' ? 'customer' : 'vendor')) throw bad(`Choose a ${kind === 'invoice' ? 'customer' : 'vendor'} for this ${kind}. None is chosen, or the one chosen is the wrong type (a ${kind === 'invoice' ? 'vendor' : 'customer'}).`);
  const date = vDate(b.doc_date, 'date');
  const due = vDate(b.due_date, 'due date', false) || addDays(date, contact.terms_days);
  if (due < date) throw bad('The due date is earlier than the date of the document. Something cannot be due before it is issued; fix one of the two dates.');
  const raw = vArr(b.lines, 'lines', { min: 1, max: 200 });
  const lines = raw.map((l, i) => {
    vObj(l, `line ${i + 1}`);
    const item = l.item_id ? db.prepare('SELECT * FROM items WHERE id=?').get(vInt(l.item_id, 'item')) : null;
    const qty = vInt(l.qty_milli ?? 1000, `line ${i + 1} quantity`, { min: 1, max: 1e9 });
    const unit = vInt(l.unit_price_cents ?? item?.unit_price_cents ?? 0, `line ${i + 1} price`, { min: 0, max: 1e12 });
    const account = vInt(l.account_id ?? (kind === 'invoice' ? item?.income_account_id : item?.expense_account_id), `line ${i + 1} account`);
    const taxable = kind === 'invoice' ? (l.taxable === undefined ? (item ? item.taxable : 1) : vBool(l.taxable)) : 0;
    return { item_id: item?.id ?? null, description: vStr(l.description ?? item?.name, `line ${i + 1} description`, { max: 300 }), qty_milli: qty, unit_price_cents: unit, amount_cents: lineAmount(qty, unit), account_id: account, taxable };
  });
  const subtotal = lines.reduce((s, l) => s + l.amount_cents, 0);
  if (subtotal <= 0) throw bad('The total is zero or less. Add at least one line with an amount above zero.');
  const control = systemAccount(db, kind === 'invoice' ? 'ar' : 'ap');
  // sales tax (invoices only): a tax-exempt customer is never charged, whatever rate was chosen; otherwise the rate is applied to the lines marked taxable
  const taxExempt = kind === 'invoice' && contact.tax_exempt ? 1 : 0;
  const taxRateId = kind === 'invoice' && !taxExempt && b.tax_rate_id ? vInt(b.tax_rate_id, 'tax rate') : null;
  const taxed = taxRateId ? ST.taxFor(db, taxRateId, lines.filter((l) => l.taxable).reduce((s, l) => s + l.amount_cents, 0)) : { parts: [], total_cents: 0 };
  const taxCents = taxed.total_cents;
  const total = subtotal + taxCents;

  return tx(db, () => {
    const number = vStr(b.number, 'number', { max: 30 }) || nextNumber(db, kind);
    const entryLines = kind === 'invoice'
      ? [{ account_id: control, contact_id: contactId, debit_cents: total }, ...lines.map((l) => ({ account_id: l.account_id, contact_id: contactId, credit_cents: l.amount_cents, memo: l.description })),
        ...taxed.parts.filter((p) => p.tax_cents > 0).map((p) => ({ account_id: p.account_id, contact_id: contactId, credit_cents: p.tax_cents, memo: `Sales tax - ${p.name}` }))]
      : [...lines.map((l) => ({ account_id: l.account_id, contact_id: contactId, debit_cents: l.amount_cents, memo: l.description })), { account_id: control, contact_id: contactId, credit_cents: total }];
    const entryId = postEntry(db, { date, memo: `${kind === 'invoice' ? 'Invoice' : 'Bill'} ${number}`, ref: number, source_type: kind, user_id: ctx.user.id, lines: entryLines });
    let docId;
    try {
      docId = Number(db.prepare('INSERT INTO documents(kind,number,contact_id,doc_date,due_date,memo,total_cents,status,entry_id,created_by,created_at,tax_rate_id,tax_cents,tax_exempt) VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?)')
        .run(kind, number, contactId, date, due, vStr(b.memo, 'memo', { max: 500 }), total, 'open', entryId, ctx.user.id, Date.now(), taxRateId, taxCents, taxExempt).lastInsertRowid);
    } catch (e) {
      if (/UNIQUE/.test(e.message)) throw bad(`The ${kind} number ${number} is already used by another ${kind}. Leave the number empty to let Ledjoro pick the next free one, or type a different number.`);
      throw e;
    }
    const ins = db.prepare('INSERT INTO document_lines(document_id,item_id,description,qty_milli,unit_price_cents,amount_cents,account_id,taxable) VALUES (?,?,?,?,?,?,?,?)');
    for (const l of lines) ins.run(docId, l.item_id, l.description, l.qty_milli, l.unit_price_cents, l.amount_cents, l.account_id, l.taxable);
    const insTax = db.prepare('INSERT INTO document_tax(document_id,rate_id,rate_milli,taxable_cents,tax_cents) VALUES (?,?,?,?,?)');
    for (const p of taxed.parts) insTax.run(docId, p.rate_id, p.rate_milli, p.taxable_cents, p.tax_cents);
    audit(db, ctx, `${kind}.create`, kind, docId, `${number} total ${total}${taxCents ? ` (sales tax ${taxCents})` : ''}`);
    if (b.replaces_id) {   // "correct this invoice": the old one is voided in the same step, so there is never a half-done correction
      const old = db.prepare('SELECT * FROM documents WHERE id=?').get(vInt(b.replaces_id, 'replaces_id'));
      if (!old || old.kind !== kind) throw bad('The document being replaced could not be found.');
      if (old.status !== 'open' || old.paid_cents > 0) throw bad(`${old.number} can no longer be replaced because it has payments on it or is already voided. Void the payments first, or enter a new ${kind} instead.`);
      voidDocument(db, ctx, old.id);
      db.prepare('UPDATE documents SET memo=? WHERE id=?').run(((`Replaces ${old.number}. ` + (vStr(b.memo, 'memo', { max: 500 }) || '')).trim()).slice(0, 500), docId);
    }
    return docId;
  });
}

const listDocuments = (db, kind, status) => db.prepare(`SELECT d.*, c.name AS contact_name,
  CASE WHEN d.status='open' AND d.due_date < ? THEN 1 ELSE 0 END AS overdue FROM documents d JOIN contacts c ON c.id=d.contact_id
  WHERE d.kind=? AND (? IS NULL OR d.status=?) ORDER BY d.doc_date DESC, d.id DESC LIMIT 20000`).all(today(), kind, status ?? null, status ?? null);

function getDocument(db, id) {
  const d = db.prepare('SELECT d.*, c.name AS contact_name FROM documents d JOIN contacts c ON c.id=d.contact_id WHERE d.id=?').get(id);
  if (!d) throw new HttpError(404, 'That invoice or bill could not be found. It may have been deleted; go back to the list and refresh.');
  d.lines = db.prepare('SELECT l.*, a.name AS account_name FROM document_lines l JOIN accounts a ON a.id=l.account_id WHERE document_id=?').all(id);
  d.tax = db.prepare('SELECT dt.rate_id, r.name, r.agency, dt.rate_milli, dt.taxable_cents, dt.tax_cents FROM document_tax dt JOIN tax_rates r ON r.id=dt.rate_id WHERE dt.document_id=? ORDER BY r.name').all(id);
  d.subtotal_cents = d.total_cents - d.tax_cents;
  d.credits = db.prepare('SELECT a.id, a.amount_cents, a.credit_id, c.number, a.applied_on FROM credit_applications a JOIN credit_memos c ON c.id=a.credit_id WHERE a.document_id=? ORDER BY a.id').all(id);
  d.payments = db.prepare(`SELECT pa.amount_cents, p.id AS payment_id, p.pay_date, p.method, p.ref, p.voided FROM payment_applications pa JOIN payments p ON p.id=pa.payment_id WHERE pa.document_id=?`).all(id);
  return d;
}

function voidDocument(db, ctx, id) {
  const d = db.prepare('SELECT * FROM documents WHERE id=?').get(id);
  if (!d) throw new HttpError(404, 'That invoice or bill could not be found. It may have been deleted; go back to the list and refresh.');
  if (d.status === 'void') throw bad('This document has already been voided, so there is nothing more to do.');
  if (d.paid_cents > 0) throw bad('Payments have been recorded against this document, so it cannot be voided yet. Void those payments first (open each payment), then void the document.');
  tx(db, () => {
    reverseEntry(db, d.entry_id, { date: today() > d.doc_date ? today() : d.doc_date, userId: ctx.user.id, memo: `Void ${d.kind} ${d.number}` });
    db.prepare("UPDATE documents SET status='void' WHERE id=?").run(id);
    audit(db, ctx, `${d.kind}.void`, d.kind, id, d.number);
  });
}

// ---------------- payments ----------------
function createPayment(db, ctx, b) {
  const kind = vEnum(b.kind, 'kind', ['customer', 'vendor']);
  const contactId = vInt(b.contact_id, 'contact_id');
  const contact = db.prepare('SELECT * FROM contacts WHERE id=?').get(contactId);
  if (!contact || contact.kind !== kind) throw bad('The customer or vendor for this payment could not be found. Choose one from the list.');
  const date = vDate(b.pay_date, 'date');
  const amount = vInt(b.amount_cents, 'amount', { min: 1, max: 1e13 });
  const bankId = vInt(b.account_id, 'account');
  const bank = db.prepare('SELECT subtype FROM accounts WHERE id=?').get(bankId);
  if (!bank || !['bank', 'credit_card', 'other_current_asset'].includes(bank.subtype)) throw bad('Choose where the money went: a bank, credit card or cash account. The account chosen is a different kind of account.');
  const apps = vArr(b.applications ?? [], 'applications', { max: 200 }).map((a, i) => ({ document_id: vInt(a.document_id, `application ${i + 1} document`), amount_cents: vInt(a.amount_cents, `application ${i + 1} amount`, { min: 1 }) }));
  if (apps.reduce((s, a) => s + a.amount_cents, 0) > amount) throw bad('You are applying more money to documents than the payment is for. Lower the amounts applied, or raise the payment amount.');
  const control = systemAccount(db, kind === 'customer' ? 'ar' : 'ap');

  return tx(db, () => {
    for (const a of apps) {
      const d = db.prepare('SELECT * FROM documents WHERE id=?').get(a.document_id);
      if (!d || d.contact_id !== contactId || d.kind !== (kind === 'customer' ? 'invoice' : 'bill') || d.status !== 'open') throw bad('This payment is being applied to a document that is not open, belongs to someone else, or is the wrong kind (an invoice for a vendor payment, or the reverse). Choose an open document for the same customer or vendor.');
      if (a.amount_cents > d.total_cents - d.paid_cents) throw bad(`This payment is more than is still owed on ${d.number}. Lower the amount applied to ${d.number}, or apply the rest to another document.`);
    }
    const lines = kind === 'customer'
      ? [{ account_id: bankId, debit_cents: amount }, { account_id: control, contact_id: contactId, credit_cents: amount }]
      : [{ account_id: control, contact_id: contactId, debit_cents: amount }, { account_id: bankId, credit_cents: amount }];
    const entryId = postEntry(db, { date, memo: `${kind === 'customer' ? 'Payment received' : 'Bill payment'} - ${contact.name}`, ref: vStr(b.ref, 'reference', { max: 50 }), source_type: 'payment', user_id: ctx.user.id, lines });
    const pid = Number(db.prepare('INSERT INTO payments(kind,contact_id,pay_date,amount_cents,account_id,method,ref,entry_id,created_by,created_at) VALUES (?,?,?,?,?,?,?,?,?,?)')
      .run(kind, contactId, date, amount, bankId, vStr(b.method, 'method', { max: 30 }), vStr(b.ref, 'reference', { max: 50 }), entryId, ctx.user.id, Date.now()).lastInsertRowid);
    for (const a of apps) {
      db.prepare('INSERT INTO payment_applications(payment_id,document_id,amount_cents) VALUES (?,?,?)').run(pid, a.document_id, a.amount_cents);
      db.prepare("UPDATE documents SET paid_cents=paid_cents+?, status=CASE WHEN paid_cents+?=total_cents THEN 'paid' ELSE status END WHERE id=?").run(a.amount_cents, a.amount_cents, a.document_id);
    }
    audit(db, ctx, 'payment.create', 'payment', pid, `${kind} ${amount}`);
    return pid;
  });
}

function voidPayment(db, ctx, id) {
  const p = db.prepare('SELECT * FROM payments WHERE id=?').get(id);
  if (!p) throw new HttpError(404, 'That payment could not be found. It may have been deleted; refresh the list.');
  if (p.voided) throw bad('This payment has already been voided.');
  tx(db, () => {
    reverseEntry(db, p.entry_id, { date: today() > p.pay_date ? today() : p.pay_date, userId: ctx.user.id, memo: `Void payment #${p.id}` });
    for (const a of db.prepare('SELECT * FROM payment_applications WHERE payment_id=?').all(id)) {
      db.prepare("UPDATE documents SET paid_cents=paid_cents-?, status=CASE WHEN status='paid' THEN 'open' ELSE status END WHERE id=?").run(a.amount_cents, a.document_id);
    }
    db.prepare('UPDATE payments SET voided=1 WHERE id=?').run(id);
    audit(db, ctx, 'payment.void', 'payment', id);
  });
}

// ---------------- manual journal ----------------
function createJournal(db, ctx, b) {
  const lines = vArr(b.lines, 'lines', { min: 2, max: 500 }).map((l, i) => ({
    account_id: vInt(l.account_id, `line ${i + 1} account`),
    contact_id: l.contact_id ? vInt(l.contact_id, 'contact') : null,
    debit_cents: vInt(l.debit_cents ?? 0, `line ${i + 1} debit`, { min: 0, max: 1e13 }),
    credit_cents: vInt(l.credit_cents ?? 0, `line ${i + 1} credit`, { min: 0, max: 1e13 }),
    memo: vStr(l.memo, 'memo', { max: 200 }),
  }));
  return tx(db, () => {
    const id = postEntry(db, { date: vDate(b.date, 'date'), memo: vStr(b.memo, 'memo', { max: 300 }), ref: vStr(b.ref, 'reference', { max: 50 }), source_type: 'manual', user_id: ctx.user.id, lines });
    audit(db, ctx, 'journal.create', 'entry', id, vStr(b.memo, 'memo', { max: 100 }));
    return id;
  });
}
const listJournal = (db, limit = 200) => {
  const es = db.prepare(`SELECT e.*, (SELECT reversal_id FROM entry_reversals WHERE entry_id=e.id) AS reversed_by,
    (SELECT SUM(debit_cents) FROM journal_lines WHERE entry_id=e.id) AS total_cents FROM journal_entries e ORDER BY entry_date DESC, id DESC LIMIT ?`).all(limit);
  return es;
};
const getEntry = (db, id) => {
  const e = db.prepare('SELECT * FROM journal_entries WHERE id=?').get(id);
  if (!e) throw new HttpError(404, 'That journal entry could not be found. It may have been removed; go back and refresh.');
  e.lines = db.prepare('SELECT l.*, a.name AS account_name, c.name AS contact_name FROM journal_lines l JOIN accounts a ON a.id=l.account_id LEFT JOIN contacts c ON c.id=l.contact_id WHERE entry_id=?').all(id);
  return e;
};

// ---------------- banking ----------------
function addBankTransactions(db, accountId, txns, batchId = null) {
  const acc = db.prepare('SELECT subtype FROM accounts WHERE id=?').get(accountId);
  if (!acc || !['bank', 'credit_card'].includes(acc.subtype)) throw bad('Choose a bank or credit card account for these transactions. The account chosen is a different kind of account.');
  const ins = db.prepare('INSERT OR IGNORE INTO bank_transactions(account_id,tx_date,amount_cents,description,fitid,batch_id) VALUES (?,?,?,?,?,?)');
  let added = 0, duplicates = 0;
  tx(db, () => {
    for (const t of txns) {
      const r = ins.run(accountId, t.date, t.amount_cents, String(t.description || '').slice(0, 300), String(t.fitid).slice(0, 100), batchId);
      if (r.changes) added++; else duplicates++;
    }
  });
  return { added, duplicates };
}
const listBankTransactions = (db, status, accountId) => db.prepare(`SELECT t.*, a.name AS account_name FROM bank_transactions t JOIN accounts a ON a.id=t.account_id
  WHERE (? IS NULL OR t.status=?) AND (? IS NULL OR t.account_id=?) ORDER BY t.tx_date DESC, t.id DESC LIMIT 20000`).all(status ?? null, status ?? null, accountId ?? null, accountId ?? null);

function categorizeBank(db, ctx, id, b) {
  const t = db.prepare('SELECT * FROM bank_transactions WHERE id=?').get(id);
  if (!t) throw new HttpError(404, 'That bank transaction could not be found. It may have been removed by an undone import; refresh the list.');
  if (t.status !== 'new') throw bad('This bank transaction has already been categorized or set aside, so it cannot be changed here. Look under Categorized or Excluded.');
  const target = vInt(b.account_id, 'category account');
  const contactId = b.contact_id ? vInt(b.contact_id, 'contact') : null;
  return tx(db, () => {
    const abs = Math.abs(t.amount_cents);
    if (abs === 0) throw bad('This transaction has an amount of zero, so there is nothing to post. Use Exclude to set it aside.');
    const inflow = t.amount_cents > 0;
    const entryId = postEntry(db, {
      date: t.tx_date, memo: t.description, ref: t.fitid, source_type: 'bank', source_id: t.id, user_id: ctx.user.id,
      lines: [
        { account_id: t.account_id, debit_cents: inflow ? abs : 0, credit_cents: inflow ? 0 : abs, contact_id: contactId },
        { account_id: target, debit_cents: inflow ? 0 : abs, credit_cents: inflow ? abs : 0, contact_id: contactId },
      ],
    });
    db.prepare("UPDATE bank_transactions SET status='categorized', entry_id=? WHERE id=?").run(entryId, id);
    audit(db, ctx, 'bank.categorize', 'bank_transaction', id, `-> account ${target}`);
    return entryId;
  });
}
function excludeBank(db, ctx, id) {
  const r = db.prepare("UPDATE bank_transactions SET status='excluded' WHERE id=? AND status='new'").run(id);
  if (!r.changes) throw bad('That transaction could not be found, or it has already been dealt with. Refresh the list.');
  audit(db, ctx, 'bank.exclude', 'bank_transaction', id);
}
function createRule(db, ctx, b) {
  const r = db.prepare('INSERT INTO bank_rules(name,match_text,account_id,contact_id,priority) VALUES (?,?,?,?,?)').run(
    vStr(b.name, 'name', { required: true, max: 100 }), vStr(b.match_text, 'text to match', { required: true, min: 2, max: 100 }).toLowerCase(),
    vInt(b.account_id, 'account'), b.contact_id ? vInt(b.contact_id, 'contact') : null, vInt(b.priority ?? 100, 'priority', { min: 1, max: 1000 }));
  audit(db, ctx, 'rule.create', 'bank_rule', r.lastInsertRowid);
  return Number(r.lastInsertRowid);
}
const listRules = (db) => db.prepare('SELECT r.*, a.name AS account_name FROM bank_rules r JOIN accounts a ON a.id=r.account_id WHERE r.active=1 ORDER BY priority, id').all();
function applyRules(db, ctx) {
  const rules = db.prepare('SELECT * FROM bank_rules WHERE active=1 ORDER BY priority, id').all();
  let applied = 0;
  for (const t of db.prepare("SELECT * FROM bank_transactions WHERE status='new'").all()) {
    const desc = t.description.toLowerCase();
    const rule = rules.find((r) => desc.includes(r.match_text));
    if (rule) {
      try { categorizeBank(db, ctx, t.id, { account_id: rule.account_id, contact_id: rule.contact_id }); applied++; } catch { /* e.g. closed period: leave for manual review */ }
    }
  }
  audit(db, ctx, 'bank.apply_rules', 'bank_rule', '', `${applied} applied`);
  return applied;
}

module.exports = {
  TYPE_FOR_SUBTYPE, listAccounts, createAccount, updateAccount, listContacts, createContact, updateContact, listItems, createItem, updateItem,
  nextNumber, createDocument, listDocuments, getDocument, voidDocument, createPayment, voidPayment, createJournal, listJournal, getEntry,
  addBankTransactions, listBankTransactions, categorizeBank, excludeBank, createRule, listRules, applyRules,
};
