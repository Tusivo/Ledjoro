'use strict';
/**
 * Sales tax: rates (and groups of rates, such as state + county), the tax worked out on an invoice, the return worksheet, and
 * payments to the tax agencies. Money is integer cents; rates are whole thousandths of a percent (7.375% is 7375), so the
 * arithmetic stays exact. Tax is recorded per jurisdiction on every invoice, so a return can be worked out for any period later.
 */
const { tx, getSetting, setSetting } = require('./db');
const { bad, HttpError, vStr, vInt, vDate, vBool, vArr, today } = require('./util');
const { postEntry, reverseEntry } = require('./ledger');
const { audit } = require('./audit');

const MILLI = 100000;   // rate_milli / 100000 of the amount

/** amount x rate, rounded to the nearest cent (half up), without ever leaving integer arithmetic. */
const mulRate = (cents, milli) => Number((BigInt(cents) * BigInt(milli) + BigInt(MILLI / 2)) / BigInt(MILLI));

/** "7.375" or 7.375 -> 7375. Up to three decimals, 0 to 100. */
function pctToMilli(v) {
  const s = String(v ?? '').trim().replace(/%$/, '').trim();
  if (!/^\d{1,3}(\.\d{1,3})?$/.test(s) || Number(s) > 100) throw bad('The tax rate must be a percentage from 0 to 100 with up to three decimals, such as 6.25 or 7.375.');
  const [w, f = ''] = s.split('.');
  return Number(w) * 1000 + Number(f.padEnd(3, '0'));
}

/** The account tax is owed in. Uses the standard "Sales Tax Payable" account and makes it if it has been removed. */
function defaultTaxAccount(db) {
  const a = db.prepare("SELECT id FROM accounts WHERE name='Sales Tax Payable' COLLATE NOCASE AND active=1").get();
  if (a) return a.id;
  return Number(db.prepare("INSERT INTO accounts(number,name,type,subtype,system,created_at) VALUES (NULL,'Sales Tax Payable','liability','other_current_liability',0,?)").run(Date.now()).lastInsertRowid);
}

const defaultRateId = (db) => Number(getSetting(db, 'default_tax_rate_id')) || null;

function listRates(db) {
  const def = defaultRateId(db);
  const rates = db.prepare(`SELECT r.*, a.name AS account_name FROM tax_rates r LEFT JOIN accounts a ON a.id=r.account_id ORDER BY r.kind DESC, r.name`).all();
  const parts = db.prepare('SELECT group_id, rate_id FROM tax_group_parts').all();
  const byId = new Map(rates.map((r) => [r.id, r]));
  const used = new Set(db.prepare('SELECT DISTINCT rate_id FROM document_tax').all().map((x) => x.rate_id));
  for (const r of rates) r.parts = parts.filter((p) => p.group_id === r.id).map((p) => p.rate_id);
  for (const r of rates) {
    if (r.kind === 'group') r.rate_milli = r.parts.reduce((s, id) => s + (byId.get(id)?.rate_milli || 0), 0);
    r.is_default = r.id === def ? 1 : 0;
    r.used = used.has(r.id) || rates.some((g) => g.kind === 'group' && g.parts.includes(r.id)) ? 1 : 0;
  }
  return { rates, default_id: def };
}

function readRate(db, b, existing) {
  const kind = existing ? existing.kind : (b.kind === 'group' ? 'group' : 'rate');
  const name = b.name !== undefined || !existing ? vStr(b.name, 'name', { required: true, max: 80 }) : existing.name;
  const agency = b.agency !== undefined || !existing ? vStr(b.agency, 'tax agency', { max: 120 }) : existing.agency;
  const active = b.active !== undefined ? vBool(b.active) : existing ? existing.active : 1;
  let milli = existing ? existing.rate_milli : 0;
  if (kind === 'rate') {
    if (b.rate_milli !== undefined) milli = vInt(b.rate_milli, 'rate', { min: 0, max: MILLI });
    else if (b.rate_pct !== undefined) milli = pctToMilli(b.rate_pct);
    else if (!existing) throw bad('Type the tax rate as a percentage, such as 6.25.');
  }
  let account = existing ? existing.account_id : null;
  if (b.account_id !== undefined) account = b.account_id ? vInt(b.account_id, 'account') : null;
  if (account) {
    const a = db.prepare('SELECT type FROM accounts WHERE id=?').get(account);
    if (!a || a.type !== 'liability') throw bad('Sales tax is owed to the tax agency, so it has to be recorded in a liability account. Choose one (the usual one is Sales Tax Payable).');
  }
  let parts = null;
  if (kind === 'group' && (b.parts !== undefined || !existing)) {
    parts = [...new Set(vArr(b.parts ?? [], 'rates in the group', { min: 1, max: 12 }).map((x) => vInt(x, 'rate in the group')))];
    for (const id of parts) {
      const r = db.prepare('SELECT kind, active FROM tax_rates WHERE id=?').get(id);
      if (!r || r.kind !== 'rate') throw bad('A combined rate is made from single rates (for example a state rate plus a county rate). One of the choices is not a single rate.');
    }
  }
  return { kind, name, agency, active, milli, account, parts };
}

function createRate(db, ctx, b) {
  const f = readRate(db, b, null);
  return tx(db, () => {
    let id;
    try {
      id = Number(db.prepare('INSERT INTO tax_rates(name,kind,rate_milli,agency,account_id,active,created_at) VALUES (?,?,?,?,?,?,?)')
        .run(f.name, f.kind, f.kind === 'group' ? 0 : f.milli, f.agency, f.account, f.active, Date.now()).lastInsertRowid);
    } catch (e) {
      if (/UNIQUE/.test(e.message)) throw bad(`You already have a tax rate called "${f.name}". Use a different name.`);
      throw e;
    }
    if (f.parts) for (const p of f.parts) db.prepare('INSERT INTO tax_group_parts(group_id, rate_id) VALUES (?,?)').run(id, p);
    if (b.is_default) setSetting(db, 'default_tax_rate_id', id);
    audit(db, ctx, 'taxrate.create', 'tax_rate', id, `${f.name} ${f.milli / 1000}%`);
    return id;
  });
}

function updateRate(db, ctx, id, b) {
  const r = db.prepare('SELECT * FROM tax_rates WHERE id=?').get(id);
  if (!r) throw new HttpError(404, 'That tax rate could not be found. It may have been removed; refresh the list.');
  const f = readRate(db, b, r);
  tx(db, () => {
    try {
      db.prepare('UPDATE tax_rates SET name=?, rate_milli=?, agency=?, account_id=?, active=? WHERE id=?').run(f.name, r.kind === 'group' ? 0 : f.milli, f.agency, f.account, f.active, id);
    } catch (e) {
      if (/UNIQUE/.test(e.message)) throw bad(`You already have a tax rate called "${f.name}". Use a different name.`);
      throw e;
    }
    if (f.parts) {
      db.prepare('DELETE FROM tax_group_parts WHERE group_id=?').run(id);
      for (const p of f.parts) db.prepare('INSERT INTO tax_group_parts(group_id, rate_id) VALUES (?,?)').run(id, p);
    }
    if (b.is_default === true || b.is_default === 1) setSetting(db, 'default_tax_rate_id', id);
    else if ((b.is_default === false || b.is_default === 0) && defaultRateId(db) === id) setSetting(db, 'default_tax_rate_id', '');
    audit(db, ctx, 'taxrate.update', 'tax_rate', id, f.name);
  });
}

/**
 * Tax on `taxableCents` at tax rate (or group) `rateId`, split by jurisdiction. Each jurisdiction is rounded on its own, because that is
 * what each agency's return shows; a group's tax is the sum of its parts.
 */
function taxFor(db, rateId, taxableCents) {
  const r = db.prepare('SELECT * FROM tax_rates WHERE id=?').get(rateId);
  if (!r) throw bad('The sales tax rate chosen does not exist any more. Choose a rate from the list, or choose no tax.');
  if (!r.active) throw bad(`The tax rate "${r.name}" has been switched off, so it cannot be used on new invoices. Choose another rate, or switch it on again under Sales Tax.`);
  const comps = r.kind === 'group'
    ? db.prepare('SELECT t.* FROM tax_group_parts p JOIN tax_rates t ON t.id=p.rate_id WHERE p.group_id=? ORDER BY t.name').all(rateId)
    : [r];
  const off = comps.find((c) => !c.active);
  if (off) throw bad(`The tax rate "${off.name}" has been switched off, so "${r.name}" cannot be used on new invoices. Choose another rate, or switch it on again under Sales Tax.`);
  if (!comps.length) throw bad(`The combined rate "${r.name}" has no rates in it. Open Sales Tax and add the rates it is made of.`);
  const fallback = defaultTaxAccount(db);
  const parts = comps.map((c) => ({ rate_id: c.id, name: c.name, rate_milli: c.rate_milli, account_id: c.account_id || fallback, taxable_cents: taxableCents, tax_cents: mulRate(taxableCents, c.rate_milli) }));
  return { rate: r, parts, total_cents: parts.reduce((s, p) => s + p.tax_cents, 0) };
}

// ---------------- the return worksheet ----------------
function taxAccountIds(db) {
  const ids = new Set(db.prepare('SELECT DISTINCT account_id FROM tax_rates WHERE account_id IS NOT NULL').all().map((x) => x.account_id));
  const d = db.prepare("SELECT id FROM accounts WHERE name='Sales Tax Payable' COLLATE NOCASE").get();
  if (d) ids.add(d.id);
  return [...ids];
}

function worksheet(db, q) {
  const from = vDate(q.from, 'from date'), to = vDate(q.to, 'to date');
  if (to < from) throw bad('The "to" date is earlier than the "from" date. Swap them, or pick the period again.');
  const docs = db.prepare(`SELECT d.id, d.number, d.doc_date, d.total_cents, d.tax_cents, d.tax_rate_id, d.tax_exempt, c.id AS contact_id, c.name AS customer, c.tax_exempt_note,
      (SELECT COALESCE(SUM(amount_cents),0) FROM document_lines WHERE document_id=d.id) AS lines_cents,
      (SELECT COALESCE(SUM(amount_cents),0) FROM document_lines WHERE document_id=d.id AND taxable=1) AS taxable_lines_cents,
      (SELECT COUNT(*) FROM document_lines WHERE document_id=d.id) AS line_count
    FROM documents d JOIN contacts c ON c.id=d.contact_id WHERE d.kind='invoice' AND d.status<>'void' AND d.doc_date>=? AND d.doc_date<=? ORDER BY d.doc_date, d.number`).all(from, to);

  const sales = { total_cents: 0, taxable_cents: 0, exempt_cents: 0, nontaxable_cents: 0 };
  const invoices = [], exempt = new Map();
  for (const d of docs) {
    const subtotal = d.line_count ? d.lines_cents : d.total_cents - d.tax_cents;
    let taxable = 0, ex = 0;
    if (d.tax_exempt) ex = d.taxable_lines_cents;
    else if (d.tax_rate_id) taxable = d.taxable_lines_cents;
    const non = subtotal - taxable - ex;
    sales.total_cents += subtotal; sales.taxable_cents += taxable; sales.exempt_cents += ex; sales.nontaxable_cents += non;
    invoices.push({ id: d.id, number: d.number, date: d.doc_date, customer: d.customer, subtotal_cents: subtotal, taxable_cents: taxable, exempt_cents: ex, nontaxable_cents: non, tax_cents: d.tax_cents });
    if (ex) { const e = exempt.get(d.contact_id) || { customer: d.customer, note: d.tax_exempt_note, sales_cents: 0 }; e.sales_cents += ex; exempt.set(d.contact_id, e); }
  }

  // credit memos in the period count against sales and against the tax of the jurisdictions they were charged in
  const credits = db.prepare(`SELECT c.id, c.number, c.memo_date, c.total_cents, c.tax_cents, c.tax_rate_id, c.tax_exempt, k.id AS contact_id, k.name AS customer, k.tax_exempt_note,
      (SELECT COALESCE(SUM(amount_cents),0) FROM credit_memo_lines WHERE credit_id=c.id) AS lines_cents,
      (SELECT COALESCE(SUM(amount_cents),0) FROM credit_memo_lines WHERE credit_id=c.id AND taxable=1) AS taxable_lines_cents
    FROM credit_memos c JOIN contacts k ON k.id=c.contact_id WHERE c.status<>'void' AND c.memo_date>=? AND c.memo_date<=? ORDER BY c.memo_date, c.number`).all(from, to);
  const creditTotals = { count: credits.length, subtotal_cents: 0, tax_cents: 0 };
  for (const c of credits) {
    let taxable = 0, ex = 0;
    if (c.tax_exempt) ex = c.taxable_lines_cents; else if (c.tax_rate_id) taxable = c.taxable_lines_cents;
    const non = c.lines_cents - taxable - ex;
    sales.total_cents -= c.lines_cents; sales.taxable_cents -= taxable; sales.exempt_cents -= ex; sales.nontaxable_cents -= non;
    creditTotals.subtotal_cents += c.lines_cents; creditTotals.tax_cents += c.tax_cents;
    invoices.push({ id: c.id, kind: 'credit', number: c.number, date: c.memo_date, customer: c.customer, subtotal_cents: -c.lines_cents, taxable_cents: -taxable, exempt_cents: -ex, nontaxable_cents: -non, tax_cents: -c.tax_cents });
    if (ex) { const e = exempt.get(c.contact_id) || { customer: c.customer, note: c.tax_exempt_note, sales_cents: 0 }; e.sales_cents -= ex; exempt.set(c.contact_id, e); }
  }
  invoices.sort((a, b) => (a.date < b.date ? -1 : a.date > b.date ? 1 : a.number.localeCompare(b.number)));

  const juris = new Map();
  const addJ = (r, sign) => {
    const key = r.rate_id + '|' + r.rate_milli;
    const j = juris.get(key) || { rate_id: r.rate_id, name: r.name, agency: r.agency, rate_milli: r.rate_milli, taxable_cents: 0, tax_cents: 0 };
    j.taxable_cents += sign * r.taxable_cents; j.tax_cents += sign * r.tax_cents; juris.set(key, j);
  };
  for (const r of db.prepare(`SELECT dt.rate_id, r.name, r.agency, dt.rate_milli, SUM(dt.taxable_cents) AS taxable_cents, SUM(dt.tax_cents) AS tax_cents
    FROM document_tax dt JOIN documents d ON d.id=dt.document_id JOIN tax_rates r ON r.id=dt.rate_id
    WHERE d.kind='invoice' AND d.status<>'void' AND d.doc_date>=? AND d.doc_date<=? GROUP BY dt.rate_id, dt.rate_milli`).all(from, to)) addJ(r, 1);
  for (const r of db.prepare(`SELECT t.rate_id, r.name, r.agency, t.rate_milli, SUM(t.taxable_cents) AS taxable_cents, SUM(t.tax_cents) AS tax_cents
    FROM credit_memo_tax t JOIN credit_memos c ON c.id=t.credit_id JOIN tax_rates r ON r.id=t.rate_id
    WHERE c.status<>'void' AND c.memo_date>=? AND c.memo_date<=? GROUP BY t.rate_id, t.rate_milli`).all(from, to)) addJ(r, -1);
  const jurisdictions = [...juris.values()].sort((a, b) => (a.agency + a.name + a.rate_milli).localeCompare(b.agency + b.name + b.rate_milli));
  const collected = jurisdictions.reduce((s, j) => s + j.tax_cents, 0);

  // what the ledger says about the tax accounts, so the worksheet can be checked against the books
  const ids = taxAccountIds(db), ph = ids.map(() => '?').join(',');
  const untracked = `l.entry_id NOT IN (
      SELECT entry_id FROM documents WHERE entry_id IS NOT NULL
      UNION SELECT reversal_id FROM entry_reversals WHERE entry_id IN (SELECT entry_id FROM documents WHERE entry_id IS NOT NULL)
      UNION SELECT entry_id FROM credit_memos WHERE entry_id IS NOT NULL
      UNION SELECT reversal_id FROM entry_reversals WHERE entry_id IN (SELECT entry_id FROM credit_memos WHERE entry_id IS NOT NULL)
      UNION SELECT entry_id FROM tax_remittances WHERE entry_id IS NOT NULL
      UNION SELECT reversal_id FROM entry_reversals WHERE entry_id IN (SELECT entry_id FROM tax_remittances WHERE entry_id IS NOT NULL))`;
  const net = (cond, ...args) => ids.length ? Number(db.prepare(`SELECT COALESCE(SUM(l.credit_cents - l.debit_cents),0) AS n FROM journal_lines l JOIN journal_entries e ON e.id=l.entry_id WHERE l.account_id IN (${ph}) AND ${cond}`).get(...ids, ...args).n) : 0;
  const opening = net('e.entry_date < ?', from), closing = net('e.entry_date <= ?', to);
  const other = net(`e.entry_date >= ? AND e.entry_date <= ? AND ${untracked}`, from, to);
  const remittances = db.prepare('SELECT agency, SUM(amount_cents) AS amount_cents FROM tax_remittances WHERE voided=0 AND pay_date>=? AND pay_date<=? GROUP BY agency ORDER BY agency').all(from, to);
  const remitted = remittances.reduce((s, r) => s + r.amount_cents, 0);

  return {
    from, to, sales, jurisdictions, tax_collected_cents: collected, credit_memos: creditTotals,
    ledger: { opening_cents: opening, closing_cents: closing, other_entries_cents: other, remitted_cents: remitted },
    remittances, exempt_customers: [...exempt.values()].sort((a, b) => a.customer.localeCompare(b.customer)), invoices,
    uses_sales_tax: jurisdictions.length > 0 || ids.length > 0 || Boolean(db.prepare('SELECT 1 FROM tax_rates LIMIT 1').get()),
  };
}

// ---------------- payments to the tax agencies ----------------
function listRemittances(db) {
  return db.prepare(`SELECT t.*, b.name AS bank_name, a.name AS tax_account_name FROM tax_remittances t JOIN accounts b ON b.id=t.bank_account_id JOIN accounts a ON a.id=t.tax_account_id
    ORDER BY t.pay_date DESC, t.id DESC LIMIT 1000`).all();
}

function createRemittance(db, ctx, b) {
  const date = vDate(b.pay_date, 'payment date');
  const amount = vInt(b.amount_cents, 'amount', { min: 1, max: 1e13 });
  const bankId = vInt(b.bank_account_id, 'account you paid from');
  const bank = db.prepare('SELECT subtype FROM accounts WHERE id=?').get(bankId);
  if (!bank || !['bank', 'credit_card', 'other_current_asset'].includes(bank.subtype)) throw bad('Choose where the money came from: a bank, credit card or cash account. The account chosen is a different kind of account.');
  const taxId = b.tax_account_id ? vInt(b.tax_account_id, 'tax account') : defaultTaxAccount(db);
  const tax = db.prepare('SELECT type FROM accounts WHERE id=?').get(taxId);
  if (!tax || tax.type !== 'liability') throw bad('The tax payment is taken off a liability account (the usual one is Sales Tax Payable). The account chosen is a different kind of account.');
  const agency = vStr(b.agency, 'tax agency', { max: 120 });
  const from = vDate(b.period_from, 'period start', false) || '', to = vDate(b.period_to, 'period end', false) || '';
  return tx(db, () => {
    const memo = vStr(b.memo, 'memo', { max: 200 }) || `Sales tax payment${agency ? ' - ' + agency : ''}${from && to ? ` (${from} to ${to})` : ''}`;
    const entry = postEntry(db, { date, memo, ref: vStr(b.ref, 'reference', { max: 50 }), source_type: 'salestax', user_id: ctx.user.id,
      lines: [{ account_id: taxId, debit_cents: amount, memo }, { account_id: bankId, credit_cents: amount, memo }] });
    const id = Number(db.prepare('INSERT INTO tax_remittances(agency,period_from,period_to,pay_date,amount_cents,bank_account_id,tax_account_id,memo,entry_id,created_by,created_at) VALUES (?,?,?,?,?,?,?,?,?,?,?)')
      .run(agency, from, to, date, amount, bankId, taxId, memo, entry, ctx.user.id, Date.now()).lastInsertRowid);
    audit(db, ctx, 'salestax.pay', 'tax_remittance', id, `${agency} ${amount}`);
    return id;
  });
}

function voidRemittance(db, ctx, id) {
  const r = db.prepare('SELECT * FROM tax_remittances WHERE id=?').get(id);
  if (!r) throw new HttpError(404, 'That tax payment could not be found. It may have been removed; refresh the list.');
  if (r.voided) throw bad('This tax payment has already been voided.');
  tx(db, () => {
    reverseEntry(db, r.entry_id, { date: today() > r.pay_date ? today() : r.pay_date, userId: ctx.user.id, memo: `Void sales tax payment #${r.id}` });
    db.prepare('UPDATE tax_remittances SET voided=1 WHERE id=?').run(id);
    audit(db, ctx, 'salestax.void', 'tax_remittance', id);
  });
}

module.exports = { listRates, createRate, updateRate, taxFor, worksheet, listRemittances, createRemittance, voidRemittance, defaultTaxAccount, defaultRateId, pctToMilli, mulRate };
