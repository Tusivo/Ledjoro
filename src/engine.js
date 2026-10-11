'use strict';
/**
 * Ledjoro engine (entry point of Ledjoro-Engine.exe, embedded in the native Ledjoro.exe).
 * Headless: no window, no web pages. It serves the JSON API on 127.0.0.1 only, refuses every request that does not carry the
 * per-launch key (LEDJORO_SHELL_KEY), and exits as soon as the app that started it closes our stdin.
 * Data lives in LEDJORO_DATA, which the app sets to the Ledjoro-Data folder next to Ledjoro.exe (so the whole thing can be copied to another computer).
 * Flags: --port N   --restore <backup.obbak>  (LEDJORO_BACKUP_PASSPHRASE must be set; result is written to ledjoro.log)
 */
const fs = require('node:fs');
const path = require('node:path');
const http = require('node:http');
const cfg = require('./config');
const guard = require('./fileguard');

const args = process.argv.slice(1);
const flag = (n) => { const i = args.indexOf('--' + n); return i >= 0 ? args[i + 1] : undefined; };
const has = (n) => args.includes('--' + n);

const dataDir = path.resolve(process.env.LEDJORO_DATA || 'Ledjoro-Data'); // the app always sets this; there is no default in the user profile
fs.mkdirSync(dataDir, { recursive: true, mode: 0o700 });
cfg.dataDir = dataDir;
cfg.secureCookies = false; // plain http on loopback; cookies are still HttpOnly + SameSite=Strict

// No console exists in the GUI build, so write diagnostics to a log file (never request bodies or credentials).
const logFile = path.join(dataDir, 'ledjoro.log');
const log = (...a) => { try { fs.appendFileSync(logFile, `${new Date().toISOString()} ${a.join(' ')}\n`, { mode: 0o600 }); } catch { /* ignore */ } };
console.log = log; console.warn = log; console.error = log;
try { if (fs.existsSync(logFile) && fs.statSync(logFile).size > 2 * 1024 * 1024) fs.renameSync(logFile, logFile + '.old'); } catch { /* ignore */ }

async function main() {
  if (has('restore')) {
    try { require('./backup').restoreBackup(flag('restore'), process.env.LEDJORO_BACKUP_PASSPHRASE || '', path.join(dataDir, 'ledjoro.db')); log('restore complete'); }
    catch (e) { log('restore failed:', e.message); process.exitCode = 1; }
    return;
  }
  if (!cfg.shellKey || cfg.shellKey.length < 32) { log('refusing to start: this engine is only started by the Ledjoro app (no launch key)'); process.exit(2); }

  const { openDb } = require('./db');
  const { createApp } = require('./app');
  const { verifyAudit } = require('./audit');
  const { checkLedgerIntegrity } = require('./ledger');
  // protect the books file: clear a read-only flag, limit the folder to this Windows user, hold the file exclusively, prove it is writable
  guard.clearReadOnly(dataDir, log);
  log('folder permissions:', guard.restrictFolder(dataDir, log));
  let db;
  try {
    db = openDb(path.join(dataDir, 'ledjoro.db'), { exclusive: true });
    db.exec('BEGIN IMMEDIATE'); db.exec('COMMIT'); // takes the exclusive lock now and proves we can write
  } catch (e) { log('problem:', guard.explain(e)); process.exit(3); }
  const { handler, state } = createApp(db, cfg);
  state.integrity = guard.integrityReport(db, verifyAudit, checkLedgerIntegrity);
  if (!state.integrity.ok) log('WARNING: the books were changed outside Ledjoro:', state.integrity.problems.join('; '));
  const server = http.createServer(handler);
  server.requestTimeout = 60_000; server.headersTimeout = 15_000; server.keepAliveTimeout = 5_000;
  const port = await new Promise((resolve, reject) => { server.once('error', reject); server.listen(Number(flag('port') || 0), '127.0.0.1', () => resolve(server.address().port)); });
  log(`engine listening on 127.0.0.1:${port}; data in ${dataDir}`);

  require('./autobackup').schedule(db, dataDir, log);
  require('./watchfolder').schedule(db, cfg, log);
  require('./qbwc').start(db, log).catch((e) => log('qbwc start problem:', e && e.message));
  const quit = (why) => { log('exiting:', why); server.close(); try { db.close(); } catch { /* ignore */ } process.exit(0); };
  process.stdin.on('end', () => quit('app closed'));
  process.stdin.on('close', () => quit('app closed'));
  process.stdin.on('error', () => quit('app gone'));
  process.stdin.resume();
  for (const s of ['SIGINT', 'SIGTERM']) process.on(s, () => quit(s));
}

process.on('uncaughtException', (e) => { log('fatal:', e.stack || e.message); process.exit(1); });
process.on('unhandledRejection', (e) => { log('unhandled rejection:', e?.stack || e); process.exit(1); });
main().catch((e) => { log('startup failed:', e.stack || e.message); process.exit(1); });
