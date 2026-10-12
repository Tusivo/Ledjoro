using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Ledjoro
{
    /// <summary>Items: the products and services that are chosen on invoices, with their usual price, account and whether sales tax applies.</summary>
    sealed class ItemsScreen : Screen
    {
        static readonly KeyValuePair<string, string>[] Kinds = {
            new KeyValuePair<string, string>("service", "Service"), new KeyValuePair<string, string>("product", "Product"), new KeyValuePair<string, string>("nonstock", "Other charge") };

        public override async Task Load()
        {
            Lists.Bust();
            var items = await A.Get("api/items"); var accts = await Lists.Accounts();
            bool write = A.Can("sales.write");
            var root = new Stack { Padding = new Padding(Theme.Px(24)), Gap = Theme.Px(12) };
            root.Add(new Header("Items", "Products and services you sell, with their usual price", write ? Ui.B("New Item", BK.Primary, () => Ui.Run(() => Edit(null, accts))) : null));
            if (items.Count == 0)
                root.Add(new Notice(NK.Ok, "No items yet. Add the things you sell (a service, a product) with their usual price, and choose them on an invoice instead of typing the same description every time."));
            var g = new Grid(); g.Cols(Grid.Col("Name", 2.4f), Grid.Col("Type", 1.1f), Grid.Col(">Price", 1.1f), Grid.Col("Income account", 2f), Grid.Col("Sales tax", 1f), Grid.Col("", 0.9f));
            foreach (var i in items.Items.OrderBy(x => x["name"].Str, StringComparer.CurrentCultureIgnoreCase))
            {
                var acct = accts.Items.FirstOrDefault(a => a["id"].Long == i["income_account_id"].Long);
                string kind = Kinds.First(k => k.Key == i["kind"].Str).Value;
                g.Add(i, i["name"].Str, kind, Fmt.Money(i["unit_price_cents"].Long), acct == null ? "" : acct["name"].Str, i["taxable"].Long == 1 ? "Taxable" : "Not taxable", i["active"].Long == 1 ? "" : "Inactive");
            }
            if (write) g.Activated += tag => Ui.Run(() => Edit((Jv)tag, accts));
            root.AddFill(g);
            if (items.Count > 0 && write) root.Add(new Lbl("Double-click an item to change it. Changing an item does not change invoices you have already made. An item that is no longer sold can be made inactive.", LS.Muted, true));
            SetBody(root);
        }

        static async Task Edit(Jv it, Jv accts)
        {
            var dlg = new Dlg(it == null ? "New Item" : "Change " + it["name"].Str, 620);
            var name = new Input("Website design") { Text = it == null ? "" : it["name"].Str };
            var kind = Ui.Cb(Kinds.Select(k => Ui.It(k.Key, k.Value)), it == null ? "service" : it["kind"].Str, 200);
            var desc = new Input("Shown on the invoice (optional)") { Text = it == null ? "" : it["description"].Str };
            var price = new Input("0.00") { Text = it == null ? "" : Fmt.Plain(it["unit_price_cents"].Long), RightAlign = true };
            var income = Ui.Cb(Ui.AccountItems(accts, a => a["type"].Str == "income" || a["type"].Str == "other_income", "Choose an income account…"), it == null || it["income_account_id"].IsNull ? "" : it["income_account_id"].Str, 360);
            var expense = Ui.Cb(Ui.AccountItems(accts, a => a["type"].Str == "expense" || a["type"].Str == "cogs", "None (optional)"), it == null || it["expense_account_id"].IsNull ? "" : it["expense_account_id"].Str, 360);
            var taxable = new Check("Sales tax applies to this item", it == null || it["taxable"].Long == 1);
            var active = new Check("In use (untick when you no longer sell it)", it == null || it["active"].Long == 1);
            dlg.Body.Gap = Theme.Px(10);
            var r1 = new Flow { Fill = Pnl.K.Surface }; r1.Add(Ui.F("Name", name, 330)); r1.Add(Ui.F("Type", kind, 200)); dlg.Body.Add(r1);
            dlg.Body.Add(Ui.F("Description on invoices", desc, 560));
            var r2 = new Flow { Fill = Pnl.K.Surface }; r2.Add(Ui.F("Usual price", price, 150)); r2.Add(Ui.F("Income account", income, 360)); dlg.Body.Add(r2);
            dlg.Body.Add(Ui.F("Expense account (for products you also buy)", expense, 360));
            dlg.Body.Add(taxable); if (it != null) dlg.Body.Add(active);
            dlg.Cancel();
            dlg.Primary("Save", async () =>
            {
                long cents; if (price.Text.Trim().Length == 0) cents = 0; else if (!Fmt.TryMoney(price.Text, out cents) || cents < 0) throw new ApiException(0, Say.Money("price"), Jv.Null);
                if (name.Text.Trim().Length == 0) throw new ApiException(0, "Type a name for the item, such as the service or product as you would say it to a customer.", Jv.Null);
                var body = Json.O("name", name.Text.Trim(), "kind", kind.Value, "description", desc.Text.Trim(), "unit_price_cents", cents, "taxable", taxable.Checked,
                    "income_account_id", income.Value.Length > 0 ? (object)long.Parse(income.Value) : null, "expense_account_id", expense.Value.Length > 0 ? (object)long.Parse(expense.Value) : null);
                if (it == null) await App.Api.Post("api/items", body); else { body["active"] = active.Checked; await App.Api.Patch("api/items/" + it["id"].Str, body); }
                Lists.Bust(); App.Toast("Saved"); App.Main.Reload(); return true;
            });
            dlg.Run();
        }
    }
}
