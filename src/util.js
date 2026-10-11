'use strict';

class HttpError extends Error {
  constructor(status, message, extra) {
    super(message);
    this.status = status;
    this.extra = extra;
  }
}
const bad = (msg, extra) => new HttpError(400, msg, extra);
/** A recovery key as typed by a person: case, dashes, spaces and look-alike letters (O/0, I/L/1) do not matter. */
const normKey = (k) => String(k ?? '').toUpperCase().replace(/[^0-9A-Z]/g, '').replace(/O/g, '0').replace(/[IL]/g, '1');

const ISO = /^\d{4}-\d{2}-\d{2}$/;
function isValidDate(s) {
  if (typeof s !== 'string' || !ISO.test(s)) return false;
  const d = new Date(s + 'T00:00:00Z');
  return !Number.isNaN(d.getTime()) && d.toISOString().slice(0, 10) === s;
}
function addDays(s, n) {
  const d = new Date(s + 'T00:00:00Z');
  d.setUTCDate(d.getUTCDate() + n);
  return d.toISOString().slice(0, 10);
}
function daysBetween(a, b) {
  return Math.round((Date.parse(b + 'T00:00:00Z') - Date.parse(a + 'T00:00:00Z')) / 86400000);
}
const today = () => new Date().toISOString().slice(0, 10);

/** Parse "1,234.56", "($12.30)", "-5", 12.5 into integer cents. Never uses float math on strings. */
function parseMoney(v) {
  if (typeof v === 'number') {
    if (!Number.isFinite(v)) return null;
    const c = Math.round(v * 100);
    return Number.isSafeInteger(c) ? c : null;
  }
  if (typeof v !== 'string') return null;
  let s = v.trim();
  if (!s) return null;
  let neg = false;
  if (/^\(.*\)$/.test(s)) { neg = true; s = s.slice(1, -1); }
  s = s.replace(/[$,\s]/g, '');
  if (s.endsWith('-')) { neg = !neg; s = s.slice(0, -1); }
  if (s.startsWith('-')) { neg = !neg; s = s.slice(1); } else if (s.startsWith('+')) s = s.slice(1);
  if (!/^\d+(\.\d*)?$|^\.\d+$/.test(s)) return null;
  const [w, f = ''] = s.split('.');
  let cents = Number(w || '0') * 100 + Number((f + '00').slice(0, 2));
  if (f.length > 2 && Number(f[2]) >= 5) cents += 1;
  if (!Number.isSafeInteger(cents)) return null;
  return neg ? -cents : cents;
}
function formatMoney(c) {
  const neg = c < 0;
  const a = Math.abs(c);
  return (neg ? '-' : '') + Math.floor(a / 100) + '.' + String(a % 100).padStart(2, '0');
}

// ---- strict input validation helpers (all API input goes through these) ----
// Field names arrive as internal words ("contact_id", "unit_price_cents"); show people the words they see on screen.
const LABELS = { contact_id: 'customer or vendor', contact: 'customer or vendor', terms_days: 'payment terms (days)', unit_price_cents: 'unit price', income_account_id: 'income account',
  expense_account_id: 'expense account', ref: 'reference', kind: 'type', subtype: 'account type', filename: 'file name', file: 'file', amount_cents: 'amount', status: 'status' };
const label = (name) => LABELS[name] ?? String(name).replace(/_id$/, '').replace(/_cents$/, '').replace(/_/g, ' ');
function vStr(v, name, { min = 0, max = 200, required = false } = {}) {
  if (v === undefined || v === null || v === '') {
    if (required) throw bad(`Please fill in the ${label(name)}. It cannot be left empty.`);
    return '';
  }
  if (typeof v !== 'string') throw bad(`The ${label(name)} should be text, but something else was sent. Please type it in again.`);
  const s = v.normalize('NFC').trim();
  if (/[\u0000-\u0008\u000b\u000c\u000e-\u001f]/.test(s)) throw bad(`The ${label(name)} contains a character Ledjoro cannot store (an invisible control character, often picked up when pasting). Retype it by hand.`);
  if (s.length < min) throw bad(`The ${label(name)} is too short: it needs at least ${min} characters.`);
  if (s.length > max) throw bad(`The ${label(name)} is too long: the most Ledjoro keeps is ${max} characters, and this has ${s.length}. Shorten it.`);
  return s;
}
function vInt(v, name, { min = -Number.MAX_SAFE_INTEGER, max = Number.MAX_SAFE_INTEGER, required = true } = {}) {
  if (v === undefined || v === null || v === '') {
    if (required) throw bad(`Please fill in the ${label(name)}. It cannot be left empty.`);
    return null;
  }
  if (typeof v !== 'number' || !Number.isSafeInteger(v)) throw bad(`The ${label(name)} must be a whole number such as 5, with no decimals, letters or symbols.`);
  if (v < min || v > max) throw bad(`The ${label(name)} is outside the allowed range. Check for an extra zero, a missing decimal point or a minus sign.`);
  return v;
}
function vDate(v, name, required = true) {
  if (v === undefined || v === null || v === '') {
    if (required) throw bad(`Please fill in the ${label(name)}. It cannot be left empty.`);
    return null;
  }
  if (!isValidDate(v)) throw bad(`The ${label(name)} is not a real date. Type it as year-month-day, for example 2026-03-31, or pick it from the calendar.`);
  return v;
}
function vEnum(v, name, allowed, required = true) {
  if (v === undefined || v === null || v === '') {
    if (required) throw bad(`Please choose the ${label(name)}.`);
    return null;
  }
  if (!allowed.includes(v)) throw bad(`The ${label(name)} is not one of the choices Ledjoro offers (${allowed.join(', ')}). Choose it from the list.`);
  return v;
}
function vBool(v) { return v === true || v === 1 ? 1 : 0; }
const OUT_OF_STEP = 'The screen and the Ledjoro engine are out of step about what to send (this should not happen). Close Ledjoro and open it again.';
function vObj(v, name) {
  if (!v || typeof v !== 'object' || Array.isArray(v)) throw bad(`The information for ${label(name)} was not in the expected form. ${OUT_OF_STEP}`);
  return v;
}
function vArr(v, name, { max = 1000, min = 0 } = {}) {
  if (!Array.isArray(v)) throw bad(`The ${label(name)} were not in the expected form. ${OUT_OF_STEP}`);
  if (v.length > max) throw bad(`There are ${v.length} ${label(name)}, but the most Ledjoro accepts at once is ${max}. Split this into smaller parts.`);
  if (v.length < min) throw bad(`Add at least ${min} ${label(name)} ${min === 1 ? 'entry' : 'entries'} before saving.`);
  return v;
}

/** Canonical phone format. Accepts "(603) 555-0142", "603.555.0142", "+1 603 555 0142", ... -> "603-555-0142"; null if not 10 digits. */
function normalizePhone(s) {
  let d = String(s ?? '').replace(/\D/g, '');
  if (d.length === 11 && d[0] === '1') d = d.slice(1);
  return d.length === 10 ? `${d.slice(0, 3)}-${d.slice(3, 6)}-${d.slice(6)}` : null;
}
/** For API input: empty is allowed; anything else must be a 10-digit number and is stored as xxx-xxx-xxxx. */
function vPhone(v, name = 'phone') {
  const raw = vStr(v, name, { max: 50 });
  if (!raw) return '';
  const n = normalizePhone(raw);
  if (!n) throw bad(`The ${label(name)} must be a 10-digit number such as 603-555-0142. Remove extra letters, and include the area code.`);
  return n;
}

/** Neutralise spreadsheet formula injection for any value written to CSV. */
function csvCell(v) {
  let s = v === null || v === undefined ? '' : String(v);
  if (/^[=+\-@\t\r]/.test(s) && !/^-?\d+(\.\d+)?$/.test(s)) s = "'" + s;
  if (/[",\n\r]/.test(s)) s = '"' + s.replace(/"/g, '""') + '"';
  return s;
}

module.exports = { normKey,
  HttpError, bad, isValidDate, addDays, daysBetween, today, parseMoney, formatMoney,
  vStr, vInt, vDate, vEnum, vBool, vObj, vArr, csvCell, normalizePhone, vPhone,
};
