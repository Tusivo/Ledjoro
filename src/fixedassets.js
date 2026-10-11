'use strict';
/**
 * Fixed-asset register with straight-line depreciation (full-month convention: depreciation starts in the month the asset is placed in service).
 * Monthly amounts come from cumulative rounding, so the total over the asset's life is exactly cost - salvage with no drift.
 */
const { tx } = require('./db');
const { bad, HttpError, vStr, vInt, vDate, isValidDate, today } = require('./util');
const { postEntry } = require('./ledger');
const { audit } = require('./audit');

const lastDay = (y, m) => new Date(Date.UTC(y, m, 0)).getUTCDate(); // m is 1-12
const monthEnd = (y, m) => `${y}-${String(m).padStart(2, '0')}-${String(lastDay(y, m)).padStart(2, '0')}`;
const isMonthEnd = (d) => isValidDate(d) && Number(d.slice(8, 10)) === lastDay(Number(d.slice(0, 4)), Number(d.slice(5, 7)));

/** k-th month (1-based) of the asset's life -> its month-end date */
function dateOfMonth(a, k) {
  const y0 = Number(a.in_service.slice(0, 4)), m0 = Number(a.in_service.slice(5, 7)) - 1 + (k - 1);
  return monthEnd(y0 + Math.floor(m0 / 12), (m0 % 12) + 1);
}
/** how many months of life have elapsed by `date` (0 if not yet in service), capped at life */
function monthsThrough(a, date) {
  if (!date) return 0;
  const n = (Number(date.slice(0, 4)) - Number(a.in_service.slice(0, 4))) * 12 + (Number(date.slice(5, 7)) - Number(a.in_service.slice(5, 7))) + 1;
  return Math.max(0, Math.min(a.life_months, n));
}
const cumulative = (a, k) => Math.round(((a.cost_cents - a.salvage_cents) * Math.max(0, Math.min(k, a.life_months))) / a.life_months);
const monthlyAmount = (a, k) => (k < 1 || k > a.life_months ? 0 : cumulative(a, k) - cumulative(a, k - 1));

function schedule(a) {
  const posted = monthsThrough(a, a.posted_through);
  return Array.from({ length: a.life_months }, (_, i) => {
    const k = i + 1;
    return { k, date: dateOfMonth(a, k), amount_cents: monthlyAmount(a, k), accumulated_cents: cumulative(a, k), posted: k <= posted };
  });
}

function ensureAccount(db, name, subtype, type) {
  const r = db.prepare('SELECT id FROM accounts WHERE name=?').get(name);
  if (r) return r.id;
  return Number(db.prepare('INSERT INTO accounts(number,name,type,subtype,created_at) VALUES (NULL,?,?,?,?)').run(name, type, subtype, Date.now()).lastInsertRowid);
}

function describe(db, a) {
  const posted = monthsThrough(a, a.posted_through);
  const accumulated = cumulative(a, posted);
  const nextK = posted + 1;
  const status = a.disposed_on ? 'disposed' : a.in_service > today() ? 'not yet in service' : posted >= a.life_months ? 'fully depreciated' : 'depreciating';
  return { ...a, monthly_cents: monthlyAmount(a, Math.min(Math.max(posted, 1), a.life_months)), accumulated_cents: accumulated, book_value_cents: a.cost_cents - accumulated,
    next_date: nextK <= a.life_months && !a.disposed_on ? dateOfMonth(a, nextK) : null, status };
}
const listAssets = (db) => db.prepare('SELECT * FROM fixed_assets ORDER BY in_service, id').all().map((a) => describe(db, a));

function createAsset(db, ctx, b) {
  const cost = vInt(b.cost_cents, 'cost', { min: 1, max: 1e13 });
  const salvage = vInt(b.salvage_cents ?? 0, 'salvage value', { min: 0, max: cost - 1 });
  const inService = vDate(b.in_service, 'in-service date');
  const life = vInt(b.life_months, 'useful life (months)', { min: 1, max: 600 });
  const fixedAsset = () => db.prepare("SELECT id FROM accounts WHERE subtype='fixed_asset' AND active=1 AND name NOT LIKE '%ccumulated%' ORDER BY (name='Equipment') DESC, id LIMIT 1").get()?.id;
  const assetAcc = b.asset_account_id ? vInt(b.asset_account_id, 'asset account') : fixedAsset();
  if (!assetAcc) throw bad('There is no fixed-asset account to put this asset in. Add one under Chart of accounts (type: Fixed asset), then try again.');
  const accumAcc = b.accum_account_id ? vInt(b.accum_account_id, 'accumulated depreciation account') : ensureAccount(db, 'Accumulated Depreciation', 'fixed_asset', 'asset');
  const expAcc = b.expense_account_id ? vInt(b.expense_account_id, 'depreciation expense account') : ensureAccount(db, 'Depreciation Expense', 'expense', 'expense');
  for (const id of [assetAcc, accumAcc, expAcc]) if (!db.prepare('SELECT 1 FROM accounts WHERE id=?').get(id)) throw bad('One of the accounts chosen does not exist any more. Pick the accounts again from the lists.');
  const already = vDate(b.depreciated_through, 'already depreciated through', false) || '';
  if (already && !isMonthEnd(already)) throw bad('The "already depreciated through" date must be the last day of a month (for example 2026-03-31), because depreciation is posted one whole month at a time.');
  const r = db.prepare('INSERT INTO fixed_assets(name,cost_cents,salvage_cents,in_service,life_months,asset_account_id,accum_account_id,expense_account_id,posted_through,notes,created_at) VALUES (?,?,?,?,?,?,?,?,?,?,?)')
    .run(vStr(b.name, 'name', { required: true, max: 150 }), cost, salvage, inService, life, assetAcc, accumAcc, expAcc, already, vStr(b.notes, 'notes', { max: 500 }), Date.now());
  audit(db, ctx, 'asset.create', 'fixed_asset', r.lastInsertRowid, `${b.name} cost ${cost}`);
  return Number(r.lastInsertRowid);
}

/** Post one journal entry per asset per month for every month ending on or before `through`. */
function postDepreciation(db, ctx, through) {
  if (!isValidDate(through)) throw bad('Choose a valid date to post depreciation through, written year-month-day, such as 2026-03-31.');
  return tx(db, () => {
    let entries = 0, total = 0;
    for (const a of db.prepare("SELECT * FROM fixed_assets WHERE disposed_on='' ORDER BY id").all()) {
      let k = monthsThrough(a, a.posted_through);
      let last = a.posted_through;
      while (k < a.life_months && dateOfMonth(a, k + 1) <= through) {
        k++;
        const amt = monthlyAmount(a, k), date = dateOfMonth(a, k);
        if (amt > 0) {
          if (++entries > 5000) throw bad('That would post more than 5,000 monthly entries in one go, which is too many to do safely. Choose an earlier "through" date and repeat in smaller steps.');
          postEntry(db, { date, memo: `Depreciation - ${a.name} (${date.slice(0, 7)})`, ref: `DEP-${a.id}-${date.slice(0, 7)}`, source_type: 'depreciation', source_id: a.id, user_id: ctx.user.id,
            lines: [{ account_id: a.expense_account_id, debit_cents: amt }, { account_id: a.accum_account_id, credit_cents: amt }] });
          total += amt;
        }
        last = date;
      }
      if (last !== a.posted_through) db.prepare('UPDATE fixed_assets SET posted_through=? WHERE id=?').run(last, a.id);
    }
    audit(db, ctx, 'asset.depreciate', 'fixed_asset', '', `${entries} entries through ${through}, total ${total}`);
    return { entries, total_cents: total };
  });
}

/** Retire an asset: removes cost and accumulated depreciation, records proceeds, and books the gain or loss. */
function disposeAsset(db, ctx, id, b) {
  const a = db.prepare('SELECT * FROM fixed_assets WHERE id=?').get(id);
  if (!a) throw new HttpError(404, 'That asset could not be found. It may have been deleted; refresh the list.');
  if (a.disposed_on) throw bad('This asset has already been disposed of, so it cannot be disposed of again.');
  const date = vDate(b.date, 'disposal date');
  if (date < a.in_service) throw bad('The disposal date is earlier than the date the asset was put into service. An asset cannot be disposed of before it existed; fix the date.');
  const proceeds = vInt(b.proceeds_cents ?? 0, 'proceeds', { min: 0, max: 1e13 });
  const deposit = proceeds ? vInt(b.deposit_account_id, 'deposit account') : null;
  const need = monthsThrough(a, addMonthsEnd(date));
  if (monthsThrough(a, a.posted_through) < need) throw bad(`Depreciation has to be up to date before an asset is disposed of. Post depreciation through ${addMonthsEnd(date)} first, then dispose of the asset.`);
  return tx(db, () => {
    const accumulated = cumulative(a, monthsThrough(a, a.posted_through));
    const gain = proceeds + accumulated - a.cost_cents; // >0 gain, <0 loss
    const gl = gain ? ensureAccount(db, 'Gain or Loss on Disposal', 'other_expense', 'other_expense') : null;
    const lines = [{ account_id: a.accum_account_id, debit_cents: accumulated }, { account_id: a.asset_account_id, credit_cents: a.cost_cents }];
    if (proceeds) lines.push({ account_id: deposit, debit_cents: proceeds });
    if (gain > 0) lines.push({ account_id: gl, credit_cents: gain }); else if (gain < 0) lines.push({ account_id: gl, debit_cents: -gain });
    const entry = postEntry(db, { date, memo: `Disposal of ${a.name}`, ref: `DISP-${a.id}`, source_type: 'depreciation', source_id: a.id, user_id: ctx.user.id, lines: lines.filter((l) => (l.debit_cents || 0) + (l.credit_cents || 0) > 0) });
    db.prepare('UPDATE fixed_assets SET disposed_on=? WHERE id=?').run(date, id);
    audit(db, ctx, 'asset.dispose', 'fixed_asset', id, `${a.name} gain/loss ${gain}`);
    return { entry_id: entry, gain_cents: gain };
  });
}
/** the month-end *before* the disposal month (no depreciation is taken in the month of disposal) */
function addMonthsEnd(date) {
  const y = Number(date.slice(0, 4)), m = Number(date.slice(5, 7));
  return m === 1 ? monthEnd(y - 1, 12) : monthEnd(y, m - 1);
}

module.exports = { listAssets, createAsset, postDepreciation, disposeAsset, schedule, monthlyAmount, cumulative, monthsThrough, dateOfMonth, monthEnd };
