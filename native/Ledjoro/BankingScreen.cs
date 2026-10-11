using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Ledjoro
{
    sealed class BankingScreen : Screen
    {
        string tab = "new"; public BankingScreen(string startTab = "new") { tab = startTab; } Grid grid; TabBar tabs; Card actions; Combo category, payee; Check always; Lbl selInfo; Card matchCard; Lbl matchInfo; Btn matchBtn, removeRule; Jv matchFor; int matchToken; Jv accts, contacts;

        public override async Task Load()
        {
            accts = await Lists.Accounts(); contacts = await Lists.Contacts();
            var root = new Stack { Padding = new Padding(Theme.Px(24)), Gap = Theme.Px(12) };
            var hdr = new Header("Banking", "Review imported bank and card transactions");
            if (A.Can("banking.write")) hdr.AddAction(Ui.BA("Apply Rules", BK.Normal, async () => { var r = await A.Post("api/bank/apply-rules"); App.Toast(r["applied"].Str + " categorized by rules"); await Fill(); }, true));
            if (A.Can("banking.write")) hdr.AddAction(Ui.BA("Match Automatically", BK.Normal, async () => { var r = await A.Post("api/bank/auto-match"); App.Toast(r["matched"].Long == 0 ? "Nothing matched for certain. Open a transaction to see possible matches." : r["matched"].Str + " matched to entries already in your books"); await Fill(); }, true));
            hdr.AddAction(Ui.B("Reconcile", BK.Normal, () => Nav("#/reconcile"), true));
            if (A.Can("import")) hdr.AddAction(Ui.B("Import Statement", BK.Primary, () => Nav("#/import"), true));
            root.Add(hdr);
            if (A.Can("import")) { var watch = new WatchPanel(false); watch.Imported += () => Ui.Run(Fill); root.Add(watch); Ui.Run(async () => { await watch.Load(); Vx.Relayout(root); }); }
            tabs = root.Add(new TabBar()); tabs.Set(new[] { Ui.It("new", "To review"), Ui.It("categorized", "Categorized"), Ui.It("excluded", "Excluded"), Ui.It("rules", "Rules") }, tab);
            grid = new Grid(); root.AddFill(grid);

            actions = new Card { Visible = false }; actions.Padding = new Padding(Theme.Px(14));
            selInfo = new Lbl("Select a transaction above, then choose a category.", LS.Muted, true);
            category = new Combo(280); category.SetItems(Ui.AccountItems(accts, a => a["subtype"].Str != "bank", "Choose category…"), "");
            payee = new Combo(240); always = new Check("Always do this for similar descriptions");
            var row = new Flow(); row.Fill = Pnl.K.Surface;
            row.Add(Ui.F("Category", category, 280)); row.Add(Ui.F("Payee (optional)", payee, 240));
            var btns = new Flow { Fill = Pnl.K.Surface }; btns.Add(always); btns.Add(Ui.BA("Add", BK.Primary, Categorize)); btns.Add(Ui.BA("Exclude", BK.Normal, Exclude));
            actions.Add(selInfo, Theme.Px(8)); actions.Add(row, Theme.Px(8)); actions.Add(btns, 0);
            root.Add(actions);
            matchCard = new Card { Visible = false }; matchCard.Padding = new Padding(Theme.Px(14));
            matchInfo = new Lbl("", LS.Normal, true); matchBtn = Ui.BA("This is the same one: match It", BK.Primary, MatchIt);
            matchCard.Add(new Lbl("Already in your books?", LS.H3), Theme.Px(4)); matchCard.Add(matchInfo, Theme.Px(8)); matchCard.Add(matchBtn, 0);
            root.Add(matchCard);
            removeRule = Ui.BA("Remove the selected Rule", BK.Normal, RemoveRule); removeRule.Visible = false; root.Add(removeRule);

            tabs.Changed += k => App.Main.Go("#/banking/" + k); // each tab is a page of its own, so Back returns to the previous one
            grid.SelectionChanged += (s, e) => UpdatePayee();
            SetBody(root);
            await Fill();
        }

        void UpdatePayee()
        {
            var t = grid.SelectedTag as Jv; if (t == null || tab != "new") return;
            long amt = t["amount_cents"].Long;
            payee.SetItems(Ui.ContactItems(contacts, amt > 0 ? "customer" : "vendor", "No payee"), "");
            selInfo.Text = Fmt.Show(t["tx_date"].Str) + "   " + t["description"].Str + "   " + Fmt.Money(amt);
            Ui.Run(() => ShowMatches(t));
        }

        // a bank row that matches an entry already in the books (same amount, within ten days) is offered as a match instead of being posted twice
        async Task ShowMatches(Jv t)
        {
            int token = ++matchToken; matchFor = null; matchCard.Visible = false;
            if (!A.Can("banking.write")) return;
            var m = await A.Get("api/bank/transactions/" + t["id"].Str + "/matches");
            if (token != matchToken || m.Count == 0) return;
            var c = m[0]; matchFor = t; matchLine = c["line_id"].Long;
            string what = c["memo"].Str.Length > 0 ? c["memo"].Str : c["contact_name"].Str.Length > 0 ? c["contact_name"].Str : "an entry";
            matchInfo.Text = "Your books already have \u201C" + what + "\u201D dated " + Fmt.Show(c["date"].Str) + " for this amount" + (m.Count > 1 ? " (and " + (m.Count - 1) + " more like it)" : "") + ". If it is the same money, match it so it is not counted twice.";
            matchCard.Visible = true; Vx.Relayout(matchCard);
        }
        long matchLine;
        async Task MatchIt()
        {
            if (matchFor == null) return;
            await A.Post("api/bank/transactions/" + matchFor["id"].Str + "/match", Json.O("line_id", matchLine)); App.Toast("Matched"); matchCard.Visible = false; await Fill();
        }
        async Task RemoveRule()
        {
            var r = grid.SelectedTag as Jv; if (r == null) { App.Toast("Click a rule in the list first.", true); return; }
            if (!Msg.Confirm("Remove this rule?", "Transactions whose description contains \u201C" + r["match_text"].Str + "\u201D will no longer be sorted automatically. Transactions already sorted stay as they are.", "Remove")) return;
            await A.Post("api/bank/rules/" + r["id"].Str + "/remove"); await Fill();
        }

        async Task Fill()
        {
            string status = tab == "rules" ? "" : tab;
            actions.Visible = tab == "new" && A.Can("banking.write"); matchCard.Visible = false; matchFor = null; removeRule.Visible = tab == "rules" && A.Can("banking.write");
            if (tab == "rules")
            {
                var rules = await A.Get("api/bank/rules");
                grid.Cols(Grid.Col("Name", 2f), Grid.Col("If description contains", 2.5f), Grid.Col("Categorize as", 2.2f));
                grid.Clear(); foreach (var r in rules.Items) grid.Add(r, r["name"].Str, r["match_text"].Str, r["account_name"].Str);
                if (rules.Count == 0) App.Toast("No rules yet. Use “Always do this” when categorizing.");
                return;
            }
            var txns = await A.Get("api/bank/transactions?status=" + status);
            grid.Cols(Grid.Col("Date", 1f), Grid.Col("Account", 1.6f), Grid.Col("Description", 3.5f), Grid.Col(">Amount", 1.2f), Grid.Col("Status", 1f));
            grid.Clear();
            foreach (var t in txns.Items) grid.Add(t, Fmt.Show(t["tx_date"].Str), t["account_name"].Str, t["description"].Str, Fmt.Money(t["amount_cents"].Long), t["status"].Str == "new" ? "" : t["status"].Str);
            if (tab == "new") { if (txns.Count == 0) App.Toast("All caught up. Import a statement to add transactions."); else UpdatePayee(); }
        }

        async Task Categorize()
        {
            var t = grid.SelectedTag as Jv; if (t == null) { App.Toast("Click a transaction in the list first.", true); return; }
            if (category.Value.Length == 0) { App.Toast("Choose which account (category) this transaction belongs to, then press Add.", true); return; }
            var body = Json.O("account_id", long.Parse(category.Value)); if (payee.Value.Length > 0) body["contact_id"] = long.Parse(payee.Value);
            await A.Post("api/bank/transactions/" + t["id"].Str + "/categorize", body);
            if (always.Checked)
            {
                string words = string.Join(" ", t["description"].Str.ToLowerInvariant().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Take(2));
                var rule = Json.O("name", t["description"].Str.Substring(0, Math.Min(40, t["description"].Str.Length)), "match_text", words, "account_id", long.Parse(category.Value)); if (payee.Value.Length > 0) rule["contact_id"] = long.Parse(payee.Value);
                await A.Post("api/bank/rules", rule);
            }
            App.Toast("Added"); await Fill();
        }
        async Task Exclude()
        {
            var t = grid.SelectedTag as Jv; if (t == null) { App.Toast("Click a transaction in the list first.", true); return; }
            await A.Post("api/bank/transactions/" + t["id"].Str + "/exclude"); await Fill();
        }
    }
}
