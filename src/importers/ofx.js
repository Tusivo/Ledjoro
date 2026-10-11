'use strict';
/**
 * OFX / QFX / QBO bank-statement importer.
 * QuickBooks "Web Connect" (.QBO) files are standard OFX. Both OFX 1.x (SGML, unclosed tags) and 2.x (XML) are handled
 * with a flat tag scanner: no DTD, no entity expansion, no external references, so XXE-style attacks are structurally impossible.
 */
const crypto = require('node:crypto');
const { newModel, checkLimit } = require('./model');
const { parseMoney } = require('../util');

const ENT = { '&amp;': '&', '&lt;': '<', '&gt;': '>', '&quot;': '"', '&apos;': "'" };
const decode = (s) => s.replace(/&(amp|lt|gt|quot|apos);/g, (x) => ENT[x]).trim();

function ofxDate(s) {
  const m = String(s || '').match(/^(\d{4})(\d{2})(\d{2})/);
  if (!m) return null;
  const iso = `${m[1]}-${m[2]}-${m[3]}`;
  const d = new Date(iso + 'T00:00:00Z');
  return !Number.isNaN(d.getTime()) && d.toISOString().slice(0, 10) === iso ? iso : null;
}

function ofxAmount(s) {
  let t = String(s || '').trim();
  if (/^-?\d{1,3}(\.\d{3})+,\d+$/.test(t)) t = t.replace(/\./g, '').replace(',', '.'); // 1.234,56
  else if (/^-?\d+,\d{1,2}$/.test(t)) t = t.replace(',', '.');
  return parseMoney(t);
}

function tags(block) {
  const out = {};
  for (const m of block.matchAll(/<([A-Za-z0-9.]+)>([^<\r\n]*)/g)) {
    const k = m[1].toUpperCase();
    if (!(k in out) && m[2].trim() !== '') out[k] = decode(m[2]);
  }
  return out;
}

function parseOfx(text, limits) {
  const m = newModel('ofx');
  const body = text.replace(/^﻿/, '');
  const meta = tags(body.split(/<STMTTRN>/i)[0]);
  m.bank_meta = {
    org: meta.ORG || '', account_id_tail: (meta.ACCTID || '').slice(-4), currency: meta.CURDEF || '',
    ledger_balance_cents: meta.BALAMT !== undefined ? ofxAmount(meta.BALAMT) : null, balance_date: ofxDate(meta.DTASOF),
    kind: /<CCSTMTRS>/i.test(body) ? 'credit_card' : 'bank',
  };
  let idx = 0;
  const seenIds = new Map();
  for (const mt of body.matchAll(/<STMTTRN>([\s\S]*?)(?=<\/STMTTRN>|<STMTTRN>|<\/BANKTRANLIST>|$)/gi)) {
    checkLimit(++idx, limits, 'transactions');
    const t = tags(mt[1]);
    const date = ofxDate(t.DTPOSTED), amount = ofxAmount(t.TRNAMT);
    if (!date || amount === null) { m.errors.push(`Transaction ${idx}: its date or amount could not be read, so it was left out.`); continue; }
    const description = [t.NAME, t.MEMO].filter(Boolean).join(' - ') || t.TRNTYPE || '';
    let fitid = t.FITID || crypto.createHash('sha256').update(`${date}|${amount}|${description}`).digest('hex').slice(0, 24);
    const n = seenIds.get(fitid) ?? 0; seenIds.set(fitid, n + 1);
    if (n) { fitid += `~${n}`; m.notes.push(`Transaction ${idx} has the same bank reference number as an earlier one in this file; both were kept.`); }
    m.bank_transactions.push({ date, amount_cents: amount, description, fitid, check_number: t.CHECKNUM || '' });
  }
  if (!m.bank_transactions.length && !m.errors.length) m.warnings.push('No transactions were found in this file. It may be an empty statement, or the wrong kind of file.');
  return m;
}

module.exports = { parseOfx };
