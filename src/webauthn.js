'use strict';
/**
 * Passkey (WebAuthn / FIDO2) verification, with no dependencies: a small CBOR reader, authenticator-data parsing, and signature checks
 * for the algorithms real authenticators use (ES256 for most phones and security keys, RS256 for Windows Hello with a TPM, EdDSA).
 *
 * The program that shows the Windows passkey prompt (the Ledjoro app) only relays what the authenticator produced; everything that
 * decides whether a sign-in is genuine is checked here: the one-time challenge, the relying-party id hash, that the person was present
 * AND verified (PIN / fingerprint / face), the signature, and that the signature counter never goes backwards.
 */
const crypto = require('node:crypto');
const { bad, HttpError } = require('./util');

// ---------- CBOR (RFC 8949), the subset WebAuthn uses ----------
function cborDecode(buf, offset = 0, depth = 0, budget = { n: 0 }) {
  if (depth > 8) throw bad('The passkey data is nested too deeply to be real, so it was refused. Try again, or sign in with your password.');
  if (++budget.n > 2000) throw bad('The passkey data is far larger than a real passkey, so it was refused. Try again, or sign in with your password.');
  if (offset >= buf.length) throw bad('The passkey data ended unexpectedly (it was cut off), so it cannot be read. Try again; if it repeats, sign in with your password.');
  const head = buf[offset++];
  const major = head >> 5, info = head & 31;
  let len;
  if (info < 24) len = info;
  else if (info === 24) { len = buf[offset]; offset += 1; }
  else if (info === 25) { len = buf.readUInt16BE(offset); offset += 2; }
  else if (info === 26) { len = buf.readUInt32BE(offset); offset += 4; }
  else if (info === 27) { const v = buf.readBigUInt64BE(offset); offset += 8; if (v > BigInt(Number.MAX_SAFE_INTEGER)) throw bad('The passkey data contains a number that is too large to be real, so it was refused. Try again, or sign in with your password.'); len = Number(v); }
  else if (major === 7 && info >= 20 && info <= 22) len = info;
  else throw bad('The passkey data is written in a way Ledjoro does not support, so it cannot be read. Try again, or use a different passkey or your password.');
  if (len === undefined || Number.isNaN(len)) throw bad('The passkey data is malformed (damaged), so it cannot be read. Try again, or sign in with your password.');
  switch (major) {
    case 0: return { value: len, next: offset };
    case 1: return { value: -1 - len, next: offset };
    case 2: case 3: {
      if (offset + len > buf.length) throw bad('The passkey data ended unexpectedly (it was cut off), so it cannot be read. Try again; if it repeats, sign in with your password.');
      const slice = buf.subarray(offset, offset + len);
      return { value: major === 2 ? Buffer.from(slice) : slice.toString('utf8'), next: offset + len };
    }
    case 4: {
      if (len > 200) throw bad('The passkey data is far larger than a real passkey, so it was refused. Try again, or sign in with your password.');
      const arr = []; let o = offset;
      for (let i = 0; i < len; i++) { const r = cborDecode(buf, o, depth + 1, budget); arr.push(r.value); o = r.next; }
      return { value: arr, next: o };
    }
    case 5: {
      if (len > 200) throw bad('The passkey data is far larger than a real passkey, so it was refused. Try again, or sign in with your password.');
      const map = new Map(); let o = offset;
      for (let i = 0; i < len; i++) {
        const k = cborDecode(buf, o, depth + 1, budget); const v = cborDecode(buf, k.next, depth + 1, budget);
        map.set(k.value, v.value); o = v.next;
      }
      return { value: map, next: o };
    }
    case 6: return cborDecode(buf, offset, depth + 1, budget); // a tag: ignore it, keep the value
    case 7: return { value: len === 20 ? false : len === 21 ? true : null, next: offset };
    default: throw bad('The passkey data is written in a way Ledjoro does not support, so it cannot be read. Try again, or use a different passkey or your password.');
  }
}

const b64u = (b) => Buffer.from(b).toString('base64url');
const fromB64u = (s, what, max = 4096) => {
  if (typeof s !== 'string' || s.length > max * 2 || !/^[A-Za-z0-9_-]*$/.test(s)) throw bad(`The passkey sent something Ledjoro could not read (${what}). Try again, or sign in with your password.`);
  return Buffer.from(s, 'base64url');
};
const sha256 = (b) => crypto.createHash('sha256').update(b).digest();

// ---------- authenticator data ----------
const FLAG = { UP: 0x01, UV: 0x04, AT: 0x40 };
function parseAuthData(ad) {
  if (!Buffer.isBuffer(ad) || ad.length < 37) throw bad('The passkey response is too short to be complete, so it was refused. Try again, or sign in with your password.');
  const out = { rpIdHash: ad.subarray(0, 32), flags: ad[32], signCount: ad.readUInt32BE(33), cred: null };
  if (out.flags & FLAG.AT) {
    if (ad.length < 55) throw bad('The passkey response is too short to be complete, so it was refused. Try again, or sign in with your password.');
    const idLen = ad.readUInt16BE(53);
    if (idLen < 16 || idLen > 1023 || ad.length < 55 + idLen) throw bad('The passkey\'s identifier is not valid, so it was refused. Try again, or sign in with your password.');
    const credentialId = Buffer.from(ad.subarray(55, 55 + idLen));
    const key = cborDecode(ad, 55 + idLen);
    out.cred = { aaguid: Buffer.from(ad.subarray(37, 53)), credentialId, cose: key.value };
  }
  return out;
}

// ---------- public keys ----------
const ALGS = { '-7': 'ES256', '-257': 'RS256', '-8': 'EdDSA' };
/** COSE_Key (a CBOR map) -> { alg, spki (DER bytes) }. Only the key types listed in ALGS are accepted. */
function coseToSpki(cose) {
  if (!(cose instanceof Map)) throw bad('The passkey\'s public key is not valid, so it was refused. Try making the passkey again.');
  const kty = cose.get(1), alg = cose.get(3);
  let jwk;
  if (kty === 2 && alg === -7) {
    if (cose.get(-1) !== 1) throw bad('This passkey uses a kind of key (curve) that Ledjoro does not support. Use a different passkey or security key.');
    jwk = { kty: 'EC', crv: 'P-256', x: b64u(cose.get(-2)), y: b64u(cose.get(-3)) };
  } else if (kty === 3 && alg === -257) {
    jwk = { kty: 'RSA', n: b64u(cose.get(-1)), e: b64u(cose.get(-2)) };
  } else if (kty === 1 && alg === -8) {
    if (cose.get(-1) !== 6) throw bad('This passkey uses a kind of key (curve) that Ledjoro does not support. Use a different passkey or security key.');
    jwk = { kty: 'OKP', crv: 'Ed25519', x: b64u(cose.get(-2)) };
  } else throw bad('This passkey uses a kind of cryptography that Ledjoro does not support. Use Windows Hello or a different security key.');
  let key;
  try { key = crypto.createPublicKey({ key: jwk, format: 'jwk' }); } catch { throw bad('The passkey\'s public key is not valid, so it was refused. Try making the passkey again.'); }
  if (alg === -257 && key.asymmetricKeyDetails?.modulusLength < 2048) throw bad('This passkey\'s key is too small to be safe, so Ledjoro refused it. Use Windows Hello or a different security key.');
  return { alg, spki: key.export({ type: 'spki', format: 'der' }) };
}

function verifySignature(alg, spki, data, signature) {
  const key = crypto.createPublicKey({ key: spki, format: 'der', type: 'spki' });
  if (alg === -7) return crypto.verify('sha256', data, { key, dsaEncoding: 'der' }, signature);
  if (alg === -257) return crypto.verify('sha256', data, key, signature);
  if (alg === -8) return crypto.verify(null, data, key, signature);
  return false;
}

// ---------- client data ----------
function checkClientData(raw, type, challenge, origins) {
  let cd;
  try { cd = JSON.parse(raw.toString('utf8')); } catch { throw bad('The passkey response is not valid, so it was refused. Try again, or sign in with your password.'); }
  if (cd.type !== type) throw bad('The passkey response was meant for a different action (for example registering instead of signing in), so it was refused. Try again.');
  if (typeof cd.challenge !== 'string' || cd.challenge.length !== challenge.length || !crypto.timingSafeEqual(Buffer.from(cd.challenge), Buffer.from(challenge))) throw bad('The passkey answer does not match this sign-in attempt (it may have expired after 3 minutes). Try again.');
  if (!origins.includes(cd.origin)) throw bad('The passkey answer came from somewhere other than the Ledjoro window, so it was refused. Try again from Ledjoro itself.');
  if (cd.crossOrigin === true) throw bad('The passkey answer came from somewhere other than the Ledjoro window, so it was refused. Try again from Ledjoro itself.');
}

function checkRp(authData, rpId, needAttested) {
  if (!crypto.timingSafeEqual(authData.rpIdHash, sha256(Buffer.from(rpId)))) throw bad('This passkey was made for a different program (or a renamed Ledjoro), so it cannot be used here. Use a passkey you added in this Ledjoro, or sign in with your password.');
  if (!(authData.flags & FLAG.UP)) throw bad('The passkey was not confirmed by a person at the keyboard, so it was refused. Try again and approve the Windows prompt.');
  if (!(authData.flags & FLAG.UV)) throw bad('Passkey needs your PIN, fingerprint or face (user verification) to be accepted. Try again and complete that step in the Windows prompt.');
  if (needAttested && !authData.cred) throw bad('The passkey response did not include a new passkey, so nothing was added. Try adding it again.');
}

/** A new passkey. We do not need the maker's attestation statement (no trust decision depends on it), only the key it created. */
function verifyRegistration({ clientDataJSON, attestationObject }, { challenge, rpId, origins }) {
  checkClientData(clientDataJSON, 'webauthn.create', challenge, origins);
  const att = cborDecode(attestationObject).value;
  if (!(att instanceof Map) || !Buffer.isBuffer(att.get('authData'))) throw bad('The passkey response is not valid, so it was refused. Try again, or sign in with your password.');
  const ad = parseAuthData(att.get('authData'));
  checkRp(ad, rpId, true);
  const { alg, spki } = coseToSpki(ad.cred.cose);
  return { credentialId: ad.cred.credentialId, spki, alg, signCount: ad.signCount, aaguid: ad.cred.aaguid };
}

/** A sign-in with an existing passkey. `stored` = { spki, alg, signCount }. Returns the new signature counter. */
function verifyAssertion({ clientDataJSON, authenticatorData, signature }, stored, { challenge, rpId, origins }) {
  checkClientData(clientDataJSON, 'webauthn.get', challenge, origins);
  const ad = parseAuthData(authenticatorData);
  checkRp(ad, rpId, false);
  const signed = Buffer.concat([authenticatorData, sha256(clientDataJSON)]);
  let ok = false;
  try { ok = verifySignature(stored.alg, stored.spki, signed, signature); } catch { ok = false; }
  if (!ok) throw new HttpError(401, 'The passkey\'s signature did not check out, so it was not accepted. Try again, or sign in with your password.');
  // a counter that does not go up means this credential may have been copied
  if ((ad.signCount !== 0 || stored.signCount !== 0) && ad.signCount <= stored.signCount) throw new HttpError(401, 'This passkey looks like a copy (its internal counter went backwards, which a genuine one never does), so it was refused for your safety. Sign in with your password, then remove this passkey and add it again.');
  return { signCount: ad.signCount };
}

module.exports = { cborDecode, parseAuthData, coseToSpki, verifyRegistration, verifyAssertion, b64u, fromB64u, sha256, ALGS };
