using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Ledjoro
{
    sealed class ReportsScreen : Screen
    {
        static readonly string[][] Defs = {
            new[] { "profit-loss", "Profit & Loss", "range" }, new[] { "balance-sheet", "Balance Sheet", "asof" }, new[] { "trial-balance", "Trial Balance", "asof" },
            new[] { "ar-aging", "Receivables Aging", "asof" }, new[] { "ap-aging", "Payables Aging", "asof" }, new[] { "sales-by-customer", "Sales by Customer", "range" },
            new[] { "general-ledger", "General Ledger", "range" }, new[] { "cash-forecast", "Cash Forecast", "asof" } };

        readonly string name; string[] def; ScrollPage page; Stack outBox; DateInput from, to, asof; string title = "";

        public ReportsScreen(string name) { this.name = name; }

        public override async Task Load()
        {
            def = Defs.FirstOrDefault(d => d[0] == name) ?? Defs[0];
            page = new ScrollPage(); var s = page.Body;
            s.Add(new Header("Reports", A.Company, Ui.B("Print", BK.Normal, Print, true)));
            var tabs = s.Add(new TabBar()); tabs.Set(Defs.Select(d => Ui.It(d[0], d[1])), def[0]); tabs.Changed += k => Nav("#/reports/" + k);
            from = new DateInput(Fmt.YearStart(), 170); to = new DateInput(Fmt.Today(), 170); asof = new DateInput(Fmt.Today(), 170);
            var row = new Flow();
            if (def[2] == "range") { row.Add(Ui.F("From", from, 170)); row.Add(Ui.F("To", to, 170)); } else row.Add(Ui.F("As of", asof, 170));
            var run = new Btn("Run Report", BK.Primary); run.Click += (a, b) => Ui.Run(Run); row.Add(Ui.F(" ", run, 130));
            s.Add(row);
            outBox = new Stack { Gap = Theme.Px(12) }; s.Add(outBox);
            SetBody(page);
            await Run();
        }

        async Task Run()
        {
            string q = def[2] == "range" ? "from=" + from.Value + "&to=" + to.Value : "asof=" + asof.Value;
            if ((def[2] == "range" && (!from.Valid || !to.Valid)) || (def[2] != "range" && !asof.Valid)) { App.Toast("One of the dates is not a real date. Type dates the way they are written on this computer, for example " + Fmt.Example + ", or pick them from the calendar.", true); return; }
            var r = await A.Get("api/reports/" + def[0] + "?" + q);
            outBox.ClearItems();
            switch (def[0])
            {
                case "profit-loss": PL(r); break;
                case "balance-sheet": BS(r); break;
                case "trial-balance": TB(r); break;
                case "ar-aging": case "ap-aging": Aging(r); break;
                case "sales-by-customer": Sales(r); break;
                case "general-ledger": GL(r); break;
                case "cash-forecast": Forecast(r); break;
            }
            outBox.PerformLayout(); page.PerformLayout(); Theme.Refresh(outBox);
        }

        void Title(string t) { title = t; outBox.Add(new Lbl(t, LS.H2)); }
        static Grid Two() { var g = new Grid { AutoHeight = true }; g.Cols(Grid.Col("Account", 4f), Grid.Col(">Amount", 1.4f)); g.ColumnHeadersVisible = false; return g; }
        static void Sec(Grid g, string title, Jv sec)
        {
            g.Add(null, RS.Sub, title, "");
            foreach (var i in sec["items"].Items) g.Add(null, (i["number"].Str.Length > 0 ? i["number"].Str + "  " : "") + i["name"].Str, Fmt.Money(i["amount_cents"].Long));
            g.Add(null, RS.Total, "Total " + title.ToLowerInvariant(), Fmt.Money(sec["total_cents"].Long));
        }

        void PL(Jv r)
        {
            Title("Profit & Loss · " + Fmt.Show(r["from"].Str) + " to " + Fmt.Show(r["to"].Str)); var g = Two();
            Sec(g, "Income", r["income"]); Sec(g, "Cost of goods sold", r["cogs"]); g.Add(null, RS.Total, "Gross profit", Fmt.Money(r["gross_profit_cents"].Long));
            Sec(g, "Expenses", r["expenses"]); g.Add(null, RS.Total, "Operating income", Fmt.Money(r["operating_income_cents"].Long));
            Sec(g, "Other income", r["other_income"]); Sec(g, "Other expense", r["other_expense"]); g.Add(null, RS.Total, "Net income", Fmt.Money(r["net_income_cents"].Long));
            g.ClearSelection(); outBox.Add(g);
        }
        void BS(Jv r)
        {
            Title("Balance Sheet · as of " + Fmt.Show(r["asof"].Str)); var g = Two();
            Sec(g, "Assets", r["assets"]); Sec(g, "Liabilities", r["liabilities"]);
            g.Add(null, RS.Sub, "Equity", ""); foreach (var i in r["equity"]["items"].Items) g.Add(null, i["name"].Str, Fmt.Money(i["amount_cents"].Long));
            g.Add(null, "Current earnings", Fmt.Money(r["equity"]["current_earnings_cents"].Long)); g.Add(null, RS.Total, "Total equity", Fmt.Money(r["equity"]["total_cents"].Long));
            g.Add(null, RS.Total, "Total liabilities & equity", Fmt.Money(r["total_liabilities_and_equity_cents"].Long)); g.ClearSelection(); outBox.Add(g);
            outBox.Add(r["balanced"].Bool ? new Notice(NK.Ok, "Assets equal liabilities plus equity ✓") : new Notice(NK.Error, "The balance sheet does not balance — run Settings → Audit & integrity → Verify."));
        }
        void TB(Jv r)
        {
            Title("Trial Balance · as of " + Fmt.Show(r["asof"].Str)); var g = new Grid { AutoHeight = true }; g.Cols(Grid.Col("Account", 4f), Grid.Col(">Debit", 1.4f), Grid.Col(">Credit", 1.4f));
            foreach (var x in r["rows"].Items) g.Add(null, (x["number"].Str.Length > 0 ? x["number"].Str + "  " : "") + x["name"].Str, x["debit_cents"].Long > 0 ? Fmt.Money(x["debit_cents"].Long) : "", x["credit_cents"].Long > 0 ? Fmt.Money(x["credit_cents"].Long) : "");
            g.Add(null, RS.Total, "Total", Fmt.Money(r["totals"]["debit_cents"].Long), Fmt.Money(r["totals"]["credit_cents"].Long)); g.ClearSelection(); outBox.Add(g);
        }
        void Aging(Jv r)
        {
            Title((r["kind"].Str == "ar" ? "Receivables" : "Payables") + " Aging · as of " + Fmt.Show(r["asof"].Str));
            var g = new Grid { AutoHeight = true }; g.Cols(Grid.Col("Name", 3f), Grid.Col(">Current", 1.2f), Grid.Col(">1–30", 1.2f), Grid.Col(">31–60", 1.2f), Grid.Col(">61–90", 1.2f), Grid.Col(">90+", 1.2f), Grid.Col(">Total", 1.3f));
            Func<Jv, string[]> cells = x => new[] { x["current"].Long, x["d1_30"].Long, x["d31_60"].Long, x["d61_90"].Long, x["d90_plus"].Long, x["total_cents"].Long }.Select(Fmt.Money).ToArray();
            foreach (var x in r["rows"].Items) { var c = cells(x); g.Add(null, x["name"].Str, c[0], c[1], c[2], c[3], c[4], c[5]); }
            var t = cells(r["totals"]); g.Add(null, RS.Total, "Total", t[0], t[1], t[2], t[3], t[4], t[5]); g.ClearSelection(); outBox.Add(g);
            if (r["rows"].Count == 0) outBox.Add(new Lbl("Nothing outstanding.", LS.Muted));
        }
        void Sales(Jv r)
        {
            Title("Sales by Customer · " + Fmt.Show(r["from"].Str) + " to " + Fmt.Show(r["to"].Str)); var g = new Grid { AutoHeight = true }; g.Cols(Grid.Col("Customer", 4f), Grid.Col(">Sales", 1.4f));
            foreach (var x in r["rows"].Items) g.Add(null, x["name"].Str, Fmt.Money(x["total_cents"].Long)); g.Add(null, RS.Total, "Total", Fmt.Money(r["total_cents"].Long)); g.ClearSelection(); outBox.Add(g);
            if (r["rows"].Count == 0) outBox.Add(new Lbl("No sales in this period.", LS.Muted));
        }
        void GL(Jv r)
        {
            Title("General Ledger · " + Fmt.Show(r["from"].Str) + " to " + Fmt.Show(r["to"].Str));
            foreach (var a in r["accounts"].Items)
            {
                outBox.Add(new Lbl((a["number"].Str + " " + a["name"].Str).Trim(), LS.H3), Theme.Px(2));
                var g = new Grid { AutoHeight = true }; g.Cols(Grid.Col("Date", 1f), Grid.Col("Memo", 3f), Grid.Col("Contact", 2f), Grid.Col(">Debit", 1.1f), Grid.Col(">Credit", 1.1f), Grid.Col(">Balance", 1.2f));
                g.Add(null, RS.Sub, "", "Opening balance", "", "", "", Fmt.Money(a["opening_cents"].Long));
                foreach (var l in a["lines"].Items) g.Add(null, Fmt.Show(l["entry_date"].Str), l["memo"].Str, l["contact_name"].Str, l["debit_cents"].Long > 0 ? Fmt.Money(l["debit_cents"].Long) : "", l["credit_cents"].Long > 0 ? Fmt.Money(l["credit_cents"].Long) : "", Fmt.Money(l["balance_cents"].Long));
                g.ClearSelection(); outBox.Add(g);
            }
            if (r["accounts"].Count == 0) outBox.Add(new Lbl("No activity.", LS.Muted));
        }

        void Forecast(Jv r)
        {
            Title("Cash Forecast · next " + r["weeks"].Str + " weeks from " + Fmt.Show(r["asof"].Str));
            var tabs = new TabBar(); tabs.Set(new[] { Ui.It("typical", "Typical activity continues"), Ui.It("committed", "Only what is already committed") }, "typical");
            var holder = new Stack { Gap = Theme.Px(12) };
            Action<string> draw = key =>
            {
                holder.ClearItems(); var sc = r["scenarios"][key]; var weeks = sc["weeks"];
                long lowest = sc["lowest"]["closing_cents"].Long, end = sc["ending_cents"].Long;
                var stats = new Flow { HGap = Theme.Px(14), VGap = Theme.Px(14) };
                stats.Add(new StatCard(Fmt.Money(r["cash_now_cents"].Long), "Cash today")); stats.Add(new StatCard(Fmt.Money(lowest), "Lowest point (week of " + weeks[sc["lowest"]["week"].Int]["start"].Str + ")", lowest < 0));
                stats.Add(new StatCard(Fmt.Money(end), "Cash after " + r["weeks"].Str + " weeks", end < 0)); stats.Add(new StatCard(sc["negative_weeks"].Str, "Weeks below zero", sc["negative_weeks"].Int > 0));
                holder.Add(stats);
                var card = new Card(); card.Add(new Lbl("Closing cash by week", LS.H3)); var bars = new BarChart();
                foreach (var w in weeks.Items) bars.Add(w["start"].Str.Substring(5), w["closing_cents"].Long, Fmt.Money(w["closing_cents"].Long), w["closing_cents"].Long < 0);
                card.Add(bars, 0); holder.Add(card);
                var g = new Grid { AutoHeight = true }; g.Cols(Grid.Col("Week of", 2f), Grid.Col(">Money in", 1.3f), Grid.Col(">Money out", 1.3f), Grid.Col(">Net", 1.3f), Grid.Col(">Closing cash", 1.4f));
                foreach (var w in weeks.Items) g.Add(null, w["start"].Str + " to " + w["end"].Str.Substring(5), Fmt.Money(w["receipts_cents"].Long), Fmt.Money(w["payments_cents"].Long), Fmt.Money(w["net_cents"].Long), Fmt.Money(w["closing_cents"].Long));
                g.ClearSelection(); holder.Add(g);
                holder.PerformLayout(); outBox.PerformLayout(); page.PerformLayout(); Theme.Refresh(holder);
            };
            tabs.Changed += k => draw(k);
            outBox.Add(tabs); outBox.Add(holder); draw("typical");
            outBox.Add(new Lbl("How this was calculated", LS.H3), Theme.Px(2));
            foreach (var a in r["assumptions"].Items) outBox.Add(new Lbl("•  " + a.Str, LS.Normal, true), Theme.Px(4));
        }

        static IEnumerable<Grid> GridsIn(Control c) { foreach (Control k in c.Controls) { var g = k as Grid; if (g != null) yield return g; foreach (var x in GridsIn(k)) yield return x; } }
        void Print() { var grids = GridsIn(outBox).ToArray(); if (grids.Length == 0) { App.Toast("There is nothing to print yet. Press Run to produce the report, then print it."); return; } PrintHelper.Print(title, A.Company, grids); }
    }
}
