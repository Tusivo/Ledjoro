'use strict';
const path = require('node:path');
const crypto = require('node:crypto');
const { getSetting, setSetting, tx, PERMISSIONS } = require('./db');
const U = require('./util');
const { HttpError, bad } = U;
const A = require('./auth');
const { audit, verifyAudit } = require('./audit');
const D = require('./domain');
const R = require('./reports');
const ST = require('./salestax');
const CR = require('./credits');
const C = require('./convert');
const { reverseEntry, checkLedgerIntegrity } = require('./ledger');
const Backup = require('./backup');
const Recon = require('./reconcile');
const AutoBackup = require('./autobackup');
const Search = require('./search');
const Qbwc = require('./qbwc');
const Watch = require('./watchfolder');
const WA = require('./webauthn');
const { explainDbError, unexpected } = require('./explain');
const Health = require('./health');
const Assets = require('./fixedassets');

const SECURITY_HEADERS = {
  'Content-Security-Policy': "default-src 'none'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'",
  'X-Content-Type-Options': 'nosniff', 'X-Frame-Options': 'DENY', 'Referrer-Policy': 'no-referrer',
  'Permissions-Policy': 'camera=(), microphone=(), geolocation=(), payment=(), usb=()',
  'Cross-Origin-Opener-Policy': 'same-origin', 'Cross-Origin-Resource-Policy': 'same-origin', 'X-Permitted-Cross-Domain-Policies': 'none',
};

function createApp(db, cfg) {
  const routes = [];
  const route = (method, p, perm, handler, opts = {}) => {
    const keys = [];
    const re = new RegExp('^' + p.replace(/:(\w+)/g, (_, k) => (keys.push(k), '([^/]+)')) + '$');
    routes.push({ method, re, keys, perm, handler, ...opts });
  };
  const cookieName = cfg.secureCookies ? '__Host-ob_session' : 'ob_session';
  const state = { setupToken: db.prepare('SELECT COUNT(*) n FROM users').get().n === 0 ? (cfg.setupToken.length >= 16 ? cfg.setupToken : crypto.randomBytes(18).toString('base64url')) : null };
  const id = (p) => { const n = Number(p); if (!Number.isSafeInteger(n) || n <= 0) throw bad('The item number is not valid. Go back and choose the item again.'); return n; };

  const setCookie = (res, token) => {
    res.setHeader('Set-Cookie', `${cookieName}=${token}; Path=/; HttpOnly; SameSite=Strict${cfg.secureCookies ? '; Secure' : ''}`);
  };
  const clearCookie = (res) => res.setHeader('Set-Cookie', `${cookieName}=; Path=/; HttpOnly; SameSite=Strict; Max-Age=0${cfg.secureCookies ? '; Secure' : ''}`);

  route('GET', '/api/ping', null, () => ({ app: 'ledjoro' }), { allowAnon: true, allowPwChange: true });

  // ---------- auth & setup ----------
  route('GET', '/api/session', null, ({ session }) => {
    if (!session) return { authenticated: false, setupRequired: !!state.setupToken };
    return { authenticated: true, csrf: session.csrf, user: session.user, permissions: [...session.permissions], company: getSetting(db, 'company_name'), integrity: state.integrity ?? null, backup: { last: getSetting(db, 'last_backup') || '', auto: AutoBackup.settings(db).enabled, error: getSetting(db, 'auto_backup_error') || '' } };
  }, { allowAnon: true, allowPwChange: true });

  route('POST', '/api/setup', null, ({ b, ip, res }) => {
    if (!state.setupToken) throw new HttpError(404, 'This company has already been set up. Sign in with its administrator account instead.');
    if (!A.loginIpLimiter.hit('setup:' + ip)) throw new HttpError(429, 'Too many tries in a short time. Wait a few minutes and try again.');
    const given = Buffer.from(U.vStr(b.token, 'setup token', { required: true, max: 100 }));
    const want = Buffer.from(state.setupToken);
    if (given.length !== want.length || !crypto.timingSafeEqual(given, want)) throw new HttpError(403, 'That setup token is not right. It is shown on the first-time setup screen and normally filled in for you; if you typed it, check every character.');
    const username = U.vStr(b.username, 'username', { required: true, min: 3, max: 50 });
    if (!/^[A-Za-z0-9._-]+$/.test(username)) throw bad('A username can only use letters, numbers, dots, dashes and underscores (no spaces). Change it and try again.');
    A.checkPasswordPolicy(b.password, username);
    let recoveryKey;
    const sess = tx(db, () => {
      const role = db.prepare("SELECT id FROM roles WHERE name='Admin'").get();
      const r = db.prepare('INSERT INTO users(username,display_name,password_hash,role_id,created_at) VALUES (?,?,?,?,?)').run(username, U.vStr(b.display_name, 'name', { max: 100 }) || username, A.hashPassword(b.password), role.id, Date.now());
      setSetting(db, 'company_name', U.vStr(b.company_name, 'company name', { max: 120 }) || 'My Company');
      audit(db, { user: { id: Number(r.lastInsertRowid), username }, ip }, 'setup.complete', 'user', r.lastInsertRowid);
      recoveryKey = A.setRecoveryKey(db, Number(r.lastInsertRowid));
      return A.createSession(db, Number(r.lastInsertRowid), ip, cfg);
    });
    state.setupToken = null;
    setCookie(res, sess.token);
    return { ok: true, recovery_key: recoveryKey };
  }, { allowAnon: true, noCsrf: true });

  route('POST', '/api/login', null, ({ b, ip, res }) => {
    const sess = A.login(db, b.username, b.password, ip, cfg);
    setCookie(res, sess.token);
    return { ok: true };
  }, { allowAnon: true, noCsrf: true, noTx: true });

  // Lost password: the recovery key (shown once at setup) sets a new one and is replaced by a fresh key.
  route('POST', '/api/recover', null, ({ b, ip, res }) => {
    const out = A.recover(db, b.username, b.recovery_key, b.new_password, ip, cfg);
    setCookie(res, out.token);
    return { ok: true, recovery_key: out.recoveryKey };
  }, { allowAnon: true, noCsrf: true, noTx: true });

  // Signed-in users can replace their recovery key (needs the current password; the old key stops working).
  route('POST', '/api/recovery-key', null, ({ b, ctx, session }) => {
    const u = db.prepare('SELECT * FROM users WHERE id=?').get(session.user.id);
    if (!A.verifyPassword(String(b.password ?? ''), u.password_hash)) throw new HttpError(403, 'That is not your current password. Check for typing mistakes and that Caps Lock is off.');
    const key = A.setRecoveryKey(db, u.id);
    audit(db, ctx, 'recovery_key.regenerate', 'user', u.id);
    return { recovery_key: key };
  });

  // ---------- passkeys (WebAuthn) ----------
  // The sign-in prompt is shown by Windows (Windows Hello, a phone, or a security key); this side issues one-time challenges and checks the answer.
  const pkRp = () => ({ rpId: cfg.passkeyRpId, origin: 'https://' + cfg.passkeyRpId });
  const pkChallenges = new Map();
  const pkNew = (kind, extra = {}) => {
    const now = Date.now();
    for (const [k, v] of pkChallenges) if (v.exp < now) pkChallenges.delete(k);
    if (pkChallenges.size > 500) throw new HttpError(429, 'Too many passkey attempts. Try again in a few minutes.');
    const id = crypto.randomBytes(12).toString('base64url'), challenge = crypto.randomBytes(32).toString('base64url');
    pkChallenges.set(id, { kind, challenge, exp: now + 3 * 60e3, ...extra });
    return { id, challenge };
  };
  const pkTake = (id, kind) => { const c = pkChallenges.get(String(id)); pkChallenges.delete(String(id)); if (!c || c.kind !== kind || c.exp < Date.now()) throw bad('That passkey request expired. Please try again.'); return c; };

  route('GET', '/api/passkeys', null, ({ session }) => db.prepare('SELECT id, name, created_at, last_used_at FROM passkeys WHERE user_id=? ORDER BY id').all(session.user.id));

  route('POST', '/api/passkeys/register/options', null, ({ b, session, ip }) => {
    if (!A.loginIpLimiter.hit('pk:' + ip)) throw new HttpError(429, 'Too many tries in a short time. Wait a few minutes and try again.');
    const u = db.prepare('SELECT * FROM users WHERE id=?').get(session.user.id);
    if (!A.verifyPassword(String(b.password ?? ''), u.password_hash)) throw new HttpError(403, 'That is not your current password. Check for typing mistakes and that Caps Lock is off.');
    const mine = db.prepare('SELECT credential_id, user_handle FROM passkeys WHERE user_id=?').all(u.id);
    if (mine.length >= 10) throw bad('You already have 10 passkeys, the most allowed. Remove one first.');
    const handle = mine[0]?.user_handle ?? crypto.randomBytes(16).toString('base64url');
    const ch = pkNew('register', { userId: u.id, handle });
    return { challengeId: ch.id, challenge: ch.challenge, ...pkRp(), rpName: 'Ledjoro', user: { handle, name: u.username, displayName: u.display_name || u.username }, exclude: mine.map((m) => m.credential_id) };
  });

  route('POST', '/api/passkeys/register/finish', null, ({ b, session, ctx }) => {
    const c = pkTake(b.challengeId, 'register');
    if (c.userId !== session.user.id) throw bad('This passkey request was started by a different user, so it was refused. Start again from My account.');
    const rp = pkRp();
    const r = WA.verifyRegistration({ clientDataJSON: WA.fromB64u(b.clientDataJSON, 'Passkey data'), attestationObject: WA.fromB64u(b.attestationObject, 'Passkey data', 8192) }, { challenge: c.challenge, rpId: rp.rpId, origins: [rp.origin] });
    const name = U.vStr(b.name, 'name', { max: 60 }) || 'Passkey';
    let pid;
    try {
      pid = Number(db.prepare('INSERT INTO passkeys(user_id,credential_id,public_key,alg,sign_count,user_handle,name,created_at) VALUES (?,?,?,?,?,?,?,?)')
        .run(session.user.id, WA.b64u(r.credentialId), r.spki, r.alg, r.signCount, c.handle, name, Date.now()).lastInsertRowid);
    } catch (e) { if (/UNIQUE/.test(e.message)) throw bad('This passkey is already on your account. Use a different device or security key, or remove the old entry first.'); throw e; }
    audit(db, ctx, 'passkey.add', 'user', session.user.id, name);
    return { id: pid };
  });

  route('DELETE', '/api/passkeys/:id', null, ({ params, session, ctx }) => {
    const r = db.prepare('DELETE FROM passkeys WHERE id=? AND user_id=?').run(id(params.id), session.user.id);
    if (!r.changes) throw new HttpError(404, 'That passkey is not on your account any more. Refresh the list.');
    audit(db, ctx, 'passkey.remove', 'user', session.user.id, String(params.id));
    return { ok: true };
  });

  route('POST', '/api/passkeys/login/options', null, ({ ip }) => {
    if (!A.loginIpLimiter.hit(ip || 'unknown')) throw new HttpError(429, 'There have been too many sign-in attempts from this computer in a short time, so Ledjoro is pausing them to protect your books. Wait a few minutes and try again.');
    const ch = pkNew('login');
    return { challengeId: ch.id, challenge: ch.challenge, ...pkRp() };
  }, { allowAnon: true, noCsrf: true, noTx: true });

  route('POST', '/api/passkeys/login/finish', null, ({ b, ip, res }) => {
    if (!A.loginIpLimiter.hit(ip || 'unknown')) throw new HttpError(429, 'There have been too many sign-in attempts from this computer in a short time, so Ledjoro is pausing them to protect your books. Wait a few minutes and try again.');
    const c = pkTake(b.challengeId, 'login');
    const rp = pkRp(), fail = (msg, uid) => { audit(db, { user: { id: uid ?? null, username: '' }, ip }, 'passkey.failed', 'user', uid ?? '', msg); return new HttpError(401, msg); };
    let credId;
    try { credId = WA.b64u(WA.fromB64u(b.credentialId, 'Passkey')); } catch { throw fail('The passkey was not accepted. Try again, or sign in with your password.'); }
    const row = db.prepare('SELECT p.*, u.username, u.active, u.locked_until FROM passkeys p JOIN users u ON u.id=p.user_id WHERE p.credential_id=?').get(credId);
    if (!row || !row.active) throw fail('That passkey is not set up in this company, or its account has been turned off. Sign in with your password instead.');
    if (row.locked_until > Date.now()) throw new HttpError(429, 'This account is locked for a short while because of several wrong tries in a row. Try again in a few minutes, or sign in with your password.');
    if (b.userHandle && String(b.userHandle) !== row.user_handle) throw fail('The passkey was not accepted. Try again, or sign in with your password.', row.user_id);
    let out;
    try {
      out = WA.verifyAssertion({ clientDataJSON: WA.fromB64u(b.clientDataJSON, 'Passkey data'), authenticatorData: WA.fromB64u(b.authenticatorData, 'Passkey data'), signature: WA.fromB64u(b.signature, 'Passkey data') },
        { spki: row.public_key, alg: row.alg, signCount: row.sign_count }, { challenge: c.challenge, rpId: rp.rpId, origins: [rp.origin] });
    } catch (e) { throw fail(e.message || 'The passkey was not accepted. Try again, or sign in with your password.', row.user_id); }
    db.prepare('UPDATE passkeys SET sign_count=?, last_used_at=? WHERE id=?').run(out.signCount, Date.now(), row.id);
    db.prepare('UPDATE users SET failed_attempts=0, locked_until=0 WHERE id=?').run(row.user_id);
    const sess = A.createSession(db, row.user_id, ip, cfg);
    audit(db, { user: { id: row.user_id, username: row.username }, ip }, 'login.ok', 'user', row.user_id, 'passkey');
    setCookie(res, sess.token);
    return { ok: true };
  }, { allowAnon: true, noCsrf: true, noTx: true });

  route('POST', '/api/logout', null, ({ cookieToken, res, ctx }) => {
    audit(db, ctx, 'logout');
    A.destroySession(db, cookieToken); clearCookie(res); return { ok: true };
  }, { allowPwChange: true });

  route('POST', '/api/password', null, ({ b, ctx, res, session, ip }) => {
    const u = db.prepare('SELECT * FROM users WHERE id=?').get(session.user.id);
    if (!A.verifyPassword(String(b.current ?? ''), u.password_hash)) throw new HttpError(403, 'That is not your current password. Check for typing mistakes and that Caps Lock is off.');
    A.checkPasswordPolicy(b.next, u.username);
    if (b.next === b.current) throw bad('The new password is the same as the current one. Choose a different password.');
    db.prepare('UPDATE users SET password_hash=?, must_change_password=0 WHERE id=?').run(A.hashPassword(b.next), u.id);
    A.destroyUserSessions(db, u.id); // revoke every existing session, then issue a fresh one
    const s = A.createSession(db, u.id, ip, cfg); setCookie(res, s.token);
    audit(db, ctx, 'password.change', 'user', u.id);
    return { ok: true };
  }, { allowPwChange: true });

  // ---------- users / roles / settings ----------
  route('GET', '/api/users', 'users.manage', () => db.prepare('SELECT u.id,u.username,u.display_name,u.active,u.must_change_password,u.role_id,r.name AS role FROM users u JOIN roles r ON r.id=u.role_id ORDER BY u.username').all());
  const adminsLeft = (exceptUserId) => db.prepare(`SELECT COUNT(*) n FROM users u JOIN roles r ON r.id=u.role_id WHERE u.active=1 AND u.id<>? AND r.permissions LIKE '%"users.manage"%'`).get(exceptUserId ?? 0).n;
  route('POST', '/api/users', 'users.manage', ({ b, ctx }) => {
    const username = U.vStr(b.username, 'username', { required: true, min: 3, max: 50 });
    if (!/^[A-Za-z0-9._-]+$/.test(username)) throw bad('A username can only use letters, numbers, dots, dashes and underscores (no spaces). Change it and try again.');
    A.checkPasswordPolicy(b.password, username);
    const roleId = U.vInt(b.role_id, 'role');
    if (!db.prepare('SELECT 1 FROM roles WHERE id=?').get(roleId)) throw bad('That role does not exist any more. Choose a role from the list.');
    try {
      const r = db.prepare('INSERT INTO users(username,display_name,password_hash,role_id,must_change_password,created_at) VALUES (?,?,?,?,1,?)').run(username, U.vStr(b.display_name, 'name', { max: 100 }) || username, A.hashPassword(b.password), roleId, Date.now());
      audit(db, ctx, 'user.create', 'user', r.lastInsertRowid, username);
      return { id: Number(r.lastInsertRowid) };
    } catch (e) { if (/UNIQUE/.test(e.message)) throw bad('Someone already has that username. Choose a different one.'); throw e; }
  });
  route('PATCH', '/api/users/:id', 'users.manage', ({ params, b, ctx }) => {
    const uid = id(params.id);
    const u = db.prepare('SELECT * FROM users WHERE id=?').get(uid);
    if (!u) throw new HttpError(404, 'That user could not be found. They may have been removed; refresh the list.');
    const roleId = b.role_id !== undefined ? U.vInt(b.role_id, 'role') : u.role_id;
    const role = db.prepare('SELECT * FROM roles WHERE id=?').get(roleId);
    if (!role) throw bad('That role does not exist any more. Choose a role from the list.');
    const active = b.active !== undefined ? U.vBool(b.active) : u.active;
    const stillAdmin = active && JSON.parse(role.permissions).includes('users.manage');
    if (!stillAdmin && adminsLeft(uid) === 0) throw bad('This change would leave nobody able to manage users, so nobody could add or fix accounts later. Keep at least one active administrator.');
    tx(db, () => {
      db.prepare('UPDATE users SET role_id=?, active=?, display_name=?, failed_attempts=0, locked_until=0 WHERE id=?').run(roleId, active, b.display_name !== undefined ? U.vStr(b.display_name, 'name', { max: 100 }) : u.display_name, uid);
      if (b.password !== undefined) {
        A.checkPasswordPolicy(b.password, u.username);
        db.prepare('UPDATE users SET password_hash=?, must_change_password=1 WHERE id=?').run(A.hashPassword(b.password), uid);
        A.destroyUserSessions(db, uid);
      }
      if (!active || roleId !== u.role_id) A.destroyUserSessions(db, uid);
      audit(db, ctx, 'user.update', 'user', uid, `role=${role.name} active=${active}${b.password !== undefined ? ' password-reset' : ''}`);
    });
    return { ok: true };
  });
  route('GET', '/api/roles', 'view', () => db.prepare('SELECT * FROM roles ORDER BY builtin DESC, name').all().map((r) => ({ ...r, permissions: JSON.parse(r.permissions) })));
  route('POST', '/api/roles', 'users.manage', ({ b, ctx }) => {
    const perms = U.vArr(b.permissions, 'permissions', { max: 50 });
    if (perms.some((p) => !PERMISSIONS.includes(p))) throw bad('One of the permissions chosen is not one Ledjoro knows. Close this screen, open it again and choose again.');
    try {
      const r = db.prepare('INSERT INTO roles(name,permissions) VALUES (?,?)').run(U.vStr(b.name, 'name', { required: true, max: 50 }), JSON.stringify([...new Set(['view', ...perms])]));
      audit(db, ctx, 'role.create', 'role', r.lastInsertRowid, b.name);
      return { id: Number(r.lastInsertRowid) };
    } catch (e) { if (/UNIQUE/.test(e.message)) throw bad('You already have a role with that name. Choose a different name.'); throw e; }
  });
  route('PATCH', '/api/roles/:id', 'users.manage', ({ params, b, ctx }) => {
    const r = db.prepare('SELECT * FROM roles WHERE id=?').get(id(params.id));
    if (!r) throw new HttpError(404, 'That role could not be found. It may have been deleted; refresh the list.');
    if (r.builtin) throw bad('The built-in roles are fixed so that everyone agrees what they mean. To change what people may do, create a new custom role with the permissions you want, then give people that role.');
    const perms = U.vArr(b.permissions, 'permissions', { max: 50 });
    if (perms.some((p) => !PERMISSIONS.includes(p))) throw bad('One of the permissions chosen is not one Ledjoro knows. Close this screen, open it again and choose again.');
    db.prepare('UPDATE roles SET permissions=? WHERE id=?').run(JSON.stringify([...new Set(['view', ...perms])]), r.id);
    audit(db, ctx, 'role.update', 'role', r.id, r.name);
    return { ok: true };
  });
  route('GET', '/api/settings', 'view', () => ({ company_name: getSetting(db, 'company_name'), closing_date: getSetting(db, 'closing_date'), last_backup: getSetting(db, 'last_backup') }));
  route('PUT', '/api/settings', 'settings.manage', ({ b, ctx }) => {
    if (b.company_name !== undefined) setSetting(db, 'company_name', U.vStr(b.company_name, 'company name', { required: true, max: 120 }));
    if (b.closing_date !== undefined) setSetting(db, 'closing_date', U.vDate(b.closing_date, 'closing date', false) || '');
    audit(db, ctx, 'settings.update', 'settings', '', JSON.stringify({ company_name: b.company_name, closing_date: b.closing_date }));
    return { ok: true };
  });

  // ---------- accounting data ----------
  // ---------- company profile (what is printed on invoices) ----------
  const PROFILE = { street: 120, street2: 120, city: 80, state: 2, zip: 10, phone: 40, email: 120, website: 120, taxid: 40, note: 400 };
  route('GET', '/api/profile', 'view', () => ({
    name: getSetting(db, 'company_name'),
    ...Object.fromEntries(Object.keys(PROFILE).map((k) => [k, getSetting(db, 'profile_' + k)])),
    logo: getSetting(db, 'profile_logo'),
  }));
  route('PUT', '/api/profile', 'settings.manage', ({ b, ctx }) => {
    const changed = [];
    for (const [k, max] of Object.entries(PROFILE)) {
      if (b[k] === undefined) continue;
      let v = U.vStr(b[k], k, { max });
      if (k === 'state') { v = v.toUpperCase(); if (v && !/^[A-Z]{2}$/.test(v)) throw U.bad('The state must be the two letters the post office uses, such as NH, CA or TX.'); }
      if (k === 'zip' && v && !/^\d{5}(-\d{4})?$/.test(v)) throw U.bad('The ZIP code must be 5 digits (such as 03301) or 5+4 digits (such as 03301-1234).');
      setSetting(db, 'profile_' + k, v); changed.push(k);
    }
    if (b.logo !== undefined) {
      const v = b.logo === null ? '' : String(b.logo);
      if (v) {
        const raw = Buffer.from(v, 'base64');
        if (raw.length < 200 || raw[0] !== 0xff || raw[1] !== 0xd8) throw U.bad('The logo has to be a JPEG picture. Choose the logo again from the Settings page.');
        if (raw.length > 300 * 1024) throw U.bad('The logo picture is too large (over 300 KB). Choose a smaller picture.');
      }
      setSetting(db, 'profile_logo', v); changed.push('logo');
    }
    audit(db, ctx, 'profile.update', 'settings', '', changed.join(','));
    return { ok: true };
  });
  route('GET', '/api/dashboard', 'view', () => R.dashboard(db));
  route('GET', '/api/accounts', 'view', () => D.listAccounts(db));
  route('POST', '/api/accounts', 'accounting.write', ({ b, ctx }) => ({ id: D.createAccount(db, ctx, b) }));
  route('PATCH', '/api/accounts/:id', 'accounting.write', ({ params, b, ctx }) => (D.updateAccount(db, ctx, id(params.id), b), { ok: true }));
  route('GET', '/api/contacts', 'view', ({ query }) => D.listContacts(db, query.kind && U.vEnum(query.kind, 'kind', ['customer', 'vendor'])));
  const writePerm = (kind) => (kind === 'bill' || kind === 'vendor' ? 'purchases.write' : 'sales.write');
  route('POST', '/api/contacts', 'view', ({ b, ctx, session }) => {
    A.requirePerm(session, writePerm(b.kind));
    return { id: D.createContact(db, ctx, b) };
  });
  route('PATCH', '/api/contacts/:id', 'view', ({ params, b, ctx, session }) => {
    const c = db.prepare('SELECT kind FROM contacts WHERE id=?').get(id(params.id));
    A.requirePerm(session, writePerm(c?.kind));
    D.updateContact(db, ctx, id(params.id), b); return { ok: true };
  });
  route('GET', '/api/items', 'view', () => D.listItems(db));
  route('POST', '/api/items', 'view', ({ b, ctx, session }) => { A.requirePerm(session, 'sales.write'); return { id: D.createItem(db, ctx, b) }; });
  // ---------- sales tax ----------
  route('GET', '/api/salestax/rates', 'view', () => ST.listRates(db));
  route('POST', '/api/salestax/rates', 'sales.write', ({ b, ctx }) => ({ id: ST.createRate(db, ctx, b) }));
  route('PATCH', '/api/salestax/rates/:id', 'sales.write', ({ params, b, ctx }) => (ST.updateRate(db, ctx, id(params.id), b), { ok: true }));
  route('GET', '/api/salestax/worksheet', 'view', ({ query }) => ST.worksheet(db, query));
  route('GET', '/api/salestax/payments', 'view', () => ST.listRemittances(db));
  route('POST', '/api/salestax/payments', 'accounting.write', ({ b, ctx }) => ({ id: ST.createRemittance(db, ctx, b) }));
  route('POST', '/api/salestax/payments/:id/void', 'accounting.write', ({ params, ctx }) => (ST.voidRemittance(db, ctx, id(params.id)), { ok: true }));
  route('PATCH', '/api/items/:id', 'view', ({ params, b, ctx, session }) => { A.requirePerm(session, 'sales.write'); D.updateItem(db, ctx, id(params.id), b); return { ok: true }; });
  // ---------- credit memos ----------
  route('GET', '/api/credits', 'view', ({ query }) => CR.listCredits(db, query.status ? U.vEnum(query.status, 'status', ['open', 'used', 'void']) : null));
  route('GET', '/api/credits/:id', 'view', ({ params }) => CR.getCredit(db, id(params.id)));
  route('POST', '/api/credits', 'sales.write', ({ b, ctx }) => ({ id: CR.createCredit(db, ctx, b) }));
  route('POST', '/api/credits/:id/apply', 'sales.write', ({ params, b, ctx }) => ({ id: CR.applyCredit(db, ctx, id(params.id), b) }));
  route('POST', '/api/credits/:id/refund', 'sales.write', ({ params, b, ctx }) => ({ id: CR.refundCredit(db, ctx, id(params.id), b) }));
  route('POST', '/api/credits/:id/void', 'sales.write', ({ params, ctx }) => (CR.voidCredit(db, ctx, id(params.id)), { ok: true }));
  route('POST', '/api/credits/applications/:id/remove', 'sales.write', ({ params, ctx }) => (CR.removeApplication(db, ctx, id(params.id)), { ok: true }));
  route('POST', '/api/credits/refunds/:id/void', 'sales.write', ({ params, ctx }) => (CR.voidRefund(db, ctx, id(params.id)), { ok: true }));
  route('GET', '/api/documents', 'view', ({ query }) => D.listDocuments(db, U.vEnum(query.kind, 'kind', ['invoice', 'bill']), query.status ? U.vEnum(query.status, 'status', ['open', 'paid', 'void']) : null));
  route('GET', '/api/documents/:id', 'view', ({ params }) => D.getDocument(db, id(params.id)));
  route('POST', '/api/documents', 'view', ({ b, ctx, session }) => {
    A.requirePerm(session, writePerm(b.kind));
    return { id: D.createDocument(db, ctx, b) };
  });
  route('POST', '/api/documents/:id/void', 'view', ({ params, ctx, session }) => {
    const d = db.prepare('SELECT kind FROM documents WHERE id=?').get(id(params.id));
    A.requirePerm(session, writePerm(d?.kind));
    D.voidDocument(db, ctx, id(params.id)); return { ok: true };
  });
  route('POST', '/api/payments', 'view', ({ b, ctx, session }) => {
    A.requirePerm(session, b.kind === 'vendor' ? 'purchases.write' : 'sales.write');
    return { id: D.createPayment(db, ctx, b) };
  });
  route('POST', '/api/payments/:id/void', 'view', ({ params, ctx, session }) => {
    const p = db.prepare('SELECT kind FROM payments WHERE id=?').get(id(params.id));
    A.requirePerm(session, p?.kind === 'vendor' ? 'purchases.write' : 'sales.write');
    D.voidPayment(db, ctx, id(params.id)); return { ok: true };
  });
  route('GET', '/api/journal', 'view', () => D.listJournal(db));
  route('GET', '/api/journal/:id', 'view', ({ params }) => D.getEntry(db, id(params.id)));
  route('POST', '/api/journal', 'accounting.write', ({ b, ctx }) => ({ id: D.createJournal(db, ctx, b) }));
  route('POST', '/api/journal/:id/reverse', 'accounting.write', ({ params, b, ctx }) => {
    const rid = tx(db, () => {
      const e = db.prepare('SELECT source_type FROM journal_entries WHERE id=?').get(id(params.id));
      if (!e) throw new HttpError(404, 'That journal entry could not be found. It may have been removed; go back and refresh.');
      if (['invoice', 'bill', 'payment', 'credit_memo', 'credit_refund', 'salestax'].includes(e.source_type)) throw bad('This entry was made by an invoice, bill, credit memo, refund or payment, so it cannot be reversed on its own. Void that invoice, bill or payment instead; that corrects the books properly.');
      const r = reverseEntry(db, id(params.id), { date: U.vDate(b.date, 'date', false) || U.today(), userId: ctx.user.id });
      audit(db, ctx, 'journal.reverse', 'entry', params.id);
      return r;
    });
    return { id: rid };
  });

  // ---------- banking ----------
  route('GET', '/api/bank/transactions', 'view', ({ query }) => D.listBankTransactions(db, query.status ? U.vEnum(query.status, 'status', ['new', 'categorized', 'excluded']) : null, query.account_id ? id(query.account_id) : null));
  route('POST', '/api/bank/transactions/:id/categorize', 'banking.write', ({ params, b, ctx }) => ({ entry_id: D.categorizeBank(db, ctx, id(params.id), b) }));
  route('POST', '/api/bank/transactions/:id/exclude', 'banking.write', ({ params, ctx }) => (D.excludeBank(db, ctx, id(params.id)), { ok: true }));
  route('GET', '/api/bank/rules', 'view', () => D.listRules(db));
  route('POST', '/api/bank/rules', 'banking.write', ({ b, ctx }) => ({ id: D.createRule(db, ctx, b) }));
  route('POST', '/api/bank/rules/:id/remove', 'banking.write', ({ params, ctx }) => (Recon.removeRule(db, ctx, id(params.id)), { ok: true }));
  route('GET', '/api/bank/transactions/:id/matches', 'view', ({ params }) => Recon.matches(db, id(params.id)));
  route('POST', '/api/bank/transactions/:id/match', 'banking.write', ({ params, b, ctx }) => Recon.match(db, ctx, id(params.id), b.line_id));
  route('POST', '/api/bank/auto-match', 'banking.write', ({ ctx }) => ({ matched: Recon.autoMatch(db, ctx) }));
  // reconciliation: tick what is on the statement until the difference is zero
  route('GET', '/api/reconcile/start', 'view', ({ query }) => Recon.start(db, Number(query.account_id), query.statement_date));
  route('POST', '/api/reconcile', 'banking.write', ({ b, ctx }) => Recon.finish(db, ctx, b));
  route('GET', '/api/reconcile/history', 'view', ({ query }) => Recon.history(db, query.account_id ? U.vInt(Number(query.account_id), 'account') : null));
  route('POST', '/api/reconcile/:id/undo', 'banking.write', ({ params, ctx }) => Recon.undo(db, ctx, id(params.id)));
  route('GET', '/api/search', 'view', ({ query }) => Search.search(db, query.q));
  route('POST', '/api/bank/apply-rules', 'banking.write', ({ ctx }) => ({ applied: D.applyRules(db, ctx) }));

  // ---------- books health ----------
  route('GET', '/api/health-check', 'view', () => Health.booksHealth(db));

  // ---------- fixed assets ----------
  route('GET', '/api/assets', 'view', () => Assets.listAssets(db));
  route('POST', '/api/assets', 'accounting.write', ({ b, ctx }) => ({ id: Assets.createAsset(db, ctx, b) }));
  route('POST', '/api/assets/depreciate', 'accounting.write', ({ b, ctx }) => Assets.postDepreciation(db, ctx, b.through));
  route('GET', '/api/assets/:id/schedule', 'view', ({ params }) => {
    const a = db.prepare('SELECT * FROM fixed_assets WHERE id=?').get(id(params.id));
    if (!a) throw new HttpError(404, 'That asset could not be found. It may have been deleted; refresh the list.');
    return { asset: a, schedule: Assets.schedule(a) };
  });
  route('POST', '/api/assets/:id/dispose', 'accounting.write', ({ params, b, ctx }) => Assets.disposeAsset(db, ctx, id(params.id), b));

  // ---------- reports ----------
  route('GET', '/api/reports/:name', 'view', ({ params, query }) => R.run(db, params.name, query));

  // ---------- import / export ----------
  const MAP_KEYS = ['date', 'number', 'payee', 'account', 'memo', 'out', 'in', 'amount', 'balance'];
  const cleanMapping = (m) => { const o = {}; if (m && typeof m === 'object') for (const k of MAP_KEYS) if (Number.isInteger(m[k]) && m[k] >= 0 && m[k] < 200) o[k] = m[k]; return o; };
  const importModel = (b) => {
    const content = U.vStr(b.content, 'file', { required: true, max: cfg.maxImportBytes });
    const options = b.options && typeof b.options === 'object' ? b.options : {};
    const format = b.format ? U.vEnum(b.format, 'format', Object.keys(C.FORMATS)) : C.detectFormat(b.filename, content);
    if (!format) throw bad('Could not recognise this file: Ledjoro cannot tell what kind of file it is. Choose its format yourself in the Format list, or use "Match columns myself" if it is a CSV.');
    const opts = { asof: options.asof ? U.vDate(options.asof, 'opening balance date') : undefined, dateOrder: options.dateOrder === 'dmy' ? 'dmy' : 'mdy', mapping: cleanMapping(options.mapping), headerRows: Number.isInteger(options.headerRows) ? options.headerRows : 1, invert: options.invert === true, documents: options.documents !== false, register: options.register ? U.vStr(options.register, 'register account', { max: 120 }) : undefined };
    return { format, model: C.parse(format, content, opts, { maxRows: cfg.maxImportRows }) };
  };
  route('POST', '/api/import/sniff', 'import', ({ b }) => (() => { const content = U.vStr(b.content, 'file', { required: true, max: cfg.maxImportBytes }); return { ...C.sniff(content, { maxRows: cfg.maxImportRows }), detected: C.detectFormat(U.vStr(b.filename, 'filename', { max: 200 }), content) }; })(), { maxBody: cfg.maxImportBytes + 4096 });
  // the watched download folder (see watchfolder.js)
  route('GET', '/api/watch', 'import', () => Watch.status(db, true, cfg));
  route('POST', '/api/watch', 'import', ({ b, ctx }) => Watch.configure(db, ctx, b));
  route('POST', '/api/watch/scan', 'import', () => ({ files: Watch.scan(db, cfg) }));
  route('POST', '/api/watch/rescan', 'import', ({ ctx }) => ({ files: Watch.rescan(db, ctx) }));
  route('POST', '/api/watch/files/:hash/import', 'import', ({ params, b, ctx }) => Watch.importFile(db, ctx, params.hash, b.bank_account_id ? U.vInt(b.bank_account_id, 'bank account') : null, cfg));
  route('POST', '/api/watch/files/:hash/ignore', 'import', ({ params, ctx }) => Watch.ignoreFile(db, ctx, params.hash));

  // QuickBooks Desktop through the Web Connector (read-only; see qbwc.js)
  route('GET', '/api/qbwc/status', 'import', () => Qbwc.status(db));
  route('POST', '/api/qbwc/enable', 'import', async ({ ctx }) => Qbwc.enable(db, ctx, () => {}), { noTx: true });
  route('POST', '/api/qbwc/password', 'import', async ({ ctx }) => Qbwc.newPass(db, ctx), { noTx: true });
  route('POST', '/api/qbwc/disable', 'import', async ({ ctx }) => Qbwc.disable(db, ctx), { noTx: true });
  route('POST', '/api/qbwc/qwc', 'import', ({ b }) => Qbwc.qwc(db, b.read_only === true));
  route('GET', '/api/qbwc/preview', 'import', () => Qbwc.preview(db));
  route('POST', '/api/qbwc/commit', 'import', ({ ctx }) => Qbwc.commitPending(db, ctx));
  route('POST', '/api/qbwc/discard', 'import', () => Qbwc.discard(db));
  route('GET', '/api/import/formats', 'import', () => C.FORMATS);
  route('POST', '/api/import/preview', 'import', ({ b }) => { const { format, model } = importModel(b); return { format, label: C.FORMATS[format], ...C.plan(db, model) }; }, { maxBody: cfg.maxImportBytes + 4096 });
  route('POST', '/api/import/commit', 'import', ({ b, ctx }) => {
    const { format, model } = importModel(b);
    const r = C.commit(db, ctx, model, { filename: U.vStr(b.filename, 'filename', { max: 200 }), bank_account_id: b.bank_account_id ? U.vInt(b.bank_account_id, 'bank account') : null, allowSkip: b.allow_skip === true });
    return { format, ...r };
  }, { maxBody: cfg.maxImportBytes + 4096 });
  route('GET', '/api/import/batches', 'import', () => C.listBatches(db));
  route('POST', '/api/import/batches/:id/undo', 'import', ({ params, ctx }) => C.undoBatch(db, ctx, id(params.id)));
  route('GET', '/api/export/obx', 'export', ({ ctx }) => { audit(db, ctx, 'export.obx'); return { raw: C.exportObx(db), type: 'application/json; charset=utf-8', filename: 'ledjoro-export.obx.json' }; });
  route('GET', '/api/export/csv/:table', 'export', ({ params, ctx }) => {
    const tables = {
      accounts: 'SELECT number,name,type,subtype,description,active FROM accounts ORDER BY number,name',
      contacts: 'SELECT kind,name,email,phone,address,terms_days,is_1099 FROM contacts ORDER BY kind,name',
      journal: 'SELECT e.id AS entry,e.entry_date AS date,e.ref,e.memo,a.name AS account,l.debit_cents,l.credit_cents,c.name AS contact FROM journal_lines l JOIN journal_entries e ON e.id=l.entry_id JOIN accounts a ON a.id=l.account_id LEFT JOIN contacts c ON c.id=l.contact_id ORDER BY e.entry_date,e.id,l.id',
      documents: 'SELECT d.kind,d.number,c.name AS contact,d.doc_date,d.due_date,d.total_cents,d.paid_cents,d.status FROM documents d JOIN contacts c ON c.id=d.contact_id ORDER BY d.doc_date,d.id',
    };
    if (!tables[params.table]) throw new HttpError(404, 'That export is not available. Pick one from the Export list.');
    const rows = db.prepare(tables[params.table]).all();
    const cols = rows.length ? Object.keys(rows[0]) : [];
    const money = (k, v) => (/_cents$/.test(k) && Number.isInteger(v) ? U.formatMoney(v) : v);
    const out = [cols.map((k) => U.csvCell(k.replace(/_cents$/, ''))).join(',')].concat(rows.map((r) => cols.map((k) => U.csvCell(money(k, r[k]))).join(','))).join('\r\n') + '\r\n';
    audit(db, ctx, 'export.csv', 'table', params.table);
    return { raw: out, type: 'text/csv; charset=utf-8', filename: `ledjoro-${params.table}.csv` };
  });

  // ---------- backup ----------
  // Encrypted, integrity-checked copy of the whole database. Needs the current password (sensitive: it contains every record).
  route('POST', '/api/backup', 'settings.manage', async ({ b, ctx, session, ip }) => {
    const u = db.prepare('SELECT * FROM users WHERE id=?').get(session.user.id);
    if (!A.verifyPassword(String(b.password ?? ''), u.password_hash)) throw new HttpError(403, 'That is not your current password, so no backup was made. Your password is asked for because a backup contains every record in the books.');
    if (b.recovery_key) { // link a recovery key that was issued before backups could use it
      if (!A.loginIpLimiter.hit('linkkey:' + ip)) throw new HttpError(429, 'Too many tries with a recovery key. Wait a few minutes and try again.');
      if (!A.checkRecoveryKey(db, u.id, String(b.recovery_key))) throw bad('That is not your current recovery key. It looks like XXXXX-XXXXX-XXXXX-XXXXX and was shown once when your account was made (or the last time it was replaced). If it is lost, make a new one under My account.');
      Backup.lockSecretWithRecoveryKey(db, u.id, String(b.recovery_key));
    }
    const mode = b.mode ?? (b.passphrase ? 'passphrase' : 'recovery');
    const data = await Backup.createBackup(db, { mode, passphrase: b.passphrase });
    setSetting(db, 'last_backup', new Date().toISOString());
    audit(db, ctx, 'backup.create', 'database', '', `${data.length} bytes, ${mode === 'none' ? 'not encrypted' : mode === 'recovery' ? 'recovery key' : 'passphrase + recovery key'}`);
    const stamp = new Date().toISOString().replace(/[-:T]/g, '').slice(0, 12);
    return { raw: data, type: 'application/octet-stream', filename: `ledjoro-${stamp}.obbak` };
  }, { noTx: true });

  // Automatic backups (written by the engine itself while the app is open; see autobackup.js)
  route('GET', '/api/backup/auto', 'settings.manage', () => AutoBackup.status(db, cfg.dataDir));
  route('POST', '/api/backup/auto', 'settings.manage', ({ b, ctx }) => AutoBackup.save(db, ctx, cfg.dataDir, b));
  route('POST', '/api/backup/auto/run', 'settings.manage', async ({ ctx }) => AutoBackup.runNow(db, cfg.dataDir, ctx), { noTx: true });

  // What a backup could be protected with right now, and what an existing backup file needs to be opened.
  route('GET', '/api/backup/status', 'settings.manage', ({ session }) => ({ recovery_linked: Backup.hasRecoveryLink(db), recovery_linked_me: !!getSetting(db, 'recovery_wrap:' + session.user.id), last_backup: getSetting(db, 'last_backup') }));
  route('POST', '/api/backup/inspect', 'settings.manage', ({ b }) => {
    if (typeof b.file_b64 !== 'string' || !b.file_b64 || !/^[A-Za-z0-9+/=\s]+$/.test(b.file_b64)) throw bad('Choose a backup file first (the files ending in .obbak).');
    return Backup.inspect(Buffer.from(b.file_b64, 'base64'));
  }, { maxBody: Math.ceil(cfg.maxBackupBytes * 1.4) + 8192 });

  // Restore: replaces ALL data with a backup. Needs the password, the backup passphrase, and the word RESTORE; saves a safety copy first.
  route('POST', '/api/restore', 'settings.manage', async ({ b, ctx, session, res }) => {
    const u = db.prepare('SELECT * FROM users WHERE id=?').get(session.user.id);
    if (!A.verifyPassword(String(b.password ?? ''), u.password_hash)) throw new HttpError(403, 'That is not your current password, so nothing was restored. Your password is asked for because a restore replaces every record in the books.');
    if (b.confirm !== 'RESTORE') throw bad('To confirm, type the word RESTORE in capital letters. A restore replaces everything in the books.');
    if (typeof b.file_b64 !== 'string' || !b.file_b64 || !/^[A-Za-z0-9+/=\s]+$/.test(b.file_b64)) throw bad('Choose a backup file first (the files ending in .obbak).');
    const buf = Buffer.from(b.file_b64, 'base64');
    if (buf.length > cfg.maxBackupBytes) throw new HttpError(413, 'That file is bigger than the 48 MB Ledjoro can restore in one go, so it is probably not a Ledjoro backup. Nothing was changed.');
    const r = await Backup.restoreIntoLive(db, buf, String(b.secret ?? b.passphrase ?? ''), { dataDir: cfg.dataDir });
    audit(db, ctx, 'backup.restore', 'database', '', `restored ${r.entries} entries for "${r.company}"; safety copy ${r.safety_copy}`);
    clearCookie(res); // the session table was cleared by the restore
    return { ok: true, ...r };
  }, { noTx: true, maxBody: Math.ceil(cfg.maxBackupBytes * 1.4) + 8192 });

  // ---------- audit ----------
  route('GET', '/api/audit', 'audit.view', ({ query }) => db.prepare('SELECT id,ts,username,action,entity,entity_id,detail,ip FROM audit_log ORDER BY id DESC LIMIT ?').all(Math.min(Number(query.limit) || 200, 1000)));
  route('GET', '/api/audit/verify', 'audit.view', () => ({ audit_chain: verifyAudit(db), ledger: checkLedgerIntegrity(db) }));

  // ---------- plumbing ----------
  const clientIp = (req) => {
    return req.socket.remoteAddress || '';
  };
  const parseCookies = (h) => Object.fromEntries(String(h || '').split(';').map((c) => { const i = c.indexOf('='); return i < 0 ? [] : [c.slice(0, i).trim(), c.slice(i + 1).trim()]; }).filter((p) => p.length));
  const hostOk = (host) => {
    const h = String(host || '').toLowerCase().replace(/:\d+$/, '').replace(/^\[|\]$/g, '');
    return ['localhost', '127.0.0.1', '::1'].includes(h) || cfg.allowedHosts.includes(h) || cfg.allowedHosts.includes(String(host).toLowerCase());
  };
  const send = (res, status, body, type = 'application/json; charset=utf-8', extra = {}) => {
    const buf = Buffer.isBuffer(body) ? body : Buffer.from(body);
    res.writeHead(status, { 'Content-Type': type, 'Content-Length': buf.length, ...SECURITY_HEADERS, ...(cfg.secureCookies ? { 'Strict-Transport-Security': 'max-age=31536000; includeSubDomains' } : {}), ...extra });
    res.end(buf);
  };
  const sendJson = (res, status, obj, extra) => send(res, status, JSON.stringify(obj), undefined, { 'Cache-Control': 'no-store', ...extra });

  const readBody = (req, limit) => new Promise((resolve, reject) => {
    const declared = Number(req.headers['content-length']);
    if (declared > limit) { req.resume(); return reject(new HttpError(413, 'What was sent is larger than Ledjoro allows in one go. Use a smaller file, or split it into parts.')); }
    const chunks = []; let n = 0, over = false;
    req.on('data', (c) => { if (over) return; n += c.length; if (n > limit) { over = true; chunks.length = 0; } else chunks.push(c); });
    req.on('end', () => (over ? reject(new HttpError(413, 'What was sent is larger than Ledjoro allows in one go. Use a smaller file, or split it into parts.')) : resolve(Buffer.concat(chunks))));
    req.on('error', reject);
  });

  async function handler(req, res) {
    try {
      if (!hostOk(req.headers.host)) return sendJson(res, 421, { error: 'Unrecognised host' });
      if (cfg.shellKey) { // native-app mode: no key, no service
        const k = String(req.headers['x-ledjoro-key'] || '');
        if (k.length !== cfg.shellKey.length || !crypto.timingSafeEqual(Buffer.from(k), Buffer.from(cfg.shellKey))) return sendJson(res, 403, { error: 'Forbidden' });
      }
      const url = new URL(req.url, 'http://x');
      const ip = clientIp(req);
      if (!['GET', 'HEAD', 'POST', 'PUT', 'PATCH', 'DELETE'].includes(req.method)) return sendJson(res, 405, { error: 'Method not allowed' });
      if (!url.pathname.startsWith('/api/')) return sendJson(res, 404, { error: 'Not found' }); // there is no web UI: the engine only speaks the JSON API
      if (!A.apiLimiter.hit(ip)) throw new HttpError(429, 'Ledjoro received too many requests in a short time and is pausing them. Wait a few seconds and try again.');

      const r = routes.find((x) => x.method === req.method && x.re.test(url.pathname));
      if (!r) throw new HttpError(404, 'Ledjoro does not recognise that request. The window and the engine may be out of step; close Ledjoro and open it again.');
      const m = url.pathname.match(r.re);
      const params = Object.fromEntries(r.keys.map((k, i) => [k, decodeURIComponent(m[i + 1])]));

      const mutating = req.method !== 'GET' && req.method !== 'HEAD';
      if (mutating) {
        const origin = req.headers.origin;
        if (origin && origin !== 'null') { let oh; try { oh = new URL(origin).host; } catch { throw new HttpError(403, 'Ledjoro refused this request because it could not tell where it came from. Close Ledjoro and open it again.'); } if (oh.toLowerCase() !== String(req.headers.host).toLowerCase()) throw new HttpError(403, 'Ledjoro refused this request because it did not come from the Ledjoro window itself (a safety rule against other programs and web pages). If you did not start it yourself you can ignore this; otherwise close Ledjoro and open it again.'); }
        else if (origin === 'null') throw new HttpError(403, 'Ledjoro refused this request because it did not come from the Ledjoro window itself (a safety rule against other programs and web pages). If you did not start it yourself you can ignore this; otherwise close Ledjoro and open it again.');
        if (req.headers['sec-fetch-site'] && !['same-origin', 'none'].includes(req.headers['sec-fetch-site'])) throw new HttpError(403, 'Ledjoro refused this request because it did not come from the Ledjoro window itself (a safety rule against other programs and web pages). If you did not start it yourself you can ignore this.');
      }

      const cookieToken = parseCookies(req.headers.cookie)[cookieName];
      const session = A.loadSession(db, cookieToken, cfg);
      if (!session && !r.allowAnon) throw new HttpError(401, 'You are not signed in (you may have been signed out after 30 minutes without activity). Sign in again.');
      if (session && session.user.mustChangePassword && !r.allowPwChange) throw new HttpError(403, 'You need to choose a new password before you can do anything else. Do that first.');
      if (mutating && session && !r.noCsrf) {
        const t = String(req.headers['x-csrf-token'] || '');
        if (t.length !== session.csrf.length || !crypto.timingSafeEqual(Buffer.from(t), Buffer.from(session.csrf))) throw new HttpError(403, 'Ledjoro could not confirm this request came from your own signed-in window (the anti-forgery check failed). Sign out and in again, or close Ledjoro and open it again.');
      }
      if (r.perm) A.requirePerm(session, r.perm);

      let b = {};
      if (mutating) {
        const raw = await readBody(req, r.maxBody ?? cfg.maxJsonBytes);
        if (raw.length) {
          if (!/^application\/json\b/i.test(req.headers['content-type'] || '')) throw new HttpError(415, 'Ledjoro received data in a form it does not understand (this should not happen). Close Ledjoro and open it again.');
          try { b = JSON.parse(raw.toString('utf8')); } catch { throw bad('Ledjoro received data it could not read (this should not happen). Close Ledjoro and open it again.'); }
          if (!b || typeof b !== 'object' || Array.isArray(b)) throw bad('Ledjoro received data in the wrong shape (this should not happen). Close Ledjoro and open it again.');
        }
      }
      const query = Object.fromEntries(url.searchParams);
      const ctx = { user: session ? { id: session.user.id, username: session.user.username } : null, ip };
      const args = { req, res, db, b, query, params, session, ip, ctx, cookieToken };
      const out = mutating && !r.noTx ? tx(db, () => r.handler(args)) : await r.handler(args); // every write request is atomic
      if (res.writableEnded) return;
      if (out && out.raw !== undefined) {
        return send(res, 200, out.raw, out.type, { 'Cache-Control': 'no-store', 'Content-Disposition': `attachment; filename="${out.filename.replace(/[^\w.-]/g, '_')}"` });
      }
      sendJson(res, 200, out ?? { ok: true });
    } catch (e) {
      if (e instanceof HttpError) return sendJson(res, e.status, { error: e.message, ...(e.extra || {}) }, e.status === 413 ? { Connection: 'close' } : undefined);
      const known = explainDbError(e);
      if (known) { console.error('[error]', req.method, req.url?.split('?')[0], e?.message); return sendJson(res, known.status, { error: known.error }); }
      console.error('[error]', req.method, req.url?.split('?')[0], e?.message); // never log bodies or credentials
      const u = unexpected(); sendJson(res, u.status, { error: u.error });
    }
  }

  return { handler, state };
}

module.exports = { createApp };
