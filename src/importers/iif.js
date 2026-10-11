'use strict';
/**
 * IIF (Intuit Interchange Format) importer.
 * IIF is a documented, tab-delimited text format: "!KEYWORD" header rows name the columns, data rows start with the keyword.
 * TRNS/SPL/ENDTRNS groups are balanced double-entry transactions (amounts sum to zero; positive = debit), which map
 * 1:1 onto Ledjoro journal entries, so balances are preserved exactly.
 */
const crypto = require('node:crypto');
const { skippedNote, newModel, mapAccountType, termsToDays, parseDate, money, phone, checkLimit } = require('./model');

function splitTab(line) {
  if (!line.includes('"')) return line.split('\t');
  const out = []; let f = '', q = false;
  for (let i = 0; i < line.length; i++) {
    const ch = line[i];
    if (q) { if (ch === '"') { if (line[i + 1] === '"') { f += '"'; i++; } else q = false; } else f += ch; }
    else if (ch === '"' && f === '') q = true;
    else if (ch === '\t') { out.push(f); f = ''; }
    else f += ch;
  }
  out.push(f);
  return out;
}

function parseIif(text, limits, opts = {}) {
  const m = newModel('iif');
  const order = opts.dateOrder === 'dmy' ? 'dmy' : 'mdy';
  const headers = Object.create(null);
  const skipped = [];
  const unsupported = Object.create(null);
  const seen = new Map();
  let cur = null, n = 0;

  const finish = () => {
    if (!cur) return;
    const t = cur; cur = null;
    const label = `${t.type || 'TRANSACTION'} ${t.docnum || ''} (${t.date || 'no date'})`.trim();
    if (!t.date) return void m.errors.push(`${label}: the date is missing or not a real date, so this transaction was left out.`);
    if (t.lines.length < 2) return void m.errors.push(`${label}: it has fewer than two lines, so it cannot balance and was left out.`);
    if (t.lines.some((l) => l.amount === null)) return void m.errors.push(`${label}: one of its amounts is not a number, so it was left out.`);
    const sum = t.lines.reduce((s, l) => s + l.amount, 0);
    if (sum !== 0) return void m.errors.push(`${label}: its debits and credits differ by ${(Math.abs(sum) / 100).toFixed(2)}, and an IIF transaction must balance, so it was left out.`);
    const lines = t.lines.filter((l) => l.amount !== 0).map((l) => ({ account: l.account, debit_cents: l.amount > 0 ? l.amount : 0, credit_cents: l.amount < 0 ? -l.amount : 0, contact: l.name || '', memo: l.memo }));
    if (lines.length < 2) return;
    const key = JSON.stringify([t.date, t.docnum, lines]);
    const idx = (seen.get(key) ?? 0); seen.set(key, idx + 1);
    m.journal_entries.push({ date: t.date, ref: t.docnum, memo: t.memo || `${t.type} ${t.docnum}`.trim(), lines, external_id: crypto.createHash('sha256').update(`iif|${key}|${idx}`).digest('hex') });
  };

  for (const raw of text.replace(/^﻿/, '').split(/\r\n|\n|\r/)) {
    if (!raw.trim()) continue;
    checkLimit(++n, limits);
    const cells = splitTab(raw);
    const first = cells[0].trim();
    if (first.startsWith('!')) { headers[first.slice(1).toUpperCase()] = cells.slice(1).map((c) => c.trim().toUpperCase()); continue; }
    const kw = first.toUpperCase();
    const h = headers[kw];
    const rec = h ? Object.fromEntries(h.map((k, i) => [k, (cells[i + 1] ?? '').trim()])) : {};

    switch (kw) {
      case 'ACCNT': {
        const subtype = mapAccountType(rec.ACCNTTYPE);
        if (!rec.NAME) break;
        if (!subtype) { skipped.push({ name: rec.NAME, type: rec.ACCNTTYPE }); break; }
        m.accounts.push({ number: rec.ACCNUM || '', name: rec.NAME, subtype, description: rec.DESC || '' });
        break;
      }
      case 'CUST': case 'VEND': {
        if (!rec.NAME) break;
        const addr = ['BADDR1', 'BADDR2', 'BADDR3', 'BADDR4', 'BADDR5'].map((k) => rec[k]).filter(Boolean).join(', ');
        // NOTE: tax IDs (TAXID/SSN) are deliberately NOT imported (data minimisation).
        m.contacts.push({ kind: kw === 'CUST' ? 'customer' : 'vendor', name: rec.NAME, email: rec.EMAIL || '', phone: phone(rec.PHONE1), address: addr, terms_days: termsToDays(rec.TERMS), is_1099: kw === 'VEND' && /^y/i.test(rec.VEND1099 || '') });
        break;
      }
      case 'INVITEM': {
        if (!rec.NAME) break;
        const ty = (rec.INVITEMTYPE || '').toUpperCase();
        m.items.push({ name: rec.NAME, kind: ty === 'SERV' ? 'service' : ty === 'PART' || ty === 'INVENTORY' ? 'product' : 'nonstock', description: rec.DESC || '', unit_price_cents: Math.max(0, money(rec.PRICE) ?? 0), income_account: rec.ACCNT || '', expense_account: rec.COGSACCNT || rec.PURCHASEACCNT || '' });
        break;
      }
      case 'TRNS':
        finish();
        cur = { type: rec.TRNSTYPE || '', date: parseDate(rec.DATE, order), docnum: rec.DOCNUM || '', memo: rec.MEMO || '', lines: [{ account: rec.ACCNT, name: rec.NAME, amount: money(rec.AMOUNT), memo: rec.MEMO || '' }] };
        break;
      case 'SPL':
        if (cur) cur.lines.push({ account: rec.ACCNT, name: rec.NAME, amount: money(rec.AMOUNT), memo: rec.MEMO || '' });
        break;
      case 'ENDTRNS': finish(); break;
      default: unsupported[kw] = (unsupported[kw] || 0) + 1;
    }
  }
  finish();
  const sn = skippedNote(skipped); if (sn) m.notes.push(sn);
  for (const [k, c] of Object.entries(unsupported)) m.notes.push(`${c} row(s) of the kind "${k}" in this file were not converted: Ledjoro does not use that kind of record.`);
  return m;
}

module.exports = { parseIif };
