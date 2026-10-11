#!/usr/bin/env node
'use strict';
/**
 * Ledjoro command line.
 *   convert <file> [--format iif|ofx|qbxml|qbo-json|csv-accounts|...] [--out file.obx.json] [--asof YYYY-MM-DD] [--date-order dmy] [--register "Checking"]
 *       Converts a QuickBooks-family export into the Ledjoro Interchange Format (no database needed).
 *   formats            List supported input formats.
 *   backup <out.obbak> Encrypted (AES-256-GCM) database backup. Passphrase via LEDJORO_BACKUP_PASSPHRASE.
 *   restore <in.obbak> <target.db>
 */
const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');
const cfg = require('./config');
const { openDb } = require('./db');
const C = require('./convert');

const Backup = require('./backup');
const args = process.argv.slice(2);
const flag = (n) => { const i = args.indexOf('--' + n); return i >= 0 ? args[i + 1] : undefined; };
const die = (m) => { console.error(m); process.exit(1); };

function passphrase() {
  const p = process.env.LEDJORO_BACKUP_PASSPHRASE;
  if (!p || p.length < 12) die('Set LEDJORO_BACKUP_PASSPHRASE (12+ characters). It is read from the environment so it never appears in your shell history or process list.');
  return p;
}

async function main() {
  const cmd = args[0];
  if (cmd === 'formats') { for (const [k, v] of Object.entries(C.FORMATS)) console.log(k.padEnd(16), v); return; }

  if (cmd === 'convert') {
    const file = args[1];
    if (!file || file.startsWith('--')) die('Usage: convert <file> [--format X] [--out file]');
    const stat = fs.statSync(file);
    if (stat.size > cfg.maxImportBytes) die(`File is larger than the ${cfg.maxImportBytes} byte limit`);
    const text = fs.readFileSync(file, 'utf8');
    const format = flag('format') || C.detectFormat(path.basename(file), text);
    if (!format) die('Could not detect the format; pass --format. Run "formats" to list them.');
    const model = C.parse(format, text, { asof: flag('asof'), dateOrder: flag('date-order'), register: flag('register') }, { maxRows: cfg.maxImportRows });
    const out = flag('out') || file.replace(/\.[^.]*$/, '') + '.obx.json';
    fs.writeFileSync(out, C.toObx(model), { mode: 0o600 });
    console.log(`Format: ${format}\nWrote ${out}`);
    console.log(`  accounts ${model.accounts.length}, contacts ${model.contacts.length}, items ${model.items.length}, journal entries ${model.journal_entries.length}, bank transactions ${model.bank_transactions.length}`);
    for (const w of model.warnings) console.log('  warning:', w);
    for (const e of model.errors.slice(0, 20)) console.log('  ERROR:', e);
    if (model.errors.length) { console.log(`  ${model.errors.length} record(s) had errors and were left out of the output.`); process.exitCode = 2; }
    return;
  }

  if (cmd === 'backup') {
    const out = args[1]; if (!out) die('Usage: backup <out.obbak>');
    const db = openDb(path.join(cfg.dataDir, 'ledjoro.db'));
    // --no-encrypt: a plain copy anyone can read. With LEDJORO_BACKUP_PASSPHRASE set: that passphrase (the recovery key also opens it). Otherwise: recovery key only.
    const mode = args.includes('--no-encrypt') ? 'none' : process.env.LEDJORO_BACKUP_PASSPHRASE ? 'passphrase' : 'recovery';
    try { fs.writeFileSync(out, await Backup.createBackup(db, { mode, passphrase: process.env.LEDJORO_BACKUP_PASSPHRASE }), { mode: 0o600 }); } catch (e) { die(e.message); } finally { db.close(); }
    console.log(mode === 'none' ? `Integrity-checked backup written to ${out}. It is NOT encrypted: anyone who gets the file can read all of your records.` : `Encrypted, integrity-checked backup written to ${out}`);
    return;
  }

  if (cmd === 'restore') {
    const [, inp, target] = args; if (!inp || !target) die('Usage: restore <in.obbak> <target.db>');
    try { Backup.restoreBackup(inp, process.env.LEDJORO_BACKUP_PASSPHRASE || '', target); } catch (e) { die(e.message); } // the secret may be the passphrase or the recovery key; not needed for an unencrypted backup
    console.log(`Restored to ${target}. Point LEDJORO_DATA at its folder (file must be named ledjoro.db).`);
    return;
  }

  console.log('Commands: convert, formats, backup, restore (see header of src/cli.js)');
}
main().catch((e) => die(e.message));
