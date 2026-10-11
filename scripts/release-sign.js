#!/usr/bin/env node
'use strict';
/**
 * Release signing for Ledjoro. Uses only Node's built-in crypto.
 *
 *   node scripts/release-sign.js keygen            make the signing key (once) and write the public key into the build
 *   node scripts/release-sign.js sign <Ledjoro.exe> [--version 0.9.1-beta.1]   write <Ledjoro.exe>.sig next to the file
 *   node scripts/release-sign.js verify <Ledjoro.exe> [--version ...]           check a .sig the same way the program does
 *
 * The private key stays on your computer (default folder: .ledjoro-signing in your user folder), protected by a passphrase, and is never
 * put in the repository. The matching public key is built into every Ledjoro.exe, which refuses any update that is not signed with the
 * private key, even when the download comes from your own GitHub account.
 *
 * What is signed: the text "ledjoro-release-v1", the version and the file's SHA-256, one per line. The signature is ECDSA with the
 * NIST P-256 curve and SHA-256, 64 bytes (r then s), base64.
 */
const crypto = require('node:crypto');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');

const FORMAT = 1;
const DEFAULT_DIR = path.join(os.homedir(), '.ledjoro-signing');
const PUBLIC_KEY_FILE = path.join(__dirname, '..', 'native', 'Ledjoro', 'release-key.txt');

const message = (version, sha256) => Buffer.from(`ledjoro-release-v1\n${version}\n${sha256}\n`, 'utf8');
const sha256File = (file) => crypto.createHash('sha256').update(fs.readFileSync(file)).digest('hex');
const cleanVersion = (v) => String(v || '').replace(/^v/i, '').trim();

/** A new key pair. `passphrase` protects the private key file (PKCS#8, AES-256). Returns { privatePem, publicRaw } (publicRaw = X then Y, 64 bytes). */
function generateKey(passphrase) {
  if (!passphrase || passphrase.length < 8) throw new Error('Choose a passphrase of at least 8 characters.');
  const { privateKey, publicKey } = crypto.generateKeyPairSync('ec', { namedCurve: 'prime256v1' });
  const jwk = publicKey.export({ format: 'jwk' });
  return {
    privatePem: privateKey.export({ type: 'pkcs8', format: 'pem', cipher: 'aes-256-cbc', passphrase }),
    publicRaw: Buffer.concat([Buffer.from(jwk.x, 'base64url'), Buffer.from(jwk.y, 'base64url')]),
  };
}

/** The signature file's text for a release file with this SHA-256. */
function signRelease({ privatePem, passphrase, version, sha256 }) {
  const v = cleanVersion(version);
  if (!/^\d+\.\d+\.\d+(-[0-9A-Za-z.]+)?$/.test(v)) throw new Error(`"${version}" is not a version number such as 0.9.1-beta.1.`);
  const key = crypto.createPrivateKey({ key: privatePem, format: 'pem', passphrase });
  const signature = crypto.sign('sha256', message(v, sha256), { key, dsaEncoding: 'ieee-p1363' });
  return JSON.stringify({ format: FORMAT, version: v, sha256, signature: signature.toString('base64') }, null, 2) + '\n';
}

/** Same checks as the program's updater. Throws with a plain reason when the signature does not hold. */
function verifyRelease({ sigText, publicRaw, version, sha256 }) {
  let sig;
  try { sig = JSON.parse(sigText); } catch { throw new Error('The signature file is not readable.'); }
  if (sig.format !== FORMAT) throw new Error('The signature file is in a format this program does not know.');
  if (sig.version !== cleanVersion(version)) throw new Error(`The signature is for version ${sig.version}, not ${cleanVersion(version)}.`);
  if (String(sig.sha256).toLowerCase() !== sha256) throw new Error('The signature is for a different file than this one.');
  const key = crypto.createPublicKey({ key: { kty: 'EC', crv: 'P-256', x: publicRaw.subarray(0, 32).toString('base64url'), y: publicRaw.subarray(32).toString('base64url') }, format: 'jwk' });
  const raw = Buffer.from(String(sig.signature), 'base64');
  if (raw.length !== 64 || !crypto.verify('sha256', message(sig.version, sha256), { key, dsaEncoding: 'ieee-p1363' }, raw)) throw new Error('The signature does not match: this file was not signed with the release key.');
}

const publicKeyFileText = (raw) => `# Ledjoro release signing public key (ECDSA P-256, X then Y, base64). Built into every Ledjoro.exe so it can check updates.\n# The private key is never stored in this repository. See docs/RELEASING.md.\n${raw.toString('base64')}\n`;
const readPublicKeyFile = (file) => Buffer.from(fs.readFileSync(file, 'utf8').split(/\r?\n/).filter((l) => l.trim() && !l.startsWith('#')).join(''), 'base64');

// ---------- command line ----------
function askHidden(question) {
  return new Promise((resolve) => {
    process.stdout.write(question);
    const stdin = process.stdin; let s = '';
    if (stdin.setRawMode) stdin.setRawMode(true);
    stdin.resume(); stdin.setEncoding('utf8');
    const onData = (chunk) => {
      for (const c of chunk) {
        if (c === '\r' || c === '\n' || c === '\u0004') { if (stdin.setRawMode) stdin.setRawMode(false); stdin.pause(); stdin.removeListener('data', onData); process.stdout.write('\n'); return resolve(s); }
        if (c === '\u0003') process.exit(130);
        if (c === '\u007f' || c === '\b') s = s.slice(0, -1); else s += c;
      }
    };
    stdin.on('data', onData);
  });
}
const arg = (name) => { const i = process.argv.indexOf(name); return i >= 0 ? process.argv[i + 1] : undefined; };

async function main() {
  const [cmd, file] = process.argv.slice(2);
  const keyFile = arg('--key') || path.join(DEFAULT_DIR, 'release-private.pem');
  const env = process.env.LEDJORO_SIGNING_PASSPHRASE;
  if (cmd === 'keygen') {
    if (fs.existsSync(keyFile)) throw new Error(`A signing key already exists at ${keyFile}. It was not touched. Move it away first if you really want a new one (updates signed with a new key are refused by programs that carry the old one).`);
    const p1 = env || await askHidden('Choose a passphrase for the signing key (8+ characters; you will need it for every release): ');
    if (!env && p1 !== await askHidden('Type it again: ')) throw new Error('The two passphrases were different. Nothing was created.');
    const { privatePem, publicRaw } = generateKey(p1);
    fs.mkdirSync(path.dirname(keyFile), { recursive: true });
    fs.writeFileSync(keyFile, privatePem, { mode: 0o600 });
    fs.writeFileSync(PUBLIC_KEY_FILE, publicKeyFileText(publicRaw));
    console.log(`\nPrivate key saved to ${keyFile}\nPublic key written to ${path.relative(process.cwd(), PUBLIC_KEY_FILE)} (it goes into the build).\n\nBACK UP the private key file AND remember the passphrase: without them you cannot sign updates. Never put the private key in the repository or on GitHub.`);
  } else if (cmd === 'sign' || cmd === 'verify') {
    if (!file) throw new Error(`Say which file: node scripts/release-sign.js ${cmd} path\\to\\Ledjoro.exe`);
    const version = arg('--version') || require('../package.json').version;
    const sha = sha256File(file);
    if (cmd === 'sign') {
      const pass = env || await askHidden('Signing key passphrase: ');
      let text;
      try { text = signRelease({ privatePem: fs.readFileSync(keyFile, 'utf8'), passphrase: pass, version, sha256: sha }); }
      catch (e) { throw new Error(/bad decrypt|passphrase/i.test(e.message) ? 'That passphrase does not open the signing key.' : e.message); }
      fs.writeFileSync(file + '.sig', text);
      verifyRelease({ sigText: text, publicRaw: readPublicKeyFile(PUBLIC_KEY_FILE), version, sha256: sha });   // never hand out a signature the program would refuse
      console.log(`Signed ${path.basename(file)} as version ${cleanVersion(version)}\n  SHA-256 ${sha}\n  wrote ${file}.sig (checked against the public key built into the program)\nUpload both files, ${path.basename(file)} and ${path.basename(file)}.sig, to the GitHub release.`);
    } else {
      verifyRelease({ sigText: fs.readFileSync(file + '.sig', 'utf8'), publicRaw: readPublicKeyFile(PUBLIC_KEY_FILE), version, sha256: sha });
      console.log(`The signature for ${path.basename(file)} is good (version ${cleanVersion(version)}, SHA-256 ${sha}).`);
    }
  } else {
    console.log('Usage:\n  node scripts/release-sign.js keygen\n  node scripts/release-sign.js sign   <Ledjoro.exe> [--version 0.9.1-beta.1] [--key path]\n  node scripts/release-sign.js verify <Ledjoro.exe> [--version 0.9.1-beta.1]');
    process.exitCode = cmd ? 2 : 0;
  }
}

module.exports = { generateKey, signRelease, verifyRelease, message, publicKeyFileText, readPublicKeyFile, sha256File };
if (require.main === module) main().catch((e) => { console.error('\n' + e.message); process.exit(1); });
