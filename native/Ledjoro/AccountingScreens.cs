using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Ledjoro
{
    sealed class AccountsScreen : Screen
    {
        static readonly Dictionary<string, string> TypeLabel = new Dictionary<string, string> { { "asset", "Assets" }, { "liability", "Liabilities" }, { "equity", "Equity" }, { "income", "Income" }, { "cogs", "Cost of goods sold" }, { "expense", "Expenses" }, { "other_income", "Other income" }, { "other_expense", "Other expenses" } };
        public static readonly Dictionary<string, string> SubLabel = new Dictionary<string, string> {
            { "bank", "Bank" }, { "ar", "Accounts receivable" }, { "other_current_asset", "Other current asset" }, { "fixed_asset", "Fixed asset" }, { "other_asset", "Other asset" }, { "ap", "Accounts payable" },
            { "credit_card", "Credit card" }, { "other_current_liability", "Other current liability" }, { "long_term_liability", "Long-term liability" }, { "equity", "Equity" }, { "retained_earnings", "Retained earnings" },
            { "income", "Income" }, { "other_income", "Other income" }, { "cogs", "Cost of goods sold" }, { "expense", "Expense" }, { "other_expense", "Other expense" } };

        public override async Task Load()
        {
            Lists.Bust(); var accts = await Lists.Accounts();
            var root = new Stack { Padding = new Padding(Theme.Px(24)), Gap = Theme.Px(12) };
            root.Add(new Header("Chart of Accounts", null, A.Can("accounting.write") ? Ui.B("New Account", BK.Primary, () => Edit(null)) : null));
            var grid = new Grid(); grid.Cols(Grid.Col("Number", 0.9f), Grid.Col("Name", 3f), Grid.Col("Type", 2f), Grid.Col(">Balance", 1.4f), Grid.Col("", 0.8f));
            foreach (var t in TypeLabel)
            {
                var rows = accts.Items.Where(a => a["type"].Str == t.Key).ToList(); if (rows.Count == 0) continue;
                bool credit = new[] { "liability", "equity", "income", "other_income" }.Contains(t.Key);
                grid.Add(null, RS.Total, t.Value, "", "", "", "");
                foreach (var a in rows) { long bal = a["balance_cents"].Long; grid.Add(a, a["number"].Str, a["name"].Str, SubLabel.ContainsKey(a["subtype"].Str) ? SubLabel[a["subtype"].Str] : a["subtype"].Str, Fmt.Money(credit ? -bal : bal), a["active"].Bool ? "" : "inactive"); }
            }
            root.AddFill(grid);
            if (A.Can("accounting.write")) grid.Activated += tag => Edit(tag as Jv);
            SetBody(root);
        }

        void Edit(Jv a)
        {
            if (a == null && false) return;
            var dlg = new Dlg(a == null ? "New Account" : "Edit " + a["name"].Str, 560);
            var name = new Input("Account name") { Text = a == null ? "" : a["name"].Str }; var number = new Input("e.g. 6150") { Text = a == null ? "" : a["number"].Str, Width = Theme.Px(150) };
            var sub = Ui.Cb(SubLabel.Select(k => Ui.It(k.Key, k.Value)), a == null ? "expense" : a["subtype"].Str); sub.Enabled = a == null;
            var active = new Check("Active", a == null || a["active"].Bool);
            var f = new Flow { Fill = Pnl.K.Surface }; f.Add(Ui.F("Name", name)); f.Add(Ui.F("Number", number, 150)); f.Add(Ui.F("Type", sub));
            dlg.Body.Add(f); if (a != null) dlg.Body.Add(active);
            dlg.Cancel();
            dlg.Primary("Save", async () =>
            {
                if (a == null) await A.Post("api/accounts", Json.O("name", name.Text, "number", number.Text, "subtype", sub.Value));
                else await A.Patch("api/accounts/" + a["id"].Str, Json.O("name", name.Text, "number", number.Text, "active", active.Checked));
                Lists.Bust(); App.Toast("Saved"); App.Main.Reload(); return true;
            });
            dlg.Run();
        }
    }

    sealed class JournalScreen : Screen
    {
        public override async Task Load()
        {
            var rows = await A.Get("api/journal");
            var root = new Stack { Padding = new Padding(Theme.Px(24)), Gap = Theme.Px(12) };
            root.Add(new Header("Journal", "Every transaction is a balanced entry. Entries are never edited — to correct one, post a reversal.", A.Can("accounting.write") ? Ui.B("New Journal Entry", BK.Primary, () => Nav("#/journal/new")) : null));
            var grid = new Grid(); grid.Cols(Grid.Col("#", 0.6f), Grid.Col("Date", 1.1f), Grid.Col("Memo", 3.6f), Grid.Col("Source", 1.1f), Grid.Col(">Amount", 1.3f), Grid.Col("", 1f));
            foreach (var e in rows.Items) grid.Add(e["id"].Long, e["id"].Str, Fmt.Show(e["entry_date"].Str), e["memo"].Str, Fmt.Source(e["source_type"].Str), Fmt.Money(e["total_cents"].Long), e["reversed_by"].IsNull ? "" : new GC("Reversed", Theme.Warn));
            root.AddFill(grid);
            grid.Activated += tag => Ui.Run(() => Show((long)tag));
            SetBody(root);
        }

        async Task Show(long id)
        {
            var e = await A.Get("api/journal/" + id);
            var dlg = new Dlg("Entry #" + e["id"].Str, 640);
            dlg.Body.Add(new Lbl(Fmt.Show(e["entry_date"].Str) + "   ·   " + e["memo"].Str + "   ·   Source: " + Fmt.Source(e["source_type"].Str), LS.Muted, true));
            var g = new Grid { AutoHeight = true }; g.Cols(Grid.Col("Account", 3f), Grid.Col("Contact", 2f), Grid.Col(">Debit", 1.2f), Grid.Col(">Credit", 1.2f));
            foreach (var l in e["lines"].Items) g.Add(null, l["account_name"].Str, l["contact_name"].Str, l["debit_cents"].Long > 0 ? Fmt.Money(l["debit_cents"].Long) : "", l["credit_cents"].Long > 0 ? Fmt.Money(l["credit_cents"].Long) : "");
            g.ClearSelection(); dlg.Body.Add(g, null, g.PreferredHeight());
            string src = e["source_type"].Str;
            if (A.Can("accounting.write") && !new[] { "invoice", "bill", "payment", "reversal", "credit_memo", "credit_refund", "salestax" }.Contains(src))
                dlg.Primary("Reverse Entry", async () => { if (!Msg.Confirm("Reverse This Entry?", "A reversing entry will be posted.", "Reverse Entry", true)) return false; await A.Post("api/journal/" + id + "/reverse"); App.Toast("Reversal posted"); App.Main.Reload(); return true; }, BK.Danger);
            dlg.Cancel("Close");
            dlg.FixedBodyHeight = 0; dlg.Run();
        }
    }

    sealed class JournalFormScreen : Screen
    {
        sealed class JLine : RowLayout
        {
            public readonly Combo Account; public readonly Input Debit, Credit, Memo;
            public JLine(Jv accts, Action changed) : base(3f, 1.2f, 1.2f, 2.4f)
            {
                Account = new Combo(100); Account.SetItems(Ui.AccountItems(accts, null, "Choose account…"), "");
                Debit = new Input("0.00") { RightAlign = true, Width = 80 }; Credit = new Input("0.00") { RightAlign = true, Width = 80 }; Memo = new Input("Memo") { Width = 100 };
                foreach (Control c in new Control[] { Account, Debit, Credit, Memo }) Controls.Add(c);
                Debit.Changed += (s, e) => changed(); Credit.Changed += (s, e) => changed();
            }
        }

        public override async Task Load()
        {
            var accts = await Lists.Accounts();
            var page = new ScrollPage(); var s = page.Body;
            s.Add(new Header("New Journal Entry"));
            var date = new DateInput(Fmt.Today(), 170); var memo = new Input("What is this entry for?") { Width = Theme.Px(480) };
            var top = new Card(); var f = new Flow { Fill = Pnl.K.Surface }; f.Add(Ui.F("Date", date, 170)); f.Add(Ui.F("Memo", memo, 480)); top.Add(f); s.Add(top);

            var box = new Stack { Gap = Theme.Px(4) };
            var head = new RowLayout(3f, 1.2f, 1.2f, 2.4f) { RowHeight = Theme.Px(22) };
            foreach (var h in new[] { "Account", "Debit", "Credit", "Memo" }) head.Controls.Add(new Lbl(h, LS.Muted));
            box.Add(head, 0);
            var rows = new List<JLine>(); var diff = new Lbl("", LS.Bold);
            Action update = () =>
            {
                long d = 0, c = 0; foreach (var r in rows) { long v; if (Fmt.TryMoney(r.Debit.Text, out v)) d += v; if (Fmt.TryMoney(r.Credit.Text, out v)) c += v; }
                diff.Text = d == c && d > 0 ? "Balanced · " + Fmt.Money(d) : d == c ? "Enter amounts" : "Out of balance by " + Fmt.Money(Math.Abs(d - c)); diff.Tint = d == c && d > 0 ? Theme.Good : Theme.Bad;
            };
            Action add = () => { var r = new JLine(accts, update); rows.Add(r); box.Add(r, 4); };
            add(); add(); update(); s.Add(box);
            var err = new Notice(NK.Error);
            var bar = new Flow(); bar.Add(Ui.B("Add Line", BK.Normal, () => { add(); page.PerformLayout(); })); bar.Add(diff); s.Add(bar); s.Add(err);
            var acts = new Flow();
            acts.Add(Ui.BA("Post Entry", BK.Primary, async () =>
            {
                err.Reset();
                try
                {
                    if (!date.Valid) throw new ApiException(0, Say.Date("date of this entry"), Jv.Null);
                    var lines = new List<object>();
                    foreach (var r in rows.Where(x => x.Account.Value.Length > 0))
                    {
                        long d = 0, c = 0; if (r.Debit.Text.Trim().Length > 0 && !Fmt.TryMoney(r.Debit.Text, out d)) throw new ApiException(0, Say.Money("debit amount on a line"), Jv.Null); if (r.Credit.Text.Trim().Length > 0 && !Fmt.TryMoney(r.Credit.Text, out c)) throw new ApiException(0, Say.Money("credit amount on a line"), Jv.Null);
                        lines.Add(Json.O("account_id", long.Parse(r.Account.Value), "debit_cents", d, "credit_cents", c, "memo", r.Memo.Text));
                    }
                    await A.Post("api/journal", Json.O("date", date.Value, "memo", memo.Text, "lines", lines));
                    App.Toast("Entry posted"); Nav("#/journal");
                }
                catch (ApiException e) { err.Set(e.Message); }
            }));
            acts.Add(Ui.B("Cancel", BK.Normal, () => Nav("#/journal"))); s.Add(acts);
            SetBody(page);
        }
    }
}
