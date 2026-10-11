'use strict';
const http = require('node:http');
const path = require('node:path');
const { openDb } = require('../src/db');
const { createApp } = require('../src/app');
const baseCfg = require('../src/config');

async function startServer(overrides = {}) {
  require('../src/auth').resetLimiters(); // each test server starts with fresh rate-limit counters
  const db = openDb(':memory:');
  const dataDir = require('node:fs').mkdtempSync(require('node:path').join(require('node:os').tmpdir(), 'obdata-'));
  const cfg = { ...baseCfg, dataDir, secureCookies: false, ...overrides };
  const { handler, state } = createApp(db, cfg);
  const server = http.createServer(handler);
  await new Promise((r) => server.listen(0, '127.0.0.1', r));
  const port = server.address().port;
  return { db, cfg, state, server, port, close: () => new Promise((r) => { server.close(r); server.closeAllConnections?.(); }) };
}

class Client {
  constructor(port) { this.port = port; this.cookie = ''; this.csrf = ''; }
  request(method, p, body, { headers = {}, raw } = {}) {
    return new Promise((resolve, reject) => {
      const data = raw ?? (body === undefined ? undefined : JSON.stringify(body));
      const h = { Host: `127.0.0.1:${this.port}`, ...headers };
      if (data !== undefined) { h['Content-Type'] = h['Content-Type'] || 'application/json'; h['Content-Length'] = Buffer.byteLength(data); }
      if (this.cookie) h.Cookie = this.cookie;
      if (this.csrf && method !== 'GET' && !('X-CSRF-Token' in h)) h['X-CSRF-Token'] = this.csrf;
      const req = http.request({ host: '127.0.0.1', port: this.port, method, path: p, headers: h }, (res) => {
        const chunks = [];
        res.on('data', (c) => chunks.push(c));
        res.on('end', () => {
          const text = Buffer.concat(chunks).toString('utf8');
          const sc = res.headers['set-cookie'];
          if (sc) { const c = sc[0].split(';')[0]; this.cookie = c.endsWith('=') ? '' : c; }
          let json; try { json = JSON.parse(text); } catch { /* not json */ }
          resolve({ status: res.statusCode, headers: res.headers, text, json });
        });
      });
      req.on('error', reject);
      if (data !== undefined) req.write(data);
      req.end();
    });
  }
  get(p, o) { return this.request('GET', p, undefined, o); }
  post(p, b, o) { return this.request('POST', p, b ?? {}, o); }
  patch(p, b, o) { return this.request('PATCH', p, b ?? {}, o); }
  put(p, b, o) { return this.request('PUT', p, b ?? {}, o); }
  async refreshSession() { const r = await this.get('/api/session'); this.csrf = r.json.csrf || ''; return r.json; }
}

const PASSWORD = 'correct-horse-battery-9';
async function setupAdmin(s, username = 'admin') {
  const c = new Client(s.port);
  const r = await c.post('/api/setup', { token: s.state.setupToken, username, password: PASSWORD, display_name: 'Admin', company_name: 'Test Co' });
  if (r.status !== 200) throw new Error('setup failed ' + r.text);
  c.recoveryKey = r.json.recovery_key; // tests that need to open a backup with the recovery key use this
  await c.refreshSession();
  return c;
}
async function loginAs(s, username, password = PASSWORD) {
  const c = new Client(s.port);
  const r = await c.post('/api/login', { username, password });
  if (r.status !== 200) throw new Error('login failed ' + r.text);
  await c.refreshSession();
  return c;
}
const fixture = (...p) => require('node:fs').readFileSync(path.join(__dirname, '..', 'samples', ...p), 'utf8');

module.exports = { startServer, Client, setupAdmin, loginAs, PASSWORD, fixture };
