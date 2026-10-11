#!/usr/bin/env node
'use strict';
/**
 * Generates a fully SYNTHETIC demo company ("Birchwood Design Studio LLC", fictional) with a configurable number of years of activity (default 5).
 * Deterministic (seeded) so results are reproducible. Nothing here comes from any real business or from Intuit.
 *
 *   node scripts/make-demo-data.js             # 5 years, ending 2026-09-30
 *   node scripts/make-demo-data.js --years 1   # a single year
 *
 * Outputs
 *   samples/demo/*            QuickBooks-style files to exercise the converters (IIF, QBO/OFX, CSV)
 *   demo-data/ledjoro.db    a ready-to-open demo database (NO users: first launch asks you to create the admin)
 *
 * Self-check: the same activity is loaded two ways (native entry vs. IIF conversion) and the trial balances must match to the cent.
 */
const fs = require('node:fs');
const path = require('node:path');
const { openDb } = require('../src/db');
const D = require('../src/domain');
const C = require('../src/convert');
const R = require('../src/reports');
const { checkLedgerIntegrity } = require('../src/ledger');
const { verifyAudit } = require('../src/audit');
const { addDays, formatMoney } = require('../src/util');
const Assets = require('../src/fixedassets');
const D_assets = Assets;

const root = path.join(__dirname, '..');
const outFiles = path.join(root, 'samples', 'demo');
const outDb = path.join(root, 'demo-data');

// ---------- deterministic randomness ----------
function rng(seed) { let a = seed >>> 0; return () => { a = (a + 0x6D2B79F5) | 0; let t = Math.imul(a ^ (a >>> 15), 1 | a); t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t; return ((t ^ (t >>> 14)) >>> 0) / 4294967296; }; }
const rand = rng(20261008);
const rint = (a, b) => a + Math.floor(rand() * (b - a + 1));
const pick = (arr) => arr[Math.floor(rand() * arr.length)];
const round5 = (c) => Math.round(c / 500) * 500; // round to nearest $5

// ---------- the fictional world ----------
const args = process.argv.slice(2);
const YEARS = Math.max(1, Math.min(10, Number(args[args.indexOf('--years') + 1]) || 5));
const END = '2026-09-30', BANK_FEED_FROM = '2026-07-01';
const START = `${2026 - YEARS}-10-01`, OPEN = addDays(START, -1);
const NEW_ACCOUNTS = [ // name, number, subtype (added on top of the default chart)
  ['Design Services', '4200', 'income'], ['Retainer Revenue', '4210', 'income'], ['Subcontractors', '5100', 'cogs'],
  ['Hosting & Domains', '6120', 'expense'], ['Depreciation Expense', '6130', 'expense'], ['Owner Draw', '3200', 'equity'],
];
// name, contact, address, payment terms, first month (0 = start of data), last month (omitted = still a customer)
const ALL_CUSTOMERS = [
  ['Harborview Dental', 'Dr. Mina Alvarez', '412 Harbor Rd, Portsmouth, NH 03801', 30, 0], ['Juniper & Pine Bakery', 'Sam Okafor', '88 Willow St, Concord, NH 03301', 15, 0],
  ['Northgate Realty', 'Priya Nair', '1500 Northgate Blvd, Nashua, NH 03060', 30, 0], ['Cobalt Fitness', 'Eli Brandt', '27 Mill Pond Way, Dover, NH 03820', 30, 0, 40],
  ['Meridian Law Group', 'Hannah Cole', '9 State St, Boston, MA 02109', 45, 0], ['Fernhill Veterinary', 'Dr. Luis Ortega', '640 Fernhill Ave, Manchester, NH 03104', 30, 0],
  ['Lumen Optics', 'Jade Whitfield', '3 Observatory Ln, Keene, NH 03431', 30, 0], ['Ridgeline Outfitters', 'Tom Ibarra', '212 Summit Rd, North Conway, NH 03860', 30, 0],
  ['Sundial Cafe', 'Rosa Delgado', '55 Main St, Exeter, NH 03833', 15, 0, 30], ['Tidewater Marine Supply', 'Gus Halvorsen', '7 Wharf Ln, Rye, NH 03870', 30, 0],
  ['Alder Street Books', 'Nora Kim', '31 Alder St, Portland, ME 04101', 30, 0], ['Quill & Compass Travel', 'Ben Sato', '120 Compass Ct, Salem, MA 01970', 45, 0],
  ['Evergreen Pediatrics', 'Dr. Amara Osei', '18 Evergreen Way, Bedford, NH 03110', 30, 8], ['Copperline Electric', 'Dale Whitcomb', '402 Industrial Pkwy, Londonderry, NH 03053', 30, 14],
  ['Bluebird Preschool', 'Marcy Lindqvist', '6 Bluebird Ln, Hampton, NH 03842', 15, 19], ['Granite Peak Climbing', 'Theo Marchetti', '77 Boulder Rd, Conway, NH 03818', 30, 24],
  ['Harbor Light Brewing', 'Cass Moreau', '14 Dockside Ave, Portsmouth, NH 03801', 30, 30], ['Stonebridge Wealth Advisors', 'Ian Prescott', '200 Stonebridge Dr, Boston, MA 02110', 45, 36],
  ['Marigold Florist', 'Yuki Tanaka', '9 Marigold Ct, Nashua, NH 03063', 15, 42], ['Pinecrest Orthodontics', 'Dr. Rhea Banerjee', '321 Pinecrest Rd, Salem, NH 03079', 30, 48],
  ['Saltmarsh Yacht Club', 'Walt Everett', '1 Marina Pt, Newcastle, NH 03854', 30, 54],
].map(([name, contact, address, terms, from, until], i) => ({ idx: i, name, address, terms, from, until: until ?? null, email: `accounts@${name.toLowerCase().replace(/[^a-z]+/g, '')}.example`, phone: `603-555-01${String(10 + i).padStart(2, '0')}`, contact }));
const VENDORS = [
  ['Granite Properties', 'rent', 5, '6060', 0], ['Cloudstack Hosting', 'hosting', 15, '6120', 0], ['Paperworks Office Supply', 'office', 30, '6040', 0],
  ['Summit Insurance Agency', 'insurance', 30, '6020', 0], ['Fieldnote Software', 'software', 15, '6080', 0], ['Kestrel Freelance Collective', 'sub', 30, '5100', 1],
  ['Brightline Telecom', 'phone', 20, '6090', 0], ['Orchard Accounting Services', 'accounting', 30, '6050', 1], ['Northeast Computer Supply', 'equip', 30, '1500', 0],
].map(([name, kind, terms, acct, is1099], i) => ({ name, kind, terms, acct, is1099: !!is1099, email: `billing@${name.toLowerCase().replace(/[^a-z]+/g, '')}.example`, phone: `603-555-01${String(50 + i).padStart(2, '0')}`, address: `${100 + i * 7} Commerce Dr, Manchester, NH 03101` }));
const ITEMS = [ // name, kind, price (today's list price), income account
  ['Website design (project)', 'service', 450000, 'Design Services'], ['Brand identity package', 'service', 220000, 'Design Services'], ['Monthly retainer', 'service', 120000, 'Retainer Revenue'],
  ['Consulting (hourly)', 'service', 9500, 'Design Services'], ['Hosting setup', 'service', 30000, 'Design Services'], ['Copywriting (per page)', 'service', 18000, 'Design Services'],
];
// customer index, monthly cents (at today's prices), first month, last month
const RETAINERS = [[0, 120000, 0], [1, 90000, 0], [2, 150000, 0], [3, 100000, 0, 40], [4, 200000, 0], [5, 80000, 0], [7, 120000, 0], [12, 110000, 10], [13, 160000, 16], [15, 130000, 26], [17, 240000, 38], [19, 180000, 50]];
const OFFICE_BANK = [ // description, category account, min, max (cents, spend)
  ['PAPERWORKS #204 OFFICE SUPPLY', 'Office Supplies', 1800, 14500], ['FIELDNOTE SOFTWARE SUBSCRIPTION', 'Software & Subscriptions', 1900, 4900], ['CORNER BISTRO', 'Meals', 1400, 7800],
  ['FUEL STOP 17', 'Travel', 3200, 6900], ['STUDIO COFFEE CO', 'Meals', 600, 2400], ['PRINTWORKS EXPRESS', 'Advertising', 2500, 21000], ['PARKING AUTHORITY', 'Travel', 500, 2200],
];

// ---------- build the activity once ----------
const monthsList = []; for (let d = START; d <= END; d = addDays(d.slice(0, 7) + '-28', 4).slice(0, 7) + '-01') monthsList.push(d);
const CUSTOMERS = ALL_CUSTOMERS.filter((c) => c.from < monthsList.length);
const events = []; let invNo = 0, billNo = 0, fitid = 0;
const ev = (e) => events.push(e);
const lineAmt = (q, u) => Math.round((q * u) / 1000);
const clampDate = (d) => (d > END ? null : d);
const recentCut = addDays(END, -90);

function invoice(date, cust, lines, payPlan) {
  const number = 'INV-' + String(++invNo).padStart(4, '0');
  const ls = lines.map((l) => ({ ...l, amount: lineAmt(l.qty, l.unit) }));
  const total = ls.reduce((s, l) => s + l.amount, 0);
  ev({ kind: 'invoice', date, cust, number, lines: ls, total });
  let paid = 0;
  for (const [lag, frac] of payPlan) {
    const pd = clampDate(addDays(date, lag));
    if (!pd) continue;
    const amt = frac === 1 ? total - paid : Math.round(total * frac);
    if (amt <= 0) continue;
    ev({ kind: 'payment', date: pd, cust, amount: amt, applyTo: number, method: pick(['Check', 'ACH', 'Card']), ref: String(rint(1000, 9999)) });
    paid += amt;
  }
}
function bill(date, vendor, lines, lag) {
  const number = (vendor.name.split(' ')[0].slice(0, 4).toUpperCase()) + '-' + String(++billNo).padStart(4, '0');
  const total = lines.reduce((s, l) => s + l.amount, 0);
  ev({ kind: 'bill', date, vendor, number, lines, total });
  const pd = clampDate(addDays(date, lag));
  if (pd) ev({ kind: 'billpay', date: pd, vendor, amount: total, applyTo: number, method: pick(['ACH', 'Check']), ref: String(rint(2000, 9999)) });
}

// Opening position. The loan is sized so that it always ends the period with a $6,000 balance, however many years are generated.
const LOAN_PRINCIPAL = 40000; // per month
const loanOpen = 600000 + LOAN_PRINCIPAL * monthsList.length;
const openAssets = 3000000 + 1000000 + 600000;
ev({ kind: 'journal', date: OPEN, memo: 'Opening balances', lines: [['Checking', 3000000, 0], ['Savings', 1000000, 0], ['Equipment', 600000, 0], ['Loans Payable', 0, loanOpen], ["Owner's Equity", 0, openAssets - loanOpen]] });

let loanBal = loanOpen;
const assetList = [{ name: 'Studio equipment (opening)', cost_cents: 600000, salvage_cents: 0, in_service: START, life_months: 84 }];
monthsList.forEach((m, mi) => {
  const y = m.slice(0, 4), mo = m.slice(5, 7), yi = Math.floor(mi / 12);
  const day = (n) => `${y}-${mo}-${String(n).padStart(2, '0')}`;
  // prices and costs were lower in earlier years: today's amounts scaled back by compound growth (prices 5%/yr, costs 4%/yr)
  const revScale = Math.pow(1.05, yi - (YEARS - 1)), costScale = Math.pow(1.04, yi - (YEARS - 1));
  const rev = (c) => round5(c * revScale);
  const cost = (c) => Math.round(c * costScale);
  const active = CUSTOMERS.filter((c) => mi >= c.from && (c.until === null || mi <= c.until));
  // retainers on the 1st; mostly paid within a month, an occasional late payer
  for (const [ci, cents, from, until] of RETAINERS) {
    if (mi < from || (until !== undefined && mi > until) || !CUSTOMERS.some((c) => c.idx === ci)) continue;
    const late = rand() < 0.12;
    invoice(day(1), ALL_CUSTOMERS[ci], [{ desc: `Monthly design retainer - ${m.slice(0, 7)}`, item: 'Monthly retainer', qty: 1000, unit: rev(cents), account: 'Retainer Revenue' }], [[late ? rint(45, 70) : rint(8, 32), 1]]);
  }
  // projects: more of them as the studio grows; quieter in August and December
  const seasonal = mo === '08' || mo === '12' ? -1 : 0;
  const nProjects = Math.max(1, rint(1 + Math.min(yi, 1), 3 + Math.min(yi, 1)) + seasonal);
  for (let p = 0; p < nProjects; p++) {
    const pool = active.filter((c) => !RETAINERS.some(([x]) => x === c.idx) || rand() < 0.4);
    const c = pick(pool.length ? pool : active); const d = day(rint(3, 24));
    const kind = rint(1, 4);
    const lines = kind === 1 ? [{ desc: `Website design - ${c.name}`, item: 'Website design (project)', qty: 1000, unit: rev(rint(380000, 650000)), account: 'Design Services' }, { desc: 'Hosting setup', item: 'Hosting setup', qty: 1000, unit: rev(30000), account: 'Design Services' }]
      : kind === 2 ? [{ desc: 'Brand identity package', item: 'Brand identity package', qty: 1000, unit: rev(rint(180000, 320000)), account: 'Design Services' }]
        : kind === 3 ? [{ desc: 'Consulting (hourly)', item: 'Consulting (hourly)', qty: rint(4, 22) * 500, unit: rev(9500), account: 'Design Services' }]
          : [{ desc: 'Copywriting (per page)', item: 'Copywriting (per page)', qty: rint(3, 9) * 1000, unit: rev(18000), account: 'Design Services' }];
    const r = rand(), recent = d > recentCut;
    const normalLag = rint(c.terms - 10 > 5 ? c.terms - 10 : 5, c.terms + 25);
    // only recent invoices can still be outstanding; older ones were eventually collected (some slowly)
    invoice(d, c, lines, recent && r < 0.3 ? [] : r < 0.15 ? [[rint(20, 30), 0.5], [rint(50, 80), 1]] : r < 0.27 ? [[rint(60, 110), 1]] : [[normalLag, 1]]);
  }
  // recurring bills (paid after terms)
  const V = (k) => VENDORS.find((v) => v.kind === k);
  bill(day(1), V('rent'), [{ desc: `Studio rent ${m.slice(0, 7)}`, account: 'Rent', amount: round5(cost(185000)) }], 2);
  bill(day(5), V('hosting'), [{ desc: 'Hosting & domains', account: 'Hosting & Domains', amount: cost(7900) + rint(0, 3) * 500 }], 10);
  bill(day(10), V('insurance'), [{ desc: 'Business insurance', account: 'Insurance', amount: round5(cost(32000)) }], 20);
  bill(day(12), V('phone'), [{ desc: 'Internet & phone', account: 'Telephone & Internet', amount: cost(14000) + rint(0, 4) * 100 }], 15);
  if (mi % 3 === 2) bill(day(20), V('accounting'), [{ desc: 'Quarterly bookkeeping & tax prep', account: 'Professional Fees', amount: round5(cost(45000)) }], 25);
  for (let s = 0; s < (rand() < 0.35 ? 2 : 1); s++) bill(day(rint(6, 22)), V('sub'), [{ desc: pick(['Illustration work', 'Front-end development', 'Photography', 'Copy editing']), account: 'Subcontractors', amount: round5(rint(60000, 260000) * revScale * 1.1) }], rint(20, 40));
  if (rand() < 0.5) bill(day(rint(8, 20)), V('office'), [{ desc: 'Office supplies', account: 'Office Supplies', amount: rint(2500, 16000) }], 14);
  if (mo === '11') { const amt = round5(cost(rint(180000, 360000))); const what = pick(['Laptops and displays', 'Workstation upgrade', 'Camera and lighting kit']); assetList.push({ name: `${what} (${y})`, cost_cents: amt, salvage_cents: 0, in_service: day(14), life_months: 84 }); bill(day(14), V('equip'), [{ desc: what, account: 'Equipment', amount: amt }], 25); }
  // monthly journals: payroll, payroll-liability remittance, loan payment, depreciation, owner draw
  const grossFor = (idx) => round5(Math.round(950000 * Math.pow(1.04, Math.floor(idx / 12) - (YEARS - 1)) * Math.pow(1.10, Math.floor(idx / 12) - (YEARS - 1))));
  const gross = grossFor(mi), liab = Math.round(gross * 0.154);
  ev({ kind: 'journal', date: day(28), memo: `Payroll ${m.slice(0, 7)}`, lines: [['Wages', gross, 0], ['Checking', 0, gross - liab], ['Payroll Liabilities', 0, liab]] });
  if (mi > 0) { const prev = Math.round(grossFor(mi - 1) * 0.154); ev({ kind: 'journal', date: day(15), memo: 'Payroll tax remittance', lines: [['Payroll Liabilities', prev, 0], ['Checking', 0, prev]] }); }
  const interest = Math.round((loanBal * 0.06) / 12); loanBal -= LOAN_PRINCIPAL;
  ev({ kind: 'journal', date: day(5), memo: 'Loan payment', lines: [['Loans Payable', LOAN_PRINCIPAL, 0], ['Interest Expense', interest, 0], ['Checking', 0, LOAN_PRINCIPAL + interest]] });
  const monthEndDate = Assets.monthEnd(Number(y), Number(mo));
  const dep = assetList.reduce((s, a) => s + Assets.monthlyAmount(a, Assets.monthsThrough(a, monthEndDate)), 0);
  ev({ kind: 'journal', date: monthEndDate, memo: 'Monthly depreciation', lines: [['Depreciation Expense', dep, 0], ['Accumulated Depreciation', 0, dep]] });
  const draw = round5(cost(150000));
  ev({ kind: 'journal', date: day(28), memo: 'Owner draw', lines: [['Owner Draw', draw, 0], ['Checking', 0, draw]] });
  // card-style spending that only appears on the bank statement
  for (let k = 0; k < rint(8, 14); k++) { const [desc, account, lo, hi] = pick(OFFICE_BANK); ev({ kind: 'bank', date: day(rint(2, 27)), amount: -rint(lo, hi), desc, account, fitid: `D${++fitid}` }); }
  ev({ kind: 'bank', date: day(28), amount: -1200, desc: 'MONTHLY SERVICE FEE', account: 'Bank Fees', fitid: `D${++fitid}` });
  ev({ kind: 'bank', date: day(mo === '02' ? 28 : 29), amount: rint(900, 4200), desc: 'INTEREST PAID', account: 'Other Income', fitid: `D${++fitid}` });
});
const RANK = { journal: 0, invoice: 1, bill: 1, bank: 2, payment: 3, billpay: 3 };
const order = () => events.sort((a, b) => (a.date < b.date ? -1 : a.date > b.date ? 1 : RANK[a.kind] - RANK[b.kind]));
order();

// Keep cash believable: at each quarter-end, if checking exceeds ~$110k the owner takes a distribution back down to ~$70k.
{
  const effect = (e) => (e.kind === 'payment' ? e.amount : e.kind === 'billpay' ? -e.amount : e.kind === 'bank' ? e.amount : e.kind === 'journal' ? e.lines.reduce((s, [a, d, c]) => s + (a === 'Checking' ? d - c : 0), 0) : 0);
  let cash = 0, i = 0;
  const qEnds = []; for (let yy = Number(START.slice(0, 4)); yy <= 2026; yy++) for (const md of ['03-31', '06-30', '09-30', '12-31']) { const d = `${yy}-${md}`; if (d >= START && d <= END) qEnds.push(d); }
  const extra = [];
  for (const q of qEnds) {
    while (i < events.length && events[i].date <= q) cash += effect(events[i++]);
    if (cash > 11000000) { const amt = Math.floor((cash - 7000000) / 100000) * 100000; cash -= amt; extra.push({ kind: 'journal', date: q, memo: 'Owner distribution', lines: [['Owner Draw', amt, 0], ['Checking', 0, amt]] }); }
  }
  events.push(...extra); order();
}
const inRange = events.filter((e) => e.date <= END);

// ---------- writers: QuickBooks-style files ----------
const mdy = (d) => `${d.slice(5, 7)}/${d.slice(8, 10)}/${d.slice(0, 4)}`;
const T = (...c) => c.join('\t');
const money = (c) => formatMoney(c);
const csvq = (v) => (/[",\n]/.test(String(v)) ? `"${String(v).replace(/"/g, '""')}"` : String(v));
const acctType = { Checking: 'BANK', Savings: 'BANK', 'Accounts Receivable': 'AR', 'Accounts Payable': 'AP', Equipment: 'FIXASSET', 'Accumulated Depreciation': 'FIXASSET', 'Loans Payable': 'LTLIAB', 'Payroll Liabilities': 'OCLIAB', "Owner's Equity": 'EQUITY', 'Owner Draw': 'EQUITY', 'Design Services': 'INC', 'Retainer Revenue': 'INC', Subcontractors: 'COGS', 'Other Income': 'EXINC', 'Interest Expense': 'EXEXP' };
const NUMBERS = Object.fromEntries([...NEW_ACCOUNTS.map(([n, num]) => [n, num]), ['Checking', '1000'], ['Savings', '1010'], ['Accounts Receivable', '1100'], ['Accounts Payable', '2000'], ['Equipment', '1500'], ['Accumulated Depreciation', '1590'], ['Loans Payable', '2500'], ['Payroll Liabilities', '2300'], ["Owner's Equity", '3000'], ['Rent', '6060'], ['Insurance', '6020'], ['Telephone & Internet', '6090'], ['Professional Fees', '6050'], ['Office Supplies', '6040'], ['Software & Subscriptions', '6080'], ['Meals', '6030'], ['Travel', '6100'], ['Advertising', '6000'], ['Bank Fees', '6010'], ['Wages', '6200'], ['Interest Expense', '7000'], ['Other Income', '4900']]);

function iifFile() {
  const used = new Set(['Accounts Receivable', 'Accounts Payable']);
  for (const e of inRange) { if (e.kind === 'journal') e.lines.forEach((l) => used.add(l[0])); if (e.kind === 'bank') { used.add(e.account); } if (e.kind === 'invoice') e.lines.forEach((l) => used.add(l.account)); if (e.kind === 'bill') e.lines.forEach((l) => used.add(l.account)); }
  used.add('Checking');
  const L = [T('!ACCNT', 'NAME', 'ACCNTTYPE', 'ACCNUM', 'DESC')];
  for (const n of [...used].sort()) L.push(T('ACCNT', n, acctType[n] || 'EXP', NUMBERS[n] || '', ''));
  L.push(T('!CUST', 'NAME', 'BADDR1', 'BADDR2', 'EMAIL', 'PHONE1', 'TERMS'));
  for (const c of CUSTOMERS) L.push(T('CUST', c.name, c.contact, c.address, c.email, c.phone, `Net ${c.terms}`));
  L.push(T('!VEND', 'NAME', 'BADDR1', 'EMAIL', 'PHONE1', 'TERMS', 'VEND1099'));
  for (const v of VENDORS) L.push(T('VEND', v.name, v.address, v.email, v.phone, `Net ${v.terms}`, v.is1099 ? 'Y' : 'N'));
  L.push(T('!INVITEM', 'NAME', 'INVITEMTYPE', 'DESC', 'PRICE', 'ACCNT'));
  for (const [n, , p, a] of ITEMS) L.push(T('INVITEM', n, 'SERV', n, money(p), a));
  L.push(T('!TRNS', 'TRNSTYPE', 'DATE', 'ACCNT', 'NAME', 'AMOUNT', 'DOCNUM', 'MEMO'), T('!SPL', 'TRNSTYPE', 'DATE', 'ACCNT', 'NAME', 'AMOUNT', 'DOCNUM', 'MEMO'), T('!ENDTRNS'));
  const grp = (type, date, first, rest) => { L.push(T('TRNS', type, mdy(date), ...first)); for (const r of rest) L.push(T('SPL', type, mdy(date), ...r)); L.push(T('ENDTRNS')); };
  for (const e of inRange) {
    if (e.kind === 'invoice') grp('INVOICE', e.date, ['Accounts Receivable', e.cust.name, money(e.total), e.number, ''], e.lines.map((l) => [l.account, e.cust.name, money(-l.amount), e.number, l.desc]));
    else if (e.kind === 'payment') grp('PAYMENT', e.date, ['Checking', e.cust.name, money(e.amount), e.ref, `Payment for ${e.applyTo}`], [['Accounts Receivable', e.cust.name, money(-e.amount), e.ref, '']]);
    else if (e.kind === 'bill') grp('BILL', e.date, ['Accounts Payable', e.vendor.name, money(-e.total), e.number, ''], e.lines.map((l) => [l.account, e.vendor.name, money(l.amount), e.number, l.desc]));
    else if (e.kind === 'billpay') grp('CHECK', e.date, ['Checking', e.vendor.name, money(-e.amount), e.ref, `Payment for ${e.applyTo}`], [['Accounts Payable', e.vendor.name, money(e.amount), e.ref, '']]);
    else if (e.kind === 'bank') grp(e.amount < 0 ? 'CHECK' : 'DEPOSIT', e.date, ['Checking', '', money(e.amount), '', e.desc], [[e.account, '', money(-e.amount), '', e.desc]]);
    else if (e.kind === 'journal') grp('GENERAL JOURNAL', e.date, [e.lines[0][0], '', money(e.lines[0][1] - e.lines[0][2]), '', e.memo], e.lines.slice(1).map((l) => [l[0], '', money(l[1] - l[2]), '', e.memo]));
  }
  return L.join('\r\n') + '\r\n';
}

function ofxFile() {
  const rows = inRange.filter((e) => e.kind === 'bank' && e.date >= BANK_FEED_FROM);
  const dt = (d) => d.replace(/-/g, '') + '120000';
  const trn = rows.map((e) => `<STMTTRN><TRNTYPE>${e.amount < 0 ? 'DEBIT' : 'CREDIT'}<DTPOSTED>${dt(e.date)}<TRNAMT>${money(e.amount)}<FITID>${e.fitid}<NAME>${e.desc.replace(/&/g, '&amp;')}</STMTTRN>`).join('\n');
  return `OFXHEADER:100\nDATA:OFXSGML\nVERSION:102\nSECURITY:NONE\nENCODING:USASCII\nCHARSET:1252\nCOMPRESSION:NONE\nOLDFILEUID:NONE\nNEWFILEUID:NONE\n\n<OFX>\n<SIGNONMSGSRSV1><SONRS><STATUS><CODE>0<SEVERITY>INFO</STATUS><DTSERVER>${dt(END)}<LANGUAGE>ENG<FI><ORG>Granite State Demo Bank<FID>0001</FI></SONRS></SIGNONMSGSRSV1>\n<BANKMSGSRSV1><STMTTRNRS><TRNUID>1<STATUS><CODE>0<SEVERITY>INFO</STATUS>\n<STMTRS><CURDEF>USD<BANKACCTFROM><BANKID>000000000<ACCTID>00001234567<ACCTTYPE>CHECKING</BANKACCTFROM>\n<BANKTRANLIST><DTSTART>${dt(BANK_FEED_FROM)}<DTEND>${dt(END)}\n${trn}\n</BANKTRANLIST>\n</STMTRS></STMTTRNRS></BANKMSGSRSV1></OFX>\n`;
}

function csvFiles(tbRows) {
  const accounts = ['Account #,Account name,Type,Detail type,Description'];
  const qbType = { income: 'Income', cogs: 'Cost of Goods Sold', expense: 'Expenses', equity: 'Equity' };
  for (const [n, num, sub] of NEW_ACCOUNTS) accounts.push([num, n, qbType[sub], '', 'Added for the demo company'].map(csvq).join(','));
  const customers = ['Customer,Company,Email,Phone Numbers,Street,City,State,ZIP,Terms'];
  for (const c of CUSTOMERS) { const m = c.address.match(/^(.*), ([^,]+), ([A-Z]{2}) (\d{5})$/); customers.push([c.name, c.name, c.email, c.phone, m[1], m[2], m[3], m[4], `Net ${c.terms}`].map(csvq).join(',')); }
  const vendors = ['Vendor,Company,Email,Phone,Street,City,State,ZIP,Terms,Track payments for 1099'];
  for (const v of VENDORS) { const m = v.address.match(/^(.*), ([^,]+), ([A-Z]{2}) (\d{5})$/); vendors.push([v.name, v.name, v.email, v.phone, m[1], m[2], m[3], m[4], `Net ${v.terms}`, v.is1099 ? 'Yes' : 'No'].map(csvq).join(',')); }
  const items = ['Product/Service,Type,Sales description,Sales price,Income account'];
  for (const [n, , p, a] of ITEMS) items.push([n, 'Service', n, money(p), a].map(csvq).join(','));
  const opening = events.find((e) => e.kind === 'journal' && e.date === OPEN);
  const tbOpen = ['Account,Debit,Credit']; for (const [a, d, c] of opening.lines) tbOpen.push([a, d ? money(d) : '', c ? money(c) : ''].map(csvq).join(','));
  const tbEnd = ['Account,Debit,Credit']; for (const r of tbRows) tbEnd.push([r.name, r.debit_cents ? money(r.debit_cents) : '', r.credit_cents ? money(r.credit_cents) : ''].map(csvq).join(','));
  return { 'accounts.csv': accounts, 'customers.csv': customers, 'vendors.csv': vendors, 'items.csv': items, [`trial-balance-opening-${OPEN}.csv`]: tbOpen, 'trial-balance-expected-2026-09-30.csv': tbEnd };
}

// ---------- load the same activity into a database ----------
function populate(db, { leaveForReview }) {
  const ctx = { user: { id: null, username: 'demo-seed' }, ip: '' };
  for (const [name, number, subtype] of NEW_ACCOUNTS) D.createAccount(db, ctx, { name, number, subtype });
  const acc = (n) => { const r = db.prepare('SELECT id FROM accounts WHERE name=?').get(n); if (!r) throw new Error('missing account ' + n); return r.id; };
  const cid = {}, vid = {}, iid = {};
  for (const c of CUSTOMERS) cid[c.name] = D.createContact(db, ctx, { kind: 'customer', name: c.name, email: c.email, phone: c.phone, address: c.address, terms_days: c.terms });
  for (const v of VENDORS) vid[v.name] = D.createContact(db, ctx, { kind: 'vendor', name: v.name, email: v.email, phone: v.phone, address: v.address, terms_days: v.terms, is_1099: v.is1099 });
  for (const [n, kind, price, a] of ITEMS) iid[n] = D.createItem(db, ctx, { name: n, kind, unit_price_cents: price, income_account_id: acc(a) });
  const docId = {};
  const checking = acc('Checking');
  for (const e of inRange) {
    if (e.kind === 'journal') D.createJournal(db, ctx, { date: e.date, memo: e.memo, lines: e.lines.map(([a, d, c]) => ({ account_id: acc(a), debit_cents: d, credit_cents: c })) });
    else if (e.kind === 'invoice') docId[e.number] = D.createDocument(db, ctx, { kind: 'invoice', contact_id: cid[e.cust.name], doc_date: e.date, number: e.number, lines: e.lines.map((l) => ({ item_id: iid[l.item], description: l.desc, qty_milli: l.qty, unit_price_cents: l.unit, account_id: acc(l.account) })) });
    else if (e.kind === 'bill') docId[e.number] = D.createDocument(db, ctx, { kind: 'bill', contact_id: vid[e.vendor.name], doc_date: e.date, number: e.number, lines: e.lines.map((l) => ({ description: l.desc, qty_milli: 1000, unit_price_cents: l.amount, account_id: acc(l.account) })) });
    else if (e.kind === 'payment') D.createPayment(db, ctx, { kind: 'customer', contact_id: cid[e.cust.name], pay_date: e.date, amount_cents: e.amount, account_id: checking, method: e.method, ref: e.ref, applications: [{ document_id: docId[e.applyTo], amount_cents: e.amount }] });
    else if (e.kind === 'billpay') D.createPayment(db, ctx, { kind: 'vendor', contact_id: vid[e.vendor.name], pay_date: e.date, amount_cents: e.amount, account_id: checking, method: e.method, ref: e.ref, applications: [{ document_id: docId[e.applyTo], amount_cents: e.amount }] });
    else if (e.kind === 'bank' && e.date < BANK_FEED_FROM) D.createJournal(db, ctx, { date: e.date, memo: e.desc, lines: e.amount < 0 ? [{ account_id: acc(e.account), debit_cents: -e.amount }, { account_id: checking, credit_cents: -e.amount }] : [{ account_id: checking, debit_cents: e.amount }, { account_id: acc(e.account), credit_cents: e.amount }] });
  }
  for (const a of assetList) D_assets.createAsset(db, ctx, { ...a, depreciated_through: Assets.monthEnd(2026, 9) });
  // the most recent statement arrives through the bank-import path, partly categorized by rules, the rest left "to review"
  const feed = inRange.filter((e) => e.kind === 'bank' && e.date >= BANK_FEED_FROM);
  D.addBankTransactions(db, checking, feed.map((e) => ({ date: e.date, amount_cents: e.amount, description: e.desc, fitid: e.fitid })));
  for (const [name, match, account] of [['Bank fees', 'service fee', 'Bank Fees'], ['Fieldnote', 'fieldnote software', 'Software & Subscriptions'], ['Paperworks', 'paperworks', 'Office Supplies'], ['Interest', 'interest paid', 'Other Income']]) D.createRule(db, ctx, { name, match_text: match, account_id: acc(account) });
  D.applyRules(db, ctx);
  if (!leaveForReview) { // verification mode: categorize the rest so the ledger is comparable with the IIF import
    for (const t of db.prepare("SELECT * FROM bank_transactions WHERE status='new'").all()) D.categorizeBank(db, ctx, t.id, { account_id: acc(feed.find((e) => e.fitid === t.fitid).account) });
  }
}

// ---------- run ----------
fs.mkdirSync(outFiles, { recursive: true });
const limits = { maxRows: 200000 };

// 1) verification database (everything categorized) -> expected trial balance
const verifyDb = openDb(':memory:');
populate(verifyDb, { leaveForReview: false });
const tbExpected = R.trialBalance(verifyDb, { asof: END });

// 2) write the QuickBooks-style files
fs.writeFileSync(path.join(outFiles, 'transactions.iif'), iifFile());
fs.writeFileSync(path.join(outFiles, 'bank-statement-2026-Q3.qbo'), ofxFile());
for (const [name, lines] of Object.entries(csvFiles(tbExpected.rows))) fs.writeFileSync(path.join(outFiles, name), lines.join('\r\n') + '\r\n');

// 3) self-check: convert the IIF file into a fresh database and compare to the natively-built ledger
const viaIif = openDb(':memory:');
C.commit(viaIif, { user: { id: null, username: 'check' }, ip: '' }, C.parse('iif', fs.readFileSync(path.join(outFiles, 'transactions.iif'), 'utf8'), {}, limits));
const key = (tb) => JSON.stringify(tb.rows.map((r) => [r.name, r.debit_cents, r.credit_cents]));
const same = key(R.trialBalance(viaIif, { asof: END })) === key(tbExpected);
if (!same || !tbExpected.balanced || !checkLedgerIntegrity(verifyDb).ok) { console.error('SELF-CHECK FAILED: native ledger and IIF conversion disagree'); process.exit(1); }

// 4) the demo database people will actually open (no users: first launch creates the administrator)
fs.rmSync(outDb, { recursive: true, force: true });
const demo = openDb(path.join(outDb, 'ledjoro.db'));
demo.prepare("UPDATE settings SET value='Birchwood Design Studio LLC' WHERE key='company_name'").run();
populate(demo, { leaveForReview: true });
const bs = R.balanceSheet(demo, { asof: END }), pl = R.profitLoss(demo, { from: '2026-01-01', to: END });
const dash = R.dashboard(demo);
const stats = Object.fromEntries(['journal_entries', 'journal_lines', 'documents', 'payments', 'contacts', 'bank_transactions', 'accounts'].map((t) => [t, demo.prepare(`SELECT COUNT(*) n FROM ${t}`).get().n]));
const ar = R.aging(demo, { kind: 'ar', asof: END }), ap = R.aging(demo, { kind: 'ap', asof: END });
demo.close();

fs.writeFileSync(path.join(outFiles, 'README.md'), `# Demo data (synthetic)

Fictional company **Birchwood Design Studio LLC**, ${YEARS} year${YEARS > 1 ? 's' : ''} (${START} to ${END}). All names, amounts and addresses are invented;
domains use the reserved \`.example\` TLD. Generated by \`scripts/make-demo-data.js\` (seeded, reproducible).

| File | Format id | Try it |
|---|---|---|
| \`accounts.csv\` | \`csv-accounts\` | Import & convert → chart of accounts additions |
| \`customers.csv\`, \`vendors.csv\`, \`items.csv\` | \`csv-customers\` / \`csv-vendors\` / \`csv-items\` | contact & item lists |
| \`trial-balance-opening-${OPEN}.csv\` | \`csv-trialbalance\` (as-of ${OPEN}) | opening balances for a new company |
| \`transactions.iif\` | \`iif\` | **full year of history** (invoices, payments, bills, checks, payroll, loan, draws) — import into a fresh company |
| \`bank-statement-2026-Q3.qbo\` | \`ofx\` | Jul–Sep card/bank activity; choose the *Checking* account when importing |
| \`trial-balance-expected-2026-09-30.csv\` | — | what the books should total after importing \`transactions.iif\` |

**Self-check performed at generation time:** the native demo ledger and the IIF conversion produce identical trial balances.
Note: the bank statement contains only activity that is *not* already in \`transactions.iif\`, so importing both does not double count.

Import order for a fresh company: accounts.csv → customers.csv → vendors.csv → items.csv → transactions.iif (→ bank statement).
`);
fs.writeFileSync(path.join(outDb, 'README.txt'), `Demo database for Ledjoro (synthetic data). It contains no user accounts.\nStart the app with LEDJORO_DATA pointing at this folder, then create your own administrator when prompted:\n  PowerShell:  $env:LEDJORO_DATA=(Resolve-Path demo-data); npm start\n  Standalone:  set LEDJORO_DATA=<full path to demo-data> then run Ledjoro.exe\nCopy the folder first if you want to keep it pristine.\n`);

console.log(`Demo company ready.
  rows: ${JSON.stringify(stats)}
  balance sheet balances: ${bs.balanced}; net income 2026 YTD: $${formatMoney(pl.net_income_cents)}; cash $${formatMoney(dash.cash_cents)}
  A/R outstanding $${formatMoney(ar.totals.total_cents)} across ${ar.rows.length} customers; A/P outstanding $${formatMoney(ap.totals.total_cents)}
  bank transactions waiting for review: ${dash.bank_to_review}
  self-check (native ledger == IIF conversion): PASS
  files: ${path.relative(root, outFiles)}  database: ${path.relative(root, outDb)}`);
