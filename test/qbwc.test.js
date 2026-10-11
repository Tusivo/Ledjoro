'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const http = require('node:http');
const { startServer, setupAdmin } = require('./helpers');
const { parseXml, find } = require('../src/importers/qbxml');
const Qbwc = require('../src/qbwc');

// ---- a pretend QuickBooks: answers the questions Ledjoro sends the way QuickBooks Desktop does ----
const rs = (tag, id, inner, { code = 0, remaining = 0, iter = '' } = {}) =>
  `<?xml version="1.0" ?><QBXML><QBXMLMsgsRs><${tag}Rs requestID="${id}" statusCode="${code}" statusSeverity="${code ? 'Warn' : 'Info'}" statusMessage="${code ? 'A query request did not find a matching object in QuickBooks' : 'Status OK'}"${iter ? ` iteratorRemainingCount="${remaining}" iteratorID="${iter}"` : ''}>${inner}</${tag}Rs></QBXMLMsgsRs></QBXML>`;
const acct = (n, t, num = '') => `<AccountRet><FullName>${n}</FullName><AccountType>${t}</AccountType><AccountNumber>${num}</AccountNumber><IsActive>true</IsActive></AccountRet>`;
const FIX = {
  AccountQueryRq: () => ['<Accounts/>'].length && [acct('Checking', 'Bank', '1000'), acct('Accounts Receivable', 'AccountsReceivable'), acct('Accounts Payable', 'AccountsPayable'), acct('Sales', 'Income'), acct('Rent', 'Expense'), acct("Owner's Equity", 'Equity'), acct('Undeposited Funds', 'OtherCurrentAsset'), '<AccountRet><FullName>Estimates</FullName><AccountType>NonPosting</AccountType></AccountRet>'].join(''),
  VendorQueryRq: () => '<VendorRet><Name>Landlord</Name><IsActive>true</IsActive><Phone>603-555-0100</Phone></VendorRet>',
  ItemQueryRq: () => '<ItemServiceRet><Name>Consulting</Name><FullName>Consulting</FullName><SalesOrPurchase><Desc>Hours</Desc><Price>100.00</Price><AccountRef><FullName>Sales</FullName></AccountRef></SalesOrPurchase></ItemServiceRet>',
  InvoiceQueryRq: () => '<InvoiceRet><TxnID>I1</TxnID><TxnDate>2026-02-01</TxnDate><RefNumber>1001</RefNumber><CustomerRef><FullName>Acme Co</FullName></CustomerRef><ARAccountRef><FullName>Accounts Receivable</FullName></ARAccountRef><Subtotal>1000.00</Subtotal><SalesTaxTotal>0.00</SalesTaxTotal><InvoiceLineRet><ItemRef><FullName>Consulting</FullName></ItemRef><Amount>1000.00</Amount></InvoiceLineRet></InvoiceRet>',
  BillQueryRq: () => '<BillRet><TxnID>B1</TxnID><TxnDate>2026-02-02</TxnDate><VendorRef><FullName>Landlord</FullName></VendorRef><APAccountRef><FullName>Accounts Payable</FullName></APAccountRef><ExpenseLineRet><AccountRef><FullName>Rent</FullName></AccountRef><Amount>500.00</Amount></ExpenseLineRet></BillRet>',
  ReceivePaymentQueryRq: () => '<ReceivePaymentRet><TxnID>P1</TxnID><TxnDate>2026-02-10</TxnDate><CustomerRef><FullName>Acme Co</FullName></CustomerRef><ARAccountRef><FullName>Accounts Receivable</FullName></ARAccountRef><TotalAmount>1000.00</TotalAmount><DepositToAccountRef><FullName>Checking</FullName></DepositToAccountRef></ReceivePaymentRet>',
  CheckQueryRq: () => '<CheckRet><TxnID>K1</TxnID><TxnDate>2026-02-11</TxnDate><AccountRef><FullName>Checking</FullName></AccountRef><PayeeEntityRef><FullName>Landlord</FullName></PayeeEntityRef><Amount>50.00</Amount><ExpenseLineRet><AccountRef><FullName>Rent</FullName></AccountRef><Amount>50.00</Amount></ExpenseLineRet></CheckRet>',
  JournalEntryQueryRq: () => "<JournalEntryRet><TxnID>J1</TxnID><TxnDate>2026-01-05</TxnDate><RefNumber>1</RefNumber><JournalDebitLine><AccountRef><FullName>Checking</FullName></AccountRef><Amount>200.00</Amount></JournalDebitLine><JournalCreditLine><AccountRef><FullName>Owner's Equity</FullName></AccountRef><Amount>200.00</Amount></JournalCreditLine></JournalEntryRet>",
};
// customers come in two pages, to exercise paging
function answer(requestXml) {
  const root = parseXml(requestXml); const rq = find(root, 'QBXMLMsgsRq')[0].children[0];
  const tag = rq.name, id = rq.attrs.requestID, cont = rq.attrs.iterator === 'Continue';
  if (tag !== 'AccountQueryRq') assert.match(requestXml, /<MaxReturned>\d+<\/MaxReturned>/);   // accounts are asked for in one go: QuickBooks refuses the iterator on them
  else assert.ok(!/iterator/.test(requestXml), 'no iterator on the account query');
  if (tag === 'CustomerQueryRq') {
    return cont ? rs('CustomerQuery', id, '<CustomerRet><Name>Beta LLC</Name><FullName>Beta LLC</FullName><IsActive>true</IsActive></CustomerRet>', { remaining: 0, iter: '{IT}' })
      : rs('CustomerQuery', id, '<CustomerRet><Name>Acme Co</Name><FullName>Acme Co</FullName><IsActive>true</IsActive><Email>ap@acme.test</Email></CustomerRet>', { remaining: 1, iter: '{IT}' });
  }
  const f = FIX[tag];
  const name = tag.replace(/Rq$/, '');
  return f ? rs(name, id, f()) : rs(name, id, '', { code: 1 });
}

// ---- a pretend Web Connector ----
const esc = (s) => String(s).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
function soap(port, method, params) {
  const body = `<?xml version="1.0" encoding="utf-8"?><soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/"><soap:Body><${method} xmlns="http://developer.intuit.com/">${Object.entries(params).map(([k, v]) => `<${k}>${esc(v)}</${k}>`).join('')}</${method}></soap:Body></soap:Envelope>`;
  return new Promise((resolve, reject) => {
    const req = http.request({ host: '127.0.0.1', port, path: '/qbwc', method: 'POST', headers: { Host: `localhost:${port}`, 'Content-Type': 'text/xml; charset=utf-8', SOAPAction: '"http://developer.intuit.com/' + method + '"', 'Content-Length': Buffer.byteLength(body) } }, (res) => {
      const ch = []; res.on('data', (d) => ch.push(d)); res.on('end', () => resolve({ status: res.statusCode, text: Buffer.concat(ch).toString('utf8') }));
    });
    req.on('error', reject); req.end(body);
  });
}
const resultOf = (text, m) => { const root = parseXml(text); const r = find(root, m + 'Result')[0]; return r.children.length ? r.children.map((c) => c.text) : r.text; };

async function runSync(port, user, pw, { answerFn = answer, hresult = '' } = {}) {
  const auth = resultOf((await soap(port, 'authenticate', { strUserName: user, strPassword: pw })).text, 'authenticate');
  if (auth[1] === 'nvu') return { auth };
  const ticket = auth[0]; let progress = 0, n = 0;
  while (progress < 100) {
    const req = resultOf((await soap(port, 'sendRequestXML', { ticket, strHCPResponse: '', strCompanyFileName: '', qbXMLCountry: 'US', qbXMLMajorVers: '16', qbXMLMinorVers: '0' })).text, 'sendRequestXML');
    assert.ok(req, 'a request is always sent until 100 percent'); assert.ok(++n < 80, 'finishes');
    progress = Number(resultOf((await soap(port, 'receiveResponseXML', { ticket, response: hresult ? '' : answerFn(req), hresult, message: hresult ? 'Could not open the company file' : '' })).text, 'receiveResponseXML'));
    if (progress < 0) return { auth, error: resultOf((await soap(port, 'getLastError', { ticket })).text, 'getLastError') };
  }
  const closed = resultOf((await soap(port, 'closeConnection', { ticket })).text, 'closeConnection');
  return { auth, closed, requests: n };
}

test('Web Connector: switching on gives a connection file without a password, and a password that is not stored', async () => {
  const s = await startServer(); const c = await setupAdmin(s);
  try {
    assert.equal((await c.get('/api/qbwc/status')).json.enabled, false);
    const r = await c.post('/api/qbwc/enable'); assert.equal(r.status, 200, r.text);
    assert.ok(r.json.password.length >= 20 && r.json.port > 0 && r.json.running);
    assert.ok(!s.db.prepare("SELECT value FROM settings WHERE key='qbwc'").get().value.includes(r.json.password), 'only a hash of the password is kept');
    const f = await c.post('/api/qbwc/qwc'); assert.equal(f.status, 200);
    assert.match(f.text, /<AppURL>http:\/\/localhost:\d+\/qbwc<\/AppURL>/); assert.match(f.text, /<IsReadOnly>false<\/IsReadOnly>/, 'standard access: the Web Connector has to record an id in the company file, which QuickBooks refuses for read-only programs');
    assert.match((await c.post('/api/qbwc/qwc', { read_only: true })).text, /<IsReadOnly>true<\/IsReadOnly>/, 'read-only can still be asked for'); assert.ok(!f.text.includes(r.json.password));
    assert.match(f.text, new RegExp('<UserName>' + r.json.username + '</UserName>'));
    assert.equal((await soap(r.json.port, 'serverVersion', {})).status, 200);
    const wrong = await runSync(r.json.port, r.json.username, 'not-the-password'); assert.equal(wrong.auth[1], 'nvu');
    const wrongUser = await runSync(r.json.port, 'someone-else', r.json.password); assert.equal(wrongUser.auth[1], 'nvu');
  } finally { await Qbwc.stop(s.db); await s.close(); }
});

test('Web Connector: a full read-only sync is previewed, then imported once, and a second sync adds nothing twice', async () => {
  const s = await startServer(); const c = await setupAdmin(s);
  try {
    const r = (await c.post('/api/qbwc/enable')).json;
    const asked = [];
    const out = await runSync(r.port, r.username, r.password, { answerFn: (x) => { asked.push(x); return answer(x); } });
    assert.match(out.closed, /ready to review/);
    assert.ok(asked.every((x) => /Query/.test(x) && !/<(\w+Add|\w+Mod|\w+Del)Rq/.test(x)), 'only questions, never changes');
    const st = (await c.get('/api/qbwc/status')).json; assert.equal(st.phase, 'ready'); assert.equal(st.progress, 100); assert.ok(st.pending.counts.customers === 2);
    // nothing is in the books until the user says so
    assert.equal(s.db.prepare('SELECT COUNT(*) n FROM journal_entries WHERE source_type=?').get('import').n, 0);
    const pv = (await c.get('/api/qbwc/preview')).json;
    assert.equal(pv.canCommit, true, JSON.stringify(pv.errors)); assert.equal(pv.summary.journal_entries.new, 5);
    const done = await c.post('/api/qbwc/commit'); assert.equal(done.status, 200, done.text);
    assert.equal(done.json.journal_entries, 5); assert.equal(done.json.contacts >= 3, true);
    const dr = (n) => s.db.prepare('SELECT COALESCE(SUM(l.debit_cents-l.credit_cents),0) b FROM journal_lines l JOIN accounts a ON a.id=l.account_id WHERE a.name=?').get(n).b;
    assert.equal(dr('Checking'), 20000 + 100000 - 5000, 'journal entry + customer payment - check');
    assert.equal(dr('Accounts Receivable'), 0, 'invoice and its payment net out');
    assert.equal(dr('Accounts Payable'), -50000); assert.equal(dr('Sales'), -100000); assert.equal(dr('Rent'), 55000);
    assert.equal(s.db.prepare("SELECT COUNT(*) n FROM contacts WHERE name='Acme Co' AND kind='customer'").get().n, 1);
    assert.ok((await c.get('/api/health')).json, 'books still check out');
    const unbalanced = s.db.prepare('SELECT entry_id FROM journal_lines GROUP BY entry_id HAVING SUM(debit_cents)<>SUM(credit_cents)').all(); assert.equal(unbalanced.length, 0);
    assert.equal((await c.get('/api/qbwc/status')).json.pending, null);
    // second sync: the same QuickBooks data again
    const seen = []; await runSync(r.port, r.username, r.password, { answerFn: (x) => { seen.push(x); return answer(x); } });
    assert.ok(seen.some((x) => /ModifiedDateRangeFilter/.test(x)), 'later syncs only ask for what changed');
    const pv2 = (await c.get('/api/qbwc/preview')).json; assert.equal(pv2.summary.journal_entries.new, 0); assert.equal(pv2.summary.journal_entries.duplicate, 5);
    assert.equal((await c.post('/api/qbwc/discard')).status, 200);
    assert.equal((await c.get('/api/qbwc/preview')).status, 400);
  } finally { await Qbwc.stop(s.db); await s.close(); }
});

test('Web Connector: problems are explained, wrong answers do not break the sync, and switching off closes the door', async () => {
  const s = await startServer(); const c = await setupAdmin(s);
  try {
    const r = (await c.post('/api/qbwc/enable')).json;
    const bad = await runSync(r.port, r.username, r.password, { hresult: '0x80040408' });
    assert.match(bad.error, /QuickBooks reported a problem.*company file/);
    assert.equal((await c.get('/api/qbwc/status')).json.phase, 'error');
    // QuickBooks refusing one question (error code) is reported, and the others still arrive
    await runSync(r.port, r.username, r.password, { answerFn: (x) => (/InvoiceQueryRq/.test(x) ? rs('InvoiceQuery', 4, '', { code: 3250 }) : answer(x)) });
    const pv = (await c.get('/api/qbwc/preview')).json; assert.ok(pv.warnings.some((w) => /invoices.*3250/.test(w)), pv.warnings.join('|'));
    // a hostile or garbled request is refused politely
    const garbage = await new Promise((res) => { const q = http.request({ host: '127.0.0.1', port: r.port, path: '/qbwc', method: 'POST', headers: { Host: `localhost:${r.port}` } }, (x) => { x.resume(); res(x.statusCode); }); q.end('<not xml'); });
    assert.equal(garbage, 400);
    const badHost = await new Promise((res) => { const q = http.request({ host: '127.0.0.1', port: r.port, path: '/', method: 'GET', headers: { Host: 'evil.example' } }, (x) => { x.resume(); res(x.statusCode); }); q.end(); });
    assert.equal(badHost, 421);
    const pw2 = (await c.post('/api/qbwc/password')).json.password; assert.notEqual(pw2, r.password);
    assert.equal((await runSync(r.port, r.username, r.password)).auth[1], 'nvu', 'the old password stops working');
    assert.notEqual((await runSync(r.port, r.username, pw2)).auth[1], 'nvu');
    await c.post('/api/qbwc/disable');
    await assert.rejects(soap(r.port, 'serverVersion', {}));
    assert.equal((await c.get('/api/qbwc/status')).json.running, false);
  } finally { await Qbwc.stop(s.db); await s.close(); }
});
