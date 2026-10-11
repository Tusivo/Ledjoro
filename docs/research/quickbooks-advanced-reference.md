# QuickBooks Online Advanced — Feature, Option & GUI Reference

*Compiled 2026-10-08. Focus: QuickBooks Online (QBO) **Advanced**, US edition, as of the August–September 2026 releases.*

---

## 0. Read this first: evidence and limits

I could not sign in to a live Advanced company (account creation/sign-in is not something I can do for you, and Intuit's "product tour" is a sales-call form). So **this is built from public Intuit sources, not from clicking through your own account.** Every statement carries a tag:

| Tag | Meaning |
|---|---|
| **[P]** | Stated on Intuit's public product/pricing/what's-new pages (fetched 2026-10-08) |
| **[H]** | Stated in an Intuit Help (learn-support) article, with menu paths and option names |
| **[V]** | Seen in an Intuit screenshot or demo animation I actually viewed (see §3) |
| **[T]** | Third-party source (accounting-firm blogs, release trackers) |
| **[K]** | My background knowledge of the product; **not verified this session**, may be outdated |

Other limits:
- **Not literally every feature/option.** Intuit ships continuously, rolls out in waves, and gates by region/role. Menu-level detail below is strongest for workflows, custom roles, KPIs, charts, custom report builder, custom fields, Spreadsheet Sync, projects and bill-approval; it is thin elsewhere.
- **Videos:** I could not play video. I extracted frames from four Intuit demo animations (stylised mock-ups, not live product) and read the accompanying page text. YouTube demos on Intuit's pages were not transcribed.
- **Dated visuals:** several Intuit marketing screenshots (the dark left-nav with "Overview / Dashboard / Banking / Invoicing / Workers…", the KPI Explorer wheel) are **older UI generations**. The current navigation is described in §2 from help text. Check your own account.
- **Prices:** Intuit's pricing page shows Advanced at **$340/mo list ($170/mo for 3 months at 50% off)**, 25 users, 30-day trial **[P]**. Renewals on/after **Aug 1 2026** changed Essentials/Plus/Advanced prices; Simple Start, Free, Lite, Ledger unchanged **[P]**. Third parties give conflicting before/after numbers; confirm at checkout.

---

## 1. What "Advanced" is (positioning and limits)

- Top tier of QBO, marketed as an "all-in-one AI platform" bringing books, bills, payments, payroll, time, HR, insights and industry workflows together; "replaces 8–12 separate tools" **[P]**.
- **25 users**, plus **3 accountant seats** (Plus has 5 users / 2 accountants) **[P][T]**.
- "No fees per user, app, ACH transfer, or Form 1099" per Intuit's page (with an asterisk: **+$0.50 per Bill Pay transaction over monthly allotment**; Bill Pay and Payments subject to eligibility/credit approval) **[P]**.
- Over **1,500 integrations** advertised **[P]**.
- **Unlimited** chart of accounts entries (other plans cap at 250), **unlimited classes and locations** (other plans: 40 combined), unlimited KPIs **[P][T]**.
- Intuit points out-growers to **Intuit Enterprise Suite (IES)** — an AI ERP for multi-entity businesses; IES-only items (dimensions, consolidated KPI tabs, units of measure on invoices, etc.) are marked below **[P]**.

### Included with Advanced (headline list from Intuit's page **[P]**)
Intuit Intelligence · intelligent onboarding with expert-guided setup (NEW) · AI Chat for instant insights (BETA, limited capacity) · Project Management AI · continuous books-quality checks with quarterly insights (NEW) · Finance AI · customizable dashboards with personalized KPIs · unlimited classes/locations/custom KPIs · **free 1099 e-filing and approval workflows with Bill Pay Elite (NEW)** · **Construction financials (NEW)** · **Advanced project financials (NEW)** · custom user permissions and access · workflow automation · Excel data sync · batch invoices and expenses · priority support and training.

### What changed in August 2026 (Advanced-specific) **[P][T]**
1. **Bill Pay Elite now included** (was a $540/yr add-on).
2. **Construction financials** and **Professional-services industry solution** included (construction was a paid add-on).
3. **Reporting & analytics suite**: redesigned KPI scorecards, management reports, custom dashboards, custom report builder, budget/forecast tools, conversational forecasting, two-way spreadsheet sync.
4. **Books Upkeep** (expert-backed "continuously clean books").
5. **Inventory**: sales orders with stock reservation, auto-PO at reorder point, item receipts, moving-average cost.
6. **Finance AI** monthly narratives and KPI anomaly alerts.
7. **QuickBooks Workforce** data surfaced in financial reports; **HR Manager** role.

---

## 2. GUI notes — layout and navigation

### 2.1 Current navigation ("QuickBooks on the Intuit Platform") **[H]**
Left navigation (hover for pop-up submenus; some items open only on click):
1. **+ Create** — opens a new transaction, project, task or goal.
2. **Bookmarks** — pages you saved; has an **Edit** (pencil) function and "bookmark this page."
3. **Home** — widgets for planning, budgeting, monitoring; **Add or remove widgets**.
4. **Feed** — AI-generated summary of recent transactions and the state of your finances.
5. **Reports** — all reports; Advanced adds **KPIs**, **Performance center**, **Custom reports**, management reports.
6. **All apps** — task hubs and services grouped by category (Accounting, Sales, Customers, Time, Projects, etc.). **Accounting, Sales, Customers** are pinned by default; **Customize** to pin others.
- **Intuit Intelligence chat bar** (AI "ask anything" bar) is part of the shell **[H][P]**.
- **Settings (gear):** *Account and settings*, *Manage users*, *Manage workflows*, *Custom fields*, etc. **[H]**
- Navigation paths quoted in help articles, e.g. **All apps → Sales & Get Paid → Sales transactions / Invoices**, **All apps → Projects → Projects**, **Expenses → Bill Payments → Pending Approvals** **[H]**. Menu labels differ between "classic" and "platform" views; not every company has been migrated **[K]**.

### 2.2 Older navigation visible in an Intuit screenshot **[V]** (≈2020 UI)
Dark left rail: **+ New**, Overview, Dashboard, Banking, Expenses, Invoicing, Projects (NEW badge), Workers, Reports, Taxes, Mileage, Accounting, My Accountant, Capital, Apps. Top bar: company name + logo, **Priority Circle** (premium-support entry), Help, Search, Notifications, Settings gear, **Privacy** toggle. Dashboard cards: **Revenue Streams** (line chart, period dropdown "Last year", right-hand **Filter by Product/Services** with "Top performers" list), **Bank accounts**, **Cash Flow** (bar/line, "Filter by Activity"), **Invoices** (unpaid/overdue/not-due and paid/not-deposited bars).

### 2.3 Visual style observed **[V]**
- Green primary buttons (**Save**, **Add task**, **Create workflow**, **Save and send**, **Select data to get**, **Export** all green); white cards with soft shadows; grey form fields; blue links ("Get all the details", "Clear calculations").
- AI surfaces use a **four-point blue "sparkle" icon** and a **blue-to-green gradient outline** on AI input bars/cards (Intuit Intelligence, Finance Agent, Business Tax AI cards).
- Right-hand slide-in panels for configuration (e.g., "Add custom field," "Customize report"), modals for confirmations; toast-style confirmations with a green check ("Your custom report has been saved.").
- Tables: bold uppercase column headers, row **Action** column with a text link plus ▾ (e.g., "Edit ▾"), kebab (⋮) menus for item actions **[V][H]**.

### 2.4 Screen-by-screen GUI notes (what I could see/read)

**Workflows** **[V][H]** — Page title **Workflows**; tabs **Templates | My workflows | History**; green **Create workflow** button; table columns NAME, TRANSACTION TYPE, CREATED, STATUS (Enabled/Disabled), ACTIONS (**Edit ▾**). Toast example: "Purchase order 1007 saved and auto approved." The workflow editor ("Custom workflow") is a two-step form: **1. When this happens** (condition row: field dropdown + operator/value dropdown, e.g., *Invoice payment status — is unpaid*; **+ Add another condition**) and **2. Do this in QuickBooks** (action field e.g. *Send reminder*, **Days** number box, **Time frame** dropdown e.g. *Before invoice is due*). Output example: a phone push notification "An invoice needs your attention." Bill-pay workflows are reached via a **lightning-bolt (⚡) icon → Templates tab** **[H]**.

**Tasks** **[V]** — Slide panel titled **Tasks**; owner filter pill (**Everyone ▾**), green **+ Add task**; groups **Overdue / Today / Upcoming** with DUE DATE column; items like "Approve 2 invoices" and "Call John Smith" with ⋮ menu; **Give feedback** button at bottom. Workflow approvals create tasks here.

**Add a new role** **[V]** — Title "Add a new role", prompt "What can they access?"; sections with checkboxes/radios: **Banking** (☑ Bank deposits), **Sales** (No access / All sales transactions, products and services, and customers / Select the types…), **Expenses** (☑ All expense transactions, products and services, and customers — "Includes limited access to sales tax and currencies. Get all the details"), **Workers** (Payroll, workers' comp, benefits and payroll settings; includes payroll reports).

**Add custom field** **[V]** — Right panel "Add custom field — What do you want to track?" with three icon cards: **Customer info** (e.g., sales rep, customer ID), **Transaction info** (e.g., service date, shown on sales forms/purchase orders), **Vendor info** (e.g., vendor ID, vendor type; shown on expense forms). Underlying list shows TYPE column (Text, Dropdown list, Date…).

**Manage users** **[V][H]** — Title "Manage users"; tabs **Users (25)** and **Accounting firms (0)** with count badges; "Find by name" search; table columns NAME, EMAIL, … ; a **Roles** tab (Add role) per help text.

**Batch Transactions** **[V]** — Title "Batch Transactions"; controls **Select transaction type** (dropdown: Invoices / Checks / Expenses / Bills), **Action** (dropdown, e.g., Create), a running **TOTAL**; below is a spreadsheet-style grid with numbered rows and columns such as Email; a dashboard teaser shows an **Attachments** column ("22 bills" etc.) and **Save and send**. Advanced supports pasting from Excel; up to **1,000 transaction lines** per invoice upload **[P][H]**.

**Custom report builder** **[V][H]** — Report toolbar: **Group (n)**, **Filter (n)**, **Customize**, plus **Save** and **Export** (green). The **Customize report** right panel has **Rules** (Accounting method: Cash basis / Accrual basis radio; **Unrealized gain or loss** toggle) and **Columns** (search box "Search columns…", collapsible groups such as Sales, Expenses, Invoices with **Select all** and per-column checkboxes e.g. Invoice date, Transaction type; **Clear all**). A modal **Edit group calculations**: "Calculate [Percentage ▾] for [Amount ▾] columns" with **+ Add** / **Clear calculations**. Toast: "Your custom report has been saved."

**Performance center / charts** **[V][H]** — Cards: **Cash Flow** (area/line chart with hover tooltip listing Operating / Investing / Financing / Net Cashflow for the month; right panel "Filter to compare"; "Other parameters" with Operations, Investments, Financing, Free cash each showing ▲/▼ %), **Revenue Streams**, **Accounts Receivable** (donut, "As of today", top customers) and **Accounts Payable** (stacked bars by aging bucket 1–7, 8–14, 15–23 days). Each widget has a ⋮ menu (Edit widget / Edit chart / Delete chart) and mobile equivalent ("Performance center" screen on phone with period pills 1D 1W 1M 3M 1Y 5Y Custom, **See details**).

**Industry benchmark dashboard** **[P][H]** — Compare NPM (net profit margin) or GPM (gross profit margin) against 30+ similar businesses; chart picker options "NPM vs industry benchmarks" / "GPM vs industry benchmarks."

**KPI Scorecard** **[H]** — **Reports → KPIs** tab (Consolidated/Company tabs are IES-only). Controls: **Period** dropdown, **Compare with** dropdown, **Category** filter; rows show current period, comparison period, variance; **Action** column with expand + dropdown. **Manage KPIs** screen: category list, search, **+ Add to scorecard**, **X Remove from scorecard**, **Connect data source** (CRMs), **Done**. **Create custom KPI** form: name, description, category, unit (Currency / Percentage / Number), variance rule ("Higher values are better" / "Lower values are better"), calculation (**Build a formula** or **Enter manually**), chart type (Line / Bar). Older **KPI Explorer** [V]: radial wheel grouped by perspective (Asset usage, Liquidity, Coverage, Gearing, Cash flow, Growth, Profitability, Activity, Efficiency) with red ✕ / green ○ tiles and a centre "% on track" gauge (e.g., 67%, "30 KPIs", 10 off-track, 20 on-track). Library: **80+ KPIs** (some pages say 100+) **[H][P]**.

**Customer Hub** **[V]** — Funnel strip ("CUSTOMERS FUNNEL"): Open leads → Open estimates → Open contracts → Open projects → Open invoices → Reviews with counts and sub-status; "New leads — ready to review" card from connected sources (Gmail etc.) with est. value/status/summary/Actions; **Opportunity win rate** meter; customer **labels/types** (e.g., Customer type: Commercial, Sales rep, Industry, Revenue Stream); **Add your signature** e-sign dialog; **Reviews & Testimonials** card with star ratings and "Manage survey settings."

**Business Tax AI — Deduction maximizer** **[V]** — Card list with likelihood chips ("VERY LIKELY — Home office", Travel, Taxes & licenses); AI card "Gathering commonly overlooked deductions for businesses like yours."

**Finance Agent alert** **[V]** — Mini chart card "Expenses ↑15%" with gradient-outline popover "Finance Agent — Your expenses are up."

**Conversational forecasting** **[V]** — Chat bubble "Can I afford to hire 2 sales reps in Q3?" → answer "You can afford to. But it will reduce profitability margins by 0.5%…" with a comparison table (KPI | Baseline | +2 hires: Net income, Revenue, Payroll expense, Margin).

**Intuit Intelligence report creation** **[V]** (demo animation) — Input bar "How can I help today?" (sparkle icon, gradient outline). Prompt "Give me a report of outstanding invoices over $500 from customers for the current quarter" → answer card "I've created a report for you" with a summary (Period, Showing) and blue **View report** button → report "Outstanding Invoices – Q3" (Date, Customers, Due date, Amount, Balance, Service). Follow-up prompt "Group by customers and add a column for overdue days" produces a grouped version with an **Overdue** column and a "Report created ✔" confirmation.

**Bill approval flow** **[V]** (demo animation, roles shown as avatars) — Kelly (AP Clerk) fills **Bill #1050** (Vendor, Mailing address, Terms *Net 30*, Bill date, Due date, Product/Service lines: Monthly Website Maintenance $2,500; Marketing Design Retainer $2,000; Brand Support & Collateral $500; total $5,500) → **Submit for approval** → Candace (Approver) sees **Reject / Approve** → Zip (Payor) sees a bill list (Vendor, Bill amount, Bill approval ✔ Approved) with checkboxes and **Pay with check / Pay with free standard ACH** → "Bills approved and paid."

**Project creation with AI** **[V]** (demo animation) — **All Projects** list with status chips (IN PROGRESS, COMPLETED) and **New project** button → "Project Management AI" drop-zone for a contract document → "Review Project details" (Project name, Customer, Email, Start/End date, Project status, Billing address) plus **Profit margin goal (25%)** and a **Phases** table (Phase name, Description, Status, Start/End date; e.g., Demo & site prep, Foundation & concrete, Electrical rough-in, Plumbing) → **Save and close** → project page "Kitchen remodel" with **Manage project** / **Add to project** buttons, "Project balance summary" tiles **INCOME − COSTS = PROFIT** (actual vs estimated, goal), **BUDGET** % used with "remaining," **COST BREAKDOWN TOTAL** bar chart (Material, Equipment, Permits, Overhead), and **Project phases** list with task counts and **Mark complete**.

**Books Upkeep** **[V]** (demo animation) — **Bank transactions** list with tabs **Pending | Posted | Excluded** (date, amount, payee, category); summary card "What was done in June": *984 transactions posted · ~6 hrs saved this month · 2 reports created this month · 9 of 12 books status checks passed / Books quality checked by your expert*; "**June overview**" cards (Net income, Cash position, Cash flow with In/Out bar) ending "Books are up to date."

**Spreadsheet Sync (Excel add-in task pane)** **[V]** — "Build report" panel with a three-step stepper: **Company or group** (dropdown, e.g., "All Shops" — multi-company/group support) → **Data source** (QuickBooks report or table, e.g., "Profit and loss") → **Data to get** (filter) → green **Select data to get**; "How does it work?" link.

**Import invoices** **[V]** — Result screen "Import invoices" listing "15/15 invoices were added · 4 new customers were added · 5 new products/services were added."

**Backup & restore** **[V][P]** — Icon (clock with return arrow) for "Back up & restore your data" — automatic continuous backup, restore to a chosen date/time, version-history log with change counts.

**Invoice email (customer view)** **[V]** — Email with company logo, "INVOICE 1001 $2,800.00," green/blue **Pay online** button, and accepted-method icons (Apple Pay, PayPal, Venmo, bank, Visa, Mastercard, Discover, Amex).

---

## 3. Visual material actually reviewed

| Item | Type | Notes |
|---|---|---|
| bill-pay-automation animation | Video frames (8) | Bill → approve → pay flow, roles AP Clerk / Approver / Payor |
| product-management-phase-creation animation | Video frames (8) | Project Management AI from contract |
| expert-books-upkeep animation | Video frames (8) | Bank transactions + monthly summary |
| bi-conversational-reports animation | Video frames (8) | Natural-language report build and refine |
| ~25 stills from Intuit Advanced marketing pages | Screenshots | Workflows, Tasks, custom role/field, batch, report builder, Performance center cards, KPI Explorer, Customer Hub, forecasting, AI cards |
| Intuit Advanced landing page | Live page | Text and layout; product-tour video blocked by sales form |

Several clips are **play-button teasers** (the actual videos weren't retrievable), so UI shown is the opening frame only.

---

## 4. Feature catalogue (Advanced)

### 4.1 Intuit Intelligence & AI agents
- **Intuit Intelligence** — the umbrella AI layer: conversational "ask anything" bar; prompt-to-task; connects across QuickBooks and IES **[P][T]**. Chat is **admin-only, beta, with monthly capacity limits** **[T]**.
- **Create invoices via AI chat** (admin users, beta); can accept uploads (spreadsheets, project files, images); "Ask your accountant" suggestion creates a single-thread context **[T]**.
- **Custom Automations** (build via chat) **[H]**: needs Primary/Company Admin; describe in plain English ("email me a P&L summary every Friday", "draft reminder emails for invoices 3 days overdue") → review draft (schedule, trigger, output) → request modifications → **preview with live data** → confirm/save. **Automation Hub** inside Intuit Intelligence: list of active automations/status, next run, run history, pause/resume/edit, failure notifications. High-stakes (outward-facing) actions require **approve/edit of the draft** via email/push/in-app notification. All actions appear in the **Audit Log** as "<name> via Intuit Intelligence." Not available in Solopreneur or IES (IES uses workflows).
- **Conversational reports** — build/refine reports by prompt ([V] above).
- **Conversational forecasting / scenario planning** — plain-language "what-if" (hiring, price change), variance questions, cash flow projections; no export needed **[P][T][V]**.
- **Accounting AI / Agent** — categorizes and posts transactions continuously; reconciliation help; anomaly/duplicate detection; learns from your corrections; flags inconsistencies before month-end **[P][T]**.
- **Finance AI / Agent** — tailored insights, monthly performance narrative, KPI anomaly alerts, cash-flow trends, scenario planning **[P][T]**.
- **Project Management AI / Agent** — turns contracts into draft projects, generates estimates, allocates costs, monitors budgets and flags potential overruns early **[P][T][V]**.
- **Payments Agent** — drafts/sends invoices and reminders, predicts late payers, tracks AR **[T]**. **Invoicing on Autopilot** (beta; Essentials/Plus/Advanced) **[P]**.
- **Customer Agent / Customer Hub** — lead sourcing (Gmail beta), outreach drafts **[P][V]**.
- **Business Tax AI / Sales Tax AI** — deduction maximizer; discrepancy catching **[P][V]**.
- **Payroll Agent** — collects hours by text, validates, runs payroll **[T]** (payroll add-on).
- **Integration:** QuickBooks Payments connectors for **Claude and ChatGPT** (create estimates, invoices, payment links; draft approval before sending; real-time payment visibility) **[P/T: one8solutions summary of Intuit release]**.
- **Intelligent onboarding** with expert-guided setup **[P]**.

### 4.2 Books Upkeep & books quality
- **Books Upkeep**: Intuit Intelligence automatically posts **high-confidence transactions under customer-defined policies**; surfaces only exceptions needing input via a collaboration tool; month-end issue flagging; **expert-verified** posting **[P][T]**.
- Setup per help text **[H-ish via search]**: go to **Intuit Experts Hub → Schedule an appointment** for a mandatory **~45-minute setup call**; expert reviews your **posting policies**, runs **AI-assisted chart-of-accounts cleanup** (you approve hide/inactivate), then activates service. Ongoing: monthly categorization, monthly quality checks, **quarterly insights calls**.
- **Off by default** for companies with an attached accountant/bookkeeper; firm-billed clients controlled by the firm **[T]**.
- **Continuous books-quality checks** with quarterly insights; "books status checks" counter ([V] "9 of 12 checks passed") **[P][V]**.
- Auto-categorization improvements: flags duplicate/conflicting rules before application, faster suggestion refresh, better transfer and credit-card-payment handling; bank connection expiry warnings **[T]**.

### 4.3 Reporting & analytics suite
- **Report library**: standard reports (see baseline §6) plus Advanced-only tools below.
- **Custom Report Builder** **[H]**: **Reports → Custom reports**. Start with the **report-creation wizard** (search a report type) or **add data columns**. Panels: **Reorder** tab (drag), **More Columns** tab (check fields), **Banded rows**; **Filters** (data type → operation → values, multiple conditions); **Groups → Group by** up to **3 columns**; **Edit group calculations** (totals, averages, percentages); **Pivot** (rows/columns/Values, Show totals); number format (thousands, cents, zeros, currency symbols, up to 3 decimals), header/footer, "Show entity names in columns," gridlines, view options (text size/spacing/fonts, expand/collapse all); **Display columns by** Days/Weeks/Months/Calendar quarters (Weeks unavailable with unrealized gain/loss on); **Cash vs Accrual** toggle; **Chart View** (chart type, X/Y axes, **Split by**); **Excel export keeps formulas** for calculated rows/columns; auto-refresh with manual refresh icon; drill-downs keep settings. **Schedule report** (⋯ menu → name the workflow, recurrence, subject, message → *Save and turn on*; manage under Action → *View all schedules*). Dimension-based (class, location, department) free-form reporting **[P]**.
- **Management Reports** **[P][T]**: structured templates pulling **live KPIs, dashboards, financial summaries** into one branded package; **AI-drafted editable summaries**; cover/branding customization; settings reapplied each cycle; **recurring report automation**; PDF output. (Older UK help: view/edit management reports with cover page, executive summary, etc. **[K]**.)
- **KPI Scorecards (redesigned Aug 2026)** **[P][H]**: real-time financial **and non-financial** metrics; **filter by class**; **drill down** to source reports; larger charts and detail views; pre-built library (80+/100+); **custom formula-based metrics**; account-specific tracking; metrics flow into dashboards and management reports; CRM data sources (Salesforce, HubSpot, Monday, Excel, Google Sheets) via **Connect data source** **[P]**. Categories in QBO Advanced: **Growth, Profitability** (more in IES) **[H]**. Standard KPIs listed: Revenue, COGS, Total Expenses, Gross Profit, Gross Profit Margin, Net Profit, Net Profit Margin, Operating Expense, Net Operating Income, Operating Margin **[H]**.
- **Performance center (charts)** **[H]**: **Reports → Performance center**. **Quick add charts** (presets) or **+ Add new chart**: choose metric → name → chart type (**Trend line / Vertical bar / Pie**, by metric) → **Time period** → **Group by** (classes, vendors, locations…) → **Select from multiple** → **Filter by** (up to **10** filters) → **Compare** (Previous period / Previous year same period) → **Add to Dashboard**. Trackable metrics: Expenses, Revenue, Gross profit, Net profit, AR/AP, COGS, Cash flow, Current ratio, Quick ratio. **Max 25 charts**; edit via ⋮ → Edit widget/Edit chart; **admin access required**. Includes **Revenue Streams** dashboard (accrual/cash, fiscal year, date range, classes, locations; shareable with accountants), **Cash Flow** trend lines with drill-down, and **industry benchmark** dashboards **[P][H]**.
- **Customizable dashboards** with personalised KPIs, benchmarking vs time period/budget/target, drill to transactions, mobile access **[P]**.
- **13-week cash flow forecast**, P&L and balance sheet summaries, anomaly spotting **[T]**.
- **Two-way Spreadsheet Sync** **[H][P]**: Excel (Microsoft 365) and **Google Sheets** (not older Excel); admin account required; **Advanced or Intuit Accountant Suite only**; included in plan. Pull live reports and tables into Excel, edit, **post back** — bulk create/edit **invoices and bills** (incl. custom-field columns), **journal entries**, **budgets**; record/transaction **templates**; templates for P&L, trial balance, balance sheet, cash flow statement, A/P aging summary, sales reports; auto-refresh on open, quick refresh or edit filters; **company or group** selection for consolidated multi-company data. Upload of up to ~1,000 invoice lines per batch.
- **Google Sheets export** (one click) and sharing **[T]**.
- **Fathom / third-party BI** integrations **[K]**.
- **Workforce data in reports**: combine QuickBooks Workforce metrics (headcount, hours, labor cost) with financials in custom reports/dashboards **[P][T]**.

### 4.4 Workflow automation & tasks
- **Settings (gear) → Manage workflows** with tabs **Templates / My workflows / History** **[H][V]**. **60+ templates** (invoices, bills, estimates, purchase orders, bill payments…) **[H]**.
- **Custom workflow** (**+ Custom workflow**): select record type + action → name → conditions (with optional dimension) → "+ add another condition" → **Do this**: action types **Reminder, Approval, Notification, Send, Update** → **Save / Save and turn on** **[H]**.
- **Approvals**: *Request Approval* with up to **5 approver groups** per workflow, a **minimum number of approvers**, and an **Allow approval via email** checkbox **[H]**.
- **My workflows** actions: Edit, Copy, Turn on/off, Delete, **History**. Defaults enabled out-of-the-box: **Pay bill reminders**, **Unsent invoice reminders** **[H]**.
- **Project workflow templates (5)** **[H]**: Project Invoicing Reminder, Project Delay Notification, Project Profitability Reminder, Project Cost Overshoot Reminder, **Budget Approvals** (hierarchical A→B→C). Share across companies: workflow → Actions → **Publish** to child companies; recipients "Use this template." One custom role per workflow; hierarchical approvals; auto emails and tasks. Example: a custom **Invoice approver** role (Sales → view/create/edit/delete/approve invoices) **[H]**.
- **Bill approval & payment-release workflows** **[H]** (Bill Pay Elite or Advanced): ⚡ → Templates → **Bill Multi-Condition Approval**: conditions on **amount, vendor, location** (combinable), up to **7 approvers**, approval rule (**Anyone / Any 2 / All**), optional sequential layers (**up to 5**), **Email to your team** with editable email; **unreviewed bills auto-denied after 30 days**; payment-release approval template (approver = admins); approvers act from **Dashboard Task widget, Tasks menu, Bills → Unpaid tab (Approval Status column)**, or **Expenses → Bill Payments → Pending Approvals** (Approve / Reject / View Details). Roles: **Bill Approver, Bill Clerk, Bill Payer**. Downgrading removes roles/workflows and rejects pending payment approvals.
- **Invoice approval workflow** (end-to-end) and pre-set reminders (invoices, bills, expenses, payments, tasks) delivered via **Task Manager, email or push** **[P][T]**.
- **Task Manager**: assign tasks, automated task assignment, to-do delegation **[T][V]**.

### 4.5 Customization — fields, roles, users
- **Custom fields** **[H][T]**: **Settings → Custom fields → Add custom field**. **Up to 12 active per type** — Customer, Vendor, Transaction forms (sales forms), Purchase orders (total "48") plus Project **[H]**; data types **Text and number, Number only, Date, Dropdown list**; category **Customer / Transaction / Vendor / Project**; choose forms where it appears; **Print on form** (max **3** per form type); searchable/sortable/filterable; available as Spreadsheet Sync / batch columns (custom fields shown in batch grid for invoices; bills/expenses coverage has been inconsistent per community **[T]**).
- **Custom roles** **[H]**: **Settings → Manage users → Roles tab → Add role** (name, description, areas) → **Save Role**; **Users tab → Add user** → select role → *Send invitation*; **Edit** / **Deactivate** (⋮; reassign users first) / **Reactivate**. Permission areas and levels: **Sales** (All sales, Invoices, Estimates, Sales Receipts, Receive Payments, Credit Memos; View / Create / Edit / Delete / Approve / All access; **location restriction** via "All locations" dropdown), **Expenses** (All, Bills, Checks, Expenses, Bill Payments, Expense Claims, Vendor Credits, Credit Card Credits, Purchase Orders; View / Create / Edit / Delete / Approve / Submit / Pay / All), **Inventory**, **Lists** (Customers, Vendors), **Bookkeeping** (Bank Deposit, Transfers, Bank Transactions: connect accounts, rules, receipts, match), **Accounting** (Chart of Accounts, Reconciliation, Registers, Journal Entries), **Payroll** ("Workers"), **Reports** (View / Customize / Full; **standard reports only**, not other users' custom reports; new reports need permission updates), **Account Management** (edit company info; can't be saved alone), **Financial Planning** (Budgets; Forecast IES-only), **Projects** (IES-only per help; QBO Advanced projects permission also referenced **[K]**). Only admins/users with *Manage users* can manage users; multiple users per role.
- **Users:** up to **25**, **3 accountant seats**; Users tab shows count badge; **Accounting firms** tab **[V][P]**.
- **HR Manager** role (with Workforce) **[T]**.
- **Priority Circle Premium Support** — direct line to top-tier agents, phone/chat, callbacks, screen-share; **self-paced training** (advertised value $3,000) **[T]**; "priority support and training" **[P]**.
- **Backup & restore** (continuous automatic backup, restore specific versions, version history log) **[T][V]**.

### 4.6 Batch transactions
- **Batch Transactions** page: type = **Invoices, Checks, Expenses, Bills**; action = Create/Edit/…; spreadsheet grid; **paste from Excel**; CSV **import** of hundreds of invoices; **duplicate** rows; send many invoices at once; **batch edit/delete** of bills, checks, expenses; "Create invoices **37% faster**" (Intuit claim) **[P][H][V]**.
- **Batch invoicing** also shown as "bulk invoice creation (beta)" in Essentials/Plus **[P]**.

### 4.7 Accounts payable — Bill Pay Elite (included) **[P][T]**
AP hub; vendor payment processing; **free standard ACH**; (+ $0.50 per transaction over monthly allotment per Intuit footnote); **custom approval workflows**; **role-based controls** (creation / approval / release) with **audit trail**; **AI vendor-invoice automation** (bill capture); reconciliation with QuickBooks data; **unlimited 1099 e-filing** (print/mail/digital); pay by check or ACH ([V] "Pay with check / Pay with free standard ACH"). Approval/eligibility terms subject to credit and application approval.

### 4.8 Projects, job costing & construction **[P][H][T][V]**
- **Enable:** *Account and settings → Advanced → "Organize all job-related activity in one place."* **Project costing**: **Basic vs Detailed** (Detailed is irreversible) for Advanced with construction **[H]**.
- **Project creation:** All apps → Projects → Projects → **New project** (name, customer — auto-fills email/addresses —, start/end date, status default *In progress*, email override, billing/shipping address with "Same as billing," notes). **Phases** (name, description, start, end, status). Transactions attach via **Add to project**, **Customer/Project** field, **Billable** checkbox. Dashboard: income, expenses, profit margin in real time; mark completed; filter by status.
- **Enhanced/advanced project financials:** budgets & phases, **cost groups** and cost-vs-budget analysis, **milestone-based invoicing**, change-order management, profitability monitoring, **Project Management AI** estimate generation; **profit margin goal** field [V] **[P][V]**.
- **Construction financials (included from Aug 2026)** **[P][T]**: job costing; **budgets by phase** tied to estimates; **budget-versus-actuals** monitoring; **AIA-style progress billing** (pulls contract value, amount invoiced to date, remaining balance; each invoice carries forward); **change orders** (approved change order updates estimate, budget, invoice automatically); **WIP reporting with over/under billing**; profitability reports; continuous overrun monitoring by Intuit Intelligence. *Exact retainage, G702/G703 form layouts and subcontractor-compliance fields are not documented in what I could retrieve.* **[K]** (marketed on the "subcontractor" industry page).
- **Standard progress invoicing** **[H]**: *Settings → Account and settings → Sales → Progress Invoicing → Edit → "Create multiple partial invoices from a single estimate."* Convert an **Accepted** estimate (All apps → Sales & Get Paid → Sales transactions), or add the estimate from the **Add to Invoice / Suggested transactions** sidebar; bill **entire estimate / percentage / specific line items**; auto-status **Converted** at 100%; **Linked transactions**; unlink via Remove; report **Estimates & Progress Invoicing Summary by Customer**.
- **Other industry solutions** listed: project-based businesses (quote comparison, unbilled work, phase billing), legal services, nonprofit, accountants/ProAdvisors, professional services **[P]**.

### 4.9 Inventory (Advanced upgrades, Aug 2026) **[P][T]**
- **Sales orders** that **reserve stock**; **purchase orders auto-trigger at reorder thresholds**; **item receipts** update costs; **moving-average cost** with real-time recalculation. Plus-level base features: tracking, COGS, low-stock alerts, POs, Excel import, Amazon/Shopify/Etsy sync **[T]**.
- **Shopify two-way inventory sync** ("QuickBooks remains the source of truth"; PO receipts update Shopify; sales orders auto-import; audit trail) **[T]**.
- IES-only: units of measure, multiple shipping addresses per customer on invoices **[T]**.

### 4.10 Payroll, Workforce, time, HR (add-ons)
- **QuickBooks Workforce** (payroll + HCM): recruiting, hiring, onboarding, time-off, performance reviews, automated HR workflows, document management **[T]**. **Mineral HR resources** (compliance alerts; paid advisor tier) and **QuickBooks 401(k) by Vestwell** (auto deductions; Vestwell handles non-discrimination testing and Form 5500) announced Sept 2026 **[T]**.
- **Run Payroll screen** enhancements: batch actions, employee grouping, filters (base pay type, payment method, work location), name search, sortable columns, customizable density/colour/column order, **CSV export** **[T]**.
- Payroll tiers (Core/Premium/Elite), direct deposit, tax filing, workers' comp, auto-payroll, QuickBooks Time (clock, GPS, geofence, schedules, project estimates) per Intuit images **[K][P-images]**; Payroll is **optional** at checkout step 2 **[P]**.

### 4.11 Sales, customers, payments
- **Customer Hub**: lead tracking, sales-cycle status, communications, **e-signature contracts**, custom fields, review/testimonial requests, customer segmentation labels **[P][V]**.
- **Invoices**: pay-enabled invoices (cards, ACH, PayPal, Venmo, Apple Pay), estimates → invoices, mobile signature acceptance **[V][T]**; **Affirm buy-now-pay-later** on estimates/invoices with pre-qualification link and partial upfront payment **[T]**.
- **QuickBooks Payments** rates (third-party summary): cards/digital wallets ~2.8%, ACH ~1% (cap $15), in-person ~2.3%, keyed ~3.3% **[T]**.
- **Sales tax**: automated calculation; new **Sales Tax Liability report** — jurisdiction aggregation, rates and collected amounts, totals/subtotals, inline transactions, drill-down filtering, pivot between jurisdictions and rates **[T]**. Sales Tax AI discrepancy catching **[P]**.
- **Free 1099 e-file** and **1099 contractor management** (categorise vendor payments, payment history, file from QuickBooks) **[P][T]**.
- **Multi-entity**: Spreadsheet Sync "company or group" and consolidated multi-company reports **[V][T]**; true consolidation/dimensions are **IES** features **[T]**.

### 4.12 Mobile
Performance center on phone (period pills; See details; bottom tabs Dashboard / Transactions / Mileage / Invoices / Taxes) **[V]**; receipt capture; invoice push notifications **[V]**; dashboards and KPI access on mobile **[P]**.

---

## 5. Baseline features inherited from lower tiers (summary)
Unchanged from the first version of this file and tagged **[K]**: bank feeds/rules/reconciliation, invoices/estimates/recurring, bills, vendors, 1099s, purchase orders, inventory FIFO, budgets, classes/locations, multicurrency, time tracking, mileage, receipt capture, sales tax, payroll add-on options, app store/API. The detailed baseline list from the first draft was removed when this file was rewritten for Advanced; I can restore it as an appendix if you want it. Key plan-gate facts from Intuit's page **[P]**:
- Simple Start: intelligent onboarding, categorization, tax deduction maximization, invoices/payments, free ACH bill pay (limits).
- Essentials adds: AI books cleanup, bulk invoices (beta), employee time-to-invoice, quarterly tax estimates, lead forms, third-party KPI integration.
- Plus adds: AI-driven reconciliation, auto-categorization, anomaly detection, scenario-planning chat (beta), budgets, project profitability, **inventory**, 40 classes/locations, Sales Tax AI, Customer AI lead sourcing.

---

## 6. Not found / still unknown (honest gaps)
- Full **Intuit Intelligence** menu/settings (toggles, per-agent controls, data-sharing options) — "no opt-out" claims for agents come from third-party reporting and are unverified.
- AIA **G702/G703** printable layouts, **retainage**, subcontractor compliance in the new construction module.
- Exact **Reports → Management reports** screen layout in 2026; **Dashboard widget list** on Home.
- **Customer Hub** exact menu names; **Workforce** screens.
- Rate limits/plan caps for AI chat (stated only as "limited capacity").
- Whether specific features (e.g., Books Upkeep, conversational BI) are enabled in **your** company yet — rollout is phased.

**Best next step for completeness:** start the 30-day Advanced trial (you'd need to sign up yourself), then I can drive the browser pane with you signed in and screenshot/document every menu.

---

## 7. Sources
- Intuit — [QuickBooks Online Advanced](https://quickbooks.intuit.com/online/advanced/) · [Pricing](https://quickbooks.intuit.com/pricing/) · [Advanced features](https://quickbooks.intuit.com/accounting/advanced-features/) · [Spreadsheet Sync](https://quickbooks.intuit.com/online/advanced/spreadsheet-sync/) · [Batch transactions & expenses](https://quickbooks.intuit.com/online/advanced/expenses-transactions/) · [Industries](https://quickbooks.intuit.com/online/advanced/industries/)
- Intuit what's new — [Advanced, August 2026](https://quickbooks.intuit.com/r/product-update/whats-new-quickbooks-advanced-august-2026/) · [QBO, August 2026](https://quickbooks.intuit.com/r/product-update/whats-new-quickbooks-online-august-2026/) · [AI features, automation, management reports](https://quickbooks.intuit.com/r/product-update/quickbooks-advanced-most-powerful/) · [August pricing changes](https://quickbooks.intuit.com/r/product-update/quickbooks-price-changes/)
- Intuit help — [Workflows](https://quickbooks.intuit.com/learn-support/en-us/help-article/feature-preferences/use-workflows-quickbooks-online-advanced-send/L6uaB8H5G_US_en_US) · [Custom roles](https://quickbooks.intuit.com/learn-support/en-us/help-article/access-permissions/add-manage-custom-roles-quickbooks-online-advanced/L8Ugph7xl_US_en_US) · [Bill approval workflows](https://quickbooks.intuit.com/learn-support/en-us/help-article/manage-workflows/set-use-bill-approval-payment-release-workflows/L1IOLL9hv_US_en_US) · [Custom Automations](https://quickbooks.intuit.com/learn-support/en-us/help-article/business-automation/automate-tasks-custom-automations/L6qv3odlt_US_en_US) · [Project workflows](https://quickbooks.intuit.com/learn-support/en-us/help-article/manage-projects/create-use-project-workflows/L1VF7CJ77_US_en_US) · [KPIs](https://quickbooks.intuit.com/learn-support/en-us/help-article/revenue/view-key-performance-indicators-business/L26L6M0ki_US_en_US) · [Charts](https://quickbooks.intuit.com/learn-support/en-us/help-article/financial-reports/create-charts-see-business-performance/L8m11b5qY_US_en_US) · [Custom report builder](https://quickbooks.intuit.com/learn-support/en-us/help-article/customize-reports/create-custom-reports-quickbooks-online-advanced/L1LHo5Ti2_US_en_US) · [Spreadsheet Sync FAQ](https://quickbooks.intuit.com/learn-support/en-us/help-article/accounting-bookkeeping/spreadsheet-sync-faq/L3FiuAq13_US_en_US) · [Projects](https://quickbooks.intuit.com/learn-support/en-us/help-article/manage-projects/set-create-projects-quickbooks-online/L9GAdLMyT_US_en_US) · [Progress invoicing](https://quickbooks.intuit.com/learn-support/en-us/help-article/invoicing/set-send-progress-invoices-quickbooks-online/L0Ymm6WjR_US_en_US) · [Navigation menu](https://quickbooks.intuit.com/learn-support/en-us/help-article/bookkeeping-processes/understand-navigation-menu-quickbooks-online/L310NeoHY_US_en_US) · [Custom fields](https://quickbooks.intuit.com/learn-support/en-us/help-article/purchase-orders/create-edit-custom-fields-quickbooks-online/L56PQNif3_US_en_US) · [Expert books upkeep](https://quickbooks.intuit.com/learn-support/en-us/help-article/bookkeeping-processes/expert-bookkeeping-help-quickbooks-online/L4u14ocvx_US_en_US)
- Third party — [Firm of the Future: Advanced Aug 2026](https://www.firmofthefuture.com/product-update/quickbooks-online-advanced-august-2026-updates/) · [One8 Solutions: Sept 2026](https://www.one8solutions.com/updates/quickbooks-online-new-features-and-improvements-september-2026/) · [Insightful Accountant: construction](https://insightfulaccountant.com/accounting-tech/general-ledger/qbo-advanced-now-includes-construction-financial-capabilities/) · [Carly: AI tiers](https://www.usecarly.com/blog/quickbooks-ai) · [Dancing Numbers: pricing](https://www.dancingnumbers.com/how-much-is-quickbooks-online-per-month/)
