'use strict';
/**
 * Automatic backups. While Ledjoro is open it checks every half hour whether a backup is due and, if so, writes one into the folder
 * you chose (by default "Backups" inside the company folder, so the whole thing stays portable). Old automatic backups beyond the
 * number you want to keep are removed; backups you made by hand are never touched.
 * Backups are locked with your recovery key, or deliberately not locked. A passphrase is never stored, so it cannot be used here.
 */
const fs = require('node:fs');
const path = require('node:path');
const { getSetting, setSetting } = require('./db');
const { bad, vInt, vEnum, vStr } = require('./util');
const { audit } = require('./audit');
const Backup = require('./backup');

const KEY = 'autobackup';
const PREFIX = 'ledjoro-auto-';
const DEFAULTS = { enabled: false, folder: 'Backups', every_days: 1, keep: 14, mode: 'recovery' };

function settings(db) {
  let s = {}; try { s = JSON.parse(getSetting(db, KEY) || '{}'); } catch { /* damaged setting: use the defaults */ }
  return { ...DEFAULTS, ...s };
}
const folderOf = (dataDir, s) => path.resolve(dataDir, s.folder || DEFAULTS.folder);

function status(db, dataDir) {
  const s = settings(db);
  return { ...s, folder_full: folderOf(dataDir, s), last_auto: getSetting(db, 'last_auto_backup') || '', last_error: getSetting(db, 'auto_backup_error') || '', last_backup: getSetting(db, 'last_backup') || '', recovery_linked: Backup.hasRecoveryLink(db) };
}

function save(db, ctx, dataDir, b) {
  const s = {
    enabled: b.enabled === true,
    folder: vStr(b.folder ?? DEFAULTS.folder, 'folder', { required: true, max: 500 }),
    every_days: vEnum(vInt(b.every_days, 'how often', { min: 1, max: 30 }), 'how often', [1, 7, 30]),
    keep: vInt(b.keep, 'how many to keep', { min: 1, max: 365 }),
    mode: vEnum(b.mode, 'protection', ['recovery', 'none']),
  };
  if (s.enabled) {
    if (s.mode === 'recovery' && !Backup.hasRecoveryLink(db)) throw bad('Backups protected by your recovery key need the key to be linked first. Make one backup by hand on this page and type your recovery key when it asks, then turn automatic backups on. Or choose "Not locked".');
    checkFolder(folderOf(dataDir, s));
  }
  setSetting(db, KEY, JSON.stringify(s));
  setSetting(db, 'auto_backup_error', '');
  audit(db, ctx, 'backup.auto_settings', 'settings', '', s.enabled ? `every ${s.every_days} day(s), keep ${s.keep}, ${s.mode}` : 'off');
  return status(db, dataDir);
}

function checkFolder(dir) {
  try {
    fs.mkdirSync(dir, { recursive: true });
    const probe = path.join(dir, '.ledjoro-write-test');
    fs.writeFileSync(probe, 'ok'); fs.rmSync(probe, { force: true });
  } catch (e) {
    const why = e && e.code === 'ENOENT' ? 'The drive or folder is not there (is the USB stick plugged in?).'
      : e && (e.code === 'EACCES' || e.code === 'EPERM') ? 'Windows does not allow Ledjoro to write there. Choose a folder you own, such as one inside Documents.'
      : e && e.code === 'ENOSPC' ? 'That drive is full.' : 'Windows said: ' + (e && e.message ? e.message : e);
    throw bad(`Ledjoro cannot save backups to "${dir}". ${why} Choose another folder.`);
  }
}

let running = false;
async function runNow(db, dataDir, ctx = null) {
  if (running) throw bad('A backup is already being made. Wait a moment and look again.');
  running = true;
  try {
    const s = settings(db), dir = folderOf(dataDir, s);
    checkFolder(dir);
    const mode = s.mode === 'none' ? 'none' : 'recovery';
    if (mode === 'recovery' && !Backup.hasRecoveryLink(db)) throw bad('Your recovery key is not linked to backups, so a locked backup could not be opened later. Make one backup by hand and type your recovery key, or switch automatic backups to "Not locked".');
    const data = await Backup.createBackup(db, { mode });
    const now = new Date(), p2 = (n) => String(n).padStart(2, '0');
    const name = `${PREFIX}${now.getFullYear()}${p2(now.getMonth() + 1)}${p2(now.getDate())}-${p2(now.getHours())}${p2(now.getMinutes())}${p2(now.getSeconds())}.obbak`;
    const tmp = path.join(dir, name + '.part');
    fs.writeFileSync(tmp, data, { mode: 0o600 }); fs.renameSync(tmp, path.join(dir, name));   // never leaves half a backup under a real name
    const kept = fs.readdirSync(dir).filter((f) => f.startsWith(PREFIX) && f.endsWith('.obbak')).sort();
    for (const old of kept.slice(0, Math.max(0, kept.length - s.keep))) { try { fs.rmSync(path.join(dir, old)); } catch { /* in use: next time */ } }
    const iso = now.toISOString();
    setSetting(db, 'last_auto_backup', iso); setSetting(db, 'last_backup', iso); setSetting(db, 'auto_backup_error', '');
    if (ctx) audit(db, ctx, 'backup.auto_now', 'database', '', `${name}, ${data.length} bytes, ${mode === 'none' ? 'not locked' : 'recovery key'}`);
    return { file: name, folder: dir, bytes: data.length };
  } catch (e) {
    setSetting(db, 'auto_backup_error', String(e && e.message ? e.message : e).slice(0, 500));
    throw e;
  } finally { running = false; }
}

function isDue(db) {
  const s = settings(db); if (!s.enabled) return false;
  const last = Date.parse(getSetting(db, 'last_auto_backup') || '');
  return !Number.isFinite(last) || Date.now() - last >= s.every_days * 86400000 - 60000;
}

/** Check now and then; a failed attempt is remembered (and shown in the app) and retried at the next check. */
function schedule(db, dataDir, log = () => {}) {
  const check = async () => { try { if (isDue(db)) { const r = await runNow(db, dataDir); log('automatic backup written:', r.file); } } catch (e) { log('automatic backup problem:', e && e.message); } };
  setTimeout(check, 20000).unref();
  setInterval(check, 30 * 60000).unref();
}

module.exports = { settings, status, save, runNow, isDue, schedule, checkFolder };
