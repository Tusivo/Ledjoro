'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { startServer, setupAdmin } = require('./helpers');

const qbo = (org, acct, rows) => `OFXHEADER:100\r\nDATA:OFXSGML\r\nVERSION:102\r\n\r\n<OFX><SIGNONMSGSRSV1><SONRS><FI><ORG>${org}</ORG></FI></SONRS></SIGNONMSGSRSV1><BANKMSGSRSV1><STMTTRNRS><STMTRS><CURDEF>USD</CURDEF><BANKACCTFROM><ACCTID>${acct}</ACCTID></BANKACCTFROM><BANKTRANLIST>`
  + rows.map(([d, a, n, id]) => `<STMTTRN><TRNTYPE>DEBIT<DTPOSTED>${d}<TRNAMT>${a}<FITID>${id}<NAME>${n}</STMTTRN>`).join('') + '</BANKTRANLIST><LEDGERBAL><BALAMT>100.00<DTASOF>20260131</LEDGERBAL></STMTRS></STMTTRNRS></BANKMSGSRSV1></OFX>';

async function setup() {
  const s = await startServer(); const c = await setupAdmin(s);
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'ob-watch-'));
  const bank = (await c.get('/api/accounts')).json.find((a) => a.subtype === 'bank');
  return { s, c, dir, bank };
}

test('watched folder: a new statement is found, previewed, imported once, and remembered by its content', async () => {
  const { s, c, dir, bank } = await setup();
  try {
    fs.writeFileSync(path.join(dir, 'old.qbo'), qbo('Old Bank', '1111', [['20250101', '-1.00', 'OLD', 'o1']]));
    const old = new Date(Date.now() - 3 * 86400000); fs.utimesSync(path.join(dir, 'old.qbo'), old, old);
    assert.equal((await c.post('/api/watch', { enabled: true, folder: 'relative/path' })).status, 400);
    assert.equal((await c.post('/api/watch', { enabled: true, folder: path.join(dir, 'nope') })).status, 400);
    let r = await c.post('/api/watch', { enabled: true, folder: dir }); assert.equal(r.status, 200, r.text);
    assert.equal((await c.get('/api/watch')).json.files.length, 0, 'files from before it was switched on are not listed');
    fs.writeFileSync(path.join(dir, 'notes.txt'), 'hello'); fs.writeFileSync(path.join(dir, 'export.csv'), 'a,b\n1,2');
    const body = qbo('First Bank', '48291234', [['20260105', '-12.50', 'COFFEE', 'a1'], ['20260106', '1500.00', 'PAYMENT', 'a2'], ['20260107', '-45.00', 'FUEL', 'a3']]);
    fs.writeFileSync(path.join(dir, 'statement january.qbo'), body);
    r = await c.post('/api/watch/scan'); assert.equal(r.json.files.length, 1, 'only the statement is picked up (not .txt or .csv)');
    const f = r.json.files[0];
    assert.equal(f.status, 'pending'); assert.equal(f.count, 3); assert.equal(f.org, 'First Bank'); assert.equal(f.tail, '1234'); assert.equal(f.from, '2026-01-05');
    assert.equal(s.db.prepare("SELECT COUNT(*) n FROM bank_transactions").get().n, 0, 'nothing is imported until asked');
    assert.equal((await c.post(`/api/watch/files/${f.hash}/import`, {})).status, 400, 'an account must be chosen');
    r = await c.post(`/api/watch/files/${f.hash}/import`, { bank_account_id: bank.id }); assert.equal(r.status, 200, r.text);
    assert.equal(r.json.bank_added, 3);
    assert.equal(s.db.prepare("SELECT COUNT(*) n FROM bank_transactions WHERE status='new'").get().n, 3);
    assert.equal((await c.get('/api/watch')).json.files.length, 0);
    // the same statement saved again under another name is recognised, not offered twice
    fs.writeFileSync(path.join(dir, 'statement january (1).qbo'), body);
    assert.equal((await c.post('/api/watch/scan')).json.files.length, 0);
    // the next statement from the same bank and account suggests the account chosen last time
    fs.writeFileSync(path.join(dir, 'feb.qbo'), qbo('First Bank', '48291234', [['20260205', '-9.99', 'APP', 'b1']]));
    const nxt = (await c.post('/api/watch/scan')).json.files; assert.equal(nxt.length, 1); assert.equal(nxt[0].suggested_account_id, bank.id);
    // undoing the import brings its file back
    const batch = (await c.get('/api/import/batches')).json[0];
    assert.equal((await c.post(`/api/import/batches/${batch.id}/undo`)).status, 200);
    const back = (await c.post('/api/watch/scan')).json.files; assert.equal(back.length, 2, 'january is offered again after its import was undone');
  } finally { await s.close(); }
});

test('watched folder: ignoring, unreadable files, safety checks and switching off', async () => {
  const { s, c, dir, bank } = await setup();
  try {
    await c.post('/api/watch', { enabled: true, folder: dir });
    fs.writeFileSync(path.join(dir, 'a.qbo'), qbo('Bank A', '7777', [['20260110', '-5.00', 'X', 'x1']]));
    fs.writeFileSync(path.join(dir, 'broken.qbo'), 'this is not a bank file at all');
    const files = (await c.post('/api/watch/scan')).json.files;
    const good = files.find((x) => x.name === 'a.qbo'), badf = files.find((x) => x.name === 'broken.qbo');
    assert.equal(good.status, 'pending'); assert.equal(badf.status, 'unreadable'); assert.match(badf.error, /no bank transactions/);
    assert.equal((await c.post(`/api/watch/files/${badf.hash}/import`, { bank_account_id: bank.id })).status, 400, 'an unreadable file cannot be imported');
    assert.equal((await c.post('/api/watch/files/' + '0'.repeat(64) + '/import', { bank_account_id: bank.id })).status, 404);
    assert.equal((await c.post('/api/watch/files/..%2F..%2Fx/import', {})).status, 404);
    // a file that changes after it was found is refused, not imported blind
    fs.appendFileSync(path.join(dir, 'a.qbo'), '\r\n');
    const changed = await c.post(`/api/watch/files/${good.hash}/import`, { bank_account_id: bank.id }); assert.equal(changed.status, 400); assert.match(changed.json.error, /changed/);
    // a file that was removed
    fs.rmSync(path.join(dir, 'a.qbo'));
    assert.match((await c.post(`/api/watch/files/${good.hash}/import`, { bank_account_id: bank.id })).json.error, /no longer in the watched folder/);
    // ignoring
    assert.equal((await c.post(`/api/watch/files/${good.hash}/ignore`)).status, 200);
    assert.ok(!(await c.get('/api/watch')).json.files.some((x) => x.hash === good.hash));
    // looking at older files too brings in what was there before, newest first
    fs.writeFileSync(path.join(dir, 'older.ofx'), qbo('Bank B', '5555', [['20250101', '-1.00', 'Y', 'y1']]));
    const past = new Date(Date.now() - 30 * 86400000); fs.utimesSync(path.join(dir, 'older.ofx'), past, past);
    assert.ok(!(await c.post('/api/watch/scan')).json.files.some((x) => x.name === 'older.ofx'));
    assert.ok((await c.post('/api/watch/rescan')).json.files.some((x) => x.name === 'older.ofx'));
    await c.post('/api/watch', { enabled: false });
    assert.deepEqual((await c.get('/api/watch')).json.files, []); assert.equal((await c.get('/api/watch')).json.enabled, false);
  } finally { await s.close(); }
});
