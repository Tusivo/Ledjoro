'use strict';
/**
 * Conversion pipeline:  QuickBooks-family file  ->  parse  ->  canonical model  ->  plan (dry run)  ->  commit (one transaction).
 * Also reads/writes the Ledjoro Interchange Format (OBX, plain JSON) so data is never locked in.
 */
const crypto = require('node:crypto');
const { tx, purgeImportedDocuments } = require('./db');
const { bad, HttpError, vStr, parseMoney, normalizePhone } = require('./util');
const { postEntry, reverseEntry, systemAccount } = require('./ledger');
const { addBankTransactions, TYPE_FOR_SUBTYPE, nextNumber } = require('./domain');
const { audit } = require('./audit');
const { parseIif } = require('./importers/iif');
const { parseOfx } = require('./importers/ofx');
const { importQbxml, importQboJson } = require('./importers/qbxml');
const csv = require('./importers/csv');
const { newModel } = require('./importers/model');

const FORMATS = {
  iif: 'QuickBooks IIF (accounts, customers, vendors, items, transactions)',
  ofx: 'Bank/credit-card statement: OFX / QFX / QBO (QuickBooks Web Connect)',
  qbxml: 'QuickBooks Desktop qbXML (accounts, customers, vendors, items)',
  'qbo-json': 'QuickBooks Online API JSON (accounts, customers, vendors, items, journal entries)',
  'csv-accounts': 'CSV: Chart of accounts export',
  'csv-customers': 'CSV: Customer list export',
  'csv-vendors': 'CSV: Vendor list export',
  'csv-items': 'CSV: Products & services export',
  'csv-trialbalance': 'CSV: Trial balance export (opening balances)',
  'csv-mapped': 'CSV: Any other CSV (you say what each column is)',
  'csv-register': "CSV: Account register export (Date, Payee, Account, Payment, Deposit); needs the register's account name",
  obx: 'Ledjoro Interchange Format (JSON)',
};

function detectFormat(filename, text) {
  const f = String(filename || '').toLowerCase();
  const head = text.slice(0, 4096);
  if (/\.(qbo|qfx|ofx)$/.test(f) || /<OFX>|OFXHEADER:/i.test(head)) return 'ofx';
  if (/\.iif$/.test(f) || /^!(TRNS|ACCNT|CUST|VEND|HDR)/m.test(head)) return 'iif';
  if (/<QBXML|<QBXMLMsgsRs/i.test(head)) return 'qbxml';
  if (/"format"\s*:\s*"(?:ledjoro|openbooks)-interchange"/.test(head)) return 'obx';
  if (/^\s*[{[]/.test(head) && /"(QueryResponse|Account|Customer|Vendor|Item|JournalEntry)"/.test(text.slice(0, 20000))) return 'qbo-json';
  if (/\.csv$/.test(f) || head.includes(',')) {
    const h = head.split(/\r?\n/).slice(0, 8).join('\n').toLowerCase().replace(/[^a-z,\n]/g, '');
    if (/date.*payee.*payment.*deposit/.test(h)) return 'csv-register';
    if (/account.*debit.*credit/.test(h)) return 'csv-trialbalance';
    if (/accounttype|detailtype|,type/.test(h) && /account/.test(h)) return 'csv-accounts';
    if (/vendor/.test(h)) return 'csv-vendors';
    if (/customer/.test(h)) return 'csv-customers';
    if (/productservice|salesprice/.test(h)) return 'csv-items';
  }
  return null;
}

function parse(format, text, opts = {}, limits) {
  if (typeof text !== 'string' || !text.trim()) throw bad('The file is empty (there is no text in it), so there is nothing to import. Check you chose the right file and that it finished exporting or downloading.');
  switch (format) {
    case 'iif': return parseIif(text, limits, opts);
    case 'ofx': return parseOfx(text, limits);
    case 'qbxml': return importQbxml(text);
    case 'qbo-json': return importQboJson(text);
    case 'csv-accounts': return csv.importAccountsCsv(text, limits);
    case 'csv-customers': return csv.importContactsCsv(text, 'customer', limits);
    case 'csv-vendors': return csv.importContactsCsv(text, 'vendor', limits);
    case 'csv-items': return csv.importItemsCsv(text, limits);
    case 'csv-trialbalance': return csv.importTrialBalanceCsv(text, opts.asof, limits);
    case 'csv-register': return csv.importRegisterCsv(text, opts.register, opts, limits);
    case 'csv-mapped': return csv.importMappedCsv(text, opts, limits);
    case 'obx': return parseObx(text);
    default: throw bad('Ledjoro does not know how to read that kind of file. Choose the format yourself in the Format list, or export from QuickBooks as IIF, CSV, QBO/OFX or JSON.');
  }
}

// ---------- Ledjoro Interchange Format ----------
function parseObx(text) {
  let j;
  try { j = JSON.parse(text); } catch { throw bad('That file should be Ledjoro JSON but could not be read as JSON: it has a typing or formatting mistake, or it was cut off. Export it again.'); }
  if (!['ledjoro-interchange', 'openbooks-interchange'].includes(j?.format) || j.version !== 1) throw bad('That JSON file was not made by Ledjoro (or comes from a newer version), so Ledjoro cannot be sure what it means. Use a file saved with "Everything (Ledjoro JSON)".');
  const m = newModel('obx');
  for (const k of ['accounts', 'contacts', 'items', 'journal_entries', 'bank_transactions']) m[k] = Array.isArray(j[k]) ? j[k] : [];
  m.bank_meta = j.bank_meta ?? null;
  return m;
}
function toObx(model) {
  const { warnings, errors, notes, ...rest } = model; // eslint-disable-line no-unused-vars
  return JSON.stringify({ ...rest, exported_at: new Date().toISOString() }, null, 2);
}

// ---------- account reference resolution ----------
function buildResolver(db, extraAccounts = []) {
  const byName = new Map(), byNumber = new Map();
  for (const a of db.prepare('SELECT id, number, name FROM accounts').all()) { byName.set(a.name.toLowerCase(), a.id ?? true); if (a.number) byNumber.set(a.number, a.id ?? true); }
  for (const a of extraAccounts) { if (!byName.has(a.name.toLowerCase())) byName.set(a.name.toLowerCase(), null); if (a.number && !byNumber.has(a.number)) byNumber.set(a.number, null); }
  return (ref) => {
    const r = String(ref ?? '').trim();
    if (!r) return undefined;
    if (byName.has(r.toLowerCase())) return byName.get(r.toLowerCase());
    if (byNumber.has(r)) return byNumber.get(r);
    const m = r.match(/^(\S+)\s+(.+)$/); // "1000 Checking"
    if (m && byNumber.has(m[1])) return byNumber.get(m[1]);
    if (m && byName.has(m[2].toLowerCase())) return byName.get(m[2].toLowerCase());
    return undefined;
  };
}

/** Read-only dry run: what would happen, and what is wrong. */
function plan(db, model) {
  const errors = [...model.errors];
  const resolve = buildResolver(db, model.accounts);
  const existing = (sql, ...p) => db.prepare(sql).get(...p);
  const summary = {
    accounts: { new: 0, existing: 0 }, contacts: { new: 0, existing: 0 }, items: { new: 0, existing: 0 },
    journal_entries: { new: 0, duplicate: 0 }, documents: { new: 0, duplicate: 0 }, bank_history: (model.bank_history ?? []).length, bank_transactions: { count: model.bank_transactions.length },
  };
  for (const a of model.accounts) existing('SELECT 1 FROM accounts WHERE name=? OR (number<>\'\' AND number=?)', a.name, a.number || '\u0000') ? summary.accounts.existing++ : summary.accounts.new++;
  for (const c of model.contacts) existing('SELECT 1 FROM contacts WHERE kind=? AND name=?', c.kind, c.name) ? summary.contacts.existing++ : summary.contacts.new++;
  for (const i of model.items) existing('SELECT 1 FROM items WHERE name=?', i.name) ? summary.items.existing++ : summary.items.new++;
  let toPost = 0;
  const missing = new Set();
  for (const [i, e] of model.journal_entries.entries()) {
    const bad1 = e.lines.find((l) => resolve(l.account) === undefined);
    if (bad1) { missing.add(bad1.account); errors.push(`Entry ${e.ref || i + 1} (${e.date}): account "${bad1.account}" does not exist`); continue; }
    const h = entryHash(model, e);
    activeDuplicate(db, h) ? summary.journal_entries.duplicate++ : (summary.journal_entries.new++, toPost++);
  }
  for (const [i, d] of (model.documents ?? []).entries()) {
    const bad1 = [d.line.account, d.pay.account].find((a) => resolve(a) === undefined);
    if (bad1) { missing.add(bad1); errors.push(`${d.kind === 'bill' ? 'Bill' : 'Invoice'} ${i + 1} (${d.date}): account "${bad1}" does not exist`); continue; }
    activeDuplicate(db, entryHash(model, { external_id: d.external_id })) ? summary.documents.duplicate++ : summary.documents.new++;
  }
  const warnings = [...model.warnings];
  const oddPhones = model.contacts.filter((c) => c.phone && !normalizePhone(c.phone)).length;
  const notes = [...model.notes];
  if (oddPhones) notes.push(`${oddPhones} contact(s) have phone numbers that are not 10 digits; they will be kept as written (the format xxx-xxx-xxxx is only applied to valid numbers).`);
  if (missing.size) warnings.push(`Missing accounts referenced by transactions: ${[...missing].slice(0, 20).join(', ')}. Import the chart of accounts first.`);
  return { summary, warnings, notes, errors: errors.slice(0, 200), errorCount: errors.length, canCommit: errors.length === 0, bank_meta: model.bank_meta, sample: { journal_entries: model.journal_entries.slice(0, 5) } };
}

/** Is there a live (not reversed-by-undo) entry with this content hash? Variants "#2", "#3"... exist after an undo + re-import. */
function activeDuplicate(db, h) {
  for (let n = 1; n <= 50; n++) {
    const r = db.prepare('SELECT id FROM journal_entries WHERE import_hash=?').get(n === 1 ? h : `${h}#${n}`);
    if (!r) return false;
    if (!db.prepare('SELECT 1 FROM entry_reversals WHERE entry_id=?').get(r.id)) return true;
  }
  return true;
}
/** First unused hash variant (import_hash is UNIQUE, so a re-import after an undo gets "#2"). */
function freeHash(db, h) {
  for (let n = 1; n <= 50; n++) { const v = n === 1 ? h : `${h}#${n}`; if (!db.prepare('SELECT 1 FROM journal_entries WHERE import_hash=?').get(v)) return v; }
  throw bad('This same file has been imported and undone more than 50 times, which Ledjoro stops as a safety measure. Import an edited copy of the file, or ask for help.');
}
const entryHash = (model, e) => crypto.createHash('sha256').update(`${model.source.format}|${e.external_id || JSON.stringify([e.date, e.ref, e.lines])}`).digest('hex');

/** Apply the model to the database atomically. Existing records are matched by name/number and never overwritten. */
function commit(db, ctx, model, opts = {}) {
  const p = plan(db, model);
  if (!p.canCommit && !opts.allowSkip) throw bad('Some records in this file have problems (listed below). Fix them in the file and import again, or tick "Import the valid records and skip the ones with problems".', { errors: p.errors });
  return tx(db, () => {
    const entryOf = new Map();
    const out = { accounts: 0, contacts: 0, items: 0, journal_entries: 0, skipped_duplicates: 0, skipped_invalid: 0, bank_added: 0, bank_duplicates: 0 };
    const insAcc = db.prepare('INSERT INTO accounts(number,name,type,subtype,description,created_at) VALUES (?,?,?,?,?,?)');
    for (const a of model.accounts) {
      if (db.prepare("SELECT 1 FROM accounts WHERE name=? OR (number<>'' AND number=?)").get(a.name, a.number || '\u0000')) continue;
      const name = vStr(a.name, 'account name', { required: true, max: 120 });
      insAcc.run(a.number ? vStr(a.number, 'number', { max: 20 }) : null, name, TYPE_FOR_SUBTYPE[a.subtype], a.subtype, vStr(a.description, 'description', { max: 300 }), Date.now());
      out.accounts++;
    }
    const insC = db.prepare('INSERT OR IGNORE INTO contacts(kind,name,email,phone,address,terms_days,is_1099,created_at) VALUES (?,?,?,?,?,?,?,?)');
    for (const c of model.contacts) {
      const r = insC.run(c.kind, vStr(c.name, 'name', { required: true, max: 150 }), vStr(c.email, 'email', { max: 200 }), (normalizePhone(c.phone) ?? vStr(c.phone, 'phone', { max: 50 })), vStr(c.address, 'address', { max: 500 }), Number.isInteger(c.terms_days) ? c.terms_days : 30, c.is_1099 ? 1 : 0, Date.now());
      out.contacts += r.changes;
    }
    const resolve = buildResolver(db);
    const insI = db.prepare('INSERT OR IGNORE INTO items(name,kind,description,unit_price_cents,income_account_id,expense_account_id) VALUES (?,?,?,?,?,?)');
    for (const i of model.items) {
      const r = insI.run(vStr(i.name, 'name', { required: true, max: 150 }), ['service', 'product', 'nonstock'].includes(i.kind) ? i.kind : 'service', vStr(i.description, 'description', { max: 300 }), Number.isSafeInteger(i.unit_price_cents) && i.unit_price_cents >= 0 ? i.unit_price_cents : 0, resolve(i.income_account) ?? null, resolve(i.expense_account) ?? null);
      out.items += r.changes;
    }
    const contactFor = (name, accSubtype) => {
      if (!name) return null;
      const kind = accSubtype === 'ar' ? 'customer' : accSubtype === 'ap' ? 'vendor' : null;
      if (!kind) return null;
      db.prepare('INSERT OR IGNORE INTO contacts(kind,name,created_at) VALUES (?,?,?)').run(kind, name.slice(0, 150), Date.now());
      return db.prepare('SELECT id FROM contacts WHERE kind=? AND name=?').get(kind, name.slice(0, 150))?.id ?? null;
    };
    const batchId = Number(db.prepare('INSERT INTO import_batches(format,filename,created_by,created_at) VALUES (?,?,?,?)').run(model.source.format, vStr(opts.filename, 'filename', { max: 200 }), ctx.user.id, Date.now()).lastInsertRowid);
    for (const [i, e] of model.journal_entries.entries()) {
      const ids = e.lines.map((l) => resolve(l.account));
      if (ids.some((x) => x === undefined)) { out.skipped_invalid++; continue; }
      const hash = entryHash(model, e);
      if (activeDuplicate(db, hash)) { out.skipped_duplicates++; continue; }
      const subtypes = ids.map((id) => db.prepare('SELECT subtype FROM accounts WHERE id=?').get(id).subtype);
      const posted = postEntry(db, {
        date: e.date, memo: vStr(e.memo, 'memo', { max: 300 }), ref: vStr(e.ref, 'ref', { max: 50 }), source_type: 'import', source_id: batchId, import_hash: freeHash(db, hash), user_id: ctx.user.id,
        lines: e.lines.map((l, k) => ({ account_id: ids[k], debit_cents: l.debit_cents || 0, credit_cents: l.credit_cents || 0, memo: vStr(l.memo, 'memo', { max: 200 }), contact_id: contactFor(l.contact, subtypes[k]) })),
      });
      if (e.external_id) entryOf.set(e.external_id, posted);
      out.journal_entries++;
      void i;
    }
    // bills / invoices made from register rows: the document, its entry, the payment and its entry, all tagged to this batch (so Undo can reverse them)
    out.documents = 0;
    for (const d of model.documents ?? []) {
      const accId = resolve(d.line.account), bankId = resolve(d.pay.account);
      if (!accId || !bankId) { out.skipped_invalid++; continue; }
      const hash = entryHash(model, { external_id: d.external_id });
      if (activeDuplicate(db, hash)) { out.skipped_duplicates++; continue; }
      const isInv = d.kind === 'invoice', ckind = isInv ? 'customer' : 'vendor', cname = d.contact.slice(0, 150);
      db.prepare('INSERT OR IGNORE INTO contacts(kind,name,created_at) VALUES (?,?,?)').run(ckind, cname, Date.now());
      const contactId = db.prepare('SELECT id FROM contacts WHERE kind=? AND name=?').get(ckind, cname).id;
      const control = systemAccount(db, isInv ? 'ar' : 'ap'), total = d.line.amount_cents, number = nextNumber(db, d.kind);
      const common = { source_type: 'import', source_id: batchId, user_id: ctx.user.id };
      const docEntry = postEntry(db, { ...common, date: d.date, memo: `${isInv ? 'Invoice' : 'Bill'} ${number}`, ref: number, import_hash: freeHash(db, hash),
        lines: isInv ? [{ account_id: control, contact_id: contactId, debit_cents: total }, { account_id: accId, contact_id: contactId, credit_cents: total, memo: vStr(d.line.description, 'memo', { max: 200 }) }]
          : [{ account_id: accId, contact_id: contactId, debit_cents: total, memo: vStr(d.line.description, 'memo', { max: 200 }) }, { account_id: control, contact_id: contactId, credit_cents: total }] });
      const docId = Number(db.prepare('INSERT INTO documents(kind,number,contact_id,doc_date,due_date,memo,total_cents,paid_cents,status,entry_id,created_by,created_at) VALUES (?,?,?,?,?,?,?,?,?,?,?,?)')
        .run(d.kind, number, contactId, d.date, d.date, vStr(d.memo, 'memo', { max: 500 }), total, total, 'paid', docEntry, ctx.user.id, Date.now()).lastInsertRowid);
      db.prepare('INSERT INTO document_lines(document_id,item_id,description,qty_milli,unit_price_cents,amount_cents,account_id) VALUES (?,?,?,?,?,?,?)').run(docId, null, vStr(d.line.description, 'description', { max: 300 }), 1000, total, total, accId);
      const payEntry = postEntry(db, { ...common, date: d.date, memo: `${isInv ? 'Payment received' : 'Bill payment'} - ${cname}`, ref: vStr(d.pay.ref, 'reference', { max: 50 }), import_hash: freeHash(db, hash + '|pay'),
        lines: isInv ? [{ account_id: bankId, debit_cents: total }, { account_id: control, contact_id: contactId, credit_cents: total }] : [{ account_id: control, contact_id: contactId, debit_cents: total }, { account_id: bankId, credit_cents: total }] });
      const pid = Number(db.prepare('INSERT INTO payments(kind,contact_id,pay_date,amount_cents,account_id,method,ref,entry_id,created_by,created_at) VALUES (?,?,?,?,?,?,?,?,?,?)')
        .run(ckind, contactId, d.date, total, bankId, '', vStr(d.pay.ref, 'reference', { max: 50 }), payEntry, ctx.user.id, Date.now()).lastInsertRowid);
      db.prepare('INSERT INTO payment_applications(payment_id,document_id,amount_cents) VALUES (?,?,?)').run(pid, docId, total);
      entryOf.set(d.external_id, payEntry);
      out.documents++;
    }
    out.bank_history = 0;
    const insHist = db.prepare("INSERT OR IGNORE INTO bank_transactions(account_id,tx_date,amount_cents,description,fitid,status,entry_id,batch_id) VALUES (?,?,?,?,?,'categorized',?,?)");
    for (const h of model.bank_history ?? []) {
      const entryId = entryOf.get(h.external_id), acct = resolve(h.account);
      if (!entryId || !acct) continue;
      out.bank_history += insHist.run(acct, h.date, h.amount_cents, String(h.description).slice(0, 300), h.fitid, entryId, batchId).changes;
    }
    if (model.bank_transactions.length) {
      if (!opts.bank_account_id) throw bad('These lines come from a bank or card statement, so Ledjoro needs to know which account they belong to. Choose the account in the box "Import these transactions into".');
      const r = addBankTransactions(db, opts.bank_account_id, model.bank_transactions, batchId);
      out.bank_added = r.added; out.bank_duplicates = r.duplicates;
    }
    db.prepare('UPDATE import_batches SET summary=? WHERE id=?').run(JSON.stringify(out), batchId);
    audit(db, ctx, 'import.commit', 'import_batch', batchId, `${model.source.format} ${JSON.stringify(out)}`);
    return { batchId, ...out };
  });
}

// ---------- batches: history and undo ----------
function listBatches(db) {
  return db.prepare('SELECT id, format, filename, created_at, summary FROM import_batches ORDER BY id DESC LIMIT 50').all().map((b) => {
    const live = db.prepare(`SELECT COUNT(*) n FROM journal_entries e WHERE source_type='import' AND source_id=? AND NOT EXISTS (SELECT 1 FROM entry_reversals r WHERE r.entry_id=e.id)`).get(b.id).n;
    const summary = JSON.parse(b.summary || '{}');
    return { id: b.id, format: b.format, filename: b.filename, created_at: b.created_at, summary, live_entries: live, undone: !!summary.undone };
  });
}

/**
 * Undo an import without rewriting history: every journal entry it created is reversed (same dates, so every period nets to zero),
 * and bank transactions it brought in that were never categorised are removed so the statement can be imported again.
 * Accounts, contacts and items it created are left in place (they may already be in use).
 */
function undoBatch(db, ctx, batchId) {
  const b = db.prepare('SELECT * FROM import_batches WHERE id=?').get(batchId);
  if (!b) throw new HttpError(404, 'That import could not be found. Refresh the Recent imports list.');
  const summary = JSON.parse(b.summary || '{}');
  if (summary.undone) throw bad('This import has already been undone.');
  return tx(db, () => {
    const entries = db.prepare(`SELECT id, entry_date FROM journal_entries e WHERE source_type='import' AND source_id=?
      AND NOT EXISTS (SELECT 1 FROM entry_reversals r WHERE r.entry_id=e.id OR r.reversal_id=e.id) ORDER BY id`).all(batchId);
    for (const e of entries) reverseEntry(db, e.id, { date: e.entry_date, userId: ctx.user.id, memo: `Undo import #${batchId} (${b.filename || b.format})` });
    const mine = "SELECT id FROM journal_entries WHERE source_type='import' AND source_id=?";
    purgeImportedDocuments(db, [batchId]);   // the bills / invoices this import created go away; the journal keeps the entries and their reversals
    db.prepare(`DELETE FROM bank_transactions WHERE batch_id=? AND status='categorized' AND entry_id IN (${mine})`).run(batchId, batchId); // history lines this import wrote
    const removed = db.prepare("DELETE FROM bank_transactions WHERE batch_id=? AND status='new'").run(batchId).changes;
    const kept = db.prepare('SELECT COUNT(*) n FROM bank_transactions WHERE batch_id=?').get(batchId).n;
    db.prepare('UPDATE import_batches SET summary=? WHERE id=?').run(JSON.stringify({ ...summary, undone: Date.now() }), batchId);
    audit(db, ctx, 'import.undo', 'import_batch', batchId, `${entries.length} entries reversed, ${removed} bank rows removed`);
    return { reversed: entries.length, bank_removed: removed, bank_kept: kept };
  });
}

// ---------- export to OBX ----------
function exportObx(db) {
  const m = newModel('ledjoro');
  m.accounts = db.prepare('SELECT number,name,subtype,description FROM accounts WHERE active=1 ORDER BY id').all().map((a) => ({ ...a, number: a.number || '' }));
  m.contacts = db.prepare('SELECT kind,name,email,phone,address,terms_days,is_1099 FROM contacts ORDER BY id').all();
  m.items = db.prepare('SELECT i.name,i.kind,i.description,i.unit_price_cents, ia.name AS income_account, ea.name AS expense_account FROM items i LEFT JOIN accounts ia ON ia.id=i.income_account_id LEFT JOIN accounts ea ON ea.id=i.expense_account_id').all();
  const lines = db.prepare('SELECT l.entry_id, a.name AS account, l.debit_cents, l.credit_cents, l.memo, c.name AS contact FROM journal_lines l JOIN accounts a ON a.id=l.account_id LEFT JOIN contacts c ON c.id=l.contact_id ORDER BY l.id').all();
  const byEntry = new Map();
  for (const l of lines) { const { entry_id, ...rest } = l; (byEntry.get(entry_id) ?? byEntry.set(entry_id, []).get(entry_id)).push({ ...rest, contact: rest.contact || '' }); }
  m.journal_entries = db.prepare('SELECT id,entry_date AS date,ref,memo FROM journal_entries ORDER BY entry_date,id').all().map((e) => ({ date: e.date, ref: e.ref, memo: e.memo, external_id: `ob-${e.id}`, lines: byEntry.get(e.id) || [] }));
  return toObx(m);
}

const sniff = (text, limits) => csv.sniffCsv(text, limits);
module.exports = { sniff, FORMATS, detectFormat, parse, plan, commit, toObx, exportObx, parseObx, parseMoney, listBatches, undoBatch };
