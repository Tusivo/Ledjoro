'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const crypto = require('node:crypto');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const S = require('../scripts/release-sign');

const SHA = crypto.createHash('sha256').update('a program').digest('hex');

test('a signed release verifies, and any change to the file, the version or the signature is refused', () => {
  const { privatePem, publicRaw } = S.generateKey('correct horse battery');
  assert.equal(publicRaw.length, 64);
  const sigText = S.signRelease({ privatePem, passphrase: 'correct horse battery', version: 'v0.9.1-beta.1', sha256: SHA });
  const parsed = JSON.parse(sigText);
  assert.equal(parsed.version, '0.9.1-beta.1'); assert.equal(Buffer.from(parsed.signature, 'base64').length, 64);
  S.verifyRelease({ sigText, publicRaw, version: '0.9.1-beta.1', sha256: SHA });
  S.verifyRelease({ sigText, publicRaw, version: 'v0.9.1-beta.1', sha256: SHA });
  const refused = (over, re) => assert.throws(() => S.verifyRelease({ sigText, publicRaw, version: '0.9.1-beta.1', sha256: SHA, ...over }), re);
  refused({ sha256: crypto.createHash('sha256').update('another program').digest('hex') }, /different file/);
  refused({ version: '0.9.2-beta.1' }, /version 0\.9\.1-beta\.1, not 0\.9\.2/);
  refused({ publicRaw: S.generateKey('another passphrase').publicRaw }, /not signed with the release key/);
  const forged = JSON.stringify({ ...parsed, signature: Buffer.alloc(64, 7).toString('base64') });
  refused({ sigText: forged }, /not signed with the release key/);
  refused({ sigText: JSON.stringify({ ...parsed, version: '0.9.2-beta.1' }) }, /not 0\.9\.1/);   // the version is part of what was signed, so it cannot be relabelled
  refused({ sigText: '{not json' }, /not readable/);
  refused({ sigText: JSON.stringify({ ...parsed, format: 2 }) }, /format/);
});

test('the signing key needs its passphrase, and a bad version number is rejected', () => {
  const { privatePem } = S.generateKey('correct horse battery');
  assert.throws(() => S.signRelease({ privatePem, passphrase: 'wrong passphrase', version: '1.0.0', sha256: SHA }));
  assert.throws(() => S.signRelease({ privatePem, passphrase: 'correct horse battery', version: 'banana', sha256: SHA }), /version number/);
  assert.throws(() => S.generateKey('short'), /8 characters/);
  assert.ok(!privatePem.includes('BEGIN PRIVATE KEY'), 'the private key file is encrypted');
});

test('the public key file round-trips', () => {
  const { publicRaw } = S.generateKey('correct horse battery');
  const f = path.join(fs.mkdtempSync(path.join(os.tmpdir(), 'lj-key-')), 'release-key.txt');
  fs.writeFileSync(f, S.publicKeyFileText(publicRaw));
  assert.deepEqual(S.readPublicKeyFile(f), publicRaw);
});

test('the command line makes a key, signs a file and checks it', () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'lj-sign-'));
  const key = path.join(dir, 'private.pem'), exe = path.join(dir, 'Ledjoro.exe');
  fs.writeFileSync(exe, 'not really a program');
  const { execFileSync } = require('node:child_process');
  const pubFile = path.join(__dirname, '..', 'native', 'Ledjoro', 'release-key.txt');
  const hadPublic = fs.existsSync(pubFile), kept = hadPublic ? fs.readFileSync(pubFile) : null;
  const env = { ...process.env, LEDJORO_SIGNING_PASSPHRASE: 'correct horse battery' };
  const run = (...a) => execFileSync(process.execPath, [path.join(__dirname, '..', 'scripts', 'release-sign.js'), ...a], { env, encoding: 'utf8' });
  try {
    if (hadPublic) fs.unlinkSync(pubFile);
    run('keygen', '--key', key);
    assert.ok(fs.existsSync(key) && fs.existsSync(pubFile));
    assert.throws(() => run('keygen', '--key', key), /already exists/, 'an existing key is never overwritten');
    run('sign', exe, '--version', '1.2.3-beta.4', '--key', key);
    assert.match(run('verify', exe, '--version', '1.2.3-beta.4'), /good/);
    assert.throws(() => run('verify', exe, '--version', '1.2.4'), /Command failed/);
    fs.writeFileSync(exe, 'a changed program');
    assert.throws(() => run('verify', exe, '--version', '1.2.3-beta.4'), /Command failed/);
  } finally {
    if (hadPublic) fs.writeFileSync(pubFile, kept); else fs.rmSync(pubFile, { force: true });
  }
});
