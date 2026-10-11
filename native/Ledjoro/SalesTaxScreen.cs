using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Ledjoro
{
    /// <summary>
    /// Sales Tax: the rates you charge (and combined rates such as state + county), the return worksheet for any period, and payments to the
    /// tax agencies. Tax itself is added on the invoice form; this page sets it up and reports on it.
    /// </summary>
    sealed class SalesTaxScreen : Screen
    {
        readonly string tab;
        public SalesTaxScreen(string tab) { this.tab = tab == "worksheet" || tab == "payments" ? tab : "rates"; }

        // the worksheet's controls and last answer, kept so the adjustment box can redraw the totals without asking the engine again
        Stack outBox; ScrollPage page; DateInput from, to; Combo preset; Input adjust; Jv last; Stack dueBox; string title = "";

        public static string Pct(long milli) { return (milli / 1000m).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + "%"; }

        public override async Task Load()
        {
            Lists.Bust();
            page = new ScrollPage(); var s = page.Body;
            s.Add(new Header("Sales Tax", "Rates, the return worksheet, and payments to the tax agencies"));
            var tabs = s.Add(new TabBar()); tabs.Set(new[] { Ui.It("rates", "Tax Rates"), Ui.It("worksheet", "Return Worksheet"), Ui.It("payments", "Payments") }, tab);
            tabs.Changed += k => Nav("#/salestax/" + k);
            outBox = new Stack { Gap = Theme.Px(12) }; s.Add(outBox);
            SetBody(page);
            if (tab == "rates") await Rates(); else if (tab == "worksheet") await Worksheet(); else await Payments();
            outBox.PerformLayout(); page.PerformLayout(); Theme.Refresh(outBox);
        }

        // ------------------------------------------------------------------ tax rates
        async Task Rates()
        {
            var r = await A.Get("api/salestax/rates"); var accts = await Lists.Accounts();
            bool write = A.Can("sales.write"); long def = r["default_id"].IsNull ? 0 : r["default_id"].Long;
            if (r["rates"].Count == 0)
            {
                var how = new Card(); how.Padding = new Padding(Theme.Px(20)); how.Gap = Theme.Px(6);
                how.Add(new Lbl("Charging sales tax", LS.H3));
                how.Add(new Lbl("1.  Add the tax rates you charge below (for a combined rate such as state plus county, add each one, then a Combined Rate made of them).", LS.Normal, true), Theme.Px(2));
                how.Add(new Lbl("2.  On each invoice, choose the tax rate and tick which lines are taxable. Ledjoro works out the tax, shows it on the invoice and keeps it in the Sales Tax Payable account until you pay it.", LS.Normal, true), Theme.Px(2));
                how.Add(new Lbl("3.  Mark customers who do not pay sales tax (schools, resellers) as tax-exempt on their customer page.", LS.Normal, true), Theme.Px(2));
                how.Add(new Lbl("4.  At filing time, open the Return Worksheet for the period. It lists taxable, exempt and non-taxable sales and the tax collected for each agency, ready to copy onto your return.", LS.Normal, true), Theme.Px(2));
                outBox.Add(how);
            }
            if (write)
            {
                var row = new Flow { HGap = Theme.Px(12) };
                row.Add(Ui.BA("New Tax Rate", BK.Primary, () => EditRate(null, false, r, accts)));
                row.Add(Ui.BA("New Combined Rate", BK.Normal, () => EditRate(null, true, r, accts)));
                outBox.Add(row);
            }
            var g = new Grid { AutoHeight = true }; g.Cols(Grid.Col("Name", 1.8f), Grid.Col("Type", 2.6f), Grid.Col(">Rate", 0.8f), Grid.Col("Tax agency", 1.8f), Grid.Col("Owed in account", 1.8f), Grid.Col("", 1.1f));
            foreach (var x in r["rates"].Items)
            {
                bool grp = x["kind"].Str == "group"; var names = x["parts"].Items.Select(p => r["rates"].Items.First(y => y["id"].Long == p.Long)["name"].Str);
                string tag = x["id"].Long == def ? "default" : x["active"].Long == 0 ? "switched off" : "";
                g.Add(x, x["name"].Str, grp ? "Combined: " + string.Join(" + ", names) : "Single rate", Pct(x["rate_milli"].Long), x["agency"].Str, x["account_name"].IsNull || x["account_name"].Str.Length == 0 ? "Sales Tax Payable" : x["account_name"].Str, tag);
            }
            if (write) g.Activated += tag => Ui.Run(() => { var j = (Jv)tag; return EditRate(j, j["kind"].Str == "group", r, accts); });
            outBox.Add(g);
            if (r["rates"].Count > 0 && write) outBox.Add(new Lbl("Double-click a rate to change it. Changing a rate affects new invoices only; invoices already made keep the rate they were charged. A rate that has been used cannot be deleted, but it can be switched off.", LS.Muted, true));
        }

        async Task EditRate(Jv x, bool group, Jv all, Jv accts)
        {
            var dlg = new Dlg(x == null ? (group ? "New Combined Rate" : "New Tax Rate") : "Change " + x["name"].Str, 600);
            var name = new Input(group ? "Springfield (state + county)" : "State sales tax") { Text = x == null ? "" : x["name"].Str };
            var pct = new Input("6.25") { Text = x == null ? "" : (x["rate_milli"].Long / 1000m).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture), RightAlign = true };
            var agency = new Input("State Department of Revenue") { Text = x == null ? "" : x["agency"].Str };
            var acct = Ui.Cb(Ui.AccountItems(accts, a => a["type"].Str == "liability", "Sales Tax Payable (standard)"), x == null || x["account_id"].IsNull ? "" : x["account_id"].Str);
            var isDef = new Check("Use this rate for new invoices unless another is chosen", x != null && x["is_default"].Long == 1);
            var active = new Check("In use (untick to switch it off)", x == null || x["active"].Long == 1);
            var picks = new List<KeyValuePair<long, Check>>();
            dlg.Body.Gap = Theme.Px(10);
            dlg.Body.Add(Ui.F("Name", name, 540));
            if (!group) dlg.Body.Add(Ui.F("Rate (percent)", pct, 160));
            else
            {
                dlg.Body.Add(new Lbl("Tick the rates this combined rate is made of. Each one is worked out and reported separately.", LS.Muted, true));
                foreach (var s in all["rates"].Items.Where(s => s["kind"].Str == "rate" && (s["active"].Long == 1 || (x != null && x["parts"].Items.Any(p => p.Long == s["id"].Long)))))
                { var c = new Check(s["name"].Str + "  (" + Pct(s["rate_milli"].Long) + ")", x != null && x["parts"].Items.Any(p => p.Long == s["id"].Long)); picks.Add(new KeyValuePair<long, Check>(s["id"].Long, c)); dlg.Body.Add(c); }
                if (picks.Count == 0) dlg.Body.Add(new Notice(NK.Warn, "There are no single rates yet. Close this window and add the state and county rates first."));
            }
            if (!group) { dlg.Body.Add(Ui.F("Tax agency it is paid to", agency, 540)); dlg.Body.Add(Ui.F("Account the tax is owed in", acct, 360)); }
            dlg.Body.Add(isDef); if (x != null) dlg.Body.Add(active);
            dlg.Cancel();
            dlg.Primary("Save", async () =>
            {
                var body = Json.O("name", name.Text.Trim(), "is_default", isDef.Checked);
                if (!group) { body["rate_pct"] = pct.Text.Trim(); body["agency"] = agency.Text.Trim(); body["account_id"] = acct.Value.Length > 0 ? (object)long.Parse(acct.Value) : null; }
                else body["parts"] = picks.Where(p => p.Value.Checked).Select(p => (object)p.Key).ToList();
                if (x == null) { body["kind"] = group ? "group" : "rate"; await A.Post("api/salestax/rates", body); }
                else { body["active"] = active.Checked; await A.Patch("api/salestax/rates/" + x["id"].Str, body); }
                Lists.Bust(); App.Toast("Saved"); App.Main.Reload(); return true;
            });
            dlg.Run();
        }

        // ------------------------------------------------------------------ the return worksheet
        static void Period(string key, out string f, out string t)
        {
            var n = DateTime.Now; int q = (n.Month - 1) / 3; DateTime a, b;
            switch (key)
            {
                case "last-month": a = new DateTime(n.Year, n.Month, 1).AddMonths(-1); b = a.AddMonths(1).AddDays(-1); break;
                case "this-quarter": a = new DateTime(n.Year, q * 3 + 1, 1); b = a.AddMonths(3).AddDays(-1); break;
                case "last-quarter": a = new DateTime(n.Year, q * 3 + 1, 1).AddMonths(-3); b = a.AddMonths(3).AddDays(-1); break;
                case "this-year": a = new DateTime(n.Year, 1, 1); b = new DateTime(n.Year, 12, 31); break;
                case "last-year": a = new DateTime(n.Year - 1, 1, 1); b = new DateTime(n.Year - 1, 12, 31); break;
                default: a = new DateTime(n.Year, n.Month, 1); b = a.AddMonths(1).AddDays(-1); break;
            }
            f = Fmt.Date(a); t = Fmt.Date(b);
        }

        async Task Worksheet()
        {
            string f, t; Period("last-month", out f, out t);
            from = new DateInput(f, 170); to = new DateInput(t, 170);
            preset = Ui.Cb(new[] { Ui.It("last-month", "Last month"), Ui.It("this-month", "This month"), Ui.It("last-quarter", "Last quarter"), Ui.It("this-quarter", "This quarter"), Ui.It("last-year", "Last year"), Ui.It("this-year", "This year"), Ui.It("custom", "Custom dates") }, "last-month", 190);
            preset.Changed += (a, b) => { if (preset.Value == "custom") return; string pf, pt; Period(preset.Value, out pf, out pt); from.Text = pf; to.Text = pt; Ui.Run(RunWorksheet); };
            var row = new Flow { HGap = Theme.Px(12) };
            row.Add(Ui.F("Period", preset, 190)); row.Add(Ui.F("From", from, 170)); row.Add(Ui.F("To", to, 170));
            var run = new Btn("Run Worksheet", BK.Primary); run.Click += (a, b) => Ui.Run(RunWorksheet); row.Add(Ui.F(" ", run, 150));
            var print = Ui.B("Print", BK.Normal, PrintWorksheet); row.Add(Ui.F(" ", print, 100));
            outBox.Add(row);
            outBox.Add(new Lbl("Sales are counted by invoice date. The worksheet shows the figures an agency usually asks for; check your own state's form for which lines apply.", LS.Muted, true));
            dueBox = new Stack { Gap = Theme.Px(12) };
            await RunWorksheet();
        }

        async Task RunWorksheet()
        {
            if (!from.Valid || !to.Valid) { App.Toast("One of the dates is not a real date. Type dates the way they are written on this computer, for example " + Fmt.Example + ", or pick them from the calendar.", true); return; }
            last = await A.Get("api/salestax/worksheet?from=" + from.Value + "&to=" + to.Value);
            Draw(); page.PerformLayout(); Theme.Refresh(outBox);
        }

        static Grid Two() { var g = new Grid { AutoHeight = true }; g.Cols(Grid.Col("", 4f), Grid.Col(">Amount", 1.4f)); g.ColumnHeadersVisible = false; return g; }

        void Draw()
        {
            var w = last; if (w == null) return;
            // keep the first controls (period row and note); redraw everything after them
            while (outBox.Controls.Count > 2) { var c = outBox.Controls[outBox.Controls.Count - 1]; outBox.Controls.Remove(c); c.Dispose(); }
            title = "Sales Tax Return Worksheet · " + Fmt.Show(w["from"].Str) + " to " + Fmt.Show(w["to"].Str);
            outBox.Add(new Lbl(title, LS.H2));
            if (!w["uses_sales_tax"].Bool) outBox.Add(new Notice(NK.Warn, "No sales tax has been set up yet. Add your tax rates on the Tax Rates tab, then choose a rate on each invoice."));

            outBox.Add(new Lbl("Sales", LS.H3), Theme.Px(2));
            var s = w["sales"]; var g1 = Two();
            g1.Add(null, "Total sales (before tax)", Fmt.Money(s["total_cents"].Long));
            g1.Add(null, "Taxable sales", Fmt.Money(s["taxable_cents"].Long));
            g1.Add(null, "Exempt sales (to tax-exempt customers)", Fmt.Money(s["exempt_cents"].Long));
            g1.Add(null, "Non-taxable sales", Fmt.Money(s["nontaxable_cents"].Long)); g1.ClearSelection(); outBox.Add(g1);

            outBox.Add(new Lbl("Tax by jurisdiction", LS.H3), Theme.Px(2));
            var g2 = new Grid { AutoHeight = true }; g2.Cols(Grid.Col("Jurisdiction", 2.2f), Grid.Col("Tax agency", 2.2f), Grid.Col(">Rate", 0.9f), Grid.Col(">Taxable sales", 1.4f), Grid.Col(">Tax collected", 1.4f));
            foreach (var j in w["jurisdictions"].Items) g2.Add(null, j["name"].Str, j["agency"].Str, Pct(j["rate_milli"].Long), Fmt.Money(j["taxable_cents"].Long), Fmt.Money(j["tax_cents"].Long));
            g2.Add(null, RS.Total, "Total", "", "", "", Fmt.Money(w["tax_collected_cents"].Long)); g2.ClearSelection(); outBox.Add(g2);
            if (w["jurisdictions"].Count == 0) outBox.Add(new Lbl("No tax was charged on invoices in this period.", LS.Muted));

            outBox.Add(new Lbl("Amount due", LS.H3), Theme.Px(2));
            string typed = adjust == null || adjust.IsDisposed ? "0.00" : adjust.Text; adjust = new Input("0.00") { RightAlign = true, Text = typed };   // a new box each time: the old one went with the rows above
            var adjRow = new Flow { HGap = Theme.Px(12) }; adjRow.Add(Ui.F("Adjustments or discounts (a minus sign adds tax)", adjust, 330));
            adjust.Tb.Leave += AdjustLeft; outBox.Add(adjRow);
            dueBox = new Stack { Gap = Theme.Px(8) }; outBox.Add(dueBox); Due();

            if (w["exempt_customers"].Count > 0)
            {
                outBox.Add(new Lbl("Tax-exempt sales by customer", LS.H3), Theme.Px(2));
                var g3 = new Grid { AutoHeight = true }; g3.Cols(Grid.Col("Customer", 3f), Grid.Col("Exemption certificate or note", 3f), Grid.Col(">Exempt sales", 1.4f));
                foreach (var e in w["exempt_customers"].Items) g3.Add(null, e["customer"].Str, e["note"].Str, Fmt.Money(e["sales_cents"].Long)); g3.ClearSelection(); outBox.Add(g3);
            }
            outBox.Add(new Lbl("Invoices in this period", LS.H3), Theme.Px(2));
            var g4 = new Grid { AutoHeight = true }; g4.Cols(Grid.Col("Number", 1.2f), Grid.Col("Date", 1.1f), Grid.Col("Customer", 2.6f), Grid.Col(">Sales", 1.2f), Grid.Col(">Taxable", 1.2f), Grid.Col(">Exempt", 1.2f), Grid.Col(">Tax", 1.1f));
            foreach (var i in w["invoices"].Items) g4.Add(i["id"].Long, i["number"].Str, Fmt.Show(i["date"].Str), i["customer"].Str, Fmt.Money(i["subtotal_cents"].Long), Fmt.Money(i["taxable_cents"].Long), Fmt.Money(i["exempt_cents"].Long), Fmt.Money(i["tax_cents"].Long));
            g4.Activated += tag => { if (tag is long) Nav("#/document/" + tag); };
            outBox.Add(g4);
            if (w["invoices"].Count == 0) outBox.Add(new Lbl("No invoices in this period.", LS.Muted));

            if (A.Can("accounting.write"))
            {
                var pay = new Flow { HGap = Theme.Px(12) };
                pay.Add(Ui.BA("Record a Payment to the Agency", BK.Normal, () => PaymentDialog(DueCents(), w["from"].Str, w["to"].Str, Agencies(w))));
                outBox.Add(pay, Theme.Px(6));
            }
            outBox.PerformLayout();
        }

        void AdjustLeft(object s, EventArgs e) { Due(); outBox.PerformLayout(); page.PerformLayout(); Theme.Refresh(outBox); }

        long Adjustment() { long c; return Fmt.TryMoney(adjust.Text, out c) ? c : 0; }
        long DueCents() { var l = last["ledger"]; return last["tax_collected_cents"].Long + l["other_entries_cents"].Long - Adjustment(); }
        static string Agencies(Jv w) { var a = w["jurisdictions"].Items.Select(j => j["agency"].Str).Where(x => x.Length > 0).Distinct().ToList(); return a.Count == 1 ? a[0] : ""; }

        void Due()
        {
            dueBox.ClearItems(); var l = last["ledger"]; long collected = last["tax_collected_cents"].Long, other = l["other_entries_cents"].Long, adj = Adjustment(), due = collected + other - adj;
            var g = Two();
            g.Add(null, "Sales tax collected on invoices", Fmt.Money(collected));
            if (other != 0) g.Add(null, "Tax recorded another way (imports or journal entries)", Fmt.Money(other));
            if (adj != 0) g.Add(null, "Less adjustments or discounts", Fmt.Money(-adj));
            g.Add(null, RS.Total, "Tax due for this period", Fmt.Money(due));
            g.Add(null, "Payments to the agencies made in this period", Fmt.Money(l["remitted_cents"].Long));
            g.Add(null, "Sales tax owed on the books at the end of the period", Fmt.Money(l["closing_cents"].Long)); g.ClearSelection(); dueBox.Add(g);
            if (other != 0) dueBox.Add(new Notice(NK.Warn, "Some tax in this period was recorded without an invoice made in Ledjoro (for example from a QuickBooks import or a journal entry). It is included in the total above. Look at the Sales Tax Payable account in the General Ledger to see which entries."));
            dueBox.PerformLayout();
        }

        void PrintWorksheet()
        {
            var grids = new List<Grid>(); Action<Control> walk = null;
            walk = c => { foreach (Control k in c.Controls) { var g = k as Grid; if (g != null) grids.Add(g); walk(k); } };
            walk(outBox);
            if (grids.Count == 0) { App.Toast("There is nothing to print yet. Press Run Worksheet first."); return; }
            PrintHelper.Print(title, A.Company, grids.ToArray());
        }

        // ------------------------------------------------------------------ payments to the agencies
        async Task Payments()
        {
            var list = await A.Get("api/salestax/payments"); bool write = A.Can("accounting.write");
            var row = new Flow { HGap = Theme.Px(12) };
            if (write) row.Add(Ui.BA("Record a Payment", BK.Primary, () => PaymentDialog(0, "", "", "")));
            outBox.Add(row);
            var g = new Grid { AutoHeight = true }; g.Cols(Grid.Col("Date", 1.1f), Grid.Col("Tax agency", 2.2f), Grid.Col("For the period", 2.2f), Grid.Col(">Amount", 1.3f), Grid.Col("Paid from", 1.8f), Grid.Col("Status", 1f));
            foreach (var p in list.Items)
            {
                string per = p["period_from"].Str.Length > 0 ? Fmt.Show(p["period_from"].Str) + " to " + Fmt.Show(p["period_to"].Str) : "";
                g.Add(p["id"].Long, Fmt.Show(p["pay_date"].Str), p["agency"].Str, per, Fmt.Money(p["amount_cents"].Long), p["bank_name"].Str, p["voided"].Long == 1 ? new GC("void", Theme.Bad) : new GC("paid", Theme.Good));
            }
            outBox.Add(g);
            if (list.Count == 0) outBox.Add(new Lbl("No payments to a tax agency have been recorded. When you pay a return, record it here so the Sales Tax Payable balance goes down.", LS.Muted, true));
            else if (write) outBox.Add(Ui.BA("Void Selected Payment", BK.Normal, async () =>
            {
                var tag = g.SelectedTag; if (!(tag is long)) { App.Toast("Click a payment in the list first."); return; }
                if (!Msg.Confirm("Void this payment?", "A reversing entry will be posted, and the tax will be owed again.", "Void Payment", true)) return;
                await A.Post("api/salestax/payments/" + tag + "/void"); App.Toast("Payment voided"); App.Main.Reload();
            }, true), Theme.Px(4));
        }

        async Task PaymentDialog(long amount, string pf, string pt, string agencyName)
        {
            var accts = await Lists.Accounts();
            var dlg = new Dlg("Record a Payment to the Tax Agency", 600);
            var date = new DateInput(Fmt.Today(), 170); var amt = new Input("0.00") { RightAlign = true, Text = amount > 0 ? Fmt.Plain(amount) : "" };
            var agency = new Input("State Department of Revenue") { Text = agencyName };
            var bank = Ui.Cb(Ui.AccountItems(accts, a => a["subtype"].Str == "bank" || a["subtype"].Str == "credit_card"));
            var taxAcct = Ui.Cb(Ui.AccountItems(accts, a => a["type"].Str == "liability", "Sales Tax Payable (standard)"), "");
            var pFrom = new DateInput(pf.Length > 0 ? pf : Fmt.Today(), 170); var pTo = new DateInput(pt.Length > 0 ? pt : Fmt.Today(), 170);
            var withPeriod = new Check("This payment is for a return period", pf.Length > 0);
            dlg.Body.Gap = Theme.Px(10);
            var r1 = new Flow { Fill = Pnl.K.Surface }; r1.Add(Ui.F("Date paid", date, 170)); r1.Add(Ui.F("Amount", amt, 150)); r1.Add(Ui.F("Paid from", bank, 250)); dlg.Body.Add(r1);
            dlg.Body.Add(Ui.F("Tax agency", agency, 540)); dlg.Body.Add(Ui.F("Taken off this liability account", taxAcct, 360));
            dlg.Body.Add(withPeriod); var r2 = new Flow { Fill = Pnl.K.Surface }; r2.Add(Ui.F("Period from", pFrom, 170)); r2.Add(Ui.F("Period to", pTo, 170)); dlg.Body.Add(r2);
            dlg.Cancel();
            dlg.Primary("Save Payment", async () =>
            {
                long cents; if (!Fmt.TryMoney(amt.Text, out cents) || cents <= 0) throw new ApiException(0, Say.Money("payment amount"), Jv.Null);
                if (!date.Valid) throw new ApiException(0, Say.Date("payment date"), Jv.Null);
                if (bank.Value.Length == 0) throw new ApiException(0, "Choose the bank or card account the payment came from.", Jv.Null);
                var body = Json.O("pay_date", date.Value, "amount_cents", cents, "agency", agency.Text.Trim(), "bank_account_id", long.Parse(bank.Value));
                if (taxAcct.Value.Length > 0) body["tax_account_id"] = long.Parse(taxAcct.Value);
                if (withPeriod.Checked) { if (!pFrom.Valid || !pTo.Valid) throw new ApiException(0, Say.Date("period"), Jv.Null); body["period_from"] = pFrom.Value; body["period_to"] = pTo.Value; }
                await A.Post("api/salestax/payments", body); App.Toast("Payment recorded"); App.Main.Go("#/salestax/payments"); return true;
            });
            dlg.Run();
        }
    }
}
