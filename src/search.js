'use strict';
/** Search across the books for the Ctrl+K box: customers, vendors, invoices, bills, accounts and journal entries. */
const { formatMoney } = require('./util');

const like = (s) => '%' + String(s).replace(/[\\%_]/g, (c) => '\\' + c) + '%';
const money = (c) => '$' + formatMoney(c);

function search(db, q) {
  q = String(q ?? '').trim().slice(0, 100);
  if (q.length < 2) return [];
  const p = like(q), out = [];
  for (const c of db.prepare(`SELECT id, kind, name, email, phone FROM contacts WHERE active=1 AND (name LIKE ? ESCAPE '\\' OR email LIKE ? ESCAPE '\\' OR phone LIKE ? ESCAPE '\\') ORDER BY name LIMIT 8`).all(p, p, p)) {
    out.push({ type: c.kind === 'customer' ? 'Customer' : 'Vendor', title: c.name, detail: [c.email, c.phone].filter(Boolean).join('  '), hash: c.kind === 'customer' ? '#/customers' : '#/vendors' });
  }
  const cents = /^\$?\d[\d,]*(\.\d{1,2})?$/.test(q) ? Math.round(parseFloat(q.replace(/[$,]/g, '')) * 100) : null;
  for (const d of db.prepare(`SELECT d.id, d.kind, d.number, d.doc_date, d.total_cents, d.status, c.name AS contact FROM documents d JOIN contacts c ON c.id=d.contact_id
      WHERE d.number LIKE ? ESCAPE '\\' OR d.memo LIKE ? ESCAPE '\\' OR c.name LIKE ? ESCAPE '\\' OR (? IS NOT NULL AND d.total_cents=?) ORDER BY d.doc_date DESC, d.id DESC LIMIT 10`).all(p, p, p, cents, cents)) {
    out.push({ type: d.kind === 'invoice' ? 'Invoice' : 'Bill', title: `${d.kind === 'invoice' ? 'Invoice' : 'Bill'} ${d.number} - ${d.contact}`, detail: `${d.doc_date}   ${money(d.total_cents)}   ${d.status}`, hash: '#/document/' + d.id });
  }
  for (const a of db.prepare(`SELECT id, number, name, subtype FROM accounts WHERE number LIKE ? ESCAPE '\\' OR name LIKE ? ESCAPE '\\' ORDER BY COALESCE(NULLIF(number,''),'zzzz'), name LIMIT 6`).all(p, p)) {
    out.push({ type: 'Account', title: (a.number ? a.number + ' ' : '') + a.name, detail: a.subtype.replace(/_/g, ' '), hash: '#/accounts' });
  }
  for (const e of db.prepare(`SELECT id, entry_date, memo, ref FROM journal_entries WHERE memo LIKE ? ESCAPE '\\' OR ref LIKE ? ESCAPE '\\' ORDER BY entry_date DESC, id DESC LIMIT 6`).all(p, p)) {
    out.push({ type: 'Journal', title: e.memo || e.ref || 'Entry ' + e.id, detail: e.entry_date, hash: '#/journal' });
  }
  return out;
}

module.exports = { search };
