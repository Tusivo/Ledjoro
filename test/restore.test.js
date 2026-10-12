'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { DatabaseSync, backup: sqliteBackup } = require('node:sqlite');
const { startServer, setupAdmin, loginAs, PASSWORD } = require('./helpers');
const { openDb } = require('../src/db');
const Backup = require('../src/backup');
const { postEntry } = require('../src/ledger');

const b64 = (buf) => buf.toString('base64');
const PASS = 'a-long-backup-passphrase';
const tmpFile = (n) => path.join(fs.mkdtempSync(path.join(os.tmpdir(), 'obt-')), n);
const acct = (db, name) => db.prepare('SELECT id FROM accounts WHERE name=?').get(name).id;

test('restore: replaces everything with the backup, signs everyone out, keeps protections, and saves a safety copy that can undo it', async () => {
  const s = await startServer();
  try {
    const admin = await setupAdmin(s);
    await admin.post('/api/contacts', { kind: 'customer', name: 'Before Backup Co' });
    const bytes = await Backup.createBackup(s.db, PASS);

    // changes made AFTER the backup: these must disappear on restore
    await admin.post('/api/contacts', { kind: 'customer', name: 'After Backup Co' });
    const roles = (await admin.get('/api/roles')).json;
    await admin.post('/api/users', { username: 'latecomer', password: PASSWORD, role_id: roles[0].id });
    assert.equal(s.db.prepare('SELECT COUNT(*) n FROM users').get().n, 2);

    const r = await admin.post('/api/restore', { file_b64: b64(bytes), passphrase: PASS, password: PASSWORD, confirm: 'RESTORE' });
    assert.equal(r.status, 200, r.text);
    assert.equal(r.json.users, 1);
    assert.match(r.json.safety_copy, /^pre-restore-\d{14}\.obbak$/);
    assert.deepEqual(s.db.prepare('SELECT name FROM contacts').all().map((c) => c.name), ['Before Backup Co'], 'the post-backup contact is gone');
    assert.equal(s.db.prepare('SELECT COUNT(*) n FROM users').get().n, 1, 'the post-backup user is gone');
    assert.equal(s.db.prepare('SELECT COUNT(*) n FROM sessions').get().n, 0, 'every session was cleared');
    assert.equal((await admin.get('/api/accounts')).status, 401, 'the restoring user is signed out too');

    // the restored company works, and the protections are back
    const again = await loginAs(s, 'admin');
    assert.equal((await again.get('/api/contacts?kind=customer')).json[0].name, 'Before Backup Co');
    assert.throws(() => s.db.prepare('DELETE FROM audit_log').run(), /append-only/);
    const entry = postEntry(s.db, { date: '2026-01-01', lines: [{ account_id: acct(s.db, 'Checking'), debit_cents: 5 }, { account_id: acct(s.db, 'Sales'), credit_cents: 5 }] });
    assert.throws(() => s.db.prepare('UPDATE journal_lines SET debit_cents=9 WHERE entry_id=?').run(entry), /immutable/);
    const v = (await again.get('/api/audit/verify')).json;
    assert.equal(v.audit_chain.ok, true);
    assert.equal(v.ledger.ok, true);
    assert.ok(s.db.prepare("SELECT 1 FROM audit_log WHERE action='backup.restore'").get(), 'the restore itself is audited');

    // the safety copy holds what existed before the restore, and restoring it brings it back
    const safety = fs.readFileSync(path.join(s.cfg.dataDir, r.json.safety_copy));
    assert.equal(Backup.inspect(safety).hasRecovery, true, 'the safety copy is locked with the recovery key');
    const r2 = await again.post('/api/restore', { file_b64: b64(safety), secret: admin.recoveryKey, password: PASSWORD, confirm: 'RESTORE' });
    assert.equal(r2.status, 200, r2.text);
    assert.deepEqual(s.db.prepare('SELECT name FROM contacts ORDER BY id').all().map((c) => c.name), ['Before Backup Co', 'After Backup Co']);
    assert.equal(s.db.prepare('SELECT COUNT(*) n FROM users').get().n, 2, 'undo brought the late user back');
  } finally { await s.close(); }
});

test('restore: anything wrong leaves the current data completely untouched', async () => {
  const s = await startServer();
  try {
    const admin = await setupAdmin(s);
    await admin.post('/api/contacts', { kind: 'customer', name: 'Keep Me' });
    const good = await Backup.createBackup(s.db, PASS);
    await admin.post('/api/contacts', { kind: 'customer', name: 'Also Keep Me' });
    const snapshot = () => JSON.stringify([s.db.prepare('SELECT name FROM contacts ORDER BY id').all(), s.db.prepare('SELECT COUNT(*) n FROM audit_log').get().n]);
    const before = snapshot();
    const body = (over = {}) => ({ file_b64: b64(good), passphrase: PASS, password: PASSWORD, confirm: 'RESTORE', ...over });

    assert.equal((await admin.post('/api/restore', body({ password: 'wrong-password-12' }))).status, 403);
    assert.equal((await admin.post('/api/restore', body({ confirm: 'restore' }))).status, 400, 'must type RESTORE exactly');
    assert.equal((await admin.post('/api/restore', body({ passphrase: 'not-the-right-passphrase' }))).status, 400);
    assert.equal((await admin.post('/api/restore', body({ passphrase: 'short' }))).status, 400);
    assert.equal((await admin.post('/api/restore', body({ file_b64: '' }))).status, 400);
    assert.equal((await admin.post('/api/restore', body({ file_b64: '!!!not base64!!!' }))).status, 400);
    assert.equal((await admin.post('/api/restore', body({ file_b64: b64(Buffer.from('this is not a backup, not even close, no way')) }))).status, 400);
    const tampered = Buffer.from(good);
    tampered[tampered.length - 9] ^= 0x55;
    const t = await admin.post('/api/restore', body({ file_b64: b64(tampered) }));
    assert.equal(t.status, 400);
    assert.match(t.json.error, /tampered|damaged|Wrong passphrase/);
    assert.equal(snapshot(), before, 'nothing changed after any of those failures');
    assert.equal((await admin.get('/api/accounts')).status, 200, 'and the user is still signed in');
    assert.deepEqual(fs.readdirSync(s.cfg.dataDir).filter((f) => f.startsWith('pre-restore')), [], 'no safety copy for restores that never started');
  } finally { await s.close(); }
});

test('restore: refuses a backup with no administrator, a newer schema, or a non-Ledjoro database; and needs the right permission', async () => {
  const s = await startServer();
  try {
    const admin = await setupAdmin(s);
    const before = s.db.prepare('SELECT COUNT(*) n FROM users').get().n;
    const send = (buf) => admin.post('/api/restore', { file_b64: b64(buf), passphrase: PASS, password: PASSWORD, confirm: 'RESTORE' });

    const noAdmin = openDb(':memory:'); // roles exist, but no users at all
    assert.match((await send(await Backup.createBackup(noAdmin, PASS))).json.error, /no active administrator/);

    const newer = openDb(':memory:');
    newer.exec('PRAGMA user_version = 99');
    assert.match((await send(await Backup.createBackup(newer, PASS))).json.error, /newer version/);

    const foreign = tmpFile('foreign.db');
    const fdb = new DatabaseSync(foreign);
    fdb.exec('CREATE TABLE t(x); PRAGMA user_version = 1');
    fdb.close();
    assert.match((await send(Backup.encrypt(fs.readFileSync(foreign), PASS))).json.error, /not a complete Ledjoro backup|not a Ledjoro database/);
    assert.equal(s.db.prepare('SELECT COUNT(*) n FROM users').get().n, before);

    const roles = (await admin.get('/api/roles')).json;
    await admin.post('/api/users', { username: 'viewer', password: PASSWORD, role_id: roles.find((x) => x.name === 'View only').id });
    s.db.prepare("UPDATE users SET must_change_password=0 WHERE username='viewer'").run();
    const v = await loginAs(s, 'viewer');
    assert.equal((await v.post('/api/restore', { file_b64: b64(Buffer.from('x')), passphrase: PASS, password: PASSWORD, confirm: 'RESTORE' })).status, 403);
  } finally { await s.close(); }
});

test('restore: a backup from an older schema (no asset register) restores cleanly into the current version', async () => {
  const s = await startServer();
  try {
    const admin = await setupAdmin(s);
    await admin.post('/api/contacts', { kind: 'customer', name: 'Old Schema Co' });
    await admin.post('/api/assets', { name: 'Live asset', cost_cents: 10000, in_service: '2026-01-01', life_months: 12 });
    // forge a version-1 backup: the same data, but without the asset table
    const old = tmpFile('old.db');
    await sqliteBackup(s.db, old);
    const odb = new DatabaseSync(old);
    odb.exec('DROP TABLE fixed_assets; DROP TABLE passkeys; DROP TABLE reconciled_lines; DROP TABLE reconciliations; DROP TABLE watched_files; PRAGMA user_version = 1');
    odb.close();
    const bytes = Backup.encrypt(fs.readFileSync(old), PASS);
    await admin.post('/api/contacts', { kind: 'customer', name: 'Newer Co' });

    const r = await admin.post('/api/restore', { file_b64: b64(bytes), passphrase: PASS, password: PASSWORD, confirm: 'RESTORE' });
    assert.equal(r.status, 200, r.text);
    assert.equal(r.json.schema_version, 1);
    assert.deepEqual(s.db.prepare('SELECT name FROM contacts').all().map((c) => c.name), ['Old Schema Co']);
    assert.equal(s.db.prepare('PRAGMA user_version').get().user_version, 9, 'the live database keeps the current schema');
    assert.equal(s.db.prepare('SELECT COUNT(*) n FROM fixed_assets').get().n, 0, 'the asset table exists and is empty');
    const again = await loginAs(s, 'admin');
    assert.equal((await again.post('/api/assets', { name: 'New asset', cost_cents: 10000, in_service: '2026-01-01', life_months: 12 })).status, 200);
  } finally { await s.close(); }
});
