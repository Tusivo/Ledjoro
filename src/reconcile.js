'use strict';
/**
 * Bank reconciliation and matching.
 *  - Reconciling: you enter the closing balance from a bank/card statement, tick the entries that appear on it, and the books
 *    only accept it when "what you ticked" and "the statement" agree to the cent. Ticked entries are then marked cleared.
 *  - Matching: an imported bank row that is already in the books (for example a customer payment you recorded by hand) is
 *    linked to that entry instead of being posted a second time.
 * Nothing here changes a journal entry; "cleared" and "matched" are separate records, so the ledger stays immutable.
 */
const { tx } = require('./db');
const { bad, HttpError, vInt, vDate, vArr, addDays } = require('./util');
const { audit } = require('./audit');

const MATCH_DAYS = 10;

function bankAccount(db, id) {
  const a = db.prepare('SELECT id, name, subtype FROM accounts WHERE id=?').get(id);
  if (!a || !['bank', 'credit_card'].includes(a.subtype)) throw bad('Choose a bank or credit card account to reconcile. The account chosen is a different kind of account.');
  return a;
}
// What a line means on the statement: money in is positive on a bank account; a charge is positive (owed) on a credit card.
const sign = (acct) => (acct.subtype === 'credit_card' ? -1 : 1);

function clearedBalance(db, acct) {
  const r = db.prepare('SELECT COALESCE(SUM(l.debit_cents - l.credit_cents),0) AS s FROM reconciled_lines rl JOIN journal_lines l ON l.id=rl.line_id WHERE l.account_id=?').get(acct.id);
  return sign(acct) * r.s;
}

function start(db, accountId, statementDate) {
  const acct = bankAccount(db, vInt(accountId, 'account'));
  const date = vDate(statementDate, 'statement date');
  const last = db.prepare('SELECT id, statement_date, statement_cents FROM reconciliations WHERE account_id=? AND undone=0 ORDER BY statement_date DESC, id DESC LIMIT 1').get(acct.id) || null;
  const lines = db.prepare(`SELECT l.id, l.entry_id, e.entry_date AS date, e.memo, e.ref, e.source_type, l.debit_cents, l.credit_cents, c.name AS contact_name
    FROM journal_lines l JOIN journal_entries e ON e.id=l.entry_id LEFT JOIN contacts c ON c.id=l.contact_id
    WHERE l.account_id=? AND e.entry_date<=? AND l.id NOT IN (SELECT line_id FROM reconciled_lines) ORDER BY e.entry_date, l.id`).all(acct.id, date)
    .map((l) => ({ id: l.id, entry_id: l.entry_id, date: l.date, memo: l.memo || l.contact_name || '', ref: l.ref, source_type: l.source_type, contact_name: l.contact_name || '', amount_cents: sign(acct) * (l.debit_cents - l.credit_cents) }));
  return { account: acct, cleared_cents: clearedBalance(db, acct), last, lines, credit_card: acct.subtype === 'credit_card' };
}

function finish(db, ctx, b) {
  const acct = bankAccount(db, vInt(b.account_id, 'account'));
  const date = vDate(b.statement_date, 'statement date');
  const stmt = vInt(b.statement_cents, 'statement balance', { min: -1e13, max: 1e13 });
  const ids = vArr(b.line_ids, 'ticked entries', { max: 20000 }).map((x) => vInt(x, 'entry'));
  if (new Set(ids).size !== ids.length) throw bad('The same entry was ticked twice. Close this page and start the reconciliation again.');
  return tx(db, () => {
    let sum = 0;
    const get = db.prepare('SELECT l.account_id, l.debit_cents, l.credit_cents, e.entry_date, (SELECT 1 FROM reconciled_lines WHERE line_id=l.id) AS done FROM journal_lines l JOIN journal_entries e ON e.id=l.entry_id WHERE l.id=?');
    for (const id of ids) {
      const l = get.get(id);
      if (!l || l.account_id !== acct.id) throw bad('One of the ticked entries does not belong to this account, so nothing was saved. Close this page and start again.');
      if (l.done) throw bad('One of the ticked entries was already reconciled earlier, so nothing was saved. Close this page and start again to see the current list.');
      if (l.entry_date > date) throw bad('One of the ticked entries is dated after the statement date, so it cannot be on this statement. Untick it, or change the statement date.');
      sum += sign(acct) * (l.debit_cents - l.credit_cents);
    }
    const total = clearedBalance(db, acct) + sum, diff = stmt - total;
    if (diff !== 0) throw bad(`The ticked entries do not add up to the statement yet: they come to ${(total / 100).toFixed(2)} and the statement says ${(stmt / 100).toFixed(2)}, a difference of ${(Math.abs(diff) / 100).toFixed(2)}. Tick or untick entries until the difference is 0.00, then finish.`);
    const r = db.prepare('INSERT INTO reconciliations(account_id,statement_date,statement_cents,cleared_count,created_by,created_at) VALUES (?,?,?,?,?,?)').run(acct.id, date, stmt, ids.length, ctx.user.id, Date.now());
    const ins = db.prepare('INSERT INTO reconciled_lines(line_id, reconciliation_id) VALUES (?,?)');
    for (const id of ids) ins.run(id, Number(r.lastInsertRowid));
    audit(db, ctx, 'reconcile.finish', 'reconciliation', r.lastInsertRowid, `${acct.name} to ${date}, ${ids.length} entries`);
    return { id: Number(r.lastInsertRowid), cleared: ids.length };
  });
}

function history(db, accountId) {
  return db.prepare(`SELECT r.*, a.name AS account_name, u.username FROM reconciliations r JOIN accounts a ON a.id=r.account_id LEFT JOIN users u ON u.id=r.created_by
    WHERE (? IS NULL OR r.account_id=?) ORDER BY r.statement_date DESC, r.id DESC LIMIT 200`).all(accountId ?? null, accountId ?? null);
}

/** Only the newest reconciliation of an account can be undone, so the "cleared" history is never left with a hole in it. */
function undo(db, ctx, id) {
  return tx(db, () => {
    const r = db.prepare('SELECT * FROM reconciliations WHERE id=?').get(id);
    if (!r || r.undone) throw new HttpError(404, 'That reconciliation could not be found, or it was already undone. Refresh the list.');
    const newer = db.prepare('SELECT 1 FROM reconciliations WHERE account_id=? AND undone=0 AND (statement_date>? OR (statement_date=? AND id>?))').get(r.account_id, r.statement_date, r.statement_date, r.id);
    if (newer) throw bad('A later reconciliation exists for this account. Undo the latest one first, then this one.');
    db.prepare('DELETE FROM reconciled_lines WHERE reconciliation_id=?').run(id);
    db.prepare('UPDATE reconciliations SET undone=1 WHERE id=?').run(id);
    audit(db, ctx, 'reconcile.undo', 'reconciliation', id);
    return { ok: true };
  });
}

// ---------------- matching bank rows to entries already in the books ----------------
function candidates(db, t) {
  if (t.status !== 'new') return [];
  const from = addDays(t.tx_date, -MATCH_DAYS), to = addDays(t.tx_date, MATCH_DAYS);
  return db.prepare(`SELECT l.id AS line_id, e.id AS entry_id, e.entry_date AS date, e.memo, e.ref, e.source_type, c.name AS contact_name
    FROM journal_lines l JOIN journal_entries e ON e.id=l.entry_id LEFT JOIN contacts c ON c.id=l.contact_id
    WHERE l.account_id=? AND l.debit_cents - l.credit_cents = ? AND e.entry_date BETWEEN ? AND ? AND e.source_type<>'bank'
      AND e.id NOT IN (SELECT entry_id FROM bank_transactions WHERE entry_id IS NOT NULL)
      AND e.id NOT IN (SELECT entry_id FROM entry_reversals UNION SELECT reversal_id FROM entry_reversals)
      AND l.id NOT IN (SELECT line_id FROM reconciled_lines)
    ORDER BY ABS(julianday(e.entry_date) - julianday(?)), l.id LIMIT 10`).all(t.account_id, t.amount_cents, from, to, t.tx_date);
}
function matches(db, id) {
  const t = db.prepare('SELECT * FROM bank_transactions WHERE id=?').get(id);
  if (!t) throw new HttpError(404, 'That bank transaction could not be found. It may have been removed by an undone import; refresh the list.');
  return candidates(db, t);
}
function match(db, ctx, id, lineId) {
  return tx(db, () => {
    const t = db.prepare('SELECT * FROM bank_transactions WHERE id=?').get(id);
    if (!t) throw new HttpError(404, 'That bank transaction could not be found. It may have been removed by an undone import; refresh the list.');
    if (t.status !== 'new') throw bad('This bank transaction has already been dealt with, so it cannot be matched. Look under Categorized or Excluded.');
    const c = candidates(db, t).find((x) => x.line_id === vInt(lineId, 'entry'));
    if (!c) throw bad('That entry no longer fits this bank transaction (it may have been matched to another one already). Refresh the list and look again.');
    db.prepare("UPDATE bank_transactions SET status='categorized', entry_id=? WHERE id=?").run(c.entry_id, id);
    audit(db, ctx, 'bank.match', 'bank_transaction', id, `-> entry ${c.entry_id}`);
    return { entry_id: c.entry_id };
  });
}
/** Match every new bank row that has exactly one possible entry; anything uncertain is left for a person. */
function autoMatch(db, ctx) {
  let n = 0;
  for (const t of db.prepare("SELECT * FROM bank_transactions WHERE status='new' ORDER BY tx_date, id").all()) {
    const c = candidates(db, t);
    const exact = c.filter((x) => x.date === t.tx_date);
    const pick = c.length === 1 ? c[0] : exact.length === 1 ? exact[0] : null;
    if (pick) { try { match(db, ctx, t.id, pick.line_id); n++; } catch { /* leave it for review */ } }
  }
  audit(db, ctx, 'bank.auto_match', 'bank_transaction', '', `${n} matched`);
  return n;
}
function removeRule(db, ctx, id) {
  const r = db.prepare('UPDATE bank_rules SET active=0 WHERE id=? AND active=1').run(id);
  if (!r.changes) throw new HttpError(404, 'That rule could not be found, or it was already removed. Refresh the list.');
  audit(db, ctx, 'rule.remove', 'bank_rule', id);
}

module.exports = { start, finish, history, undo, matches, match, autoMatch, removeRule };
