'use strict';
const crypto = require('node:crypto');
const { HttpError, bad } = require('./util');
const { audit } = require('./audit');
const { tx, getSetting, setSetting } = require('./db');

// ---------- passwords ----------
const SCRYPT = { N: 32768, r: 8, p: 1, keylen: 64, maxmem: 128 * 1024 * 1024 };
const COMMON = new Set(['password1234', 'passwordpassword', '123456789012', 'qwertyuiopas', 'letmein12345', 'administrator', 'welcome12345', 'iloveyou1234', 'changeme1234']);

function hashPassword(pw) {
  const salt = crypto.randomBytes(16);
  const h = crypto.scryptSync(pw, salt, SCRYPT.keylen, SCRYPT);
  return `scrypt$${SCRYPT.N}$${SCRYPT.r}$${SCRYPT.p}$${salt.toString('base64')}$${h.toString('base64')}`;
}
function verifyPassword(pw, stored) {
  const parts = String(stored).split('$');
  if (parts.length !== 6 || parts[0] !== 'scrypt') return false;
  const [, N, r, p, salt, hash] = parts;
  const expected = Buffer.from(hash, 'base64');
  const got = crypto.scryptSync(pw, Buffer.from(salt, 'base64'), expected.length, { N: +N, r: +r, p: +p, maxmem: SCRYPT.maxmem });
  return got.length === expected.length && crypto.timingSafeEqual(got, expected);
}
const DUMMY_HASH = hashPassword(crypto.randomBytes(16).toString('hex')); // equalises timing for unknown users

function checkPasswordPolicy(pw, username = '') {
  if (typeof pw !== 'string') throw bad('Type a password.');
  if (pw.length < 12) throw bad('The password is too short: it needs at least 12 characters. A few ordinary words joined together is easy to remember and hard to guess.');
  if (pw.length > 256) throw bad('The password is longer than 256 characters, which is more than Ledjoro accepts.');
  if (COMMON.has(pw.toLowerCase())) throw bad('That password is one of the most commonly guessed ones. Choose something less obvious, such as a short sentence.');
  if (username && pw.toLowerCase().includes(String(username).toLowerCase())) throw bad('The password contains your username, which makes it easy to guess. Choose a password that does not include it.');
  if (/^(.)\1+$/.test(pw)) throw bad('The password is just one character repeated, which is easy to guess. Choose something less predictable.');
}

// ---------- in-memory rate limiting ----------
class Limiter {
  constructor(max, windowMs) { this.max = max; this.windowMs = windowMs; this.hits = new Map(); }
  hit(key) {
    const now = Date.now();
    const e = this.hits.get(key);
    if (!e || now > e.reset) { this.hits.set(key, { n: 1, reset: now + this.windowMs }); return true; }
    e.n++;
    return e.n <= this.max;
  }
  clear(key) { this.hits.delete(key); }
  sweep() { const now = Date.now(); for (const [k, e] of this.hits) if (now > e.reset) this.hits.delete(k); }
}
const loginIpLimiter = new Limiter(30, 15 * 60 * 1000);
const apiLimiter = new Limiter(900, 60 * 1000);
setInterval(() => { loginIpLimiter.sweep(); apiLimiter.sweep(); }, 60 * 1000).unref();

// ---------- sessions ----------
const sha256 = (s) => crypto.createHash('sha256').update(s).digest('hex');

function createSession(db, userId, ip, cfg) {
  const token = crypto.randomBytes(32).toString('base64url');
  const csrf = crypto.randomBytes(24).toString('base64url');
  const now = Date.now();
  db.prepare('INSERT INTO sessions(id,user_id,csrf,created_at,last_seen,expires_at,ip) VALUES (?,?,?,?,?,?,?)')
    .run(sha256(token), userId, csrf, now, now, now + cfg.sessionAbsoluteMs, ip || '');
  return { token, csrf };
}

function loadSession(db, token, cfg) {
  if (!token || typeof token !== 'string' || token.length > 100) return null;
  const id = sha256(token);
  const s = db.prepare(`SELECT s.*, u.username, u.display_name, u.active, u.must_change_password, r.name AS role_name, r.permissions
    FROM sessions s JOIN users u ON u.id=s.user_id JOIN roles r ON r.id=u.role_id WHERE s.id=?`).get(id);
  if (!s) return null;
  const now = Date.now();
  if (!s.active || now > s.expires_at || now - s.last_seen > cfg.sessionIdleMs) {
    db.prepare('DELETE FROM sessions WHERE id=?').run(id);
    return null;
  }
  if (now - s.last_seen > 30000) db.prepare('UPDATE sessions SET last_seen=? WHERE id=?').run(now, id);
  return {
    id, csrf: s.csrf,
    user: { id: s.user_id, username: s.username, displayName: s.display_name, role: s.role_name, mustChangePassword: !!s.must_change_password },
    permissions: new Set(JSON.parse(s.permissions)),
  };
}

const destroySession = (db, token) => token && db.prepare('DELETE FROM sessions WHERE id=?').run(sha256(token));
const destroyUserSessions = (db, userId) => db.prepare('DELETE FROM sessions WHERE user_id=?').run(userId);

// ---------- login ----------
const LOCK_STEPS = [0, 0, 0, 0, 0, 60e3, 5 * 60e3, 15 * 60e3, 60 * 60e3]; // after N failures

function login(db, username, password, ip, cfg) {
  const generic = new HttpError(401, 'The username or password is not right. Check for typing mistakes and that Caps Lock is off. After five wrong tries in a row the account is locked for a minute, then for longer.');
  if (typeof username !== 'string' || typeof password !== 'string' || username.length > 100 || password.length > 256) throw generic;
  if (!loginIpLimiter.hit(ip || 'unknown')) throw new HttpError(429, 'There have been too many sign-in attempts from this computer in a short time, so Ledjoro is pausing them to protect your books. Wait a few minutes and try again.');
  const u = db.prepare('SELECT * FROM users WHERE username=?').get(username);
  const now = Date.now();
  if (!u) { verifyPassword(password, DUMMY_HASH); throw generic; }
  if (u.locked_until > now) { verifyPassword(password, DUMMY_HASH); throw new HttpError(429, `This account is locked for a short while because of several wrong passwords in a row. Try again in about ${Math.max(1, Math.ceil((u.locked_until - now) / 60000))} minute(s), or reset the password with your recovery key.`); }
  const ok = verifyPassword(password, u.password_hash);
  if (!ok || !u.active) {
    const n = u.failed_attempts + 1;
    const lock = LOCK_STEPS[Math.min(n, LOCK_STEPS.length - 1)];
    db.prepare('UPDATE users SET failed_attempts=?, locked_until=? WHERE id=?').run(n, lock ? now + lock : 0, u.id);
    audit(db, { user: { id: u.id, username: u.username }, ip }, 'login.failed', 'user', u.id, `attempt ${n}`);
    throw generic;
  }
  db.prepare('UPDATE users SET failed_attempts=0, locked_until=0 WHERE id=?').run(u.id);
  const sess = createSession(db, u.id, ip, cfg); // fresh session id on every login (no fixation)
  audit(db, { user: { id: u.id, username: u.username }, ip }, 'login.ok', 'user', u.id);
  return sess;
}

// ---------- recovery key ----------
// A one-time-use secret shown once (setup, or "generate new key"). Only a scrypt hash is stored. It lets the account owner set a
// new password if the old one is lost; using it issues a fresh key. 100 bits of entropy, so it is not guessable even offline.
const KEY_ALPHABET = '0123456789ABCDEFGHJKMNPQRSTVWXYZ'; // Crockford base32: no I, L, O, U
function newRecoveryKey() {
  const b = crypto.randomBytes(20);
  let out = '';
  for (let i = 0; i < 20; i++) out += KEY_ALPHABET[b[i] & 31];
  return out.match(/.{5}/g).join('-');
}
const { normKey } = require('./util');
const Backup = require('./backup');
function setRecoveryKey(db, userId) {
  const key = newRecoveryKey();
  setSetting(db, 'recovery:' + userId, hashPassword(normKey(key)));
  Backup.lockSecretWithRecoveryKey(db, userId, key); // from now on this key also opens backups
  return key;
}
/** Is this the user's current recovery key? (Used to link a key that was issued before backups could use it.) */
function checkRecoveryKey(db, userId, rawKey) {
  const stored = getSetting(db, 'recovery:' + userId);
  return !!stored && verifyPassword(normKey(rawKey), stored);
}
const recoverLimiter = new Limiter(10, 15 * 60 * 1000);
setInterval(() => recoverLimiter.sweep(), 60 * 1000).unref();

function recover(db, username, key, newPassword, ip, cfg) {
  const generic = new HttpError(401, 'The username or recovery key is not right. A recovery key looks like XXXXX-XXXXX-XXXXX-XXXXX, and each one works only once (a new one is issued after every use). Check for typing mistakes.');
  if (typeof username !== 'string' || typeof key !== 'string' || typeof newPassword !== 'string' || username.length > 100 || key.length > 64) throw generic;
  if (!recoverLimiter.hit(ip || 'unknown')) throw new HttpError(429, 'There have been too many tries in a short time, so Ledjoro is pausing them to protect your books. Wait a few minutes and try again.');
  const u = db.prepare('SELECT * FROM users WHERE username=?').get(username);
  const stored = u ? getSetting(db, 'recovery:' + u.id) : '';
  const now = Date.now();
  if (!u || !stored || !u.active) { verifyPassword(normKey(key), DUMMY_HASH); throw generic; }
  if (u.locked_until > now) { verifyPassword(normKey(key), DUMMY_HASH); throw new HttpError(429, `This account is locked for a short while because of several wrong passwords in a row. Try again in about ${Math.max(1, Math.ceil((u.locked_until - now) / 60000))} minute(s), or reset the password with your recovery key.`); }
  if (!verifyPassword(normKey(key), stored)) {
    const n = u.failed_attempts + 1;
    const lock = LOCK_STEPS[Math.min(n, LOCK_STEPS.length - 1)];
    db.prepare('UPDATE users SET failed_attempts=?, locked_until=? WHERE id=?').run(n, lock ? now + lock : 0, u.id);
    audit(db, { user: { id: u.id, username: u.username }, ip }, 'recover.failed', 'user', u.id, `attempt ${n}`);
    throw generic;
  }
  checkPasswordPolicy(newPassword, u.username);
  return tx(db, () => {
    db.prepare('UPDATE users SET password_hash=?, must_change_password=0, failed_attempts=0, locked_until=0 WHERE id=?').run(hashPassword(newPassword), u.id);
    destroyUserSessions(db, u.id);
    const recoveryKey = setRecoveryKey(db, u.id); // single use: the key just used is now dead
    const sess = createSession(db, u.id, ip, cfg);
    audit(db, { user: { id: u.id, username: u.username }, ip }, 'password.recover', 'user', u.id);
    return { ...sess, recoveryKey };
  });
}

function requirePerm(session, perm) {
  if (!session) throw new HttpError(401, 'You are not signed in (you may have been signed out after 30 minutes without activity). Sign in again.');
  if (!session.permissions.has(perm)) throw new HttpError(403, 'Your role does not allow this. An administrator can change your role under Settings, Users.');
}

module.exports = {
  hashPassword, verifyPassword, checkPasswordPolicy, createSession, loadSession, destroySession, destroyUserSessions,
  login, requirePerm, apiLimiter, loginIpLimiter, sha256, setRecoveryKey, checkRecoveryKey, recover,
  /** Test hook: forget all in-memory rate-limit counters. */
  resetLimiters: () => { for (const l of [apiLimiter, loginIpLimiter, recoverLimiter]) l.hits.clear(); },
};
