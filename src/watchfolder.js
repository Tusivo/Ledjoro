'use strict';
/**
 * Watched download folder. The person picks the folder their bank downloads land in (usually Downloads); every minute or so
 * Ledjoro looks for new bank statement files there (.qbo, .ofx, .qfx), reads them, and lists them for a one-click import.
 * Nothing is imported until the person presses Import, files are never moved, changed or deleted, and a file that has been
 * dealt with (imported or ignored) is recognised by its content, so renaming or re-downloading it does not bring it back.
 * Only bank statement formats are picked up automatically: CSV downloads are laid out differently by every bank, so those go
 * through Import & Convert, where the columns can be matched by hand.
 */
const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');
const { getSetting, setSetting } = require('./db');
const { bad, HttpError } = require('./util');
const { audit } = require('./audit');
const C = require('./convert');

const KEY = 'watchfolder';
const EXT = new Set(['.qbo', '.ofx', '.qfx']);
const REPEAT_MS = 45_000;
const OLDER_CAP = 50;   // when asked to look at older files too, only the newest this many are considered
const last = new WeakMap();   // db -> time of the last scan

function settings(db) {
  let s = {}; try { s = JSON.parse(getSetting(db, KEY) || '{}'); } catch { /* damaged: start again */ }
  return { enabled: false, folder: '', since: 0, accounts: {}, ...s };
}
const save = (db, s) => setSetting(db, KEY, JSON.stringify(s));

function checkFolder(folder) {
  if (typeof folder !== 'string' || !folder.trim()) throw bad('Choose the folder your bank downloads go to (for example Downloads).');
  if (!path.isAbsolute(folder)) throw bad('Choose the folder with the Choose Folder button, so that its full location is known.');
  let st;
  try { st = fs.statSync(folder); } catch { throw bad(`Ledjoro cannot find the folder "${folder}". Is it on a drive that is not plugged in? Choose another folder.`); }
  if (!st.isDirectory()) throw bad(`"${folder}" is a file, not a folder. Choose a folder.`);
  try { fs.readdirSync(folder); } catch { throw bad(`Windows does not allow Ledjoro to look inside "${folder}". Choose a folder you own, such as Downloads.`); }
}

function configure(db, ctx, b) {
  const s = settings(db), enabled = b.enabled === true;
  if (enabled) {
    checkFolder(b.folder);
    if (!s.enabled || s.folder !== b.folder) s.since = Date.now() - 60_000;   // only files that arrive from now on, so an old Downloads folder does not flood the list
    s.folder = b.folder;
  }
  s.enabled = enabled; save(db, s);
  audit(db, ctx, 'watch.settings', 'settings', '', enabled ? `watching ${s.folder}` : 'off');
  return status(db, false);
}

function rescan(db, ctx) {
  const s = settings(db); if (!s.enabled) throw bad('Turn on the watched folder first.');
  s.since = 0; save(db, s);
  db.prepare("DELETE FROM watched_files WHERE status IN ('ignored','unreadable')").run();
  audit(db, ctx, 'watch.rescan', 'settings', '', 'looking at older files too');
  return scan(db, { limits: { maxRows: 200000 } }, true);
}

const sha = (buf) => crypto.createHash('sha256').update(buf).digest('hex');
const info = (row) => { try { return JSON.parse(row.info || '{}'); } catch { return {}; } };

/** Look at the folder now. Returns the files waiting for a decision. */
function scan(db, cfg, force = false) {
  const s = settings(db);
  last.set(db, Date.now());
  if (!s.enabled || !s.folder) return [];
  // an import that was undone makes its file importable again
  for (const r of db.prepare("SELECT hash, batch_id FROM watched_files WHERE status='imported' AND batch_id IS NOT NULL").all()) {
    const b = db.prepare('SELECT summary FROM import_batches WHERE id=?').get(r.batch_id);
    let undone = false; try { undone = !!JSON.parse(b?.summary || '{}').undone; } catch { /* ignore */ }
    if (undone) db.prepare('DELETE FROM watched_files WHERE hash=?').run(r.hash);
  }
  const maxBytes = cfg.maxImportBytes || 10 * 1024 * 1024;
  let entries = [];
  try { entries = fs.readdirSync(s.folder, { withFileTypes: true }); } catch { return pending(db, s); }
  const files = [];
  for (const e of entries) {
    if (!e.isFile() || !EXT.has(path.extname(e.name).toLowerCase())) continue;
    const p = path.join(s.folder, e.name);
    let st; try { st = fs.statSync(p); } catch { continue; }
    if (st.size === 0 || st.size > maxBytes || st.mtimeMs < s.since) continue;
    files.push({ p, name: e.name, size: st.size, mtime: Math.floor(st.mtimeMs) });
  }
  files.sort((a, b) => b.mtime - a.mtime);
  for (const f of s.since === 0 ? files.slice(0, OLDER_CAP) : files) {
    const known = db.prepare('SELECT 1 FROM watched_files WHERE path=? AND size=? AND mtime=?').get(f.p, f.size, f.mtime);
    if (known) continue;
    let buf; try { buf = fs.readFileSync(f.p); } catch { continue; }   // still being written or locked: try again next time
    const hash = sha(buf);
    const same = db.prepare('SELECT status FROM watched_files WHERE hash=?').get(hash);
    if (same) { db.prepare('UPDATE watched_files SET path=?, name=?, size=?, mtime=? WHERE hash=?').run(f.p, f.name, f.size, f.mtime, hash); continue; }
    let st = 'pending', meta = {};
    try {
      const text = buf.toString('utf8');
      const format = C.detectFormat(f.name, text);
      if (format !== 'ofx') throw bad('This file does not look like a bank statement (OFX, QFX or QBO). Open it from Import & Convert instead.');
      const model = C.parse('ofx', text, {}, { maxRows: cfg.maxImportRows || 200000 });
      const tx = model.bank_transactions, dates = tx.map((t) => t.date).sort();
      if (!tx.length) throw bad('This file has no bank transactions in it. It may be empty, or it may not be a bank statement.');
      meta = { org: model.bank_meta?.org || '', tail: model.bank_meta?.account_id_tail || '', kind: model.bank_meta?.kind || 'bank', count: tx.length, from: dates[0], to: dates[dates.length - 1], warnings: model.warnings.slice(0, 3) };
    } catch (e) { st = 'unreadable'; meta = { error: e.message || 'This file could not be read.' }; }
    db.prepare('INSERT INTO watched_files(hash,path,name,size,mtime,status,info,seen_at) VALUES (?,?,?,?,?,?,?,?)').run(hash, f.p, f.name, f.size, f.mtime, st, JSON.stringify(meta), Date.now());
  }
  void force;
  return pending(db, s);
}

function suggest(db, s, meta) {
  const key = `${meta.org}|${meta.tail}`;
  const remembered = s.accounts[key];
  if (remembered && db.prepare('SELECT 1 FROM accounts WHERE id=? AND active=1').get(remembered)) return remembered;
  if (meta.tail) {
    const hits = db.prepare("SELECT id FROM accounts WHERE subtype IN ('bank','credit_card') AND active=1 AND (name LIKE ? OR number LIKE ?)").all('%' + meta.tail + '%', '%' + meta.tail + '%');
    if (hits.length === 1) return hits[0].id;
  }
  return null;
}

function pending(db, s = settings(db)) {
  return db.prepare("SELECT * FROM watched_files WHERE status IN ('pending','unreadable') ORDER BY mtime DESC LIMIT 100").all().map((r) => {
    const m = info(r);
    return { hash: r.hash, name: r.name, status: r.status, size: r.size, mtime: r.mtime, ...m, suggested_account_id: r.status === 'pending' ? suggest(db, s, m) : null };
  });
}

function status(db, scanNow = true, cfg = {}) {
  const s = settings(db);
  if (scanNow && s.enabled && Date.now() - (last.get(db) || 0) > 20_000) scan(db, cfg);
  return { enabled: s.enabled, folder: s.folder, since: s.since, files: s.enabled ? pending(db, s) : [] };
}

function rowFor(db, hash) {
  if (!/^[0-9a-f]{64}$/.test(String(hash))) throw new HttpError(404, 'That file is not in the list any more. Press Check Now to look again.');
  const r = db.prepare('SELECT * FROM watched_files WHERE hash=?').get(hash);
  if (!r) throw new HttpError(404, 'That file is not in the list any more. Press Check Now to look again.');
  return r;
}

function importFile(db, ctx, hash, bankAccountId, cfg) {
  const s = settings(db), r = rowFor(db, hash);
  if (r.status !== 'pending') throw bad('That file has already been dealt with.');
  if (!bankAccountId) throw bad('Choose which bank or card account this statement belongs to, then press Import.');
  let real, root;
  try { real = fs.realpathSync(r.path); root = fs.realpathSync(s.folder); } catch { throw bad('That file is no longer in the watched folder (it was moved or deleted). Press Check Now to look again.'); }
  if (path.dirname(real).toLowerCase() !== root.toLowerCase()) throw bad('That file is no longer in the watched folder, so Ledjoro will not read it.');
  const buf = fs.readFileSync(real);
  if (sha(buf) !== hash) throw bad('That file changed after Ledjoro found it. Press Check Now to look at the new version.');
  const model = C.parse('ofx', buf.toString('utf8'), {}, { maxRows: cfg.maxImportRows || 200000 });
  const res = C.commit(db, ctx, model, { filename: r.name, bank_account_id: bankAccountId });
  const m = info(r);
  if (m.org || m.tail) { s.accounts[`${m.org}|${m.tail}`] = bankAccountId; save(db, s); }   // next time, this bank's statements go to the same account without asking
  db.prepare("UPDATE watched_files SET status='imported', batch_id=? WHERE hash=?").run(res.batchId, hash);
  audit(db, ctx, 'watch.import', 'import_batch', res.batchId, `${r.name}: ${res.bank_added} new, ${res.bank_duplicates} already there`);
  return res;
}

function ignoreFile(db, ctx, hash) {
  const r = rowFor(db, hash);
  db.prepare("UPDATE watched_files SET status='ignored' WHERE hash=?").run(hash);
  audit(db, ctx, 'watch.ignore', 'watched_file', hash.slice(0, 12), r.name);
  return { ok: true };
}

/** Check now and then while Ledjoro is open. */
function schedule(db, cfg, log = () => {}) {
  const tick = () => { try { if (settings(db).enabled) scan(db, cfg); } catch (e) { log('watch folder problem:', e && e.message); } };
  setTimeout(tick, 15_000).unref();
  setInterval(tick, REPEAT_MS).unref();
}

module.exports = { configure, rescan, scan, status, importFile, ignoreFile, schedule, settings };
