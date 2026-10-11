'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { execFileSync } = require('node:child_process');
const { openDb } = require('../src/db');
const guard = require('../src/fileguard');
const { verifyAudit, audit } = require('../src/audit');
const { checkLedgerIntegrity, postEntry } = require('../src/ledger');

const tmp = () => fs.mkdtempSync(path.join(os.tmpdir(), 'obguard-'));
const win = process.platform === 'win32';

test('a read-only flag on the books is cleared at start, so sign-in and saving keep working', { skip: !win }, () => {
  const dir = tmp(); const f = path.join(dir, 'ledjoro.db');
  openDb(f).close();
  execFileSync('attrib', ['+R', f]);
  assert.equal(fs.statSync(f).mode & 0o200, 0, 'the file really is read-only now');
  assert.deepEqual(guard.clearReadOnly(dir), ['ledjoro.db']);
  assert.ok(fs.statSync(f).mode & 0o200, 'the read-only flag is gone');
  const db = openDb(f); db.exec('BEGIN IMMEDIATE'); db.exec('COMMIT'); db.close(); // and the file is writable
  assert.deepEqual(guard.clearReadOnly(dir), [], 'nothing to fix the second time');
});

test('held exclusively, no other connection can read or write the books while Ledjoro runs', () => {
  const dir = tmp(); const f = path.join(dir, 'ledjoro.db');
  const mine = openDb(f, { exclusive: true }); mine.exec('BEGIN IMMEDIATE'); mine.exec('COMMIT');
  const { DatabaseSync } = require('node:sqlite');
  const other = new DatabaseSync(f); other.exec('PRAGMA busy_timeout = 100');
  assert.throws(() => other.prepare('SELECT COUNT(*) FROM users').get(), /locked/);
  assert.throws(() => other.prepare("UPDATE settings SET value='x' WHERE key='company_name'").run(), /locked/);
  other.close(); mine.close();
  const after = new DatabaseSync(f); assert.equal(after.prepare('SELECT COUNT(*) n FROM users').get().n, 0, 'readable again once Ledjoro has closed it'); after.close();
});

test('the start-up check notices a log that was edited outside Ledjoro, and an unbalanced ledger', () => {
  const db = openDb(':memory:');
  db.prepare("INSERT INTO users(username,display_name,password_hash,role_id,created_at) VALUES ('a','a','x',1,1)").run();
  for (let i = 0; i < 3; i++) audit(db, { user: { id: 1, username: 'a' } }, 'test.action', 'x', i, 'detail ' + i);
  const accts = db.prepare('SELECT id FROM accounts ORDER BY id LIMIT 2').all();
  postEntry(db, { date: '2026-01-01', memo: 'ok', source_type: 'manual', user_id: 1, lines: [{ account_id: accts[0].id, debit_cents: 500 }, { account_id: accts[1].id, credit_cents: 500 }] });
  assert.deepEqual(guard.integrityReport(db, verifyAudit, checkLedgerIntegrity), { ok: true, problems: [] });
  // someone edits history with another tool (the append-only triggers are dropped first, as such a tool could do)
  for (const t of db.prepare("SELECT name FROM sqlite_master WHERE type='trigger'").all()) db.exec(`DROP TRIGGER "${t.name}"`);
  db.prepare("UPDATE audit_log SET detail='edited' WHERE id=2").run();
  db.prepare('UPDATE journal_lines SET debit_cents=999 WHERE debit_cents=500').run();
  const r = guard.integrityReport(db, verifyAudit, checkLedgerIntegrity);
  assert.equal(r.ok, false);
  assert.ok(r.problems.some((p) => /activity log was edited/.test(p)), r.problems.join(' | '));
  assert.ok(r.problems.some((p) => /do not balance|does not balance/.test(p)), r.problems.join(' | '));
});

test('folder permissions: limited to this user, still fully usable, safe to repeat', { skip: !win }, () => {
  const dir = tmp();
  const a = guard.restrictFolder(dir); assert.ok(['restricted', 'already'].includes(a), a);
  fs.writeFileSync(path.join(dir, 'x.txt'), 'still works'); assert.equal(fs.readFileSync(path.join(dir, 'x.txt'), 'utf8'), 'still works');
  const out = execFileSync('icacls', [dir]).toString();
  assert.ok(!/BUILTIN\\Users|Everyone|Authenticated Users/i.test(out), 'other people and groups are not listed:\n' + out);
  assert.equal(guard.restrictFolder(dir), 'already', 'a second run changes nothing');
  const db = openDb(path.join(dir, 'ledjoro.db')); db.close(); // the books can be created and opened inside it
});

test('plain-language reasons for the usual file problems', () => {
  assert.match(guard.explain(new Error('database is locked')), /another program/);
  assert.match(guard.explain(new Error('attempt to write a readonly database')), /Read-only/);
  assert.match(guard.explain(Object.assign(new Error('EACCES: permission denied'), { code: 'EACCES' })), /locked to a different Windows user/);
});
