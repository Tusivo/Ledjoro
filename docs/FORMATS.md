# Formats

## Inputs (what each importer reads and how it maps)

| Format id | Source | Public basis | Becomes |
|---|---|---|---|
| `iif` | Intuit Interchange Format, tab-delimited | Documented text format: `!KEYWORD` header rows define columns; `ACCNT`, `CUST`, `VEND`, `INVITEM`, `TRNS`/`SPL`/`ENDTRNS` | accounts, contacts, items, **journal entries** (TRNS+SPL amounts must sum to 0; positive = debit) |
| `ofx` | `.QBO` / `.QFX` / `.OFX` bank & card statements | OFX 1.x (SGML) and 2.x (XML) open standard; QBO is OFX | bank transactions (`STMTTRN`: DTPOSTED, TRNAMT, FITID, NAME/MEMO) + statement metadata |
| `qbxml` | QuickBooks Desktop SDK XML responses | Published qbXML schema | accounts (`AccountRet`), customers/vendors (`CustomerRet`/`VendorRet`), items (`Item*Ret`); transaction records are counted and reported as not converted |
| `qbo-json` | QuickBooks Online API JSON (`QueryResponse`) | Published REST API entity shapes | accounts, customers, vendors, items, **JournalEntry** |
| `csv-accounts` | Chart-of-accounts export | CSV headers (`Account #`, `Account name`, `Type`, `Detail type`) | accounts (QuickBooks type names mapped to Ledjoro subtypes) |
| `csv-customers` / `csv-vendors` | Contact list exports | CSV headers (name/email/phone/address/terms) | contacts |
| `csv-items` | Products & services export | CSV headers | items |
| `csv-trialbalance` | Trial balance export + as-of date | CSV `Account, Debit, Credit` | one balanced opening-balance entry (any imbalance goes to *Opening Balance Equity*, with a warning) |
| `csv-register` | Account register export (QuickBooks Desktop *Register*, one account's history) + the register's account name | CSV `Date, Number, Payee, Account, Memo, Payment, C, Deposit, Balance` | one balanced entry per row between the register's account and the row's `Account`; missing accounts are created with a type guessed from use (listed in a warning); `Accounts Payable`/`Receivable` payees become vendors/customers; `-split-` rows go to *Uncategorized Split Transactions* (the export has no detail); the file's own `Balance` column is re-computed as a cross-check |
| `csv-mapped` | Any CSV you map yourself: drag each column onto *Date, Number, Payee, Account, Memo, Money out, Money in, Amount (+/-), Balance* | any layout | **with an Account column:** treated exactly like `csv-register` (needs the register's account name); **without one:** a bank statement, so every line waits under *Banking > To review* for the bank account chosen at import (duplicates are skipped on re-import). "Positive means money out" flips the sign for card statements; the first row can be skipped as a header |
| `obx` | Ledjoro Interchange Format | this document | everything below |

### Account type mapping
Bank→`bank` · Accounts receivable→`ar` · Other current assets→`other_current_asset` · Fixed assets→`fixed_asset` · Other assets→`other_asset` · Accounts payable→`ap` · Credit card→`credit_card` · Other current liabilities→`other_current_liability` · Long-term liabilities→`long_term_liability` · Equity→`equity` · Income→`income` · Cost of goods sold→`cogs` · Expenses→`expense` · Other income→`other_income` · Other expense→`other_expense`. IIF codes (`BANK, AR, OCASSET, FIXASSET, OASSET, AP, CCARD, OCLIAB, LTLIAB, EQUITY, INC, COGS, EXP, EXINC, EXEXP`) map the same way. `NONPOSTING` and unknown types are skipped with a warning.

### Import behaviour
1. **Parse** into the canonical model (never touches the database).
2. **Plan** (`/api/import/preview`): counts new vs existing records, unknown account references, unbalanced/invalid transactions, warnings.
3. **Commit** (`/api/import/commit`): single transaction. Existing accounts/contacts/items (matched by name or account number) are **never overwritten**. Journal entries are de-duplicated by content hash, so re-running an import is safe. Bank transactions are de-duplicated by `(account, FITID)`.
4. A/R and A/P lines carry the contact (`NAME`) so aging reports work on imported history. Tax IDs are ignored.
5. IIF dates are read as month/day/year by default; use `--date-order dmy` (CLI) or the *Date order* option (UI).

### Not supported (by design)
`.QBW`/`.QBB` company files; QuickBooks "Portable" files; payroll subledgers; inventory quantities/assemblies; sales-tax detail; attachments. Export those as CSV/IIF and import what is supported.

## Ledjoro Interchange Format (OBX) v1
Plain UTF-8 JSON. Amounts are **integer cents**. Dates are `YYYY-MM-DD`. Accounts are referenced by name.

```json
{
  "format": "ledjoro-interchange",
  "version": 1,
  "source": { "format": "iif" },
  "exported_at": "2026-10-08T12:00:00.000Z",
  "accounts":  [ { "number": "1000", "name": "Checking", "subtype": "bank", "description": "" } ],
  "contacts":  [ { "kind": "customer", "name": "Acme", "email": "", "phone": "", "address": "", "terms_days": 30, "is_1099": false } ],
  "items":     [ { "name": "Consulting", "kind": "service", "description": "", "unit_price_cents": 10000, "income_account": "Sales", "expense_account": "" } ],
  "journal_entries": [ { "date": "2026-01-15", "ref": "1001", "memo": "Widgets", "external_id": "…",
      "lines": [ { "account": "Accounts Receivable", "debit_cents": 125000, "credit_cents": 0, "contact": "Acme", "memo": "" },
                 { "account": "Sales", "debit_cents": 0, "credit_cents": 125000, "contact": "Acme", "memo": "" } ] } ],
  "bank_transactions": [ { "date": "2026-02-05", "amount_cents": -4567, "description": "OFFICE SUPPLY CO", "fitid": "20260205-001" } ],
  "bank_meta": null
}
```
`subtype` ∈ `bank, ar, other_current_asset, fixed_asset, other_asset, ap, credit_card, other_current_liability, long_term_liability, equity, retained_earnings, income, other_income, cogs, expense, other_expense`. Every entry must balance (Σdebit = Σcredit).

## Outputs
`GET /api/export/obx` (everything), `GET /api/export/csv/{accounts|contacts|journal|documents}`. CSV cells are formula-injection-safe.
