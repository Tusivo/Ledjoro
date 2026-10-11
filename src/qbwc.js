'use strict';
/**
 * QuickBooks Web Connector support (QuickBooks Desktop).
 *
 * The Web Connector is Intuit's small Windows program that sits next to QuickBooks Desktop. It does not accept calls from other
 * programs; instead it connects out to a web address you give it, says "I'm here", and does what that service asks (here: look up
 * accounts, customers, vendors, items and transactions, READ-ONLY) and hands back the answers.
 *
 * So Ledjoro runs a tiny service of its own for that, only while the link is switched on, only on this computer
 * (127.0.0.1 / ::1, never the network), and only for the Web Connector's one-time username and password.
 * What comes back is held for the user to preview; nothing reaches the books until they confirm in Ledjoro.
 */
const http = require('node:http');
const crypto = require('node:crypto');
const { getSetting, setSetting } = require('./db');
const { bad, HttpError } = require('./util');
const { audit } = require('./audit');
const A = require('./auth');
const { execFileSync } = require('node:child_process');

// The company file the Web Connector recorded for this application (Windows only; '' when unknown).
function storedCompanyFile(user) {
  if (process.platform !== 'win32') return '';
  try {
    const base = 'HKCU\\Software\\Intuit\\QBWebConnector\\SOAPWebServices';
    const out = execFileSync('reg', ['query', base, '/s', '/f', user, '/d'], { encoding: 'utf8', timeout: 4000, windowsHide: true });
    const key = out.split(/\r?\n/).find((l) => l.startsWith('HKEY_'));
    if (!key) return '';
    const v = execFileSync('reg', ['query', key, '/v', 'CompanyFile'], { encoding: 'utf8', timeout: 4000, windowsHide: true });
    const m = v.match(/CompanyFile\s+REG_SZ\s+(.+)/);
    return m ? m[1].trim() : '';
  } catch { return ''; }
}
const C = require('./convert');
const { parseXml, find, val } = require('./importers/qbxml');
const Q = require('./qbsync');

const KEY = 'qbwc';
const NS = 'http://developer.intuit.com/';
const MAX_BODY = 128 * 1024 * 1024;
const states = new WeakMap();   // one per database (one per running engine)

const esc = (s) => String(s ?? '').replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
const guid = () => '{' + crypto.randomUUID().toUpperCase() + '}';

function conf(db) { try { return JSON.parse(getSetting(db, KEY) || '{}'); } catch { return {}; } }
function saveConf(db, c) { setSetting(db, KEY, JSON.stringify(c)); }
function st(db) {
  let s = states.get(db);
  if (!s) { s = { servers: [], sessions: new Map(), fails: [], lastContact: 0, phase: 'idle', step: '', progress: 0, error: '', pending: null, portChanged: false, log: () => {} }; states.set(db, s); }
  return s;
}

// ---------- switching it on and off ----------
const newPassword = () => crypto.randomBytes(15).toString('base64url');

async function listenOn(s, host, port) {
  return new Promise((resolve) => {
    const server = http.createServer((req, res) => handle(s.db, req, res));
    server.requestTimeout = 5 * 60_000; server.headersTimeout = 15_000; server.keepAliveTimeout = 5_000;
    server.once('error', (e) => resolve({ error: e }));
    server.listen(port, host, () => resolve({ server, port: server.address().port }));
  });
}

/** Start the service if it is switched on (called when the engine starts, and when the user switches it on). */
async function start(db, log = () => {}) {
  const s = st(db); s.db = db; s.log = log;
  const c = conf(db);
  if (!c.enabled || s.servers.length) return status(db);
  let r = await listenOn(s, '127.0.0.1', c.port || 0);
  if (r.error && c.port) { r = await listenOn(s, '127.0.0.1', 0); if (!r.error) { c.port = r.port; saveConf(db, c); s.portChanged = true; } }   // the old port is busy: pick another; the connection file must be saved again
  if (r.error) { s.error = 'Ledjoro could not open its local connection for QuickBooks: ' + (r.error.message || r.error); log('qbwc:', s.error); return status(db); }
  if (!c.port) { c.port = r.port; saveConf(db, c); }
  s.servers.push(r.server);
  const v6 = await listenOn(s, '::1', c.port);   // "localhost" may mean ::1 on this PC; still this computer only
  if (!v6.error) s.servers.push(v6.server);
  log('qbwc: listening on this computer, port', c.port);
  return status(db);
}
async function stop(db) {
  const s = st(db);
  for (const sv of s.servers) { try { sv.close(); sv.closeAllConnections?.(); } catch { /* already closed */ } }
  s.servers = []; s.sessions.clear();
}

async function enable(db, ctx, log) {
  const c = conf(db), password = newPassword();
  c.enabled = true; c.username = c.username || 'ledjoro-' + crypto.randomBytes(3).toString('hex');
  c.password_hash = A.hashPassword(password); c.owner_id = c.owner_id || guid(); c.file_id = c.file_id || guid();
  saveConf(db, c);
  audit(db, ctx, 'qbwc.enable', 'settings', '', 'QuickBooks Web Connector link switched on');
  await stop(db); await start(db, log);
  const s = st(db);
  if (!s.servers.length) throw bad(s.error || 'Ledjoro could not open its local connection for QuickBooks. Close other programs that may be using network ports and try again.');
  return { ...status(db), password };
}
async function newPass(db, ctx) {
  const c = conf(db); if (!c.enabled) throw bad('The QuickBooks link is switched off. Turn it on first.');
  const password = newPassword(); c.password_hash = A.hashPassword(password); saveConf(db, c);
  audit(db, ctx, 'qbwc.password', 'settings', '', 'QuickBooks Web Connector password replaced');
  st(db).sessions.clear();
  return { ...status(db), password };
}
async function disable(db, ctx) {
  const c = conf(db); c.enabled = false; saveConf(db, c);
  audit(db, ctx, 'qbwc.disable', 'settings', '', 'QuickBooks Web Connector link switched off');
  await stop(db); const s = st(db); s.pending = null; s.phase = 'idle'; s.error = '';
  return status(db);
}

function status(db) {
  const c = conf(db), s = st(db);
  const p = s.pending;
  return {
    enabled: !!c.enabled, running: s.servers.length > 0, port: c.port || 0, username: c.username || '', port_changed: s.portChanged,
    last_contact: s.lastContact, phase: s.phase, step: s.step, progress: s.progress, error: s.error,
    pending: p ? { counts: p.counts, started: p.started, finished: p.finished } : null, since: c.since || 0, last_import: c.last_import || 0,
  };
}

/** The small file the Web Connector reads to learn about Ledjoro (it contains no password). */
function qwc(db, readOnly = false) {
  const c = conf(db); if (!c.enabled) throw bad('The QuickBooks link is switched off. Turn it on first.');
  const company = getSetting(db, 'company_name') || 'My Company';
  const xml = `<?xml version="1.0"?>
<QBWCXML>
  <AppName>Ledjoro - ${esc(company)}</AppName>
  <AppID></AppID>
  <AppURL>http://localhost:${c.port}/qbwc</AppURL>
  <AppDescription>Copies your QuickBooks accounts, customers, vendors, items and transactions into Ledjoro on this computer. Ledjoro only asks questions: nothing is changed in QuickBooks.</AppDescription>
  <AppSupport>http://localhost:${c.port}/</AppSupport>
  <UserName>${esc(c.username)}</UserName>
  <OwnerID>${esc(c.owner_id)}</OwnerID>
  <FileID>${esc(c.file_id)}</FileID>
  <QBType>QBFS</QBType>
  <IsReadOnly>${readOnly ? 'true' : 'false'}</IsReadOnly>
</QBWCXML>
`;
  return { raw: xml, type: 'application/xml; charset=utf-8', filename: 'Ledjoro.qwc' };
}

// ---------- the Web Connector's conversation ----------
const soapArray = (m, strings) => `<${m}Response xmlns="${NS}"><${m}Result>${strings.map((x) => `<string>${esc(x)}</string>`).join('')}</${m}Result></${m}Response>`;
const soapOne = (m, x) => `<${m}Response xmlns="${NS}"><${m}Result>${esc(x)}</${m}Result></${m}Response>`;
const wrap = (inner) => `<?xml version="1.0" encoding="utf-8"?><soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema"><soap:Body>${inner}</soap:Body></soap:Envelope>`;
const local = (n) => n.name.replace(/^.*:/, '');

function authFailed(s) {
  const now = Date.now(); s.fails = s.fails.filter((t) => now - t < 10 * 60_000);
  s.fails.push(now); return s.fails.length;
}

function dispatch(db, method, p) {
  const s = st(db), c = conf(db);
  s.lastContact = Date.now();
  switch (method) {
    case 'serverVersion': return soapOne(method, 'Ledjoro QuickBooks link 1');
    case 'clientVersion': return soapOne(method, '');
    case 'authenticate': {
      s.fails = s.fails.filter((t) => Date.now() - t < 10 * 60_000);
      if (s.fails.length >= 10) return soapArray(method, ['', 'nvu']);   // too many wrong passwords recently: refuse for a while
      const ok = c.enabled && p.strUserName === c.username && c.password_hash && A.verifyPassword(String(p.strPassword ?? ''), c.password_hash);
      if (!ok) { authFailed(s); s.error = 'The Web Connector connected, but its username or password was wrong. In the Web Connector, type the password Ledjoro showed you (or make a new one in Ledjoro).'; return soapArray(method, ['', 'nvu']); }
      const ticket = crypto.randomBytes(24).toString('base64url');
      s.sessions.clear();   // one conversation at a time
      s.sessions.set(ticket, { cursor: 0, iteratorId: '', requestId: 0, run: Q.newRun(), started: Date.now(), since: c.since ? Math.max(0, c.since - 86_400_000) : 0, version: Q.QBXML_VERSION, error: '' });
      s.phase = 'syncing'; s.progress = 0; s.step = 'Starting'; s.error = ''; s.pending = null;
      return soapArray(method, [ticket, storedCompanyFile(String(p.strUserName ?? ''))]);   // the file the Web Connector has on record for us ('' = whichever is open)
    }
    case 'sendRequestXML': {
      const ses = s.sessions.get(p.ticket); if (!ses) return soapOne(method, '');
      const major = Number(p.qbXMLMajorVers), minor = Number(p.qbXMLMinorVers);
      if (Number.isFinite(major) && major > 0 && major < 13) ses.version = `${major}.${Number.isFinite(minor) ? minor : 0}`;
      if (ses.cursor >= Q.STEPS.length) return soapOne(method, '');
      const step = Q.STEPS[ses.cursor];
      s.step = 'Reading ' + step.label;
      ses.requestId++;
      ses.lastRequest = Q.buildRequest(step, ses.requestId, { since: step.kind === 'txn' ? ses.since : 0, iteratorId: ses.iteratorId, version: ses.version });
      return soapOne(method, ses.lastRequest);
    }
    case 'receiveResponseXML': {
      const ses = s.sessions.get(p.ticket); if (!ses) return soapOne(method, '-1');
      const step = Q.STEPS[ses.cursor];
      if (p.hresult) {
        const code = String(p.hresult).toUpperCase();
        const where = step ? ` while asking for the ${step.label}` : '';
        if (code === '0X80040400' || code === '0X80040401') {   // QuickBooks could not read the request: a problem on Ledjoro's side, so keep what was sent for diagnosis
          ses.error = `QuickBooks could not understand a request Ledjoro sent${where} (${p.hresult}: ${p.message || 'no details'}). This is a fault in Ledjoro, not in your QuickBooks or your setup. The exact request was saved in the log file next to your books (ledjoro.log); please send that file along when you report it.`;
          s.log('qbwc: QuickBooks refused this request' + where + ':', ses.lastRequest || '(none)');
        } else {
          ses.error = `QuickBooks reported a problem${where}${p.message ? ': ' + p.message : ''}. Check that QuickBooks Desktop is open with your company file, that you are logged in as an administrator, and that you approved Ledjoro when QuickBooks asked.`;
        }
        s.phase = 'error'; s.error = ses.error; return soapOne(method, '-1');
      }
      const res = Q.readResponse(step, String(p.response ?? ''));
      if (!res.ok) { ses.run.failed.push(`${step.label}: ${res.message || 'QuickBooks could not answer'}${res.code ? ' (code ' + res.code + ')' : ''}`); ses.cursor++; ses.iteratorId = ''; }
      else {
        Q.absorb(ses.run, step, res);
        if (res.more > 0 && res.iteratorId) ses.iteratorId = res.iteratorId; else { ses.cursor++; ses.iteratorId = ''; }
      }
      if (ses.cursor >= Q.STEPS.length) {
        const done = Q.finishRun(ses.run, ses.started);
        s.pending = { ...done, finished: Date.now() }; s.phase = 'ready'; s.progress = 100; s.step = 'Done';
        s.log('qbwc: sync finished', JSON.stringify(done.counts));
        return soapOne(method, '100');
      }
      s.progress = Math.min(99, Math.floor((100 * ses.cursor) / Q.STEPS.length));
      return soapOne(method, String(s.progress));
    }
    case 'getLastError': { const ses = s.sessions.get(p.ticket); return soapOne(method, (ses && ses.error) || s.error || ''); }
    case 'connectionError': s.phase = 'error'; s.error = `The Web Connector could not use QuickBooks${p.message ? ': ' + p.message : ''}. Open QuickBooks with your company file and try again.`; return soapOne(method, 'DONE');
    case 'closeConnection': s.sessions.delete(p.ticket); return soapOne(method, s.phase === 'ready' ? 'Ledjoro has your QuickBooks data ready to review.' : 'OK');
    case 'getInteractiveURL': return soapOne(method, '');
    case 'interactiveDone': case 'interactiveRejected': return soapOne(method, '');
    default: return null;
  }
}

function handle(db, req, res) {
  const host = String(req.headers.host || '').toLowerCase().replace(/:\d+$/, '').replace(/^\[|\]$/g, '');
  const send = (code, type, body) => { res.writeHead(code, { 'Content-Type': type, 'Cache-Control': 'no-store', 'X-Content-Type-Options': 'nosniff' }); res.end(body); };
  if (!['localhost', '127.0.0.1', '::1'].includes(host)) return send(421, 'text/plain', 'Unrecognised host');
  const c = conf(db);
  if (!c.enabled) return send(503, 'text/plain', 'The Ledjoro QuickBooks link is switched off.');
  const path = (req.url || '').split('?')[0];
  if (req.method === 'GET') return send(200, 'text/plain; charset=utf-8', 'Ledjoro is ready to talk to the QuickBooks Web Connector on this computer.\r\nThis page is only for the Web Connector; you do not need to do anything here.');
  if (req.method !== 'POST' || path !== '/qbwc') return send(404, 'text/plain', 'Not found');
  const chunks = []; let size = 0, dead = false;
  req.on('data', (d) => { size += d.length; if (size > MAX_BODY) { dead = true; send(413, 'text/plain', 'Too large'); req.destroy(); } else chunks.push(d); });
  req.on('end', () => {
    if (dead) return;
    try {
      const env = parseXml(Buffer.concat(chunks).toString('utf8'));
      const body = find(env, 'Body')[0] || find(env, 'soap:Body')[0];
      const call = body && body.children[0];
      if (!call) return send(400, 'text/plain', 'Bad request');
      const params = {}; for (const k of call.children) params[local(k)] = k.text;
      const inner = dispatch(db, local(call), params);
      if (inner === null) return send(500, 'text/xml; charset=utf-8', wrap('<soap:Fault><faultcode>soap:Client</faultcode><faultstring>Unknown request</faultstring></soap:Fault>'));
      send(200, 'text/xml; charset=utf-8', wrap(inner));
    } catch (e) { st(db).log('qbwc: bad request', e && e.message); send(400, 'text/plain', 'Bad request'); }
  });
  req.on('error', () => {});
}

// ---------- the result: preview, import, throw away ----------
function preview(db) {
  const p = st(db).pending; if (!p) throw bad('There is nothing waiting to be imported. Run a sync from the QuickBooks Web Connector first.');
  const plan = C.plan(db, p.model);
  return { counts: p.counts, started: p.started, finished: p.finished, ...plan };
}
function commitPending(db, ctx) {
  const s = st(db), p = s.pending; if (!p) throw bad('There is nothing waiting to be imported. Run a sync from the QuickBooks Web Connector first.');
  const r = C.commit(db, ctx, p.model, { filename: 'QuickBooks Web Connector sync', allowSkip: true });
  const c = conf(db); c.since = p.started; c.last_import = Date.now(); saveConf(db, c);
  audit(db, ctx, 'qbwc.import', 'import_batch', r.batchId, 'QuickBooks Web Connector sync imported');
  s.pending = null; s.phase = 'idle'; s.step = ''; s.progress = 0;
  return r;
}
function discard(db) { const s = st(db); s.pending = null; if (s.phase === 'ready') s.phase = 'idle'; return status(db); }

module.exports = { start, stop, enable, newPass, disable, status, qwc, preview, commitPending, discard, HttpError };
