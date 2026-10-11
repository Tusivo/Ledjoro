'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const { startServer, Client, setupAdmin, loginAs, PASSWORD, fixture } = require('./helpers');

test('first-run setup needs the one-time token, then disappears', async () => {
  const s = await startServer();
  try {
    const c = new Client(s.port);
    assert.equal((await c.get('/api/session')).json.setupRequired, true);
    assert.equal((await c.post('/api/setup', { token: 'wrong', username: 'admin', password: PASSWORD })).status, 403);
    assert.equal((await c.post('/api/setup', { token: s.state.setupToken, username: 'admin', password: 'short' })).status, 400);
    const ok = await c.post('/api/setup', { token: s.state.setupToken, username: 'admin', password: PASSWORD, company_name: 'Acme' });
    assert.equal(ok.status, 200);
    assert.equal((await c.post('/api/setup', { token: 'x', username: 'b', password: PASSWORD })).status, 404);
    const sess = await c.refreshSession();
    assert.equal(sess.authenticated, true); assert.equal(sess.company, 'Acme');
  } finally { await s.close(); }
});

test('security headers, cookie flags and no CORS', async () => {
  const s = await startServer();
  try {
    const c = new Client(s.port);
    const r = await c.get('/api/ping');
    assert.equal(r.status, 200);
    assert.match(r.headers['content-security-policy'], /default-src 'none'/);
    assert.match(r.headers['content-security-policy'], /frame-ancestors 'none'/);
    assert.equal(r.headers['x-content-type-options'], 'nosniff');
    assert.equal(r.headers['access-control-allow-origin'], undefined);
    const o = await c.request('OPTIONS', '/api/session');
    assert.equal(o.status, 405);
    await c.post('/api/setup', { token: s.state.setupToken, username: 'admin', password: PASSWORD });
    const l = await new Client(s.port).request('POST', '/api/login', { username: 'admin', password: PASSWORD });
    const sc = l.headers['set-cookie'][0];
    assert.match(sc, /HttpOnly/); assert.match(sc, /SameSite=Strict/);
  } finally { await s.close(); }
});

test('secure mode uses __Host- cookie, Secure flag and HSTS', async () => {
  const s = await startServer({ secureCookies: true });
  try {
    const c = new Client(s.port);
    const r = await c.post('/api/setup', { token: s.state.setupToken, username: 'admin', password: PASSWORD });
    assert.match(r.headers['set-cookie'][0], /^__Host-ob_session=.*; Secure/);
    assert.ok(r.headers['strict-transport-security']);
  } finally { await s.close(); }
});

test('anonymous access is refused; CSRF token and origin are enforced', async () => {
  const s = await startServer();
  try {
    const admin = await setupAdmin(s);
    assert.equal((await new Client(s.port).get('/api/accounts')).status, 401);
    const noCsrf = await admin.post('/api/contacts', { kind: 'customer', name: 'A' }, { headers: { 'X-CSRF-Token': 'nope' } });
    assert.equal(noCsrf.status, 403);
    const cross = await admin.post('/api/contacts', { kind: 'customer', name: 'A' }, { headers: { Origin: 'https://evil.example' } });
    assert.equal(cross.status, 403);
    const crossSite = await admin.post('/api/contacts', { kind: 'customer', name: 'A' }, { headers: { 'Sec-Fetch-Site': 'cross-site' } });
    assert.equal(crossSite.status, 403);
    const nullOrigin = await admin.post('/api/contacts', { kind: 'customer', name: 'A' }, { headers: { Origin: 'null' } });
    assert.equal(nullOrigin.status, 403);
    assert.equal((await admin.post('/api/contacts', { kind: 'customer', name: 'A' }, { headers: { Origin: `http://127.0.0.1:${s.port}` } })).status, 200);
    const form = await admin.post('/api/contacts', undefined, { raw: 'kind=customer', headers: { 'Content-Type': 'application/x-www-form-urlencoded' } });
    assert.equal(form.status, 415);
  } finally { await s.close(); }
});

test('Host header allow-list blocks DNS-rebinding style requests', async () => {
  const s = await startServer();
  try {
    const c = new Client(s.port);
    const r = await c.get('/api/session', { headers: { Host: 'attacker.example' } });
    assert.equal(r.status, 421);
  } finally { await s.close(); }
});

test('login: generic errors, escalating lockout, and sessions are revoked by logout', async () => {
  const s = await startServer();
  try {
    await setupAdmin(s);
    const c = new Client(s.port);
    const unknown = await c.post('/api/login', { username: 'nobody', password: 'x'.repeat(14) });
    const wrong = await c.post('/api/login', { username: 'admin', password: 'x'.repeat(14) });
    assert.equal(unknown.status, 401); assert.equal(wrong.status, 401);
    assert.equal(unknown.json.error, wrong.json.error);
    for (let i = 0; i < 4; i++) await c.post('/api/login', { username: 'admin', password: 'bad-bad-bad-bad' });
    const locked = await c.post('/api/login', { username: 'admin', password: PASSWORD });
    assert.equal(locked.status, 429, 'correct password is refused while locked');
    s.db.prepare('UPDATE users SET locked_until=0').run();
    const good = await loginAs(s, 'admin');
    const tok = good.cookie;
    await good.post('/api/logout');
    const replay = new Client(s.port); replay.cookie = tok;
    assert.equal((await replay.get('/api/accounts')).status, 401, 'old session token no longer works');
  } finally { await s.close(); }
});

test('sessions expire when idle', async () => {
  const s = await startServer();
  try {
    const admin = await setupAdmin(s);
    s.db.prepare('UPDATE sessions SET last_seen=?').run(Date.now() - 31 * 60 * 1000);
    assert.equal((await admin.get('/api/accounts')).status, 401);
  } finally { await s.close(); }
});

test('roles: view-only users cannot write; bookkeepers cannot manage users; last admin is protected', async () => {
  const s = await startServer();
  try {
    const admin = await setupAdmin(s);
    const roles = (await admin.get('/api/roles')).json;
    const role = (n) => roles.find((r) => r.name === n).id;
    for (const [u, r] of [['viewer', 'View only'], ['book', 'Bookkeeper']]) {
      const cu = await admin.post('/api/users', { username: u, password: PASSWORD, role_id: role(r) });
      assert.equal(cu.status, 200);
    }
    // new users must change their password first
    const v0 = new Client(s.port); await v0.post('/api/login', { username: 'viewer', password: PASSWORD }); await v0.refreshSession();
    assert.equal((await v0.get('/api/accounts')).status, 403);
    assert.equal((await v0.post('/api/password', { current: PASSWORD, next: PASSWORD })).status, 400);
    assert.equal((await v0.post('/api/password', { current: PASSWORD, next: 'a-brand-new-passphrase-1' })).status, 200);
    await v0.refreshSession();
    assert.equal((await v0.get('/api/accounts')).status, 200);
    assert.equal((await v0.post('/api/contacts', { kind: 'customer', name: 'X' })).status, 403);
    assert.equal((await v0.get('/api/users')).status, 403);
    assert.equal((await v0.get('/api/audit')).status, 403);
    assert.equal((await v0.get('/api/export/obx')).status, 403);

    const b0 = new Client(s.port); await b0.post('/api/login', { username: 'book', password: PASSWORD }); await b0.refreshSession();
    await b0.post('/api/password', { current: PASSWORD, next: 'another-long-passphrase-2' }); await b0.refreshSession();
    assert.equal((await b0.post('/api/contacts', { kind: 'customer', name: 'Cust' })).status, 200);
    assert.equal((await b0.post('/api/accounts', { name: 'Hack', subtype: 'expense' })).status, 403);
    assert.equal((await b0.post('/api/users', { username: 'x', password: PASSWORD, role_id: 1 })).status, 403);
    assert.equal((await b0.put('/api/settings', { closing_date: '2020-01-01' })).status, 403);

    const me = (await admin.get('/api/users')).json.find((u) => u.username === 'admin');
    assert.equal((await admin.patch(`/api/users/${me.id}`, { active: false })).status, 400, 'cannot disable the last administrator');
    assert.equal((await admin.patch(`/api/users/${me.id}`, { role_id: role('View only') })).status, 400);
  } finally { await s.close(); }
});

test('request body limits and malformed JSON are rejected cleanly', async () => {
  const s = await startServer({ maxJsonBytes: 2000 });
  try {
    const admin = await setupAdmin(s);
    assert.equal((await admin.post('/api/contacts', { kind: 'customer', name: 'x'.repeat(5000) })).status, 413);
    assert.equal((await admin.post('/api/contacts', undefined, { raw: '{not json' })).status, 400);
    assert.equal((await admin.post('/api/contacts', undefined, { raw: '[1,2]' })).status, 400);
    assert.equal((await admin.get('/api/session')).status, 200, 'server still healthy');
  } finally { await s.close(); }
});

test('there is no web UI: only /api/ paths answer, everything else is a 404', async () => {
  const s = await startServer();
  try {
    const c = new Client(s.port);
    for (const p of ['/', '/index.html', '/app.js', '/../package.json', '/%2e%2e/package.json', '/.env', '/src/db.js']) {
      const r = await c.get(p);
      assert.ok([404, 400].includes(r.status), `${p} -> ${r.status}`);
    }
  } finally { await s.close(); }
});

test('SQL-injection style input is treated as data', async () => {
  const s = await startServer();
  try {
    const admin = await setupAdmin(s);
    const evil = "Robert'); DROP TABLE accounts;--";
    assert.equal((await admin.post('/api/contacts', { kind: 'customer', name: evil })).status, 200);
    const list = (await admin.get('/api/contacts?kind=customer')).json;
    assert.equal(list[0].name, evil);
    assert.equal((await admin.get('/api/accounts')).json.length > 10, true);
    assert.equal((await admin.get("/api/contacts?kind=customer'%20OR%201=1")).status, 400);
  } finally { await s.close(); }
});

test('end-to-end: invoice -> partial payment -> bill -> bank import -> reports stay balanced', async () => {
  const s = await startServer();
  try {
    const admin = await setupAdmin(s);
    const acc = (n) => admin.get('/api/accounts').then((r) => r.json.find((a) => a.name === n).id);
    const cust = (await admin.post('/api/contacts', { kind: 'customer', name: 'Globex', terms_days: 15 })).json.id;
    const vend = (await admin.post('/api/contacts', { kind: 'vendor', name: 'Paper Co' })).json.id;
    const sales = await acc('Sales'), checking = await acc('Checking'), supplies = await acc('Office Supplies');

    const inv = await admin.post('/api/documents', { kind: 'invoice', contact_id: cust, doc_date: '2026-03-01', lines: [{ description: 'Consulting', qty_milli: 2500, unit_price_cents: 10000, account_id: sales }] });
    assert.equal(inv.status, 200);
    const doc = (await admin.get(`/api/documents/${inv.json.id}`)).json;
    assert.equal(doc.total_cents, 25000); assert.equal(doc.due_date, '2026-03-16'); assert.equal(doc.number, 'INV-0001');

    assert.equal((await admin.post('/api/payments', { kind: 'customer', contact_id: cust, pay_date: '2026-03-10', amount_cents: 10000, account_id: checking, applications: [{ document_id: doc.id, amount_cents: 10000 }] })).status, 200);
    assert.equal((await admin.post('/api/payments', { kind: 'customer', contact_id: cust, pay_date: '2026-03-10', amount_cents: 99999, account_id: checking, applications: [{ document_id: doc.id, amount_cents: 99999 }] })).status, 400, 'over-payment refused');
    assert.equal((await admin.get(`/api/documents/${doc.id}`)).json.paid_cents, 10000);

    const bill = await admin.post('/api/documents', { kind: 'bill', contact_id: vend, doc_date: '2026-03-02', lines: [{ description: 'Paper', qty_milli: 1000, unit_price_cents: 4000, account_id: supplies }] });
    assert.equal(bill.status, 200);

    const imp = await admin.post('/api/import/preview', { filename: 'x.qbo', content: fixture('sample.qbo') });
    assert.equal(imp.json.format, 'ofx'); assert.equal(imp.json.summary.bank_transactions.count, 3);
    const noAcct = await admin.post('/api/import/commit', { filename: 'x.qbo', content: fixture('sample.qbo') });
    assert.equal(noAcct.status, 400);
    assert.equal((await admin.post('/api/import/commit', { filename: 'x.qbo', content: fixture('sample.qbo'), bank_account_id: checking })).status, 200);
    const txns = (await admin.get('/api/bank/transactions?status=new')).json;
    assert.equal(txns.length, 3);
    await admin.post('/api/bank/rules', { name: 'Software', match_text: 'cloud software', account_id: await acc('Software & Subscriptions') });
    assert.equal((await admin.post('/api/bank/apply-rules')).json.applied, 1);
    const paper = txns.find((t) => /OFFICE SUPPLY/.test(t.description));
    assert.equal((await admin.post(`/api/bank/transactions/${paper.id}/categorize`, { account_id: supplies })).status, 200);
    assert.equal((await admin.post(`/api/bank/transactions/${paper.id}/categorize`, { account_id: supplies })).status, 400, 'cannot categorize twice');

    const tb = (await admin.get('/api/reports/trial-balance?asof=2026-12-31')).json;
    assert.equal(tb.balanced, true);
    const bs = (await admin.get('/api/reports/balance-sheet?asof=2026-12-31')).json;
    assert.equal(bs.balanced, true);
    const pl = (await admin.get('/api/reports/profit-loss?from=2026-01-01&to=2026-12-31')).json;
    assert.equal(pl.income.total_cents, 25000);
    assert.equal(pl.net_income_cents, 25000 - 4000 - 4567 - 1999);
    const ar = (await admin.get('/api/reports/ar-aging?asof=2026-04-30')).json;
    assert.equal(ar.totals.total_cents, 15000); assert.equal(ar.rows[0].d31_60 + ar.rows[0].d1_30 > 0, true);

    // void the bill, then payment reversal restores the invoice
    assert.equal((await admin.post(`/api/documents/${bill.json.id}/void`)).status, 200);
    assert.equal((await admin.post(`/api/documents/${doc.id}/void`)).status, 400, 'cannot void a paid-on invoice');
    const pid = (await admin.get(`/api/documents/${doc.id}`)).json.payments[0].payment_id;
    assert.equal((await admin.post(`/api/payments/${pid}/void`)).status, 200);
    assert.equal((await admin.get(`/api/documents/${doc.id}`)).json.paid_cents, 0);
    assert.equal((await admin.post(`/api/documents/${doc.id}/void`)).status, 200);

    // closing date blocks back-dated changes
    assert.equal((await admin.put('/api/settings', { closing_date: '2026-03-31' })).status, 200);
    const late = await admin.post('/api/journal', { date: '2026-03-15', lines: [{ account_id: checking, debit_cents: 1 }, { account_id: sales, credit_cents: 1 }] });
    assert.equal(late.status, 400); assert.match(late.json.error, /closed/);

    const v = (await admin.get('/api/audit/verify')).json;
    assert.equal(v.audit_chain.ok, true); assert.equal(v.ledger.ok, true);
    assert.ok(v.audit_chain.checked > 10);
  } finally { await s.close(); }
});

test('exports are protected and CSV-safe', async () => {
  const s = await startServer();
  try {
    const admin = await setupAdmin(s);
    await admin.post('/api/contacts', { kind: 'customer', name: '=cmd|calc!A1' });
    const r = await admin.get('/api/export/csv/contacts');
    assert.match(r.headers['content-disposition'], /attachment/);
    assert.ok(r.text.includes("'=cmd|calc!A1"));
    const o = await admin.get('/api/export/obx');
    assert.equal(JSON.parse(o.text).format, 'ledjoro-interchange');
    assert.equal((await admin.get('/api/export/csv/users')).status, 404, 'users table is never exportable');
  } finally { await s.close(); }
});

test('import: oversized or hostile files are rejected without crashing', async () => {
  const s = await startServer({ maxImportBytes: 5000 });
  try {
    const admin = await setupAdmin(s);
    assert.equal((await admin.post('/api/import/preview', { filename: 'a.iif', content: 'x'.repeat(6000) })).status, 400);
    assert.equal((await admin.post('/api/import/preview', { filename: 'a.csv', content: 'a,"b' })).status, 400);
    assert.equal((await admin.post('/api/import/preview', { filename: 'a.xml', format: 'qbxml', content: '<a>'.repeat(200) })).status, 400);
    assert.equal((await admin.post('/api/import/preview', { filename: 'a.bin', content: 'garbage that is not any format' })).status, 400);
    assert.equal((await admin.get('/api/session')).status, 200);
  } finally { await s.close(); }
});
