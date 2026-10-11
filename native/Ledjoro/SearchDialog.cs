using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Ledjoro
{
    /// <summary>Ctrl+K: type a few letters, find a customer, invoice, bill, account, journal entry or page, press Enter to go there.</summary>
    public sealed class SearchDialog
    {
        sealed class Hit { public string Hash; public Hit(string h) { Hash = h; } }
        static readonly string[][] Pages =
        {
            new[] { "Dashboard", "#/dashboard" }, new[] { "Invoices", "#/invoices" }, new[] { "New Invoice", "#/invoices/new" }, new[] { "Customers", "#/customers" },
            new[] { "Bills", "#/bills" }, new[] { "New Bill", "#/bills/new" }, new[] { "Vendors", "#/vendors" }, new[] { "Bank Transactions", "#/banking" },
            new[] { "Bank rules", "#/banking/rules" }, new[] { "Reconcile a statement", "#/reconcile" }, new[] { "Chart of Accounts", "#/accounts" }, new[] { "Journal", "#/journal" },
            new[] { "New Journal Entry", "#/journal/new" }, new[] { "Fixed Assets", "#/assets" }, new[] { "Reports", "#/reports" }, new[] { "Books Health", "#/health" },
            new[] { "Import & Convert", "#/import" }, new[] { "QuickBooks Sync (Web Connector)", "#/quickbooks" }, new[] { "Settings", "#/settings" }, new[] { "About Ledjoro (version)", "#/about" }, new[] { "Backups (Data & Backup)", "#/settings/data" },
        };

        public readonly Dlg Dialog; public readonly Input Box; public readonly Grid Results; readonly System.Windows.Forms.Timer delay = new System.Windows.Forms.Timer { Interval = 200 }; int token;

        public SearchDialog()
        {
            Dialog = new Dlg("Search", 720);
            Box = new Input("Type a name, invoice number, amount or page…", 660); Results = new Grid { MultiSelect = false };
            Results.Cols(Grid.Col("", 1.1f), Grid.Col("Result", 3.4f), Grid.Col("Details", 2.4f));
            Dialog.Body.Add(Box); Dialog.Body.Add(Results, null, Theme.Px(300));
            Dialog.Body.Add(new Lbl("Up and Down choose a result, Enter opens it, Esc closes.", LS.Muted));
            Dialog.Primary("Open", () => { Go(); return Task.FromResult(false); }); Dialog.Cancel("Close");
            delay.Tick += (s, e) => { delay.Stop(); Ui.Run(Search); };
            Box.Changed += (s, e) => { delay.Stop(); delay.Start(); };
            Box.Tb.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Down || e.KeyCode == Keys.Up) { Move(e.KeyCode == Keys.Down ? 1 : -1); e.Handled = e.SuppressKeyPress = true; }
            };
            Results.Activated += o => Go();
            Dialog.Shown += (s, e) => Box.Tb.Focus();
            Dialog.FormClosed += (s, e) => delay.Dispose();
        }

        void Move(int d) { if (Results.Rows.Count == 0) return; int i = Math.Max(0, Math.Min(Results.Rows.Count - 1, (Results.CurrentRow == null ? -1 : Results.CurrentRow.Index) + d)); Results.CurrentCell = Results.Rows[i].Cells[1]; }

        /// <summary>Run the search for what is typed now (also used by the self-test).</summary>
        public async Task Search()
        {
            int mine = ++token; string q = Box.Text.Trim();
            Results.Clear();
            if (q.Length < 2) return;
            var pages = Pages.Where(p => p[0].IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0).Take(4).ToList();
            Jv found = Jv.Null;
            try { found = await App.Api.Get("api/search?q=" + Uri.EscapeDataString(q)); } catch (ApiException) { }
            if (mine != token || Results.IsDisposed) return;
            foreach (var p in pages) Results.Add(new Hit(p[1]), "Page", p[0], "");
            foreach (var r in found.Items) Results.Add(new Hit(r["hash"].Str), r["type"].Str, r["title"].Str, r["detail"].Str);
            if (Results.Rows.Count == 0) Results.Add(null, "", "Nothing found for “" + q + "”", "Try fewer letters, or part of a name.");
            else Results.CurrentCell = Results.Rows[0].Cells[1];
        }

        public void Go()
        {
            var h = Results.SelectedTag as Hit; if (h == null) return;
            Dialog.DialogResult = DialogResult.OK; Dialog.Close(); App.Main.Go(h.Hash);
        }

        public static void Show() { new SearchDialog().Dialog.Run(); }
    }
}
