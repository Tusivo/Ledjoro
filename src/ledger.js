'use strict';
const usd = (c) => '$' + (c / 100).toLocaleString('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 2 });
const { tx, getSetting } = require('./db');
const { bad, isValidDate } = require('./util');

/**
 * Post a balanced journal entry. This is the ONLY way money is recorded.
 * lines: [{account_id, debit_cents, credit_cents, contact_id?, memo?}]
 * Entries are immutable once posted (DB triggers); corrections are made with reverseEntry().
 */
function postEntry(db, e, { skipClosingCheck = false } = {}) {
  if (!isValidDate(e.date)) throw bad('The date on this entry is not a real date. Type it as year-month-day, for example 2026-03-31.');
  const closing = getSetting(db, 'closing_date');
  if (!skipClosingCheck && closing && e.date <= closing) throw bad(`The books are closed through ${closing}, so nothing can be added or changed on or before that date. Use a later date, or ask an administrator to move the closing date under Settings, Company.`);
  if (!Array.isArray(e.lines) || e.lines.length < 2) throw bad('A journal entry needs at least two lines (a debit and a credit) so that it balances. Add the missing line.');
  if (e.lines.length > 500) throw bad('This entry has more than 500 lines, which is more than Ledjoro accepts in one entry. Split it into two or more entries.');

  let debits = 0, credits = 0;
  const accountOk = db.prepare('SELECT active FROM accounts WHERE id=?');
  for (const [i, l] of e.lines.entries()) {
    const d = l.debit_cents || 0, c = l.credit_cents || 0;
    if (!Number.isSafeInteger(d) || !Number.isSafeInteger(c) || d < 0 || c < 0) throw bad(`Line ${i + 1}: the amount is not a valid money amount. Use dollars and cents, such as 125.50.`);
    if ((d > 0) === (c > 0)) throw bad(`Line ${i + 1} has an amount in both the debit and credit columns, or in neither. Each line needs an amount in exactly one of the two.`);
    const a = accountOk.get(l.account_id);
    if (!a) throw bad(`Line ${i + 1}: the account chosen does not exist any more. Pick an account from the list.`);
    if (!a.active) throw bad(`Line ${i + 1}: that account has been made inactive, so nothing new can be posted to it. Choose a different account, or make it active again under Chart of accounts.`);
    debits += d; credits += c;
    if (!Number.isSafeInteger(debits) || !Number.isSafeInteger(credits)) throw bad('The amounts on this entry are too large for Ledjoro to add up safely. Check for a misplaced decimal point or an extra zero.');
  }
  if (debits !== credits) throw bad(`This entry does not balance: debits come to ${usd(debits)} but credits come to ${usd(credits)}, a difference of ${usd(Math.abs(debits - credits))}. Debits and credits must be equal. Fix the amounts or add the missing line.`);

  return tx(db, () => {
    const r = db.prepare('INSERT INTO journal_entries(entry_date,memo,ref,source_type,source_id,import_hash,created_by,created_at) VALUES (?,?,?,?,?,?,?,?)')
      .run(e.date, e.memo || '', e.ref || '', e.source_type || 'manual', e.source_id ?? null, e.import_hash ?? null, e.user_id ?? null, Date.now());
    const id = Number(r.lastInsertRowid);
    const ins = db.prepare('INSERT INTO journal_lines(entry_id,account_id,contact_id,debit_cents,credit_cents,memo) VALUES (?,?,?,?,?,?)');
    for (const l of e.lines) ins.run(id, l.account_id, l.contact_id ?? null, l.debit_cents || 0, l.credit_cents || 0, l.memo || '');
    return id;
  });
}

function reverseEntry(db, entryId, { date, userId, memo } = {}) {
  const e = db.prepare('SELECT * FROM journal_entries WHERE id=?').get(entryId);
  if (!e) throw bad('That journal entry could not be found. It may have been removed, or you may be looking at an old screen. Go back and refresh.');
  if (db.prepare('SELECT 1 FROM entry_reversals WHERE entry_id=? OR reversal_id=?').get(entryId, entryId)) throw bad('This entry has already been reversed, or it is itself a reversal, so it cannot be reversed again. If a further correction is needed, post a new entry.');
  const lines = db.prepare('SELECT * FROM journal_lines WHERE entry_id=?').all(entryId);
  return tx(db, () => {
    const rid = postEntry(db, {
      date: date || e.entry_date, memo: memo || `Reversal of #${e.id}${e.memo ? ': ' + e.memo : ''}`, ref: e.ref,
      source_type: 'reversal', source_id: e.id, user_id: userId,
      lines: lines.map((l) => ({ account_id: l.account_id, contact_id: l.contact_id, debit_cents: l.credit_cents, credit_cents: l.debit_cents, memo: l.memo })),
    });
    db.prepare('INSERT INTO entry_reversals(entry_id, reversal_id) VALUES (?,?)').run(entryId, rid);
    return rid;
  });
}

const systemAccount = (db, subtype) => {
  const a = db.prepare('SELECT id FROM accounts WHERE subtype=? AND system=1 AND active=1 ORDER BY id LIMIT 1').get(subtype)
    || db.prepare('SELECT id FROM accounts WHERE subtype=? AND active=1 ORDER BY id LIMIT 1').get(subtype);
  if (!a) throw bad(`Ledjoro needs an active ${({ ar: 'Accounts Receivable', ap: 'Accounts Payable' })[subtype] || subtype} account to post this, but there is none (it may have been made inactive). Make one active under Chart of accounts, then try again.`);
  return a.id;
};

/** Global integrity check: every entry balances; no orphan lines. Used by /api/audit/verify and tests. */
function checkLedgerIntegrity(db) {
  const unbalanced = db.prepare(`SELECT entry_id, SUM(debit_cents) d, SUM(credit_cents) c FROM journal_lines GROUP BY entry_id HAVING d <> c`).all();
  const empty = db.prepare(`SELECT id FROM journal_entries WHERE id NOT IN (SELECT entry_id FROM journal_lines)`).all();
  return { ok: unbalanced.length === 0 && empty.length === 0, unbalanced, empty };
}

module.exports = { postEntry, reverseEntry, systemAccount, checkLedgerIntegrity };
