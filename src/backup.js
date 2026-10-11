'use strict';
/**
 * Verified backups (used by the app, the CLI and the standalone exe).
 *
 * A backup is a checked copy of the whole database, made with SQLite's online-backup API (safe while the app runs). Three ways to save it:
 *   - "recovery key"  (recommended): encrypted, and the company's recovery key opens it. Nothing extra to remember or lose.
 *   - "passphrase":    encrypted with a passphrase the person chooses; the recovery key ALSO opens it, so forgetting the passphrase is not fatal.
 *   - "none":          not encrypted at all (a plain database copy). Anyone who gets the file can read every record.
 *
 * Encrypted format "OBBK2":  magic(5) | mode(1) = 1 | headerLen(4) | header(JSON) | iv(12) | tag(16) | AES-256-GCM(database)
 * Every backup gets a fresh random 256-bit data key (DEK). The header holds that key wrapped in two ways, so either opens the backup:
 *    pass     = DEK locked with scrypt(passphrase)                                     (only when a passphrase was chosen)
 *    recovery = S locked with scrypt(recovery key), plus  dek_s = DEK locked with S    (S is a long-lived random secret kept in the
 *               books; each person's recovery key has its own lock on it, made at the moment the key was issued, when the key is known)
 * The header is authenticated, so editing it, or the data, is detected. Older "OBBK1" files (passphrase only) can still be restored.
 * A recovery key opens the backups made while it was the current one; after a key is replaced, older backups still open with the older key.
 */
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const crypto = require('node:crypto');
const { backup, DatabaseSync } = require('node:sqlite');
const { bad, normKey } = require('./util');
const { SCHEMA_VERSION, getSetting, setSetting } = require('./db');

const MAGIC1 = Buffer.from('OBBK1'), MAGIC2 = Buffer.from('OBBK2');
const KDF = { N: 32768, r: 8, p: 1, maxmem: 128 * 1024 * 1024 };
const kdf = (secret, salt) => crypto.scryptSync(secret, salt, 32, KDF);

// ---------- small AES-GCM helpers (hex fields so a header is plain JSON) ----------
function seal(key, plain, aad) {
  const iv = crypto.randomBytes(12); const c = crypto.createCipheriv('aes-256-gcm', key, iv); if (aad) c.setAAD(aad);
  const ct = Buffer.concat([c.update(plain), c.final()]);
  return { iv: iv.toString('hex'), tag: c.getAuthTag().toString('hex'), ct: ct.toString('hex') };
}
function open(key, w, aad) {
  try {
    const d = crypto.createDecipheriv('aes-256-gcm', key, Buffer.from(w.iv, 'hex')); if (aad) d.setAAD(aad); d.setAuthTag(Buffer.from(w.tag, 'hex'));
    return Buffer.concat([d.update(Buffer.from(w.ct, 'hex')), d.final()]);
  } catch { return null; }
}

// ---------- the recovery-key side: a long-lived secret S, locked once per recovery key ----------
function ensureSecret(db) {
  let s = getSetting(db, 'backup_secret');
  if (!/^[0-9a-f]{64}$/.test(s)) { s = crypto.randomBytes(32).toString('hex'); setSetting(db, 'backup_secret', s); }
  return Buffer.from(s, 'hex');
}
/** Called whenever a recovery key is issued (the only moment the key itself is known). */
function lockSecretWithRecoveryKey(db, userId, rawKey) {
  const salt = crypto.randomBytes(16);
  setSetting(db, 'recovery_wrap:' + userId, JSON.stringify({ salt: salt.toString('hex'), ...seal(kdf(normKey(rawKey), salt), ensureSecret(db)) }));
}
const recoveryWraps = (db) => db.prepare("SELECT value FROM settings WHERE key LIKE 'recovery_wrap:%' ORDER BY key").all()
  .map((r) => { try { return JSON.parse(r.value); } catch { return null; } }).filter(Boolean).slice(0, 20);
const hasRecoveryLink = (db) => recoveryWraps(db).length > 0;

function checkPassphrase(p) {
  if (typeof p !== 'string' || p.length < 12) throw bad('The backup passphrase needs at least 12 characters. A longer phrase is harder to guess; for example three or four ordinary words you will remember.');
  if (p.length > 256) throw bad('The backup passphrase is longer than 256 characters, which is more than Ledjoro accepts. Use a shorter phrase.');
}

// ---------- make ----------
function encryptData(plain, { passphrase, list, secret }) {
  const dek = crypto.randomBytes(32);
  const header = { v: 2, dek_s: null, pass: null, recovery: [] };
  if (list.length) { header.dek_s = seal(secret, dek); header.recovery = list; }
  if (passphrase) { const salt = crypto.randomBytes(16); header.pass = { salt: salt.toString('hex'), ...seal(kdf(passphrase, salt), dek) }; }
  const hdr = Buffer.from(JSON.stringify(header));
  const len = Buffer.alloc(4); len.writeUInt32BE(hdr.length);
  const body = seal(dek, plain, hdr);
  return Buffer.concat([MAGIC2, Buffer.from([1]), len, hdr, Buffer.from(body.iv, 'hex'), Buffer.from(body.tag, 'hex'), Buffer.from(body.ct, 'hex')]);
}

function integrityOk(file) {
  const db = new DatabaseSync(file, { readOnly: true });
  try { return db.prepare('PRAGMA integrity_check').get().integrity_check === 'ok'; } finally { db.close(); }
}

/**
 * @param {{mode?: 'recovery'|'passphrase'|'none', passphrase?: string}} opts  (mode defaults to 'passphrase' when a passphrase is given, else 'recovery')
 * @returns {Promise<Buffer>} a backup of the live database
 */
async function createBackup(db, opts = {}) {
  if (typeof opts === 'string') opts = { mode: 'passphrase', passphrase: opts }; // older callers: a bare passphrase
  const mode = opts.mode ?? (opts.passphrase ? 'passphrase' : 'recovery');
  if (!['recovery', 'passphrase', 'none'].includes(mode)) throw bad('Choose how to protect the backup: with your recovery key, with a passphrase, or not at all.');
  const list = recoveryWraps(db);
  if (mode === 'recovery' && !list.length) throw bad('Your recovery key is not linked to backups yet, so a backup protected by it could not be opened later. Type your current recovery key in the box provided, or make a new recovery key under My account, or choose a passphrase instead.');
  if (mode === 'passphrase') checkPassphrase(opts.passphrase);
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'ob-'));
  const tmp = path.join(dir, 'copy.db');
  try {
    await backup(db, tmp);
    if (!integrityOk(tmp)) throw bad('Ledjoro checked the copy of your books before saving it and found it damaged, so it did not make a backup. Your live books were not changed. Close Ledjoro and open it again; if this repeats, restore your most recent good backup.');
    const plain = fs.readFileSync(tmp);
    if (mode === 'none') return Buffer.concat([MAGIC2, Buffer.from([0]), plain]);
    return encryptData(plain, { passphrase: mode === 'passphrase' ? opts.passphrase : null, list, secret: list.length ? ensureSecret(db) : null });
  } finally {
    fs.rmSync(dir, { recursive: true, force: true }); // never leave a plaintext copy behind
  }
}

// ---------- open ----------
function readHeader(buf) {
  try {
    const len = buf.readUInt32BE(6);
    if (len > 100000 || 10 + len + 28 > buf.length) throw new Error('size');
    const hdr = buf.subarray(10, 10 + len);
    return { header: JSON.parse(hdr.toString('utf8')), hdr, rest: buf.subarray(10 + len) };
  } catch { throw bad('This backup file is damaged: its first part, which says how it is locked, cannot be read. It was probably only partly copied or downloaded. Make a new backup, or use an earlier one.'); }
}

/** What kind of file is this, and what does it need to be opened? (No secret required.) */
function inspect(buf) {
  if (!Buffer.isBuffer(buf) || buf.length < 6) throw bad('This file is not a Ledjoro backup: it is empty or far too small. Backups are the files ending in .obbak that Ledjoro saves from Settings, Data & backup.');
  if (buf.subarray(0, 5).equals(MAGIC1)) return { kind: 'encrypted', version: 1, hasPassphrase: true, hasRecovery: false };
  if (!buf.subarray(0, 5).equals(MAGIC2)) throw bad('This file is not a Ledjoro backup: it does not start the way Ledjoro backups do. Check that you chose the right file. Backups end in .obbak.');
  if (buf[5] === 0) return { kind: 'plain', version: 2, hasPassphrase: false, hasRecovery: false };
  const h = readHeader(buf);
  return { kind: 'encrypted', version: 2, hasPassphrase: !!h.header.pass, hasRecovery: h.header.recovery.length > 0 };
}

const WRONG_SECRET = 'That is not the right passphrase or recovery key for this backup. A backup opens with the passphrase typed when it was made, or with the company\'s recovery key (the one that was current on the day of the backup; an older key still opens older backups). Check for typing mistakes. A recovery key looks like XXXXX-XXXXX-XXXXX-XXXXX.';

/** @returns {Buffer} the database inside a backup. `secret` = passphrase or recovery key (not needed for unencrypted backups). */
function decryptBackup(buf, secret) {
  const info = inspect(buf);
  if (info.kind === 'plain') return buf.subarray(6);
  if (typeof secret !== 'string' || !secret.trim()) throw bad('This backup is locked. Type the passphrase it was made with, or the company\'s recovery key.');
  if (secret.length > 300) throw bad(WRONG_SECRET);
  if (info.version === 1) { // older file: passphrase only
    const d = crypto.createDecipheriv('aes-256-gcm', kdf(secret, buf.subarray(5, 21)), buf.subarray(21, 33)); d.setAuthTag(buf.subarray(33, 49));
    try { return Buffer.concat([d.update(buf.subarray(49)), d.final()]); } catch { throw bad(WRONG_SECRET + ' (If the passphrase is right, the file may be damaged.)'); }
  }
  const { header, hdr, rest } = readHeader(buf);
  let dek = null;
  if (header.pass) dek = open(kdf(secret, Buffer.from(header.pass.salt, 'hex')), header.pass);
  if (!dek && header.dek_s) {
    const norm = normKey(secret);
    for (const w of header.recovery.slice(0, 20)) {
      const S = open(kdf(norm, Buffer.from(w.salt, 'hex')), w);
      if (S) { dek = open(S, header.dek_s); if (dek) break; }
    }
  }
  if (!dek) throw bad(WRONG_SECRET);
  const plain = open(dek, { iv: rest.subarray(0, 12).toString('hex'), tag: rest.subarray(12, 28).toString('hex'), ct: rest.subarray(28).toString('hex') }, hdr);
  if (!plain) throw bad('The passphrase or recovery key is right, but the backup\'s contents do not pass their safety check. The file is damaged or was changed after it was made (for example a partly finished copy). Use a different backup.');
  return plain;
}

/** Writes the database from a backup to `target` (refuses to overwrite). */
function restoreBackup(file, secret, target) {
  if (fs.existsSync(target)) throw bad('Ledjoro will not overwrite an existing database file, so nothing was restored. Choose a new, empty location for the restored copy.');
  const plain = decryptBackup(fs.readFileSync(file), secret);
  fs.mkdirSync(path.dirname(target), { recursive: true, mode: 0o700 });
  fs.writeFileSync(target, plain, { mode: 0o600 });
  if (!integrityOk(target)) { fs.rmSync(target, { force: true }); throw bad('The backup opened, but the books inside it are damaged (they fail the database\'s own consistency check), so nothing was restored. Try an earlier backup.'); }
}

const REQUIRED_TABLES = ['settings', 'roles', 'users', 'sessions', 'accounts', 'contacts', 'journal_entries', 'journal_lines', 'audit_log'];
const q = (n) => '"' + String(n).replace(/"/g, '""') + '"';

/**
 * Replace the contents of the RUNNING database with a backup, atomically.
 * Everything happens in one SQLite transaction: if anything fails the live data is untouched.
 * Before the swap, a safety copy of the current data is written to dataDir (locked with the recovery key when it is linked, otherwise not locked,
 * which is why that folder is restricted to the current Windows user).
 * Every session is cleared afterwards (a backup must not resurrect old logins).
 */
async function restoreIntoLive(db, fileBuffer, secret, { dataDir }) {
  const plain = decryptBackup(fileBuffer, secret);
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'ob-'));
  const tmp = path.join(dir, 'restore.db');
  try {
    fs.writeFileSync(tmp, plain, { mode: 0o600 });
    let info;
    const src = new DatabaseSync(tmp, { readOnly: true });
    try {
      if (src.prepare('PRAGMA integrity_check').get().integrity_check !== 'ok') throw bad('The backup opened, but the books inside it are damaged (they fail the database\'s own consistency check), so it was not restored and your current books were not changed. Try an earlier backup.');
      const ver = src.prepare('PRAGMA user_version').get().user_version;
      if (ver < 1 || ver > SCHEMA_VERSION) throw bad(ver > SCHEMA_VERSION ? 'This backup was made by a newer version of Ledjoro than the one you are running, so this version cannot read it safely. Nothing was changed. Update Ledjoro first, then try again.' : 'This file opened, but it is not a Ledjoro database, so nothing was restored.');
      for (const t of REQUIRED_TABLES) if (!src.prepare("SELECT 1 FROM sqlite_master WHERE type='table' AND name=?").get(t)) throw bad(`This file opened, but it is missing a part every Ledjoro database has (${t}), so it is not a complete Ledjoro backup. Nothing was restored.`);
      const admins = src.prepare(`SELECT COUNT(*) n FROM users u JOIN roles r ON r.id=u.role_id WHERE u.active=1 AND r.permissions LIKE '%"users.manage"%'`).get().n;
      if (!admins) throw bad('This backup has no active administrator, so nobody could sign in after restoring it. Nothing was changed. (Use a backup from a time when an administrator existed.)');
      info = {
        company: src.prepare("SELECT value FROM settings WHERE key='company_name'").get()?.value || '',
        users: src.prepare('SELECT COUNT(*) n FROM users').get().n, entries: src.prepare('SELECT COUNT(*) n FROM journal_entries').get().n, schema_version: ver,
      };
    } finally { src.close(); }

    // 1) safety copy of what is there now, so a restore can itself be undone
    const stamp = new Date().toISOString().replace(/[-:T]/g, '').slice(0, 14);
    const safety = `pre-restore-${stamp}.obbak`;
    fs.mkdirSync(dataDir, { recursive: true, mode: 0o700 });
    fs.writeFileSync(path.join(dataDir, safety), await createBackup(db, { mode: hasRecoveryLink(db) ? 'recovery' : 'none' }), { mode: 0o600 });

    // 2) atomic swap
    swapContents(db, tmp);
    return { ...info, safety_copy: safety };
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
}

function swapContents(db, file) {
  const triggers = db.prepare("SELECT name, sql FROM sqlite_master WHERE type='trigger'").all();
  const tables = db.prepare("SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'").all().map((r) => r.name);
  db.exec(`ATTACH DATABASE '${file.replace(/'/g, "''")}' AS restore_src`);
  try {
    db.exec('PRAGMA foreign_keys = OFF');
    db.exec('BEGIN IMMEDIATE');
    try {
      // the append-only / immutable triggers would (rightly) block wholesale replacement, so they are dropped and recreated INSIDE the transaction
      for (const t of triggers) db.exec(`DROP TRIGGER ${q(t.name)}`);
      const inBackup = new Set(db.prepare("SELECT name FROM restore_src.sqlite_master WHERE type='table'").all().map((r) => r.name));
      for (const t of tables) {
        db.exec(`DELETE FROM main.${q(t)}`);
        if (!inBackup.has(t)) continue; // a table that did not exist when the backup was made (older schema) simply ends up empty
        const live = db.prepare(`PRAGMA main.table_info(${q(t)})`).all().map((c) => c.name);
        const old = new Set(db.prepare(`PRAGMA restore_src.table_info(${q(t)})`).all().map((c) => c.name));
        const cols = live.filter((c) => old.has(c)).map(q).join(', ');
        if (cols) db.exec(`INSERT INTO main.${q(t)} (${cols}) SELECT ${cols} FROM restore_src.${q(t)}`);
      }
      db.exec('DELETE FROM main.sessions'); // everyone signs in again
      for (const t of triggers) db.exec(t.sql);
      if (db.prepare('PRAGMA foreign_key_check').all().length) throw bad('The backup is internally inconsistent (records that point at records that do not exist), so it was not restored and your current books were not changed.');
      db.exec('COMMIT');
    } catch (e) { try { db.exec('ROLLBACK'); } catch { /* nothing open */ } throw e; }
  } finally {
    try { db.exec('DETACH DATABASE restore_src'); } catch { /* ignore */ }
    db.exec('PRAGMA foreign_keys = ON');
  }
}

/** Passphrase-only encryption of raw bytes (used by tools and tests; real backups go through createBackup). */
const encrypt = (plain, passphrase) => encryptData(plain, { passphrase, list: [], secret: null });

module.exports = { encrypt, createBackup, restoreBackup, restoreIntoLive, inspect, decryptBackup, checkPassphrase, lockSecretWithRecoveryKey, hasRecoveryLink, ensureSecret };
