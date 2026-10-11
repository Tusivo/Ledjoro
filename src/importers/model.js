'use strict';
const { parseMoney, isValidDate, normalizePhone } = require('../util');

/** Canonical in-memory model every importer produces (and the Ledjoro Interchange Format serialises). */
function newModel(sourceFormat) {
  return {
    format: 'ledjoro-interchange', version: 1, source: { format: sourceFormat },
    accounts: [], contacts: [], items: [], journal_entries: [], documents: [], bank_history: [], bank_transactions: [], bank_meta: null,
    warnings: [], errors: [], notes: [],   // warnings = something the person should look at; notes = information only (shown quietly)
  };
}

/** One quiet note for every account left out because its type holds no money (QuickBooks "non-posting" accounts); never one message per account. */
function skippedNote(skipped) {
  if (!skipped.length) return null;
  const one = skipped.length === 1;
  const types = [...new Set(skipped.map((s) => s.type || '(no type)'))].slice(0, 6).join(', ');
  const names = skipped.slice(0, 5).map((s) => `"${s.name}"`).join(', ');
  return `${skipped.length} account${one ? ' was' : 's were'} left out because ${one ? 'its' : 'their'} type (${types}) is not one that holds money in the books, such as QuickBooks "non-posting" accounts (estimates, purchase orders and the like). ${one ? 'It has' : 'They have'} no effect on your reports. First few: ${names}.`;
}

const norm = (s) => String(s ?? '').toLowerCase().replace(/[^a-z]/g, '');
const ACCOUNT_TYPE_MAP = {
  bank: 'bank',
  accountsreceivable: 'ar', accountsreceivablear: 'ar', ar: 'ar',
  othercurrentassets: 'other_current_asset', othercurrentasset: 'other_current_asset', ocasset: 'other_current_asset',
  fixedassets: 'fixed_asset', fixedasset: 'fixed_asset', fixasset: 'fixed_asset',
  otherassets: 'other_asset', otherasset: 'other_asset', oasset: 'other_asset',
  accountspayable: 'ap', accountspayableap: 'ap', ap: 'ap',
  creditcard: 'credit_card', ccard: 'credit_card',
  othercurrentliabilities: 'other_current_liability', othercurrentliability: 'other_current_liability', ocliab: 'other_current_liability',
  longtermliabilities: 'long_term_liability', longtermliability: 'long_term_liability', ltliab: 'long_term_liability',
  equity: 'equity',
  income: 'income', inc: 'income', revenue: 'income',
  costofgoodssold: 'cogs', cogs: 'cogs',
  expenses: 'expense', expense: 'expense', exp: 'expense',
  otherincome: 'other_income', exinc: 'other_income',
  otherexpense: 'other_expense', otherexpenses: 'other_expense', exexp: 'other_expense',
};
/** @returns {string|null} Ledjoro subtype, or null if unknown / non-posting */
const mapAccountType = (s) => ACCOUNT_TYPE_MAP[norm(s)] ?? null;

function termsToDays(s) {
  const t = String(s ?? '').toLowerCase();
  if (!t) return 30;
  if (/receipt|cod|cash/.test(t)) return 0;
  const m = t.match(/(\d{1,3})/);
  return m ? Math.min(365, Number(m[1])) : 30;
}

/** Parse an IIF/CSV date. order: 'mdy' (US, default) | 'dmy'. Returns YYYY-MM-DD or null. */
function parseDate(s, order = 'mdy') {
  const t = String(s ?? '').trim();
  if (!t) return null;
  if (isValidDate(t)) return t;
  const m = t.match(/^(\d{1,2})[/.-](\d{1,2})[/.-](\d{2}|\d{4})$/);
  if (!m) return null;
  let [, a, b, y] = m;
  if (y.length === 2) y = (Number(y) <= 69 ? '20' : '19') + y;
  const [mo, d] = order === 'dmy' ? [b, a] : [a, b];
  const iso = `${y}-${mo.padStart(2, '0')}-${d.padStart(2, '0')}`;
  return isValidDate(iso) ? iso : null;
}

const money = (s) => parseMoney(String(s ?? '').trim());
/** xxx-xxx-xxxx when it is a 10-digit number, otherwise the original text (never silently dropped). */
const phone = (s) => normalizePhone(s) ?? String(s ?? '').trim();

/** Reject absurd input early (defence against resource exhaustion). */
function checkLimit(count, limits, what = 'rows') {
  if (count > limits.maxRows) {
    const { bad } = require('../util');
    throw bad(`This file has more ${what} than Ledjoro will read at once (the limit is ${limits.maxRows}). Split it into smaller files, for example one per year.`);
  }
}

module.exports = { skippedNote, newModel, mapAccountType, termsToDays, parseDate, money, phone, norm, checkLimit };
