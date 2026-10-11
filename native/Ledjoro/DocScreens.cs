using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Ledjoro
{
    // ------------------------------------------------------------ invoice / bill lists
    sealed class DocListScreen : Screen
    {
        readonly string kind; string status = "open"; Grid grid; TabBar tabs;
        public DocListScreen(string kind, string status = "open") { this.kind = kind; this.status = status; }

        public override async Task Load()
        {
            bool inv = kind == "invoice";
            var root = new Stack { Padding = new Padding(Theme.Px(24)), Gap = Theme.Px(12) };
            Btn newBtn = A.Can(inv ? "sales.write" : "purchases.write") ? Ui.B(inv ? "New Invoice" : "New Bill", BK.Primary, () => Nav("#/" + kind + "s/new")) : null;
            root.Add(new Header(inv ? "Invoices" : "Bills", inv ? "Money customers owe you" : "Money you owe suppliers", newBtn));
            tabs = root.Add(new TabBar()); tabs.Set(new[] { Ui.It("open", "Open"), Ui.It("paid", "Paid"), Ui.It("void", "Void"), Ui.It("all", "All") }, status);
            if (status == "void") root.Add(new Lbl("Voided " + kind + "s stay on record so history is never lost. Voiding one posts a reversing entry.", LS.Muted, true));
            grid = new Grid(); grid.Cols(Grid.Col("Number", 1.1f), Grid.Col(inv ? "Customer" : "Vendor", 2.4f), Grid.Col("Date", 1.1f), Grid.Col("Due", 1.1f), Grid.Col(">Total", 1.2f), Grid.Col(">Balance", 1.2f), Grid.Col("Status", 1f));
            root.AddFill(grid);
            grid.Activated += tag => Nav("#/document/" + tag);
            tabs.Changed += k => Nav("#/" + kind + "s/" + k); // each tab is a page of its own, so Back returns to the previous tab
            SetBody(root);
            await Fill();
        }

        async Task Fill()
        {
            var rows = await A.Get("api/documents?kind=" + kind + (status == "all" ? "" : "&status=" + status));
            grid.Clear();
            foreach (var d in rows.Items)
            {
                string st = d["status"].Str; GC cell = d["overdue"].Long == 1 ? new GC("Overdue", Theme.Bad, true) : st == "paid" ? new GC("paid", Theme.Good) : st == "void" ? new GC("void", Theme.Muted) : new GC(st);
                grid.Add(d["id"].Long, d["number"].Str, d["contact_name"].Str, Fmt.Show(d["doc_date"].Str), Fmt.Show(d["due_date"].Str), Fmt.Money(d["total_cents"].Long), Fmt.Money(d["total_cents"].Long - d["paid_cents"].Long), cell);
            }
            if (grid.Rows.Count == 0) App.Toast("No " + (status == "all" ? "" : status + " ") + kind + "s.");
        }
    }

    // ------------------------------------------------------------ one invoice / bill
    sealed class DocDetailScreen : Screen
    {
        readonly int id;
        public DocDetailScreen(int id) { this.id = id; }

        public override async Task Load()
        {
            var d = await A.Get("api/documents/" + id);
            bool inv = d["kind"].Str == "invoice"; string status = d["status"].Str;
            bool write = A.Can(inv ? "sales.write" : "purchases.write");
            long total = d["total_cents"].Long, paid = d["paid_cents"].Long;
            var page = new ScrollPage(); var s = page.Body;

            var head = new Header((inv ? "Invoice " : "Bill ") + d["number"].Str, d["contact_name"].Str + "   ·   " + Fmt.Show(d["doc_date"].Str) + "   ·   due " + Fmt.Show(d["due_date"].Str));
            head.AddAction(new Pill(status, status == "paid" ? PK.Good : status == "void" ? PK.Bad : PK.Neutral));
            var lines = new Grid { AutoHeight = true };
            lines.Cols(Grid.Col("Description", 3f), Grid.Col(">Qty", 0.8f), Grid.Col(">Price", 1.2f), Grid.Col(">Amount", 1.2f), Grid.Col("Account", 2f));
            foreach (var l in d["lines"].Items) lines.Add(null, l["description"].Str, Fmt.Qty(l["qty_milli"].Long), Fmt.Money(l["unit_price_cents"].Long), Fmt.Money(l["amount_cents"].Long), l["account_name"].Str);
            if (d["tax_cents"].Long > 0 || d["tax_exempt"].Long == 1)
            {
                lines.Add(null, RS.Sub, "Subtotal", "", "", Fmt.Money(d["subtotal_cents"].Long), "");
                foreach (var x in d["tax"].Items) lines.Add(null, "Sales tax: " + x["name"].Str + " (" + SalesTaxScreen.Pct(x["rate_milli"].Long) + " of " + Fmt.Money(x["taxable_cents"].Long) + ")", "", "", Fmt.Money(x["tax_cents"].Long), "");
                if (d["tax_exempt"].Long == 1) lines.Add(null, RS.Sub, "Tax-exempt customer: no sales tax charged", "", "", "", "");
            }
            lines.Add(null, RS.Total, "Total", "", "", Fmt.Money(total), "");
            lines.Add(null, RS.Sub, "Paid", "", "", Fmt.Money(paid), "");
            lines.Add(null, RS.Total, "Balance due", "", "", Fmt.Money(total - paid), "");
            lines.ClearSelection();

            var pays = new Grid { AutoHeight = true };
            pays.Cols(Grid.Col("Date", 1f), Grid.Col("Method", 1.2f), Grid.Col("Reference", 1.4f), Grid.Col(">Amount", 1.2f), Grid.Col("Status", 1f));
            foreach (var p in d["payments"].Items) pays.Add(p["payment_id"].Long, Fmt.Show(p["pay_date"].Str), p["method"].Str, p["ref"].Str, Fmt.Money(p["amount_cents"].Long), p["voided"].Long == 1 ? new GC("void", Theme.Bad) : new GC("applied", Theme.Good));

            head.AddAction(Ui.B("Print", BK.Normal, () => PrintHelper.Print((inv ? "Invoice " : "Bill ") + d["number"].Str, d["contact_name"].Str + " — " + Fmt.Show(d["doc_date"].Str), lines, pays), true));
            if (write && status == "open") head.AddAction(Ui.BA(inv ? "Receive payment" : "Pay bill", BK.Primary, () => PaymentDialog(d), true));
            if (write && status != "void" && paid == 0) head.AddAction(Ui.BA("Void", BK.Danger, async () =>
            {
                if (!Msg.Confirm("Void this document?", "A reversing entry will be posted. This cannot be undone.", "Void", true)) return;
                await A.Post("api/documents/" + id + "/void"); App.Toast("Voided"); Nav(inv ? "#/invoices" : "#/bills");
            }, true));
            s.Add(head);
            s.Add(lines);
            if (d["memo"].Str.Length > 0) s.Add(new Lbl(d["memo"].Str, LS.Muted, true));
            if (pays.Rows.Count > 0)
            {
                s.Add(new Lbl("Payments", LS.H2));
                s.Add(pays);
                if (write) s.Add(Ui.BA("Void selected Payment", BK.Normal, async () =>
                {
                    var tag = pays.SelectedTag; if (!(tag is long)) { App.Toast("Click a payment in the list first."); return; }
                    if (!Msg.Confirm("Void this payment?", "A reversing entry will be posted.", "Void Payment", true)) return;
                    await A.Post("api/payments/" + tag + "/void"); App.Toast("Payment voided"); App.Main.Reload();
                }, true), 0);
            }
            SetBody(page);
        }

        async Task PaymentDialog(Jv d)
        {
            bool inv = d["kind"].Str == "invoice"; long due = d["total_cents"].Long - d["paid_cents"].Long;
            var accts = await Lists.Accounts();
            var dlg = new Dlg(inv ? "Receive payment" : "Pay bill", 560);
            var date = new DateInput(Fmt.Today(), 250); var amt = new Input("0.00") { RightAlign = true, Text = Fmt.Plain(due) };
            var acct = Ui.Cb(Ui.AccountItems(accts, a => a["subtype"].Str == "bank" || a["subtype"].Str == "credit_card")); var method = new Input("Check, ACH, card…"); var rf = new Input("Reference / check #");
            var f = new Flow { Fill = Pnl.K.Surface }; f.Add(Ui.F("Date", date)); f.Add(Ui.F("Amount", amt)); f.Add(Ui.F(inv ? "Deposit to" : "Pay from", acct)); f.Add(Ui.F("Method", method)); f.Add(Ui.F("Reference", rf));
            dlg.Body.Add(f);
            dlg.Cancel();
            dlg.Primary("Save Payment", async () =>
            {
                long cents; if (!Fmt.TryMoney(amt.Text, out cents) || cents <= 0) throw new ApiException(0, Say.Money("payment amount"), Jv.Null);
                if (!date.Valid) throw new ApiException(0, Say.Date("payment date"), Jv.Null);
                await A.Post("api/payments", Json.O("kind", inv ? "customer" : "vendor", "contact_id", d["contact_id"].Long, "pay_date", date.Value, "amount_cents", cents, "account_id", long.Parse(acct.Value),
                    "method", method.Text, "ref", rf.Text, "applications", new[] { Json.O("document_id", d["id"].Long, "amount_cents", Math.Min(cents, due)) }));
                App.Toast("Payment recorded"); App.Main.Reload(); return true;
            });
            dlg.Run();
        }
    }

    // ------------------------------------------------------------ new invoice / bill
    sealed class DocFormScreen : Screen
    {
        readonly string kind;
        public DocFormScreen(string kind) { this.kind = kind; }

        sealed class Line : RowLayout
        {
            public readonly Combo Item, Account; public readonly Input Desc, Qty, Price; public readonly Check Tax = new Check("", true); public readonly Lbl Amount = new Lbl("$0.00") { RightAlign = true, VCenter = true }; public readonly Btn Remove;
            public Line(Jv items, Jv accts, bool inv, Action changed, Action<Line> remove) : base(inv ? new[] { 2.0f, 2.9f, 0.8f, 1.2f, 1.2f, 0.6f, 2.0f, 0.5f } : new[] { 2.1f, 3.1f, 0.9f, 1.3f, 1.3f, 2.2f, 0.5f })
            {
                Fill = K.Inherit;
                var list = new List<KeyValuePair<string, string>> { Ui.It("", "— item —") }; list.AddRange(items.Items.Where(i => i["active"].Bool).Select(i => Ui.It(i["id"].Str, i["name"].Str)));
                Item = new Combo(100); Item.SetItems(list, ""); Desc = new Input("Description") { Width = 100 }; Qty = new Input("1") { RightAlign = true, Width = 60, Text = "1" }; Price = new Input("0.00") { RightAlign = true, Width = 80 };
                Account = new Combo(100); Account.SetItems(Ui.AccountItems(accts, a => inv ? (a["type"].Str == "income" || a["type"].Str == "other_income") : (new[] { "expense", "cogs", "other_expense", "asset" }.Contains(a["type"].Str))));
                Remove = new Btn("×", BK.Normal, null, true);
                foreach (Control c in inv ? new Control[] { Item, Desc, Qty, Price, Amount, Tax, Account, Remove } : new Control[] { Item, Desc, Qty, Price, Amount, Account, Remove }) Controls.Add(c);
                Tax.AccessibleName = "Charge sales tax on this line"; Tax.Changed += (s, e) => changed();
                Item.Changed += (s, e) =>
                {
                    var it = items.Items.FirstOrDefault(x => x["id"].Str == Item.Value);
                    if (it != null && !it.IsNull) { Desc.Text = it["name"].Str; Price.Text = Fmt.Plain(it["unit_price_cents"].Long); long aid = inv ? it["income_account_id"].Long : it["expense_account_id"].Long; if (aid > 0) Account.Value = aid.ToString(); if (inv && it["taxable"].Long == 0) Tax.Checked = false; }
                    changed();
                };
                Qty.Changed += (s, e) => changed(); Price.Changed += (s, e) => changed(); Account.Changed += (s, e) => changed();
                Remove.Click += (s, e) => remove(this);
            }
            public long Cents { get { long q, p; return Fmt.TryQty(Qty.Text, out q) && Fmt.TryMoney(Price.Text, out p) ? (q * p + 500) / 1000 : 0; } }
        }

        public override async Task Load()
        {
            bool inv = kind == "invoice"; var A0 = App.Api;
            var contacts = await Lists.Contacts(); var items = await Lists.Items(); var accts = await Lists.Accounts(); var taxJ = inv ? await Lists.TaxRates() : null;
            var page = new ScrollPage(); var s = page.Body;
            s.Add(new Header(inv ? "New Invoice" : "New Bill"));

            var contact = Ui.Cb(Ui.ContactItems(contacts, inv ? "customer" : "vendor", inv ? "Choose a customer…" : "Choose a vendor…"), "");
            var date = new DateInput(Fmt.Today(), 250); var due = new DateInput("", 250); due.Cue = "automatic"; var number = new Input("Automatic"); var memo = new Input("Memo (optional)") { Width = Theme.Px(520) };
            var top = new Card(); var f = new Flow(); f.Fill = Pnl.K.Surface;
            f.Add(Ui.F(inv ? "Customer" : "Vendor", contact));
            if (A0.Can(inv ? "sales.write" : "purchases.write"))
            {   // the person is not in the list yet: add them here, and the form keeps everything typed so far
                var addPerson = Ui.B(inv ? "New Customer" : "New Vendor", BK.Primary, () => Ui.Run(() => ContactsScreen.EditContact(inv ? "customer" : "vendor", null, async id =>
                {
                    var fresh = await Lists.Contacts();
                    contact.SetItems(Ui.ContactItems(fresh, inv ? "customer" : "vendor", inv ? "Choose a customer…" : "Choose a vendor…"), id.ToString());
                })));
                f.Add(Ui.F(" ", addPerson, 150));
            }
            f.Add(Ui.F("Date", date, 170)); f.Add(Ui.F("Due date (optional)", due, 170)); f.Add(Ui.F("Number", number, 170));
            Combo taxRate = null; Lbl taxNote = null; Func<Jv> chosenContact = () => contacts.Items.FirstOrDefault(c => c["id"].Str == contact.Value);
            if (inv && taxJ["rates"].Items.Any(r => r["active"].Long == 1))
            {
                var tl = new List<KeyValuePair<string, string>> { Ui.It("", "No sales tax") }; tl.AddRange(taxJ["rates"].Items.Where(r => r["active"].Long == 1).Select(r => Ui.It(r["id"].Str, r["name"].Str + "  (" + SalesTaxScreen.Pct(r["rate_milli"].Long) + ")")));
                taxRate = new Combo(100); taxRate.SetItems(tl, taxJ["default_id"].IsNull ? "" : taxJ["default_id"].Str);
                f.Add(Ui.F("Sales tax", taxRate, 250));
                taxNote = new Lbl("", LS.Muted, true);
            }
            top.Add(f); if (taxNote != null) top.Add(taxNote, Theme.Px(4)); s.Add(top);

            s.Add(new Lbl("Lines", LS.H2));
            var linesBox = new Stack { Gap = Theme.Px(4) }; linesBox.Fill = Pnl.K.Bg;
            var headRow = new RowLayout(inv ? new[] { 2.0f, 2.9f, 0.8f, 1.2f, 1.2f, 0.6f, 2.0f, 0.5f } : new[] { 2.1f, 3.1f, 0.9f, 1.3f, 1.3f, 2.2f, 0.5f }) { RowHeight = Theme.Px(22) };
            foreach (var h in inv ? new[] { "Item", "Description", "Qty", "Price", "Amount", "Tax", "Income account", "" } : new[] { "Item", "Description", "Qty", "Price", "Amount", "Expense account", "" }) headRow.Controls.Add(new Lbl(h, LS.Muted));
            linesBox.Add(headRow, 0);
            var total = new Lbl("$0.00", LS.Bold); var subtotal = new Lbl("$0.00", LS.Normal); var taxTotal = new Lbl("$0.00", LS.Normal);
            var rows = new List<Line>();
            Func<long, long> taxOn = taxable =>
            {   // the same sum the engine makes: each jurisdiction rounded on its own, a combined rate being the sum of its parts
                if (taxRate == null || taxRate.Value.Length == 0) return 0;
                var rate = taxJ["rates"].Items.FirstOrDefault(r => r["id"].Str == taxRate.Value); if (rate == null) return 0;
                var comps = rate["kind"].Str == "group" ? rate["parts"].Items.Select(pid => taxJ["rates"].Items.First(r => r["id"].Long == pid.Long)) : new[] { rate };
                return comps.Sum(c => (taxable * c["rate_milli"].Long + 50000) / 100000);
            };
            Action recalc = () =>
            {
                var cust = chosenContact(); bool exempt = inv && cust != null && !cust.IsNull && cust["tax_exempt"].Long == 1;
                long sub = 0, taxable = 0; foreach (var l in rows) { l.Amount.Text = Fmt.Money(l.Cents); sub += l.Cents; if (l.Tax.Checked) taxable += l.Cents; }
                long tax = exempt ? 0 : taxOn(taxable); subtotal.Text = Fmt.Money(sub); taxTotal.Text = Fmt.Money(tax); total.Text = Fmt.Money(sub + tax);
                if (taxNote != null) taxNote.Text = exempt ? "This customer is tax-exempt" + (cust["tax_exempt_note"].Str.Length > 0 ? " (" + cust["tax_exempt_note"].Str + ")" : "") + ", so no sales tax is charged." : taxRate != null && taxRate.Value.Length > 0 ? "Tax is charged on the lines with Tax ticked." : "";
            };
            if (taxRate != null) taxRate.Changed += (a, b) => recalc();
            contact.Changed += (a, b) =>
            {
                var cust = chosenContact(); if (taxRate == null || cust == null || cust.IsNull) { recalc(); return; }
                if (cust["tax_exempt"].Long == 1) taxRate.Value = ""; else if (cust["tax_rate_id"].Long > 0) taxRate.Value = cust["tax_rate_id"].Str; else taxRate.Value = taxJ["default_id"].IsNull ? "" : taxJ["default_id"].Str;
                recalc();
            };
            Action<Line> remove = null;
            Func<Line> addLine = () => { var l = new Line(items, accts, inv, recalc, remove); rows.Add(l); linesBox.Add(l, 4); recalc(); return l; };
            remove = l => { if (rows.Count <= 1) return; rows.Remove(l); linesBox.Controls.Remove(l); l.Dispose(); linesBox.PerformLayout(); page.PerformLayout(); recalc(); };
            addLine();
            s.Add(linesBox);

            var err = new Notice(NK.Error);
            var bottom = new Flow(); bottom.Add(Ui.B("Add Line", BK.Normal, () => { addLine(); page.PerformLayout(); })); if (inv && taxRate != null) { bottom.Add(new Lbl("Subtotal:", LS.Muted)); bottom.Add(subtotal); bottom.Add(new Lbl("Sales tax:", LS.Muted)); bottom.Add(taxTotal); }
            bottom.Add(new Lbl("Total:", LS.Muted)); bottom.Add(total);
            s.Add(bottom); s.Add(err);
            var actions = new Flow();
            actions.Add(Ui.BA("Save", BK.Primary, async () =>
            {
                err.Reset();
                try
                {
                    if (contact.Value.Length == 0) throw new ApiException(0, "Choose a " + (inv ? "customer" : "vendor") + " from the list. If the list is empty, add one first under " + (inv ? "Customers" : "Vendors") + ".", Jv.Null);
                    if (!date.Valid) throw new ApiException(0, Say.Date("payment date"), Jv.Null);
                    if (due.Text.Trim().Length > 0 && !due.Valid) throw new ApiException(0, Say.Date("due date") + " Or leave it empty to use the customer\u2019s usual payment terms.", Jv.Null);
                    var lines = new List<object>(); int n = 0;
                    foreach (var r in rows)
                    {
                        n++; long q, p;
                        if (!Fmt.TryQty(r.Qty.Text, out q) || q <= 0) throw new ApiException(0, "Line " + n + ": the quantity is missing or is not a number above zero. Type a number such as 1 or 2.5.", Jv.Null);
                        if (!Fmt.TryMoney(r.Price.Text, out p) || p < 0) throw new ApiException(0, "Line " + n + ": " + Say.Money("price").Replace("The price", "the price"), Jv.Null);
                        if (r.Account.Value.Length == 0) throw new ApiException(0, "Line " + n + ": choose which account this line belongs to (for example an income account for an invoice, or an expense account for a bill).", Jv.Null);
                        var line = Json.O("description", r.Desc.Text, "qty_milli", q, "unit_price_cents", p, "account_id", long.Parse(r.Account.Value));
                        if (r.Item.Value.Length > 0) line["item_id"] = long.Parse(r.Item.Value);
                        if (inv) line["taxable"] = r.Tax.Checked;
                        lines.Add(line);
                    }
                    var body = Json.O("kind", kind, "contact_id", long.Parse(contact.Value), "doc_date", date.Value, "memo", memo.Text, "lines", lines);
                    if (due.Text.Trim().Length > 0) body["due_date"] = due.Value; if (number.Text.Trim().Length > 0) body["number"] = number.Text.Trim();
                    if (inv && taxRate != null && taxRate.Value.Length > 0) body["tax_rate_id"] = long.Parse(taxRate.Value);
                    var res = await A.Post("api/documents", body);
                    App.Toast((inv ? "Invoice" : "Bill") + " saved"); Nav("#/document/" + res["id"].Str);
                }
                catch (ApiException e) { err.Set(e.Message); }
            }));
            actions.Add(Ui.B("Cancel", BK.Normal, () => Nav(inv ? "#/invoices" : "#/bills")));
            s.Add(new Field("Memo", memo, 520)); s.Add(actions);
            SetBody(page);
        }
    }
}
