using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Ledjoro
{
    /// <summary>The list of credit memos (money owed back to customers).</summary>
    sealed class CreditListScreen : Screen
    {
        public override async Task Load()
        {
            var all = await A.Get("api/credits");
            var root = new Stack { Padding = new Padding(Theme.Px(24)), Gap = Theme.Px(12) };
            root.Add(new Header("Credit Memos", "Money owed back to customers: returns, overcharges and discounts", A.Can("sales.write") ? Ui.B("New Credit Memo", BK.Primary, () => Nav("#/credits/new")) : null));
            if (all.Count == 0) root.Add(new Notice(NK.Ok, "No credit memos yet. Make one when you owe a customer money back. It can then be used against their open invoices, or refunded."));
            var g = new Grid(); g.Cols(Grid.Col("Number", 1.2f), Grid.Col("Date", 1.1f), Grid.Col("Customer", 3f), Grid.Col(">Total", 1.2f), Grid.Col(">Left to use", 1.3f), Grid.Col("Status", 1f));
            foreach (var c in all.Items)
            {
                string st = c["status"].Str;
                g.Add(c["id"].Long, c["number"].Str, Fmt.Show(c["memo_date"].Str), c["contact_name"].Str, Fmt.Money(c["total_cents"].Long), st == "void" ? "" : Fmt.Money(c["remaining_cents"].Long),
                    st == "void" ? new GC("Void", Theme.Bad) : st == "used" ? new GC("Used up", Theme.Muted) : new GC("Open", Theme.Good));
            }
            g.Activated += tag => { if (tag is long) Nav("#/credit/" + tag); };
            root.AddFill(g);
            SetBody(root);
        }
    }

    /// <summary>One credit memo: its lines, what it has been used for, and what can still be done with it.</summary>
    sealed class CreditDetailScreen : Screen
    {
        readonly int id;
        public CreditDetailScreen(int id) { this.id = id; }

        public override async Task Load()
        {
            var d = await A.Get("api/credits/" + id);
            string status = d["status"].Str; bool write = A.Can("sales.write"); long left = d["remaining_cents"].Long;
            var page = new ScrollPage(); var s = page.Body;
            var head = new Header("Credit Memo " + d["number"].Str, d["contact_name"].Str + "   ·   " + Fmt.Show(d["memo_date"].Str));
            head.AddAction(new Pill(status == "used" ? "Used up" : Fmt.Cap(status), status == "open" ? PK.Good : status == "void" ? PK.Bad : PK.Neutral));

            var lines = new Grid { AutoHeight = true };
            lines.Cols(Grid.Col("Description", 3f), Grid.Col(">Qty", 0.8f), Grid.Col(">Price", 1.2f), Grid.Col(">Amount", 1.2f), Grid.Col("Account", 2f));
            foreach (var l in d["lines"].Items) lines.Add(null, l["description"].Str, Fmt.Qty(l["qty_milli"].Long), Fmt.Money(l["unit_price_cents"].Long), Fmt.Money(l["amount_cents"].Long), l["account_name"].Str);
            if (d["tax_cents"].Long > 0)
            {
                lines.Add(null, RS.Sub, "Subtotal", "", "", Fmt.Money(d["subtotal_cents"].Long), "");
                foreach (var x in d["tax"].Items) lines.Add(null, "Sales tax: " + x["name"].Str + " (" + SalesTaxScreen.Pct(x["rate_milli"].Long) + " of " + Fmt.Money(x["taxable_cents"].Long) + ")", "", "", Fmt.Money(x["tax_cents"].Long), "");
            }
            else if (d["tax_exempt"].Long == 1) lines.Add(null, RS.Sub, "Tax-exempt customer: no sales tax", "", "", "", "");
            lines.Add(null, RS.Total, "Credit total", "", "", Fmt.Money(d["total_cents"].Long), "");
            if (d["applied_cents"].Long > 0) lines.Add(null, RS.Sub, "Applied to invoices", "", "", Fmt.Money(d["applied_cents"].Long), "");
            if (d["refunded_cents"].Long > 0) lines.Add(null, RS.Sub, "Refunded", "", "", Fmt.Money(d["refunded_cents"].Long), "");
            lines.Add(null, RS.Total, "Left to use", "", "", Fmt.Money(status == "void" ? 0 : left), "");
            lines.ClearSelection();

            head.AddAction(Ui.B("Print", BK.Normal, () => PrintHelper.Print("Credit memo " + d["number"].Str, d["contact_name"].Str + " — " + Fmt.Show(d["memo_date"].Str), lines), true));
            head.AddAction(Ui.BA("Save as PDF", BK.Normal, () => PdfActions.Save(d, true, false), true));
            head.AddAction(Ui.BA("Email", BK.Normal, () => PdfActions.Save(d, true, true), true));
            if (write && status == "open") head.AddAction(Ui.BA("Apply to an Invoice", BK.Primary, () => ApplyDialog(d), true));
            if (write && status == "open") head.AddAction(Ui.BA("Refund", BK.Normal, () => RefundDialog(d), true));
            if (write && status != "void" && d["applied_cents"].Long == 0 && d["refunded_cents"].Long == 0) head.AddAction(Ui.BA("Void", BK.Danger, async () =>
            {
                if (!Msg.Confirm("Void this credit memo?", "A reversing entry will be posted and the customer owes the money again. This cannot be undone.", "Void", true)) return;
                await A.Post("api/credits/" + id + "/void"); App.Toast("Voided"); Nav("#/credits");
            }, true));
            s.Add(head); s.Add(lines);
            if ((d["memo"].Str ?? "").Length > 0) s.Add(new Lbl(d["memo"].Str, LS.Muted, true));

            if (d["applications"].Count > 0)
            {
                s.Add(new Lbl("Used on invoices", LS.H2));
                var ag = new Grid { AutoHeight = true }; ag.Cols(Grid.Col("Invoice", 2f), Grid.Col("Applied on", 1.4f), Grid.Col(">Amount", 1.2f));
                foreach (var a in d["applications"].Items) ag.Add(a["id"].Long, a["document_number"].Str, Fmt.Show(a["applied_on"].Str), Fmt.Money(a["amount_cents"].Long));
                s.Add(ag);
                if (write) s.Add(Ui.BA("Remove Selected Use", BK.Normal, async () =>
                {
                    var tag = ag.SelectedTag; if (!(tag is long)) { App.Toast("Click a line in the list first."); return; }
                    if (!Msg.Confirm("Take this credit back off the invoice?", "The invoice owes that amount again, and the credit becomes available again.", "Remove")) return;
                    await A.Post("api/credits/applications/" + tag + "/remove"); App.Toast("Removed"); App.Main.Reload();
                }, true), 0);
            }
            var refunds = d["refunds"].Items.ToList();
            if (refunds.Count > 0)
            {
                s.Add(new Lbl("Refunds", LS.H2));
                var rg = new Grid { AutoHeight = true }; rg.Cols(Grid.Col("Date", 1.2f), Grid.Col("Method", 1.2f), Grid.Col("Reference", 1.4f), Grid.Col("Paid from", 2f), Grid.Col(">Amount", 1.2f), Grid.Col("Status", 1f));
                foreach (var r in refunds) rg.Add(r["id"].Long, Fmt.Show(r["pay_date"].Str), r["method"].Str, r["ref"].Str, r["bank_name"].Str, Fmt.Money(r["amount_cents"].Long), r["voided"].Long == 1 ? new GC("Void", Theme.Bad) : new GC("Paid", Theme.Good));
                s.Add(rg);
                if (write) s.Add(Ui.BA("Void Selected Refund", BK.Normal, async () =>
                {
                    var tag = rg.SelectedTag; if (!(tag is long)) { App.Toast("Click a refund in the list first."); return; }
                    if (!Msg.Confirm("Void this refund?", "A reversing entry will be posted and the amount is left on the credit memo again.", "Void Refund", true)) return;
                    await A.Post("api/credits/refunds/" + tag + "/void"); App.Toast("Refund voided"); App.Main.Reload();
                }, true), 0);
            }
            SetBody(page);
        }

        async Task ApplyDialog(Jv d)
        {
            var open = await A.Get("api/documents?kind=invoice&status=open");
            var invoices = open.Items.Where(x => x["contact_id"].Long == d["contact_id"].Long && x["total_cents"].Long > x["paid_cents"].Long).ToList();
            if (invoices.Count == 0) { Msg.Info("No open invoices", d["contact_name"].Str + " has no open invoices to use this credit on. You can refund it instead, or keep it for their next invoice."); return; }
            long left = d["remaining_cents"].Long;
            var dlg = new Dlg("Apply to an Invoice", 580);
            var pick = Ui.Cb(invoices.Select(x => Ui.It(x["id"].Str, x["number"].Str + "  (" + Fmt.Money(x["total_cents"].Long - x["paid_cents"].Long) + " owed)")), invoices[0]["id"].Str, 360);
            var amt = new Input("0.00") { RightAlign = true };
            Action fill = () => { var x = invoices.First(i => i["id"].Str == pick.Value); amt.Text = Fmt.Plain(Math.Min(left, x["total_cents"].Long - x["paid_cents"].Long)); };
            pick.Changed += (a, b) => fill(); fill();
            dlg.Body.Add(new Lbl(d["number"].Str + " has " + Fmt.Money(left) + " left. Choose an invoice and how much to use.", LS.Normal, true));
            var f = new Flow { Fill = Pnl.K.Surface }; f.Add(Ui.F("Invoice", pick, 360)); f.Add(Ui.F("Amount to apply", amt, 150)); dlg.Body.Add(f);
            dlg.Cancel();
            dlg.Primary("Apply Credit", async () =>
            {
                long cents; if (!Fmt.TryMoney(amt.Text, out cents) || cents <= 0) throw new ApiException(0, Say.Money("amount"), Jv.Null);
                await A.Post("api/credits/" + id + "/apply", Json.O("document_id", long.Parse(pick.Value), "amount_cents", cents));
                App.Toast("Credit applied"); App.Main.Reload(); return true;
            });
            dlg.Run();
        }

        async Task RefundDialog(Jv d)
        {
            var accts = await Lists.Accounts();
            var dlg = new Dlg("Refund this Credit", 600);
            var date = new DateInput(Fmt.Today(), 170); var amt = new Input("0.00") { RightAlign = true, Text = Fmt.Plain(d["remaining_cents"].Long) };
            var bank = Ui.Cb(Ui.AccountItems(accts, a => a["subtype"].Str == "bank" || a["subtype"].Str == "credit_card"), "", 250);
            var method = new Input("Check, ACH, card…"); var rf = new Input("Reference / check #");
            dlg.Body.Add(new Lbl("Use this when you pay the money back to " + d["contact_name"].Str + " instead of using it on an invoice.", LS.Normal, true));
            var f = new Flow { Fill = Pnl.K.Surface }; f.Add(Ui.F("Date", date, 170)); f.Add(Ui.F("Amount", amt, 150)); f.Add(Ui.F("Paid from", bank, 250)); f.Add(Ui.F("Method", method)); f.Add(Ui.F("Reference", rf)); dlg.Body.Add(f);
            dlg.Cancel();
            dlg.Primary("Save Refund", async () =>
            {
                long cents; if (!Fmt.TryMoney(amt.Text, out cents) || cents <= 0) throw new ApiException(0, Say.Money("refund amount"), Jv.Null);
                if (!date.Valid) throw new ApiException(0, Say.Date("refund date"), Jv.Null);
                if (bank.Value.Length == 0) throw new ApiException(0, "Choose the bank or card account the refund was paid from.", Jv.Null);
                await A.Post("api/credits/" + id + "/refund", Json.O("pay_date", date.Value, "amount_cents", cents, "account_id", long.Parse(bank.Value), "method", method.Text, "ref", rf.Text));
                App.Toast("Refund recorded"); App.Main.Reload(); return true;
            });
            dlg.Run();
        }
    }
}
