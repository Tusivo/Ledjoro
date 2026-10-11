using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Ledjoro
{
    sealed class AssetsScreen : Screen
    {
        public override async Task Load()
        {
            var rows = await A.Get("api/assets"); var accts = await Lists.Accounts();
            var root = new Stack { Padding = new Padding(Theme.Px(24)), Gap = Theme.Px(12) };
            bool write = A.Can("accounting.write");
            root.Add(new Header("Fixed Assets", "Equipment and other long-lived purchases, depreciated straight-line (full-month convention).", write ? Ui.B("New Asset", BK.Primary, () => AssetDialog()) : null));
            if (write)
            {
                var card = new Card { Padding = new Padding(Theme.Px(14)) }; var f = new Flow { Fill = Pnl.K.Surface };
                var through = new DateInput(Fmt.LastMonthEnd(), 170);
                f.Add(new Lbl("Post depreciation through")); f.Add(through);
                f.Add(Ui.BA("Post Depreciation", BK.Normal, async () =>
                {
                    if (!through.Valid) { App.Toast(Say.Date("date to post depreciation through"), true); return; }
                    var r = await A.Post("api/assets/depreciate", Json.O("through", through.Value)); Lists.Bust();
                    App.Toast(r["entries"].Int > 0 ? "Posted " + r["entries"].Str + " entries (" + Fmt.Money(r["total_cents"].Long) + ")" : "Nothing new to post"); App.Main.Reload();
                }));
                f.Add(new Lbl("Creates one journal entry per asset per month; safe to run repeatedly.", LS.Muted));
                card.Add(f, 0); root.Add(card);
            }
            var grid = new Grid(); grid.Cols(Grid.Col("Asset", 2.6f), Grid.Col("In service", 1.1f), Grid.Col(">Cost", 1.2f), Grid.Col("Life", 0.8f), Grid.Col(">Per month", 1.1f), Grid.Col(">Accumulated", 1.2f), Grid.Col(">Book value", 1.2f), Grid.Col("Status", 1.3f));
            long tc = 0, ta = 0;
            foreach (var a in rows.Items)
            {
                tc += a["cost_cents"].Long; ta += a["accumulated_cents"].Long; string st = a["status"].Str;
                grid.Add(a, a["name"].Str, a["in_service"].Str, Fmt.Money(a["cost_cents"].Long), a["life_months"].Str + " mo", Fmt.Money(a["monthly_cents"].Long), Fmt.Money(a["accumulated_cents"].Long), Fmt.Money(a["book_value_cents"].Long), st == "depreciating" ? new GC(st, Theme.Good) : st == "disposed" ? new GC(st, Theme.Warn) : new GC(st));
            }
            if (rows.Count > 0) grid.Add(null, RS.Total, "Total", "", Fmt.Money(tc), "", "", Fmt.Money(ta), Fmt.Money(tc - ta), "");
            root.AddFill(grid);
            grid.Activated += tag => Ui.Run(() => Schedule(tag as Jv, accts));
            SetBody(root);
            if (rows.Count == 0) App.Toast("No assets yet. Add one to start tracking depreciation.");
        }

        void AssetDialog()
        {
            var dlg = new Dlg("Add fixed asset", 600);
            var name = new Input("e.g. Laptop"); var cost = new Input("0.00") { RightAlign = true }; var salvage = new Input("0.00") { RightAlign = true, Text = "0.00" };
            var date = new DateInput(Fmt.Today(), 250); var life = new Input("60") { Text = "60", RightAlign = true }; var through = new DateInput("", 250); through.Cue = "optional month-end";
            dlg.Body.Add(new Lbl("Record the purchase itself (a bill or journal entry to the equipment account) as usual. This register only tracks depreciation.", LS.Muted, true));
            var f = new Flow { Fill = Pnl.K.Surface }; f.Add(Ui.F("Name", name)); f.Add(Ui.F("Cost", cost)); f.Add(Ui.F("Salvage value", salvage)); f.Add(Ui.F("Placed in service", date)); f.Add(Ui.F("Useful life (months)", life)); f.Add(Ui.F("Already depreciated through", through));
            dlg.Body.Add(f);
            dlg.Cancel();
            dlg.Primary("Add Asset", async () =>
            {
                long c, sv, lf; if (!Fmt.TryMoney(cost.Text, out c) || c <= 0) throw new ApiException(0, "The purchase cost is missing, or is not a valid amount above zero. Type dollars and cents, such as 12500.00.", Jv.Null);
                if (!Fmt.TryMoney(salvage.Text, out sv)) sv = 0; if (!long.TryParse(life.Text.Trim(), out lf) || lf < 1 || lf > 600) throw new ApiException(0, "The useful life must be a whole number of months from 1 to 600 (for example 60 for five years).", Jv.Null);
                if (!date.Valid) throw new ApiException(0, Say.Date("in-service date"), Jv.Null);
                var body = Json.O("name", name.Text, "cost_cents", c, "salvage_cents", sv, "in_service", date.Value, "life_months", lf); if (through.Text.Trim().Length > 0) body["depreciated_through"] = through.Value;
                await A.Post("api/assets", body); App.Toast("Asset added"); App.Main.Reload(); return true;
            });
            dlg.Run();
        }

        async Task Schedule(Jv a, Jv accts)
        {
            var r = await A.Get("api/assets/" + a["id"].Str + "/schedule");
            var dlg = new Dlg(a["name"].Str + " — depreciation schedule", 680);
            var g = new Grid(); g.Cols(Grid.Col("#", 0.5f), Grid.Col("Month end", 1.3f), Grid.Col(">Depreciation", 1.4f), Grid.Col(">Accumulated", 1.4f), Grid.Col("", 1f));
            foreach (var x in r["schedule"].Items.Take(180)) g.Add(null, x["k"].Str, Fmt.Show(x["date"].Str), Fmt.Money(x["amount_cents"].Long), Fmt.Money(x["accumulated_cents"].Long), x["posted"].Bool ? new GC("posted", Theme.Good) : new GC(""));
            dlg.Body.Add(g, null, Theme.Px(380));
            if (A.Can("accounting.write") && a["status"].Str != "disposed") dlg.Primary("Dispose or Sell…", async () => { await Task.FromResult(0); DisposeAsset(a, accts); return true; }, BK.Danger);
            dlg.Cancel("Close"); dlg.Run();
        }

        void DisposeAsset(Jv a, Jv accts)
        {
            var dlg = new Dlg("Dispose of " + a["name"].Str, 580);
            var date = new DateInput(Fmt.Today(), 250); var proceeds = new Input("0.00") { Text = "0.00", RightAlign = true };
            var dep = Ui.Cb(Ui.AccountItems(accts, x => new[] { "bank", "credit_card", "other_current_asset" }.Contains(x["subtype"].Str)));
            dlg.Body.Add(new Lbl("Depreciation must be posted through the month before the disposal date. The entry removes the cost and accumulated depreciation, records any proceeds, and books the gain or loss.", LS.Muted, true));
            var f = new Flow { Fill = Pnl.K.Surface }; f.Add(Ui.F("Disposal date", date)); f.Add(Ui.F("Sale proceeds (0 if scrapped)", proceeds)); f.Add(Ui.F("Deposit to", dep)); dlg.Body.Add(f);
            dlg.Cancel();
            dlg.Primary("Dispose", async () =>
            {
                long p; if (!Fmt.TryMoney(proceeds.Text, out p)) p = 0; if (!date.Valid) throw new ApiException(0, Say.Date("disposal date"), Jv.Null);
                var body = Json.O("date", date.Value, "proceeds_cents", p); if (p > 0 && dep.Value.Length > 0) body["deposit_account_id"] = long.Parse(dep.Value);
                var r = await A.Post("api/assets/" + a["id"].Str + "/dispose", body); Lists.Bust();
                App.Toast(r["gain_cents"].Long >= 0 ? "Gain " + Fmt.Money(r["gain_cents"].Long) : "Loss " + Fmt.Money(-r["gain_cents"].Long)); App.Main.Reload(); return true;
            }, BK.Danger);
            dlg.Run();
        }
    }

    sealed class HealthScreen : Screen
    {
        public override async Task Load()
        {
            var r = await A.Get("api/health-check");
            var page = new ScrollPage(); var s = page.Body;
            s.Add(new Header("Books Health", "An explainable review of your books. Every finding says why it matters and links to the records. Nothing leaves your computer.", Ui.B("Run Again", BK.Normal, () => App.Main.Reload(), true)));
            var stats = new Flow { HGap = Theme.Px(14), VGap = Theme.Px(14) }; int score = r["score"].Int;
            stats.Add(new StatCard(score + " / 100", "Health score", score < 70));
            stats.Add(new StatCard(r["counts"]["high"].Str, "Needs action now", r["counts"]["high"].Int > 0)); stats.Add(new StatCard(r["counts"]["medium"].Str, "Should review")); stats.Add(new StatCard(r["counts"]["low"].Str, "Worth a look")); stats.Add(new StatCard(r["counts"]["info"].Str, "For your information"));
            s.Add(stats);
            if (r["findings"].Count == 0) s.Add(new Notice(NK.Ok, "Nothing to report. Your books look healthy."));
            foreach (var f in r["findings"].Items)
            {
                string sev = f["severity"].Str; var card = new Card(); var head = new Flow { Fill = Pnl.K.Surface };
                head.Add(new Pill(sev, sev == "high" ? PK.Bad : sev == "medium" ? PK.Warn : sev == "info" ? PK.Accent : PK.Neutral)); head.Add(new Lbl(f["title"].Str, LS.Bold)); if (f["count"].Int > 0) head.Add(new Lbl("(" + f["count"].Str + ")", LS.Muted));
                card.Add(head, Theme.Px(6)); card.Add(new Lbl(f["detail"].Str, LS.Normal, true), Theme.Px(8));
                foreach (var i in f["items"].Items)
                {
                    string label = i["label"].Str, hash = i["hash"].Str; var link = new Lbl("•  " + label, hash.Length > 0 ? LS.Link : LS.Normal, true) { Underline = false };
                    if (hash.Length > 0) link.Click += (a, b) => App.Main.Go(hash); card.Add(link, Theme.Px(3));
                }
                if (f["count"].Int > f["items"].Count) card.Add(new Lbl("…and " + (f["count"].Int - f["items"].Count) + " more.", LS.Muted));
                s.Add(card);
            }
            var passed = new Card(); passed.Add(new Lbl(r["passed"].Count + " checks passed", LS.Bold), Theme.Px(6));
            foreach (var p in r["passed"].Items) passed.Add(new Lbl("✓  " + p.Str, LS.Muted, true), Theme.Px(2));
            s.Add(passed);
            SetBody(page);
        }
    }
}
