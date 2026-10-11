'use strict';
/**
 * Plain-language explanations for errors that come from deep inside (the database, the file system) rather than from a check we wrote.
 * Every result says what happened, the likely reason, and what the person can do. Nothing here ever includes passwords, request bodies or SQL.
 */
const { explain: explainFile } = require('./fileguard');

const NOUN = {
  accounts: 'account', contacts: 'customer or vendor', items: 'product or service', documents: 'invoice or bill', users: 'user', roles: 'role',
  bank_transactions: 'bank transaction', passkeys: 'passkey', fixed_assets: 'fixed asset', journal_entries: 'journal entry', bank_rules: 'bank rule',
};
const FIELD = { name: 'name', number: 'number', username: 'username', credential_id: 'passkey', fitid: 'bank reference', import_hash: 'import record', ref: 'reference' };

/** @returns {{status:number, error:string}|null} null when the error is not one we can explain better than "unexpected problem" */
function explainDbError(e) {
  const m = String((e && e.message) || e || '');
  let x;
  if ((x = m.match(/UNIQUE constraint failed: (\w+)\.(\w+)/))) {
    const what = NOUN[x[1]] || 'record', field = FIELD[x[2]] || x[2].replace(/_/g, ' ');
    return { status: 400, error: `That would make a duplicate: another ${what} already has the same ${field}. Change it so it is different, or open the existing one instead.` };
  }
  if (/FOREIGN KEY constraint failed/.test(m)) return { status: 400, error: 'That change would leave something pointing at a record that does not exist, or remove a record that other records still depend on (for example deleting a customer who has invoices). Remove or change the dependent records first.' };
  if (/CHECK constraint failed/.test(m)) return { status: 400, error: 'One of the values is not allowed here (for example a negative amount where only zero or more is allowed, or a choice that is not in the list). Check the numbers and choices you entered.' };
  if (/NOT NULL constraint failed: (\w+)\.(\w+)/.test(m)) { const y = m.match(/NOT NULL constraint failed: (\w+)\.(\w+)/); return { status: 400, error: `A required value is missing: the ${(FIELD[y[2]] || y[2]).replace(/_/g, ' ')} of this ${NOUN[y[1]] || 'record'} was left empty. Fill it in and try again.` }; }
  if (/immutable|append-only|cannot be (modified|deleted)/i.test(m)) return { status: 400, error: 'That record is locked: posted entries and the activity log are never edited or deleted, so the books stay trustworthy. To correct a mistake, post a reversing entry (or void the invoice, bill or payment) instead.' };
  if (/database is locked|SQLITE_BUSY|busy/i.test(m)) return { status: 503, error: 'The books are busy with another action. Wait a few seconds and try again. If this keeps happening, another program may have the books file open; close it.' };
  if (/readonly|read-only/i.test(m)) return { status: 503, error: explainFile(m) };
  if (/disk I\/O error|SQLITE_IOERR/i.test(m)) return { status: 503, error: 'Windows could not read or write the books file. The drive may have been unplugged, may have a fault, or a security program may be interfering. Nothing was half-saved. Check the drive, close Ledjoro and open it again.' };
  if (/database or disk is full|SQLITE_FULL/i.test(m)) return { status: 507, error: 'The disk is full, so the books could not be saved. Nothing was half-saved. Free up some space (or move Ledjoro to a drive with room) and try again.' };
  if (/malformed|not a database|SQLITE_CORRUPT|SQLITE_NOTADB/i.test(m)) return { status: 500, error: 'The books file looks damaged (it does not pass the database\'s own consistency check). Close Ledjoro and restore your most recent backup from Settings, Data & backup. Do not keep entering new records into a damaged file.' };
  if (/too many SQL variables|string or blob too big|SQLITE_TOOBIG/i.test(m)) return { status: 413, error: 'That is more data than Ledjoro can handle in one step. Split it into smaller parts.' };
  return null;
}

/** The last resort: something unexpected. Says so honestly, and what is safe to assume. */
function unexpected() {
  return { status: 500, error: 'Ledjoro hit an unexpected problem inside its engine and could not finish. Every change is all-or-nothing, so nothing was half-saved. Try again. If it keeps happening, close Ledjoro and open it again, and tell whoever supports you the time it happened: the details are in the file ledjoro.log in the Ledjoro-Data folder (it never contains passwords).' };
}

module.exports = { explainDbError, unexpected };
