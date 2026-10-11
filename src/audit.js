'use strict';
const crypto = require('node:crypto');

const sha = (s) => crypto.createHash('sha256').update(s).digest('hex');
const rowHash = (prev, r) => sha([prev, r.ts, r.user_id ?? '', r.username, r.action, r.entity, r.entity_id, r.detail, r.ip].join('\u001f'));

/**
 * Append a tamper-evident audit record. Each row's hash covers the previous row's hash,
 * so any edit/deletion in the middle of the log is detectable by verifyAudit().
 */
function audit(db, ctx, action, entity = '', entityId = '', detail = '') {
  const prev = db.prepare('SELECT hash FROM audit_log ORDER BY id DESC LIMIT 1').get()?.hash || 'GENESIS';
  const r = {
    ts: Date.now(),
    user_id: ctx?.user?.id ?? null,
    username: ctx?.user?.username ?? '',
    action,
    entity,
    entity_id: String(entityId ?? ''),
    detail: String(detail ?? '').slice(0, 2000),
    ip: ctx?.ip ?? '',
  };
  const hash = rowHash(prev, r);
  db.prepare('INSERT INTO audit_log(ts,user_id,username,action,entity,entity_id,detail,ip,prev_hash,hash) VALUES (?,?,?,?,?,?,?,?,?,?)')
    .run(r.ts, r.user_id, r.username, r.action, r.entity, r.entity_id, r.detail, r.ip, prev, hash);
}

function verifyAudit(db) {
  let prev = 'GENESIS';
  let n = 0;
  for (const r of db.prepare('SELECT * FROM audit_log ORDER BY id').all()) {
    if (r.prev_hash !== prev || rowHash(prev, r) !== r.hash) return { ok: false, brokenAtId: r.id, checked: n };
    prev = r.hash;
    n++;
  }
  return { ok: true, checked: n };
}

module.exports = { audit, verifyAudit };
