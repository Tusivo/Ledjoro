using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Ledjoro
{
    /// <summary>
    /// Reconcile a bank or card account with its statement: type the closing balance, tick what appears on the statement,
    /// and finish when the difference is zero.
    /// </summary>
    sealed class ReconcileScreen : Screen
    {
        Jv accts; Combo account; DateInput date; Input balance; Notice setupErr;
        ScrollPage page; Stack work, past; Grid grid, pastGrid;
        Lbl statementLbl, tickedLbl, diffLbl, hint; Btn finish;
        Jv current; long statementCents; string stmtDate; readonly HashSet<long> ticked = new HashSet<long>();
        public Grid Lines { get { return grid; } }
        public long Difference { get { return statementCents - (current == null ? 0 : current["cleared_cents"].Long) - TickedSum(); } }
        public bool Started { get { return current != null; } }

        public override async Task Load()
        {
            accts = await Lists.Accounts();
            page = new ScrollPage(); var s = page.Body;
            s.Add(new Header("Reconcile", "Check your books against a bank or credit card statement"));

            var setup = new Card(); setup.Add(new Lbl("Start with your statement", LS.H3));
            setup.Add(new Lbl("Have the statement in front of you. Pick the account, then type the date and the closing (ending) balance printed on it.", LS.Muted, true), Theme.Px(6));
            account = Ui.Cb(Ui.AccountItems(accts, a => a["subtype"].Str == "bank" || a["subtype"].Str == "credit_card", "Choose an account…"), "", 320);
            date = new DateInput(Fmt.LastMonthEnd()); balance = new Input("0.00", 170) { RightAlign = true };
            var row = new Flow { Fill = Pnl.K.Surface };
            row.Add(Ui.F("Account", account, 320)); row.Add(Ui.F("Statement date", date, 170)); row.Add(Ui.F("Closing balance on the statement", balance, 220));
            setup.Add(row, Theme.Px(8));
            hint = new Lbl("", LS.Muted, true); setup.Add(hint, Theme.Px(6));
            setupErr = new Notice(NK.Error); setup.Add(setupErr, Theme.Px(6));
            account.Changed += (a, e) => UpdateHint();
            setup.Add(Ui.BA("Start Reconciling", BK.Primary, Start), 0);
            s.Add(setup);

            work = new Stack { Visible = false, Gap = Theme.Px(12) };
            var sum = new Card { Gap = Theme.Px(4) }; var cols = new Flow { Fill = Pnl.K.Surface, HGap = Theme.Px(28) };
            statementLbl = new Lbl("", LS.Big); tickedLbl = new Lbl("", LS.Big); diffLbl = new Lbl("", LS.Big);
            cols.Add(Pair("Statement says", statementLbl)); cols.Add(Pair("Your books, ticked so far", tickedLbl)); cols.Add(Pair("Difference (must be 0.00)", diffLbl));
            sum.Add(cols, Theme.Px(6));
            sum.Add(new Lbl("Click a line to tick it when you see it on your statement. Lines are what your books say happened; anything on the statement that is missing here still needs to be added (use Bank transactions or the Journal), then come back.", LS.Muted, true), 0);
            work.Add(sum);
            grid = new Grid { AutoHeight = false, Height = Theme.Px(380) };
            grid.CellClick += (o, e) => { if (e.RowIndex >= 0) Toggle(e.RowIndex); };
            grid.KeyDown += (o, e) => { if (e.KeyCode == Keys.Space && grid.CurrentRow != null) { Toggle(grid.CurrentRow.Index); e.Handled = true; } };
            work.Add(grid);
            var btns = new Flow(); btns.Add(Ui.B("Tick All", BK.Normal, () => TickAll(true))); btns.Add(Ui.B("Untick All", BK.Normal, () => TickAll(false)));
            finish = Ui.BA("Finish Reconciling", BK.Primary, Finish); btns.Add(finish); work.Add(btns);
            s.Add(work);

            past = new Stack { Gap = Theme.Px(8) }; s.Add(past);
            SetBody(page);
            await LoadHistory();
        }

        Control Pair(string caption, Lbl value) { return new Figure(caption, value); }

        /// <summary>A caption above a large number, a fixed width so several sit side by side.</summary>
        sealed class Figure : Pnl
        {
            readonly Lbl cap, val;
            public Figure(string caption, Lbl value) { Fill = K.Surface; cap = new Lbl(caption, LS.Muted); val = value; Controls.Add(cap); Controls.Add(val); }
            public override System.Drawing.Size GetPreferredSize(System.Drawing.Size proposed) { return new System.Drawing.Size(Theme.Px(250), cap.GetPreferredSize(System.Drawing.Size.Empty).Height + Theme.Px(4) + val.GetPreferredSize(System.Drawing.Size.Empty).Height); }
            protected override void OnLayout(LayoutEventArgs e)
            {
                base.OnLayout(e); int ch = cap.GetPreferredSize(System.Drawing.Size.Empty).Height;
                cap.SetBounds(0, 0, Width, ch); val.SetBounds(0, ch + Theme.Px(4), Width, Height - ch - Theme.Px(4));
            }
        }

        void UpdateHint()
        {
            hint.Text = ""; var a = SelectedAccount();
            if (a != null && a["subtype"].Str == "credit_card") hint.Text = "For a credit card, type the amount you owe as a positive number, exactly as the statement shows it.";
        }
        Jv SelectedAccount() { return accts.Items.FirstOrDefault(a => a["id"].Str == account.Value); }

        async Task LoadHistory()
        {
            var h = await A.Get("api/reconcile/history");
            past.ClearItems();
            var live = h.Items.Where(x => x["undone"].Long == 0).ToList();
            if (live.Count == 0) { page.PerformLayout(); return; }
            past.Add(new Lbl("Past reconciliations", LS.H3));
            pastGrid = new Grid { AutoHeight = true };
            pastGrid.Cols(Grid.Col("Account", 2f), Grid.Col("Statement date", 1.2f), Grid.Col(">Closing balance", 1.3f), Grid.Col(">Entries cleared", 1.1f), Grid.Col("Done by", 1f));
            foreach (var r in live) pastGrid.Add(r, r["account_name"].Str, Fmt.Show(r["statement_date"].Str), Fmt.Money(r["statement_cents"].Long), r["cleared_count"].Str, r["username"].Str);
            past.Add(pastGrid);
            if (A.Can("banking.write"))
                past.Add(Ui.BA("Undo the selected Reconciliation", BK.Normal, async () =>
                {
                    var r = pastGrid.SelectedTag as Jv; if (r == null) { App.Toast("Click a reconciliation in the list first.", true); return; }
                    if (!Msg.Confirm("Undo this reconciliation?", "The " + Fmt.Show(r["statement_date"].Str) + " reconciliation of " + r["account_name"].Str + " will be taken back and its " + r["cleared_count"].Str + " entries become un-ticked again. Nothing in your books changes. Only the most recent reconciliation of an account can be undone.", "Undo It")) return;
                    await A.Post("api/reconcile/" + r["id"].Str + "/undo"); App.Toast("Reconciliation undone"); await LoadHistory();
                    if (current != null) await Start();
                }));
            Vx.Relayout(past); page.PerformLayout();
        }

        async Task Start()
        {
            setupErr.Reset();
            if (account.Value.Length == 0) { setupErr.Set("Choose which bank or credit card account the statement is for."); return; }
            if (!Fmt.IsDate(date.Value)) { setupErr.Set("Type the statement date the way dates are written on this computer, for example " + Fmt.Example + ", or pick it with the arrow."); return; }
            long cents; if (!Fmt.TryMoney(balance.Text, out cents)) { setupErr.Set("Type the closing balance exactly as printed on the statement, for example 12,345.67 (a minus sign for an overdrawn balance)."); return; }
            try { current = await A.Get("api/reconcile/start?account_id=" + account.Value + "&statement_date=" + date.Value); }
            catch (ApiException e) { setupErr.Set(e.Message); return; }
            statementCents = cents; stmtDate = date.Value; ticked.Clear();
            grid.Cols(Grid.Col("", 0.35f), Grid.Col("Date", 1f), Grid.Col("What", 3.2f), Grid.Col("Reference", 1.3f), Grid.Col(">" + (current["credit_card"].Bool ? "Charges (+) / payments (−)" : "Money in (+) / out (−)"), 1.8f));
            grid.Clear();
            foreach (var l in current["lines"].Items) grid.Add(l, "☐", Fmt.Show(l["date"].Str), l["memo"].Str.Length > 0 ? l["memo"].Str : l["source_type"].Str, l["ref"].Str, Fmt.Money(l["amount_cents"].Long));
            work.Visible = true; Recalc();
            if (current["lines"].Count == 0) App.Toast("Nothing is waiting to be reconciled up to that date.");
            Vx.Relayout(work); page.PerformLayout();
        }

        long TickedSum() { long t = 0; if (current == null) return 0; foreach (var l in current["lines"].Items) if (ticked.Contains(l["id"].Long)) t += l["amount_cents"].Long; return t; }

        void Toggle(int row)
        {
            var l = grid.Rows[row].Cells[0].Tag as Jv; if (l == null) return; long id = l["id"].Long;
            if (!ticked.Add(id)) ticked.Remove(id);
            grid.Rows[row].Cells[0].Value = ticked.Contains(id) ? "☑" : "☐"; Recalc();
        }
        void TickAll(bool on)
        {
            if (current == null) return; ticked.Clear();
            foreach (DataGridViewRow r in grid.Rows) { var l = r.Cells[0].Tag as Jv; if (l == null) continue; if (on) ticked.Add(l["id"].Long); r.Cells[0].Value = on ? "☑" : "☐"; }
            Recalc();
        }
        public void SelectAllForTest() { TickAll(true); }

        void Recalc()
        {
            long total = current["cleared_cents"].Long + TickedSum(), diff = statementCents - total;
            statementLbl.Text = Fmt.Money(statementCents); tickedLbl.Text = Fmt.Money(total); diffLbl.Text = Fmt.Money(diff);
            diffLbl.Tint = diff == 0 ? Theme.Good : Theme.Bad; finish.Enabled = diff == 0;
            diffLbl.Invalidate(); statementLbl.Invalidate(); tickedLbl.Invalidate();
        }

        async Task Finish()
        {
            if (current == null || Difference != 0) { App.Toast("The difference must be 0.00 before you can finish. Tick or untick lines until it is.", true); return; }
            try
            {
                var r = await A.Post("api/reconcile", Json.O("account_id", long.Parse(account.Value), "statement_date", stmtDate, "statement_cents", statementCents, "line_ids", ticked.ToList()));
                App.Toast("Reconciled: " + r["cleared"].Str + " entries cleared");
                current = null; ticked.Clear(); work.Visible = false; balance.Text = ""; await LoadHistory(); Vx.Relayout(page);
            }
            catch (ApiException e) { App.Toast(e.Message, true); }
        }
    }
}
