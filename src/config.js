'use strict';
const path = require('node:path');

const env = process.env;
const bool = (v, d) => (v === undefined ? d : /^(1|true|yes)$/i.test(v));

const config = {
  dataDir: path.resolve(env.LEDJORO_DATA || path.join(__dirname, '..', 'data')),
  secureCookies: bool(env.LEDJORO_SECURE_COOKIES, false), // the engine is loopback-only plain http, so cookies are HttpOnly + SameSite=Strict but not Secure
  // Extra Host header values accepted (anti DNS-rebinding). localhost names are always allowed.
  allowedHosts: (env.LEDJORO_ALLOWED_HOSTS || '').split(',').map((s) => s.trim().toLowerCase()).filter(Boolean),
  // The native shell starts the engine with a per-launch secret. Every request must present it, so nothing else
  // on the machine (another program, a web browser) can talk to the engine.
  shellKey: env.LEDJORO_SHELL_KEY || '',
  setupToken: env.LEDJORO_SETUP_TOKEN || '', // lets the shell pass the one-time first-run token privately (never shown or logged)
  // Passkeys are bound to this name. It is never contacted (no website is involved); changing it later would make existing passkeys unusable.
  passkeyRpId: env.LEDJORO_PASSKEY_RPID || 'ledjoro.local',
  sessionIdleMs: 30 * 60 * 1000,
  sessionAbsoluteMs: 12 * 60 * 60 * 1000,
  maxJsonBytes: 1 * 1024 * 1024,
  maxImportBytes: 10 * 1024 * 1024,
  maxImportRows: 200000,
  maxBackupBytes: 64 * 1024 * 1024, // largest backup file the Restore button accepts
};

module.exports = config;
