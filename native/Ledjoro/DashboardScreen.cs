using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Ledjoro
{
    sealed class DashboardScreen : Screen
    {
        public override async Task Load()
        {
            var d = await A.Get("api/dashboard");
            var page = new ScrollPage(); var s = page.Body;
            s.Add(new Header("Dashboard", A.Company + "   ·   " + DateTime.Now.ToString("MMMM d, yyyy")));

            var stats = new Flow { HGap = Theme.Px(14), VGap = Theme.Px(14) };
            stats.Add(new StatCard(Fmt.Money(d["cash_cents"].Long), "Cash in bank", d["cash_cents"].Long < 0));
            stats.Add(new StatCard(Fmt.Money(d["receivable_cents"].Long), "Owed to you"));
            stats.Add(new StatCard(Fmt.Money(d["payable_cents"].Long), "You owe"));
            stats.Add(new StatCard(Fmt.Money(d["ytd"]["net_cents"].Long), "Net income this year", d["ytd"]["net_cents"].Long < 0));
            s.Add(stats);

            var year = new Card(); year.Add(new Lbl("This year", LS.H3));
            var bars = new BarChart();
            bars.Add("Income", d["ytd"]["income_cents"].Long, Fmt.Money(d["ytd"]["income_cents"].Long));
            bars.Add("Expenses", d["ytd"]["expenses_cents"].Long, Fmt.Money(d["ytd"]["expenses_cents"].Long), true);
            year.Add(bars);
            year.Add(new Lbl("Last 30 days: income " + Fmt.Money(d["last30"]["income_cents"].Long) + ", net " + Fmt.Money(d["last30"]["net_cents"].Long), LS.Muted, true), 0);

            var attn = new Card { Gap = 0 }; attn.Add(new Lbl("Needs attention", LS.H3), Theme.Px(8));
            int od = d["overdue_invoices"].Int, due = d["bills_due_soon"].Int, bank = d["bank_to_review"].Int;
            attn.Add(new AttnRow("Overdue invoices", od.ToString(), od > 0 ? PK.Bad : PK.Good, () => Nav("#/invoices")), 0);
            attn.Add(new AttnRow("Bills due within 7 days", due.ToString(), due > 0 ? PK.Warn : PK.Good, () => Nav("#/bills")), 0);
            attn.Add(new AttnRow("Bank transactions to review", bank.ToString(), bank > 0 ? PK.Accent : PK.Good, () => Nav("#/banking")), 0);
            if (A.Can("import"))
            {
                var w = await A.Get("api/watch"); int waiting = w["enabled"].Bool ? w["files"].Items.Count(f => f["status"].Str == "pending") : 0;
                if (waiting > 0) attn.Add(new AttnRow("Bank statement files waiting to import", waiting.ToString(), PK.Accent, () => Nav("#/banking")), 0);
            }
            if (A.Can("settings.manage"))
            {
                string lb = d["last_backup"].Str; string text; PK kind;
                if (lb.Length == 0) { text = "never"; kind = PK.Bad; }
                else { DateTime t; DateTime.TryParse(lb, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out t); int days = (int)Math.Floor((DateTime.UtcNow - t).TotalDays); text = days <= 0 ? "today" : days + " d ago"; kind = days > 30 ? PK.Bad : days > 7 ? PK.Warn : PK.Good; }
                attn.Add(new AttnRow("Last encrypted backup", text, kind, () => Nav("#/settings/data")), 0);
            }
            var cols = new Cols(); cols.Add(year); cols.Add(attn); s.Add(cols);
            SetBody(page);
        }
    }
}
