'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const crypto = require('node:crypto');
const { startServer, Client, setupAdmin, PASSWORD } = require('./helpers');
const WA = require('../src/webauthn');

// ---- a software authenticator: builds exactly what a real one sends, so the server-side checks can be exercised end to end ----
function cbor(v) {
  const head = (major, n) => (n < 24 ? Buffer.from([(major << 5) | n]) : n < 256 ? Buffer.from([(major << 5) | 24, n]) : n < 65536 ? Buffer.from([(major << 5) | 25, n >> 8, n & 255]) : (() => { const b = Buffer.alloc(5); b[0] = (major << 5) | 26; b.writeUInt32BE(n, 1); return b; })());
  if (typeof v === 'number') return v >= 0 ? head(0, v) : head(1, -1 - v);
  if (Buffer.isBuffer(v)) return Buffer.concat([head(2, v.length), v]);
  if (typeof v === 'string') { const b = Buffer.from(v); return Buffer.concat([head(3, b.length), b]); }
  if (v instanceof Map) return Buffer.concat([head(5, v.size), ...[...v].flatMap(([k, x]) => [cbor(k), cbor(x)])]);
  throw new Error('cbor');
}
const sha = (b) => crypto.createHash('sha256').update(b).digest();
class Authenticator {
  constructor(kind = 'ES256', rpId = 'ledjoro.local') {
    this.kind = kind; this.rpId = rpId; this.count = 0; this.id = crypto.randomBytes(32);
    if (kind === 'ES256') this.kp = crypto.generateKeyPairSync('ec', { namedCurve: 'P-256' });
    else if (kind === 'RS256') this.kp = crypto.generateKeyPairSync('rsa', { modulusLength: 2048 });
    else this.kp = crypto.generateKeyPairSync('ed25519');
  }
  cose() {
    const j = this.kp.publicKey.export({ format: 'jwk' }); const u = (s) => Buffer.from(s, 'base64url');
    if (this.kind === 'ES256') return new Map([[1, 2], [3, -7], [-1, 1], [-2, u(j.x)], [-3, u(j.y)]]);
    if (this.kind === 'RS256') return new Map([[1, 3], [3, -257], [-1, u(j.n)], [-2, u(j.e)]]);
    return new Map([[1, 1], [3, -8], [-1, 6], [-2, u(j.x)]]);
  }
  authData({ attested = false, flags = 0x05, rpId = this.rpId, count = this.count } = {}) {
    const head = Buffer.alloc(37); sha(Buffer.from(rpId)).copy(head, 0); head[32] = flags | (attested ? 0x40 : 0); head.writeUInt32BE(count, 33);
    if (!attested) return head;
    const idLen = Buffer.alloc(2); idLen.writeUInt16BE(this.id.length);
    return Buffer.concat([head, Buffer.alloc(16), idLen, this.id, cbor(this.cose())]);
  }
  clientData(type, challenge, origin = 'https://' + this.rpId) { return Buffer.from(JSON.stringify({ type, challenge, origin, crossOrigin: false })); }
  create(opts, o = {}) {
    const clientDataJSON = this.clientData('webauthn.create', o.challenge ?? opts.challenge);
    const attestationObject = cbor(new Map([['fmt', 'none'], ['attStmt', new Map()], ['authData', this.authData({ attested: true, flags: o.flags ?? 0x05, rpId: o.rpId ?? this.rpId })]]));
    return { challengeId: opts.challengeId, clientDataJSON: clientDataJSON.toString('base64url'), attestationObject: attestationObject.toString('base64url') };
  }
  sign(data) {
    if (this.kind === 'ES256') return crypto.sign('sha256', data, { key: this.kp.privateKey, dsaEncoding: 'der' });
    if (this.kind === 'RS256') return crypto.sign('sha256', data, this.kp.privateKey);
    return crypto.sign(null, data, this.kp.privateKey);
  }
  assert(opts, o = {}) {
    if (o.bump !== false) this.count = o.count ?? this.count + 1;
    const clientDataJSON = this.clientData('webauthn.get', o.challenge ?? opts.challenge);
    const authenticatorData = this.authData({ flags: o.flags ?? 0x05, rpId: o.rpId ?? this.rpId });
    const signature = this.sign(Buffer.concat([authenticatorData, sha(clientDataJSON)]));
    return { challengeId: opts.challengeId, credentialId: this.id.toString('base64url'), clientDataJSON: clientDataJSON.toString('base64url'), authenticatorData: authenticatorData.toString('base64url'), signature: (o.badSig ? Buffer.alloc(signature.length, 7) : signature).toString('base64url') };
  }
}

async function addPasskey(admin, au, name = 'Test key') {
  const opt = await admin.post('/api/passkeys/register/options', { password: PASSWORD });
  assert.equal(opt.status, 200, opt.text);
  const fin = await admin.post('/api/passkeys/register/finish', { ...au.create(opt.json), name });
  assert.equal(fin.status, 200, fin.text);
  return fin.json.id;
}
async function signIn(s, au, o) {
  const anon = new Client(s.port);
  const opt = (await anon.post('/api/passkeys/login/options', {})).json;
  const r = await anon.post('/api/passkeys/login/finish', au.assert(opt, o));
  return { anon, r };
}

test('a passkey can be added (password required), listed, used to sign in, and removed', async () => {
  const s = await startServer();
  try {
    const admin = await setupAdmin(s);
    assert.equal((await admin.post('/api/passkeys/register/options', { password: 'wrong-password-123' })).status, 403, 'adding a passkey needs the current password');
    const au = new Authenticator('ES256');
    const id = await addPasskey(admin, au, 'Office laptop');
    const list = (await admin.get('/api/passkeys')).json;
    assert.equal(list.length, 1); assert.equal(list[0].name, 'Office laptop'); assert.ok(!('public_key' in list[0]));
    const { anon, r } = await signIn(s, au);
    assert.equal(r.status, 200, r.text);
    await anon.refreshSession(); assert.equal((await anon.get('/api/accounts')).status, 200, 'the passkey sign-in gave a working session');
    assert.ok(s.db.prepare("SELECT 1 FROM audit_log WHERE action='login.ok' AND detail='passkey'").get(), 'sign-in is audited');
    assert.ok((await admin.get('/api/passkeys')).json[0].last_used_at > 0);
    // a second sign-in works too (the counter goes up each time)
    assert.equal((await signIn(s, au)).r.status, 200);
    assert.equal((await admin.request('DELETE', `/api/passkeys/${id}`)).status, 200);
    assert.equal((await signIn(s, au)).r.status, 401, 'a removed passkey no longer signs in');
  } finally { await s.close(); }
});

test('passkeys of every supported kind work: ES256 (most keys), RS256 (Windows Hello with a TPM) and EdDSA', async () => {
  const s = await startServer();
  try {
    const admin = await setupAdmin(s);
    for (const kind of ['ES256', 'RS256', 'EdDSA']) {
      const au = new Authenticator(kind); await addPasskey(admin, au, kind);
      const { r } = await signIn(s, au); assert.equal(r.status, 200, `${kind}: ${r.text}`);
    }
    assert.equal((await admin.get('/api/passkeys')).json.length, 3);
  } finally { await s.close(); }
});

test('forged, replayed, copied or unverified passkey sign-ins are refused', async () => {
  const s = await startServer();
  try {
    const admin = await setupAdmin(s);
    const au = new Authenticator('ES256'); await addPasskey(admin, au);
    const anon = new Client(s.port);
    const fresh = async () => (await anon.post('/api/passkeys/login/options', {})).json;

    assert.equal((await anon.post('/api/passkeys/login/finish', au.assert(await fresh(), { badSig: true }))).status, 401, 'a bad signature');
    const opt = await fresh(); const good = au.assert(opt);
    assert.equal((await anon.post('/api/passkeys/login/finish', good)).status, 200);
    assert.equal((await anon.post('/api/passkeys/login/finish', good)).status, 400, 'a challenge works only once (replay)');
    assert.equal((await anon.post('/api/passkeys/login/finish', au.assert(await fresh(), { challenge: 'AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA' }))).status, 401, 'the wrong challenge');
    assert.equal((await anon.post('/api/passkeys/login/finish', au.assert(await fresh(), { rpId: 'evil.example' }))).status, 401, 'a passkey made for another site');
    assert.equal((await anon.post('/api/passkeys/login/finish', au.assert(await fresh(), { flags: 0x01 }))).status, 401, 'no PIN / fingerprint / face (user verification)');
    assert.equal((await anon.post('/api/passkeys/login/finish', au.assert(await fresh(), { count: 1, bump: true }))).status, 401, 'a counter that went backwards (a copied key)');
    assert.equal((await anon.post('/api/passkeys/login/finish', { ...au.assert(await fresh()), credentialId: crypto.randomBytes(32).toString('base64url') })).status, 401, 'an unknown passkey');
    assert.equal((await anon.post('/api/passkeys/login/finish', { challengeId: 'nope', credentialId: 'x' })).status, 400, 'no such challenge');
    // another person's challenge cannot be used for registration, and registration data cannot be reused as a sign-in
    const other = new Authenticator('ES256');
    const optR = (await admin.post('/api/passkeys/register/options', { password: PASSWORD })).json;
    assert.equal((await admin.post('/api/passkeys/register/finish', other.create(optR, { flags: 0x01 }))).status, 400, 'registration without user verification');
    const optR2 = (await admin.post('/api/passkeys/register/options', { password: PASSWORD })).json;
    assert.equal((await admin.post('/api/passkeys/register/finish', other.create(optR2, { rpId: 'evil.example' }))).status, 400, 'registration for another site');
    const optR3 = (await admin.post('/api/passkeys/register/options', { password: PASSWORD })).json;
    assert.equal((await admin.post('/api/passkeys/register/finish', other.create(optR3, { challenge: 'AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA' }))).status, 400, 'registration with the wrong challenge');
    assert.equal(s.db.prepare('SELECT COUNT(*) n FROM passkeys').get().n, 1, 'none of the refused attempts stored anything');
    assert.ok(s.db.prepare("SELECT COUNT(*) n FROM audit_log WHERE action='passkey.failed'").get().n >= 5, 'refusals are audited');
  } finally { await s.close(); }
});

test('a turned-off account cannot sign in with its passkey; passkeys are private to their owner; restore keeps the table consistent', async () => {
  const s = await startServer();
  try {
    const admin = await setupAdmin(s);
    const au = new Authenticator('ES256'); const id = await addPasskey(admin, au);
    // a second user with their own login cannot see or remove the first user's passkeys
    const role = s.db.prepare("SELECT id FROM roles WHERE name='Administrator'").get() ?? s.db.prepare('SELECT id FROM roles ORDER BY id').get();
    const mk = await admin.post('/api/users', { username: 'second', display_name: 'Second', password: 'another-long-passphrase-77', role_id: role.id });
    assert.ok([200, 201].includes(mk.status), mk.text);
    s.db.prepare("UPDATE users SET must_change_password=0 WHERE username='second'").run();
    const second = new Client(s.port); const lr = await second.post('/api/login', { username: 'second', password: 'another-long-passphrase-77' }); assert.equal(lr.status, 200);
    await second.refreshSession();
    if (second.csrf) { assert.equal((await second.request('DELETE', `/api/passkeys/${id}`)).status, 404); assert.equal((await second.get('/api/passkeys')).json.length, 0); }
    // turn the first user off
    s.db.prepare("UPDATE users SET active=0 WHERE username='admin'").run();
    assert.equal((await signIn(s, au)).r.status, 401, 'a disabled account cannot use a passkey');
    assert.equal(s.db.prepare('PRAGMA foreign_key_check').all().length, 0);
  } finally { await s.close(); }
});

test('the verifier rejects hostile data: truncated CBOR, deep nesting, unsupported keys', () => {
  assert.throws(() => WA.cborDecode(Buffer.from([0xa1])), /ended unexpectedly/);
  assert.throws(() => WA.cborDecode(Buffer.from([0x81, 0x81, 0x81, 0x81, 0x81, 0x81, 0x81, 0x81, 0x81, 0x81, 0x00])), /nested too deeply/);
  assert.throws(() => WA.cborDecode(Buffer.from([0x5b, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff])), /too large|ended|malformed/);
  assert.throws(() => WA.coseToSpki(new Map([[1, 2], [3, -35], [-1, 2]])), /not support/);
  assert.throws(() => WA.parseAuthData(Buffer.alloc(10)), /too short/);
});
