'use strict';
const fs = require('node:fs');
const path = require('node:path');
const { DatabaseSync } = require('node:sqlite');

const ACCOUNT_TYPES = ['asset', 'liability', 'equity', 'income', 'cogs', 'expense', 'other_income', 'other_expense'];
const SUBTYPES = [
  'bank', 'ar', 'other_current_asset', 'fixed_asset', 'other_asset',
  'ap', 'credit_card', 'other_current_liability', 'long_term_liability',
  'equity', 'retained_earnings', 'income', 'other_income', 'cogs', 'expense', 'other_expense',
];
const PERMISSIONS = [
  'view', 'sales.write', 'purchases.write', 'banking.write', 'accounting.write',
  'import', 'export', 'users.manage', 'settings.manage', 'audit.view',
];
const BUILTIN_ROLES = {
  Admin: PERMISSIONS,
  Accountant: PERMISSIONS.filter((p) => p !== 'users.manage'),
  Bookkeeper: ['view', 'sales.write', 'purchases.write', 'banking.write', 'import'],
  'View only': ['view'],
};

const SCHEMA = `
CREATE TABLE settings(key TEXT PRIMARY KEY, value TEXT NOT NULL);
CREATE TABLE roles(id INTEGER PRIMARY KEY, name TEXT UNIQUE NOT NULL COLLATE NOCASE, permissions TEXT NOT NULL, builtin INTEGER NOT NULL DEFAULT 0);
CREATE TABLE users(
  id INTEGER PRIMARY KEY, username TEXT UNIQUE NOT NULL COLLATE NOCASE, display_name TEXT NOT NULL,
  password_hash TEXT NOT NULL, role_id INTEGER NOT NULL REFERENCES roles(id),
  active INTEGER NOT NULL DEFAULT 1, failed_attempts INTEGER NOT NULL DEFAULT 0, locked_until INTEGER NOT NULL DEFAULT 0,
  must_change_password INTEGER NOT NULL DEFAULT 0, created_at INTEGER NOT NULL);
CREATE TABLE sessions(
  id TEXT PRIMARY KEY, user_id INTEGER NOT NULL REFERENCES users(id) ON DELETE CASCADE, csrf TEXT NOT NULL,
  created_at INTEGER NOT NULL, last_seen INTEGER NOT NULL, expires_at INTEGER NOT NULL, ip TEXT);
CREATE TABLE accounts(
  id INTEGER PRIMARY KEY, number TEXT, name TEXT NOT NULL UNIQUE COLLATE NOCASE,
  type TEXT NOT NULL CHECK(type IN (${ACCOUNT_TYPES.map((t) => `'${t}'`).join(',')})),
  subtype TEXT NOT NULL CHECK(subtype IN (${SUBTYPES.map((t) => `'${t}'`).join(',')})),
  description TEXT NOT NULL DEFAULT '', active INTEGER NOT NULL DEFAULT 1, system INTEGER NOT NULL DEFAULT 0, created_at INTEGER NOT NULL);
CREATE UNIQUE INDEX accounts_number ON accounts(number) WHERE number IS NOT NULL AND number <> '';
CREATE TABLE contacts(
  id INTEGER PRIMARY KEY, kind TEXT NOT NULL CHECK(kind IN ('customer','vendor')), name TEXT NOT NULL COLLATE NOCASE,
  email TEXT NOT NULL DEFAULT '', phone TEXT NOT NULL DEFAULT '', address TEXT NOT NULL DEFAULT '',
  terms_days INTEGER NOT NULL DEFAULT 30, is_1099 INTEGER NOT NULL DEFAULT 0, active INTEGER NOT NULL DEFAULT 1,
  created_at INTEGER NOT NULL, UNIQUE(kind, name));
CREATE TABLE items(
  id INTEGER PRIMARY KEY, name TEXT NOT NULL UNIQUE COLLATE NOCASE, kind TEXT NOT NULL CHECK(kind IN ('service','product','nonstock')),
  description TEXT NOT NULL DEFAULT '', unit_price_cents INTEGER NOT NULL DEFAULT 0,
  income_account_id INTEGER REFERENCES accounts(id), expense_account_id INTEGER REFERENCES accounts(id), active INTEGER NOT NULL DEFAULT 1);
CREATE TABLE journal_entries(
  id INTEGER PRIMARY KEY, entry_date TEXT NOT NULL, memo TEXT NOT NULL DEFAULT '', ref TEXT NOT NULL DEFAULT '',
  source_type TEXT NOT NULL, source_id INTEGER, import_hash TEXT UNIQUE, created_by INTEGER REFERENCES users(id), created_at INTEGER NOT NULL);
CREATE TABLE journal_lines(
  id INTEGER PRIMARY KEY, entry_id INTEGER NOT NULL REFERENCES journal_entries(id), account_id INTEGER NOT NULL REFERENCES accounts(id),
  contact_id INTEGER REFERENCES contacts(id), debit_cents INTEGER NOT NULL DEFAULT 0, credit_cents INTEGER NOT NULL DEFAULT 0, memo TEXT NOT NULL DEFAULT '',
  CHECK(debit_cents >= 0 AND credit_cents >= 0 AND (debit_cents = 0 OR credit_cents = 0) AND (debit_cents + credit_cents) > 0));
CREATE INDEX jl_account ON journal_lines(account_id);
CREATE INDEX jl_entry ON journal_lines(entry_id);
CREATE INDEX jl_contact ON journal_lines(contact_id);
CREATE INDEX je_date ON journal_entries(entry_date);
CREATE TABLE entry_reversals(entry_id INTEGER PRIMARY KEY REFERENCES journal_entries(id), reversal_id INTEGER UNIQUE NOT NULL REFERENCES journal_entries(id));
CREATE TABLE documents(
  id INTEGER PRIMARY KEY, kind TEXT NOT NULL CHECK(kind IN ('invoice','bill')), number TEXT NOT NULL,
  contact_id INTEGER NOT NULL REFERENCES contacts(id), doc_date TEXT NOT NULL, due_date TEXT NOT NULL, memo TEXT NOT NULL DEFAULT '',
  total_cents INTEGER NOT NULL CHECK(total_cents >= 0), paid_cents INTEGER NOT NULL DEFAULT 0 CHECK(paid_cents >= 0 AND paid_cents <= total_cents),
  status TEXT NOT NULL CHECK(status IN ('open','paid','void')), entry_id INTEGER REFERENCES journal_entries(id),
  created_by INTEGER REFERENCES users(id), created_at INTEGER NOT NULL, UNIQUE(kind, number));
CREATE TABLE document_lines(
  id INTEGER PRIMARY KEY, document_id INTEGER NOT NULL REFERENCES documents(id) ON DELETE CASCADE, item_id INTEGER REFERENCES items(id),
  description TEXT NOT NULL DEFAULT '', qty_milli INTEGER NOT NULL, unit_price_cents INTEGER NOT NULL, amount_cents INTEGER NOT NULL,
  account_id INTEGER NOT NULL REFERENCES accounts(id));
CREATE TABLE payments(
  id INTEGER PRIMARY KEY, kind TEXT NOT NULL CHECK(kind IN ('customer','vendor')), contact_id INTEGER NOT NULL REFERENCES contacts(id),
  pay_date TEXT NOT NULL, amount_cents INTEGER NOT NULL CHECK(amount_cents > 0), account_id INTEGER NOT NULL REFERENCES accounts(id),
  method TEXT NOT NULL DEFAULT '', ref TEXT NOT NULL DEFAULT '', entry_id INTEGER REFERENCES journal_entries(id),
  voided INTEGER NOT NULL DEFAULT 0, created_by INTEGER REFERENCES users(id), created_at INTEGER NOT NULL);
CREATE TABLE payment_applications(
  id INTEGER PRIMARY KEY, payment_id INTEGER NOT NULL REFERENCES payments(id), document_id INTEGER NOT NULL REFERENCES documents(id),
  amount_cents INTEGER NOT NULL CHECK(amount_cents > 0));
CREATE TABLE bank_transactions(
  id INTEGER PRIMARY KEY, account_id INTEGER NOT NULL REFERENCES accounts(id), tx_date TEXT NOT NULL, amount_cents INTEGER NOT NULL,
  description TEXT NOT NULL DEFAULT '', fitid TEXT NOT NULL, status TEXT NOT NULL DEFAULT 'new' CHECK(status IN ('new','categorized','excluded')),
  entry_id INTEGER REFERENCES journal_entries(id), batch_id INTEGER, UNIQUE(account_id, fitid));
CREATE TABLE bank_rules(
  id INTEGER PRIMARY KEY, name TEXT NOT NULL, match_text TEXT NOT NULL, account_id INTEGER NOT NULL REFERENCES accounts(id),
  contact_id INTEGER REFERENCES contacts(id), priority INTEGER NOT NULL DEFAULT 100, active INTEGER NOT NULL DEFAULT 1);
CREATE TABLE import_batches(id INTEGER PRIMARY KEY, format TEXT NOT NULL, filename TEXT NOT NULL DEFAULT '', created_by INTEGER, created_at INTEGER NOT NULL, summary TEXT NOT NULL DEFAULT '{}');
CREATE TABLE audit_log(
  id INTEGER PRIMARY KEY, ts INTEGER NOT NULL, user_id INTEGER, username TEXT NOT NULL DEFAULT '', action TEXT NOT NULL,
  entity TEXT NOT NULL DEFAULT '', entity_id TEXT NOT NULL DEFAULT '', detail TEXT NOT NULL DEFAULT '', ip TEXT NOT NULL DEFAULT '',
  prev_hash TEXT NOT NULL, hash TEXT NOT NULL);

-- Append-only guarantees enforced by the database itself (defence in depth; code also never issues these).
CREATE TRIGGER audit_no_update BEFORE UPDATE ON audit_log BEGIN SELECT RAISE(ABORT, 'audit_log is append-only'); END;
CREATE TRIGGER audit_no_delete BEFORE DELETE ON audit_log BEGIN SELECT RAISE(ABORT, 'audit_log is append-only'); END;
CREATE TRIGGER je_no_update BEFORE UPDATE ON journal_entries BEGIN SELECT RAISE(ABORT, 'posted entries are immutable; post a reversal'); END;
CREATE TRIGGER je_no_delete BEFORE DELETE ON journal_entries BEGIN SELECT RAISE(ABORT, 'posted entries are immutable; post a reversal'); END;
CREATE TRIGGER jl_no_update BEFORE UPDATE ON journal_lines BEGIN SELECT RAISE(ABORT, 'posted entries are immutable; post a reversal'); END;
CREATE TRIGGER jl_no_delete BEFORE DELETE ON journal_lines BEGIN SELECT RAISE(ABORT, 'posted entries are immutable; post a reversal'); END;
`;

const DEFAULT_ACCOUNTS = [
  ['1000', 'Checking', 'asset', 'bank'], ['1010', 'Savings', 'asset', 'bank'],
  ['1100', 'Accounts Receivable', 'asset', 'ar', 1], ['1300', 'Prepaid Expenses', 'asset', 'other_current_asset'],
  ['1500', 'Equipment', 'asset', 'fixed_asset'], ['1590', 'Accumulated Depreciation', 'asset', 'fixed_asset'],
  ['2000', 'Accounts Payable', 'liability', 'ap', 1], ['2100', 'Credit Card', 'liability', 'credit_card'],
  ['2200', 'Sales Tax Payable', 'liability', 'other_current_liability'], ['2300', 'Payroll Liabilities', 'liability', 'other_current_liability'],
  ['2500', 'Loans Payable', 'liability', 'long_term_liability'],
  ['3000', "Owner's Equity", 'equity', 'equity'], ['3100', 'Opening Balance Equity', 'equity', 'equity', 1],
  ['3900', 'Retained Earnings', 'equity', 'retained_earnings', 1],
  ['4000', 'Sales', 'income', 'income'], ['4100', 'Service Revenue', 'income', 'income'], ['4900', 'Other Income', 'other_income', 'other_income'],
  ['5000', 'Cost of Goods Sold', 'cogs', 'cogs'],
  ['6000', 'Advertising', 'expense', 'expense'], ['6010', 'Bank Fees', 'expense', 'expense'], ['6020', 'Insurance', 'expense', 'expense'],
  ['6030', 'Meals', 'expense', 'expense'], ['6040', 'Office Supplies', 'expense', 'expense'], ['6050', 'Professional Fees', 'expense', 'expense'],
  ['6060', 'Rent', 'expense', 'expense'], ['6070', 'Repairs & Maintenance', 'expense', 'expense'], ['6080', 'Software & Subscriptions', 'expense', 'expense'],
  ['6090', 'Telephone & Internet', 'expense', 'expense'], ['6100', 'Travel', 'expense', 'expense'], ['6110', 'Utilities', 'expense', 'expense'],
  ['6200', 'Wages', 'expense', 'expense'], ['7000', 'Interest Expense', 'other_expense', 'other_expense'],
];

const SCHEMA_VERSION = 9;
const MIGRATION_2 = `
CREATE TABLE fixed_assets(
  id INTEGER PRIMARY KEY, name TEXT NOT NULL, cost_cents INTEGER NOT NULL CHECK(cost_cents > 0), salvage_cents INTEGER NOT NULL DEFAULT 0 CHECK(salvage_cents >= 0 AND salvage_cents < cost_cents),
  in_service TEXT NOT NULL, life_months INTEGER NOT NULL CHECK(life_months BETWEEN 1 AND 600),
  asset_account_id INTEGER NOT NULL REFERENCES accounts(id), accum_account_id INTEGER NOT NULL REFERENCES accounts(id), expense_account_id INTEGER NOT NULL REFERENCES accounts(id),
  posted_through TEXT NOT NULL DEFAULT '', disposed_on TEXT NOT NULL DEFAULT '', notes TEXT NOT NULL DEFAULT '', created_at INTEGER NOT NULL);
`;

// v8: sales tax. Rates (and groups of rates, such as state + county), tax recorded on each invoice (per jurisdiction, so a return can be
// worked out later), tax-exempt customers, taxable items and lines, and payments made to the tax agencies.
const MIGRATION_8_TABLES = `
CREATE TABLE IF NOT EXISTS tax_rates(
  id INTEGER PRIMARY KEY, name TEXT NOT NULL UNIQUE COLLATE NOCASE, kind TEXT NOT NULL DEFAULT 'rate' CHECK(kind IN ('rate','group')),
  rate_milli INTEGER NOT NULL DEFAULT 0 CHECK(rate_milli BETWEEN 0 AND 100000),
  agency TEXT NOT NULL DEFAULT '', account_id INTEGER REFERENCES accounts(id), active INTEGER NOT NULL DEFAULT 1, created_at INTEGER NOT NULL);
CREATE TABLE IF NOT EXISTS tax_group_parts(
  group_id INTEGER NOT NULL REFERENCES tax_rates(id), rate_id INTEGER NOT NULL REFERENCES tax_rates(id), PRIMARY KEY(group_id, rate_id));
CREATE TABLE IF NOT EXISTS document_tax(
  id INTEGER PRIMARY KEY, document_id INTEGER NOT NULL REFERENCES documents(id) ON DELETE CASCADE, rate_id INTEGER NOT NULL REFERENCES tax_rates(id),
  rate_milli INTEGER NOT NULL, taxable_cents INTEGER NOT NULL, tax_cents INTEGER NOT NULL);
CREATE INDEX IF NOT EXISTS dt_doc ON document_tax(document_id);
CREATE TABLE IF NOT EXISTS tax_remittances(
  id INTEGER PRIMARY KEY, agency TEXT NOT NULL DEFAULT '', period_from TEXT NOT NULL DEFAULT '', period_to TEXT NOT NULL DEFAULT '', pay_date TEXT NOT NULL,
  amount_cents INTEGER NOT NULL CHECK(amount_cents > 0), bank_account_id INTEGER NOT NULL REFERENCES accounts(id), tax_account_id INTEGER NOT NULL REFERENCES accounts(id),
  memo TEXT NOT NULL DEFAULT '', entry_id INTEGER REFERENCES journal_entries(id), voided INTEGER NOT NULL DEFAULT 0, created_by INTEGER REFERENCES users(id), created_at INTEGER NOT NULL);
`;
const MIGRATION_8_COLUMNS = [
  ['contacts', 'tax_exempt', 'INTEGER NOT NULL DEFAULT 0'], ['contacts', 'tax_exempt_note', "TEXT NOT NULL DEFAULT ''"], ['contacts', 'tax_rate_id', 'INTEGER'],
  ['items', 'taxable', 'INTEGER NOT NULL DEFAULT 1'],
  ['documents', 'tax_rate_id', 'INTEGER'], ['documents', 'tax_cents', 'INTEGER NOT NULL DEFAULT 0'], ['documents', 'tax_exempt', 'INTEGER NOT NULL DEFAULT 0'],
  ['document_lines', 'taxable', 'INTEGER NOT NULL DEFAULT 0'],
];
function migrate8(db) {
  db.exec(MIGRATION_8_TABLES);
  for (const [table, col, ddl] of MIGRATION_8_COLUMNS) {
    if (!db.prepare(`PRAGMA table_info(${table})`).all().some((c) => c.name === col)) db.exec(`ALTER TABLE ${table} ADD COLUMN ${col} ${ddl}`);
  }
}

// v9: credit memos (money owed back to a customer), what has been applied to invoices, and refunds.
const MIGRATION_9 = `
CREATE TABLE IF NOT EXISTS credit_memos(
  id INTEGER PRIMARY KEY, number TEXT NOT NULL UNIQUE, contact_id INTEGER NOT NULL REFERENCES contacts(id), memo_date TEXT NOT NULL, memo TEXT NOT NULL DEFAULT '',
  total_cents INTEGER NOT NULL CHECK(total_cents > 0), tax_cents INTEGER NOT NULL DEFAULT 0, tax_rate_id INTEGER, tax_exempt INTEGER NOT NULL DEFAULT 0,
  applied_cents INTEGER NOT NULL DEFAULT 0 CHECK(applied_cents >= 0), refunded_cents INTEGER NOT NULL DEFAULT 0 CHECK(refunded_cents >= 0),
  status TEXT NOT NULL CHECK(status IN ('open','used','void')), entry_id INTEGER REFERENCES journal_entries(id),
  created_by INTEGER REFERENCES users(id), created_at INTEGER NOT NULL, CHECK(applied_cents + refunded_cents <= total_cents));
CREATE TABLE IF NOT EXISTS credit_memo_lines(
  id INTEGER PRIMARY KEY, credit_id INTEGER NOT NULL REFERENCES credit_memos(id), item_id INTEGER REFERENCES items(id),
  description TEXT NOT NULL DEFAULT '', qty_milli INTEGER NOT NULL, unit_price_cents INTEGER NOT NULL, amount_cents INTEGER NOT NULL,
  account_id INTEGER NOT NULL REFERENCES accounts(id), taxable INTEGER NOT NULL DEFAULT 0);
CREATE TABLE IF NOT EXISTS credit_memo_tax(
  id INTEGER PRIMARY KEY, credit_id INTEGER NOT NULL REFERENCES credit_memos(id), rate_id INTEGER NOT NULL REFERENCES tax_rates(id),
  rate_milli INTEGER NOT NULL, taxable_cents INTEGER NOT NULL, tax_cents INTEGER NOT NULL);
CREATE TABLE IF NOT EXISTS credit_applications(
  id INTEGER PRIMARY KEY, credit_id INTEGER NOT NULL REFERENCES credit_memos(id), document_id INTEGER NOT NULL REFERENCES documents(id),
  amount_cents INTEGER NOT NULL CHECK(amount_cents > 0), applied_on TEXT NOT NULL, created_by INTEGER REFERENCES users(id), created_at INTEGER NOT NULL);
CREATE TABLE IF NOT EXISTS credit_refunds(
  id INTEGER PRIMARY KEY, credit_id INTEGER NOT NULL REFERENCES credit_memos(id), pay_date TEXT NOT NULL, amount_cents INTEGER NOT NULL CHECK(amount_cents > 0),
  account_id INTEGER NOT NULL REFERENCES accounts(id), method TEXT NOT NULL DEFAULT '', ref TEXT NOT NULL DEFAULT '',
  entry_id INTEGER REFERENCES journal_entries(id), voided INTEGER NOT NULL DEFAULT 0, created_by INTEGER REFERENCES users(id), created_at INTEGER NOT NULL);
CREATE INDEX IF NOT EXISTS cm_contact ON credit_memos(contact_id);
CREATE INDEX IF NOT EXISTS ca_doc ON credit_applications(document_id);
`;

const MIGRATION_7 = `
CREATE TABLE IF NOT EXISTS watched_files(
  hash TEXT PRIMARY KEY, path TEXT NOT NULL, name TEXT NOT NULL, size INTEGER NOT NULL, mtime INTEGER NOT NULL,
  status TEXT NOT NULL CHECK(status IN ('pending','imported','ignored','unreadable')), info TEXT NOT NULL DEFAULT '{}', batch_id INTEGER, seen_at INTEGER NOT NULL);
CREATE INDEX IF NOT EXISTS wf_path ON watched_files(path, size, mtime);
`;

const MIGRATION_6 = `
ALTER TABLE contacts ADD COLUMN first_name TEXT NOT NULL DEFAULT '';
ALTER TABLE contacts ADD COLUMN middle_name TEXT NOT NULL DEFAULT '';
ALTER TABLE contacts ADD COLUMN last_name TEXT NOT NULL DEFAULT '';
ALTER TABLE contacts ADD COLUMN phone2 TEXT NOT NULL DEFAULT '';
ALTER TABLE contacts ADD COLUMN addr_street TEXT NOT NULL DEFAULT '';
ALTER TABLE contacts ADD COLUMN addr_street2 TEXT NOT NULL DEFAULT '';
ALTER TABLE contacts ADD COLUMN addr_city TEXT NOT NULL DEFAULT '';
ALTER TABLE contacts ADD COLUMN addr_state TEXT NOT NULL DEFAULT '';
ALTER TABLE contacts ADD COLUMN addr_zip TEXT NOT NULL DEFAULT '';
`;

const MIGRATION_5 = `
CREATE TABLE reconciliations(
  id INTEGER PRIMARY KEY, account_id INTEGER NOT NULL REFERENCES accounts(id), statement_date TEXT NOT NULL, statement_cents INTEGER NOT NULL,
  cleared_count INTEGER NOT NULL, created_by INTEGER REFERENCES users(id), created_at INTEGER NOT NULL, undone INTEGER NOT NULL DEFAULT 0);
CREATE TABLE reconciled_lines(
  line_id INTEGER PRIMARY KEY REFERENCES journal_lines(id), reconciliation_id INTEGER NOT NULL REFERENCES reconciliations(id));
CREATE INDEX rl_rec ON reconciled_lines(reconciliation_id);
`;

const MIGRATION_4 = `
CREATE TABLE passkeys(
  id INTEGER PRIMARY KEY, user_id INTEGER NOT NULL REFERENCES users(id), credential_id TEXT NOT NULL UNIQUE,
  public_key BLOB NOT NULL, alg INTEGER NOT NULL, sign_count INTEGER NOT NULL DEFAULT 0, user_handle TEXT NOT NULL,
  name TEXT NOT NULL DEFAULT '', created_at INTEGER NOT NULL, last_used_at INTEGER NOT NULL DEFAULT 0);
CREATE INDEX passkeys_user ON passkeys(user_id);
`;

const inTx = new WeakSet();
/** Run fn atomically. Re-entrant: nested calls join the outer transaction. */
function tx(db, fn) {
  if (inTx.has(db)) return fn();
  db.exec('BEGIN IMMEDIATE');
  inTx.add(db);
  try {
    const r = fn();
    db.exec('COMMIT');
    return r;
  } catch (e) {
    try { db.exec('ROLLBACK'); } catch { /* already rolled back */ }
    throw e;
  } finally {
    inTx.delete(db);
  }
}

/**
 * Bills / invoices / payments that an import created are removed when that import is undone (the journal keeps the entries and their
 * reversals as the permanent record). `batchIds` = import batches to purge; documents a person voided themselves are never touched.
 */
function purgeImportedDocuments(db, batchIds) {
  if (!batchIds.length) return 0;
  const ph = batchIds.map(() => '?').join(',');
  const mine = `SELECT id FROM journal_entries WHERE source_type='import' AND source_id IN (${ph})`;
  const docs = `SELECT id FROM documents WHERE entry_id IN (${mine})`;
  db.prepare(`DELETE FROM payment_applications WHERE document_id IN (${docs})`).run(...batchIds);
  db.prepare(`DELETE FROM payments WHERE entry_id IN (${mine})`).run(...batchIds);
  db.prepare(`DELETE FROM document_lines WHERE document_id IN (${docs})`).run(...batchIds);
  return Number(db.prepare(`DELETE FROM documents WHERE entry_id IN (${mine})`).run(...batchIds).changes);
}

function openDb(file, opts = {}) {
  if (file !== ':memory:') {
    fs.mkdirSync(path.dirname(file), { recursive: true, mode: 0o700 });
  }
  const db = new DatabaseSync(file);
  // exclusive = this process keeps the file to itself (no other program can read or write it while we run)
  db.exec((opts.exclusive ? 'PRAGMA locking_mode = EXCLUSIVE; ' : '') + 'PRAGMA journal_mode = WAL; PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000; PRAGMA trusted_schema = OFF; PRAGMA secure_delete = ON;');
  const v = db.prepare('PRAGMA user_version').get().user_version;
  if (v === 0) {
    tx(db, () => {
      db.exec(SCHEMA);
      const insRole = db.prepare('INSERT INTO roles(name, permissions, builtin) VALUES (?,?,1)');
      for (const [n, p] of Object.entries(BUILTIN_ROLES)) insRole.run(n, JSON.stringify(p));
      const insAcc = db.prepare('INSERT INTO accounts(number,name,type,subtype,system,created_at) VALUES (?,?,?,?,?,?)');
      for (const [num, name, type, sub, sys] of DEFAULT_ACCOUNTS) insAcc.run(num, name, type, sub, sys ? 1 : 0, Date.now());
      db.prepare("INSERT INTO settings(key,value) VALUES ('company_name','My Company'),('closing_date',''),('next_invoice','1'),('next_bill','1')").run();
      db.exec('PRAGMA user_version = 1');
    });
  }
  const cur = db.prepare('PRAGMA user_version').get().user_version;
  if (cur === 1) tx(db, () => { db.exec(MIGRATION_2); db.exec('PRAGMA user_version = 2'); });
  if (db.prepare('PRAGMA user_version').get().user_version === 2) {
    // v3: clear the bills / invoices that earlier "Undo import" runs left behind as voided copies. A copy of the database is saved first.
    const undone = db.prepare("SELECT id FROM import_batches WHERE json_extract(summary,'$.undone') IS NOT NULL").all().map((b) => b.id);
    if (file !== ':memory:' && undone.length) {
      const backup = file.replace(/\.db$/, '') + '.before-v3.db';
      try { fs.rmSync(backup, { force: true }); db.exec(`VACUUM INTO '${backup.replace(/'/g, "''")}'`); } catch { /* the upgrade below is small and transactional; carry on */ }
    }
    tx(db, () => { purgeImportedDocuments(db, undone); db.exec('PRAGMA user_version = 3'); });
  }
  if (db.prepare('PRAGMA user_version').get().user_version === 3) tx(db, () => { db.exec(MIGRATION_4); db.exec('PRAGMA user_version = 4'); });
  if (db.prepare('PRAGMA user_version').get().user_version === 4) tx(db, () => { db.exec(MIGRATION_5); db.exec('PRAGMA user_version = 5'); });
  if (db.prepare('PRAGMA user_version').get().user_version === 5) tx(db, () => { const have = new Set(db.prepare('PRAGMA table_info(contacts)').all().map((c) => c.name)); for (const stmt of MIGRATION_6.split(';').map((x) => x.trim()).filter(Boolean)) { const col = /ADD COLUMN (\w+)/.exec(stmt)[1]; if (!have.has(col)) db.exec(stmt); } db.exec('PRAGMA user_version = 6'); });
  if (db.prepare('PRAGMA user_version').get().user_version === 6) tx(db, () => { db.exec(MIGRATION_7); db.exec('PRAGMA user_version = 7'); });
  if (db.prepare('PRAGMA user_version').get().user_version === 7) tx(db, () => { migrate8(db); db.exec('PRAGMA user_version = 8'); });
  if (db.prepare('PRAGMA user_version').get().user_version === 8) tx(db, () => { db.exec(MIGRATION_9); db.exec('PRAGMA user_version = 9'); });
  if (cur > 9) throw new Error(`Database schema v${cur} is newer than this program supports`);
  if (file !== ':memory:') {
    try { fs.chmodSync(file, 0o600); } catch { /* best effort on Windows */ }
  }
  return db;
}

const getSetting = (db, k) => db.prepare('SELECT value FROM settings WHERE key=?').get(k)?.value ?? '';
const setSetting = (db, k, v) => db.prepare('INSERT INTO settings(key,value) VALUES (?,?) ON CONFLICT(key) DO UPDATE SET value=excluded.value').run(k, String(v));

module.exports = { openDb, migrate8, MIGRATION_9, purgeImportedDocuments, tx, getSetting, setSetting, SCHEMA_VERSION, ACCOUNT_TYPES, SUBTYPES, PERMISSIONS, BUILTIN_ROLES };
