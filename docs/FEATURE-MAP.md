# Feature map: market-leading Advanced-tier accounting capabilities → Ledjoro

Built from the research in `docs/research/quickbooks-advanced-reference.md`. We implement the **capabilities** (not Intuit’s expression of them). Status key: ✔ done · ◐ partial · ○ planned · ✖ out of scope by design.

## Core accounting
| Capability | Status | Notes |
|---|---|---|
| Double-entry general ledger, chart of accounts, journal entries | ✔ | Integer cents; immutable entries; reversals |
| Closing date / period lock | ✔ | Settings → Books closed through |
| Unlimited accounts / classes / locations | ◐ | Unlimited accounts ✔. **Classes/locations/tags** (dimensions on journal lines + reports by dimension) ○ |
| Multi-currency | ○ | Single currency (USD display) today |
| Fixed assets & depreciation | ✔ | Register, straight-line schedules, monthly posting, disposal with gain/loss (QuickBooks Online has none) |
| Budgets, budget vs actual | ○ | First target for the reporting phase |
| Recurring transactions | ○ | |
| Reconciliation to statement | ○ | Bank import + categorize ✔; statement-ending-balance reconcile ○ |

## Sales & receivables
| Capability | Status | Notes |
|---|---|---|
| Customers, items, invoices, payments, voids | ✔ | |
| Estimates → invoices, progress invoicing | ○ | |
| Credit memos, refunds, customer deposits | ○ | Overpayments can be left as unapplied credit on A/R |
| Online payment links / card & ACH processing | ✖ for core | Would require a payment-processor integration (opt-in plug-in) |
| Late fees, reminders, recurring invoices | ○ | Needs a scheduler + e-mail (opt-in) |
| Sales tax | ○ | Manual via accounts today |
| PDF/e-mail invoices | ◐ | Print view ✔; PDF/e-mail ○ |

## Purchases & payables
| Capability | Status | Notes |
|---|---|---|
| Vendors, bills, bill payments, 1099 flag | ✔ | |
| Purchase orders, vendor credits | ○ | |
| Bill approval workflows (amount/vendor conditions, multi-level) | ○ | Roles exist; approval states need schema work |
| 1099 reporting/e-file | ○ | Report first; e-file is a regulated service ✖ |
| ACH bill-pay disbursement | ✖ for core | Money movement needs a licensed provider |

## Banking
| Capability | Status | Notes |
|---|---|---|
| Statement import (OFX/QFX/QBO), de-duplication | ✔ | |
| Categorize / exclude / rules / “always do this” | ✔ | Match-text rules; condition builder ○ |
| Live bank feeds (aggregators) | ✖ for core | Optional connector; privacy review needed |
| Match to existing invoices/bills | ○ | |
| Receipt capture / OCR | ○ | Local OCR plug-in idea |

## Reporting & analytics
| Capability | Status | Notes |
|---|---|---|
| P&L, balance sheet, trial balance, general ledger, A/R & A/P aging, sales by customer | ✔ | |
| Cash-flow statement | ○ | |
| 13-week cash forecast with scenarios | ✔ | From your own payment history; "typical" vs "already committed" |
| Books health check (duplicates, odd balances, gaps, stale items) | ✔ | Rule-based and explainable; no data leaves the machine |
| Report customization (columns, group-by, filters, saved reports) / custom report builder | ○ | Next major feature |
| KPI scorecards, dashboards with custom charts, benchmarks | ◐ | Fixed dashboard ✔; configurable KPIs ○; benchmarks ✖ (needs external data) |
| Management report packs (PDF, branding) | ○ | |
| Scheduled report e-mail | ○ | |
| Spreadsheet two-way sync | ◐ | CSV/JSON export ✔; import of edited sheets ○ (CSV importers exist) |

## Automation, AI
| Capability | Status | Notes |
|---|---|---|
| Rule-based auto-categorization | ✔ | |
| Workflow automation (triggers/conditions/actions, reminders, approvals) | ○ | Design: event bus + rule table; audit-logged |
| Batch transaction entry (grid, paste from spreadsheet) | ○ | CSV importers cover bulk loads |
| AI assistants (categorization, anomaly detection, conversational reports, forecasting) | ○ | Must be **opt-in, local-first or bring-your-own-model**; never send books to third parties by default |

## Projects & industry
| Capability | Status | Notes |
|---|---|---|
| Projects/job costing, profitability | ○ | |
| Construction: WIP, progress billing (AIA-style), change orders, retainage | ○ | Large module |
| Inventory (FIFO/average, reorder, sales/purchase orders) | ○ | |
| Time tracking, mileage | ○ | |
| Payroll, HR, workforce | ✖ for core | Regulated, jurisdiction-specific; integrate with a payroll provider instead |

## Platform
| Capability | Status | Notes |
|---|---|---|
| Users, roles (custom), permissions by area | ✔ | Location-restricted permissions ○ (needs dimensions) |
| Audit log | ✔ | Hash-chained, verifiable |
| Backup & restore | ✔ | Encrypted, integrity-checked; one click each in the app (restore saves a safety copy first); CLI and `Ledjoro.exe --restore` also available |
| Undo an import | ✔ | Reversal-based, history preserved; re-import afterwards |
| Lost-password recovery without a vendor account | ✔ | One-use recovery key + printable sign-in sheet |
| Light/dark themes | ✔ | Black default, toggle top-right |
| Data portability (full export) | ✔ | OBX JSON + CSV |
| QuickBooks-format conversion | ✔ | IIF, OFX/QBO, qbXML, QBO JSON, CSV |
| MFA / SSO / passkeys | ○ | Top security priority |
| API & webhooks, app marketplace | ○ | Versioned REST API for integrations |
| Mobile app | ○ | Not offered: Ledjoro is a native Windows program |
| Accountant access (multi-client console) | ○ | Roles support an accountant user today |

## Where Ledjoro already goes beyond the market leader
Gaps users commonly complain about, and what we did about them: no real backup/restore in the online product (**encrypted backup**), imports that cannot be undone (**undo import**), no fixed-asset manager online (**asset register**), thin audit trail (**hash-chained audit log + verify**), cash forecasting reserved for top tiers (**free 13-week forecast**), expensive "books review" add-ons (**free books health check**), per-user pricing and vendor lock-in (**open format export, no per-seat fees**).

## Suggested build order
1. **Security hardening:** TOTP/passkeys, rate-limit persistence, optional SQLCipher.
2. **Accounting depth:** dimensions (classes/locations), reconciliation, recurring entries, credit memos, estimates, cash-flow statement.
3. **Reporting:** configurable report builder and saved reports, budgets vs actuals, scheduled exports.
4. **Workflows:** approvals and reminders on an auditable rules engine.
5. **Projects/job costing**, then inventory, then construction add-ons.
6. **Opt-in integrations:** payment processors, bank aggregators, payroll, e-mail, AI.
