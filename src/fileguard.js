'use strict';
/**
 * Protecting the books file. The database lives in one folder next to the program, and while Ledjoro runs:
 *  - a "read-only" flag on it is cleared (otherwise SQLite quietly opens it read-only and every sign-in and save fails with a vague error),
 *  - the file is held open exclusively, so no other program can read, edit or copy it half-written,
 *  - the folder's permissions are limited to the current Windows user (and the system itself),
 *  - at every start the file is checked for changes made outside Ledjoro (structure, tamper-evident activity log, balanced ledger).
 * Honest limit: while Ledjoro is NOT running, the Windows user who owns the folder can still change the file with other tools.
 * Their edits are caught by the start-up check; they cannot be prevented by the operating system.
 */
const fs = require('node:fs');
const path = require('node:path');
const { spawnSync } = require('node:child_process');

const DB_FILES = ['ledjoro.db', 'ledjoro.db-wal', 'ledjoro.db-shm'];

/** Clear the read-only flag on the database files. Returns the names it fixed. */
function clearReadOnly(dir, log = () => {}) {
  const fixed = [];
  for (const f of DB_FILES) {
    const p = path.join(dir, f);
    try {
      if (!fs.existsSync(p)) continue;
      if (!(fs.statSync(p).mode & 0o200)) { fs.chmodSync(p, 0o600); fixed.push(f); log('cleared the read-only flag on', f); }
    } catch (e) { log('could not clear read-only on', f, e.message); }
  }
  return fixed;
}

const icacls = (...args) => spawnSync('icacls', args, { windowsHide: true, encoding: 'utf8', timeout: 20000 });

/**
 * Windows: only this user and SYSTEM may use the data folder (inherited permissions are removed). Done in small steps and verified, and
 * undone if it would lock us out. Elsewhere: owner-only permissions. Returns 'restricted', 'already', or a reason it was skipped.
 */
function restrictFolder(dir, log = () => {}) {
  if (process.platform !== 'win32') { try { fs.chmodSync(dir, 0o700); return 'restricted'; } catch (e) { return 'skipped: ' + e.message; } }
  const name = process.env.USERNAME; if (!name) return 'skipped: no user name';
  const me = process.env.USERDOMAIN ? `${process.env.USERDOMAIN}\\${name}` : name;
  const cur = icacls(dir);
  if (cur.status === 0) {
    const aces = cur.stdout.split(/\r?\n/).map((l) => l.replace(dir, '').trim()).filter((l) => /:\(/.test(l));
    const onlyUs = aces.length > 0 && aces.every((l) => !/\(I\)/.test(l) && (l.toLowerCase().startsWith(me.toLowerCase() + ':') || /^NT AUTHORITY\\SYSTEM:/i.test(l)));
    if (onlyUs) return 'already';
  }
  const g = icacls(dir, '/grant:r', `${me}:(OI)(CI)F`, 'SYSTEM:(OI)(CI)F');
  if (g.status !== 0) { log('permissions: could not grant access to', me, (g.stderr || g.stdout || '').trim()); return 'skipped: grant failed'; }
  const r = icacls(dir, '/inheritance:r');
  if (r.status !== 0) { log('permissions: could not remove inherited access', (r.stderr || r.stdout || '').trim()); return 'skipped: inheritance'; }
  try { const probe = path.join(dir, '.access-check'); fs.writeFileSync(probe, 'ok'); fs.rmSync(probe, { force: true }); fs.readdirSync(dir); }
  catch (e) { icacls(dir, '/inheritance:e'); log('permissions: restricting would have locked this user out, so it was undone', e.message); return 'skipped: would lock out'; }
  log('permissions: data folder is now limited to', me, 'and SYSTEM');
  return 'restricted';
}

/** Anything in the books that does not look like Ledjoro wrote it. Cheap enough to run at every start. */
function integrityReport(db, verifyAudit, checkLedgerIntegrity) {
  const problems = [];
  try { const qc = Object.values(db.prepare('PRAGMA quick_check').get())[0]; if (qc !== 'ok') problems.push('the database file is damaged (' + String(qc).slice(0, 80) + ')'); }
  catch (e) { problems.push('the database file could not be checked (' + e.message + ')'); }
  try { const a = verifyAudit(db); if (!a.ok) problems.push(`the activity log was edited or has missing entries (first problem at entry ${a.brokenAtId})`); } catch (e) { problems.push('the activity log could not be checked'); }
  try {
    const l = checkLedgerIntegrity(db);
    if (l.unbalanced.length) problems.push(`${l.unbalanced.length} journal entr${l.unbalanced.length === 1 ? 'y does' : 'ies do'} not balance`);
    if (l.empty.length) problems.push(`${l.empty.length} journal entr${l.empty.length === 1 ? 'y has' : 'ies have'} no lines`);
  } catch (e) { problems.push('the ledger could not be checked'); }
  return { ok: problems.length === 0, problems };
}

/** Plain-language reason a database could not be opened or written. */
function explain(e) {
  const m = String(e && e.message || e);
  if (/locked|busy/i.test(m)) return 'The books file is open in another program (or another copy of Ledjoro). Close it and start Ledjoro again.';
  if (/readonly|read-only/i.test(m)) return 'The books file is read-only and Windows would not let Ledjoro change that. Right-click the file, choose Properties, untick Read-only, and start Ledjoro again.';
  if (/EACCES|EPERM|access is denied|unable to open/i.test(m)) return 'Windows will not let this account use the books folder. It was probably locked to a different Windows user (move the folder with Copy, not Cut, or restore a backup). Details: ' + m;
  return 'The books file could not be opened: ' + m;
}

module.exports = { clearReadOnly, restrictFolder, integrityReport, explain, DB_FILES };
