using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace Ledjoro
{
    /// <summary>A cell with its own colour.</summary>
    public sealed class GC
    {
        public string Text; public Color? Color; public bool Bold;
        public GC(string text, Color? color = null, bool bold = false) { Text = text; Color = color; Bold = bold; }
    }

    public enum RS { Normal, Total, Sub }

    /// <summary>Read-only data table styled for the theme. Money-style columns are right-aligned and negative amounts turn red.</summary>
    public sealed class Grid : DataGridView, IThemed
    {
        public event Action<object> Activated;
        readonly ThemedScrollBar vbar = new ThemedScrollBar(); int barPad;
        int RowH { get { return Math.Max(1, RowTemplate.Height); } }
        int View { get { return Math.Max(1, ClientSize.Height - ColumnHeadersHeight); } }
        int MaxFirst { get { return Math.Max(0, Rows.Count - View / RowH); } }
        void ScrollToBar()
        {
            if (Rows.Count == 0) return;
            int idx = Math.Min(MaxFirst, (vbar.Value + RowH - 1) / RowH);
            try { if (idx != FirstDisplayedScrollingRowIndex) FirstDisplayedScrollingRowIndex = idx; } catch (InvalidOperationException) { }
        }
        /// <summary>Show or hide the themed scroll bar to suit the rows, and keep its handle where the rows are.</summary>
        void SyncBar()
        {
            if (!IsHandleCreated) return;
            int total = Rows.Count * RowH; bool need = !AutoHeight && total > View;
            if (need != vbar.Visible || (need && barPad == 0) || (!need && barPad != 0))
            {
                vbar.Visible = need; barPad = need ? ThemedScrollBar.Thickness : 0;
                if (Columns.Count > 0) { var last = Columns[Columns.Count - 1]; var p = last.DefaultCellStyle.Padding; last.DefaultCellStyle.Padding = new Padding(p.Left, p.Top, Theme.Px(6) + barPad, p.Bottom); }
            }
            if (!need) return;
            vbar.SetBounds(ClientSize.Width - ThemedScrollBar.Thickness, ColumnHeadersHeight, ThemedScrollBar.Thickness, View);
            vbar.Maximum = total; vbar.LargeChange = View; vbar.Value = Math.Max(0, FirstDisplayedScrollingRowIndex) * RowH; vbar.BringToFront();
        }
        protected override void OnScroll(ScrollEventArgs e) { base.OnScroll(e); SyncBar(); }
        protected override void OnRowsAdded(DataGridViewRowsAddedEventArgs e) { base.OnRowsAdded(e); SyncBar(); }
        protected override void OnRowsRemoved(DataGridViewRowsRemovedEventArgs e) { base.OnRowsRemoved(e); SyncBar(); }
        protected override void OnSizeChanged(EventArgs e) { base.OnSizeChanged(e); SyncBar(); }
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            var he = e as HandledMouseEventArgs; if (he != null) he.Handled = true;
            if (!vbar.Visible || Rows.Count == 0) return;
            int idx = Math.Max(0, Math.Min(MaxFirst, Math.Max(0, FirstDisplayedScrollingRowIndex) - e.Delta / 120 * 3));
            try { FirstDisplayedScrollingRowIndex = idx; } catch (InvalidOperationException) { }
            SyncBar();
        }
        public bool AutoHeight;

        public Grid()
        {
            DoubleBuffered = true; ReadOnly = true; AllowUserToAddRows = false; AllowUserToDeleteRows = false; AllowUserToResizeRows = false;
            AllowUserToOrderColumns = false; RowHeadersVisible = false; MultiSelect = false; SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            BorderStyle = BorderStyle.None; CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal; EnableHeadersVisualStyles = false;
            ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing; ColumnHeadersHeight = Theme.Px(34);
            ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None; RowTemplate.Height = Theme.Px(32);
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill; ScrollBars = ScrollBars.None; StandardTab = true;   // the scroll bar is our own, drawn in the theme colours
            Controls.Add(vbar); vbar.Visible = false; vbar.Scrolled += () => ScrollToBar();
            ShowCellToolTips = true;
            OnTheme();
        }

        public void OnTheme()
        {
            BackgroundColor = Theme.Surface; GridColor = Theme.Line;
            DefaultCellStyle.BackColor = Theme.Surface; DefaultCellStyle.ForeColor = Theme.Ink; DefaultCellStyle.SelectionBackColor = Theme.AccentSoft; DefaultCellStyle.SelectionForeColor = Theme.Ink;
            DefaultCellStyle.Font = Theme.F(10f); DefaultCellStyle.Padding = new Padding(Theme.Px(6), 0, Theme.Px(6), 0);
            ColumnHeadersDefaultCellStyle.BackColor = Theme.Soft; ColumnHeadersDefaultCellStyle.ForeColor = Theme.Muted; ColumnHeadersDefaultCellStyle.Font = Theme.F(8.5f, FontStyle.Bold);
            ColumnHeadersDefaultCellStyle.SelectionBackColor = Theme.Soft; ColumnHeadersDefaultCellStyle.SelectionForeColor = Theme.Muted; ColumnHeadersDefaultCellStyle.Padding = new Padding(Theme.Px(6), 0, Theme.Px(6), 0);
            foreach (DataGridViewRow r in Rows) StyleRow(r, r.Tag is RS ? (RS)r.Tag : RS.Normal);
            vbar.Invalidate(); Invalidate();
        }
        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); SyncBar(); }

        /// <summary>Define columns: header text and relative width. Prefix a header with ">" to right-align it (numbers).</summary>
        public void Cols(params KeyValuePair<string, float>[] cols)
        {
            Columns.Clear(); barPad = 0; vbar.Visible = false;
            foreach (var c in cols)
            {
                bool right = c.Key.StartsWith(">");
                var col = new DataGridViewTextBoxColumn { HeaderText = right ? c.Key.Substring(1) : c.Key, FillWeight = c.Value, SortMode = DataGridViewColumnSortMode.NotSortable, MinimumWidth = Theme.Px(40), Tag = right };
                col.DefaultCellStyle.Alignment = right ? DataGridViewContentAlignment.MiddleRight : DataGridViewContentAlignment.MiddleLeft;
                col.HeaderCell.Style.Alignment = col.DefaultCellStyle.Alignment;
                Columns.Add(col);
            }
        }
        public static KeyValuePair<string, float> Col(string header, float weight = 1) { return new KeyValuePair<string, float>(header, weight); }

        public void Clear() { Rows.Clear(); SyncBar(); }
        public void Add(object tag, params object[] cells) { Add(tag, RS.Normal, cells); }
        public void Add(object tag, RS style, params object[] cells)
        {
            var vals = new object[Columns.Count];
            for (int i = 0; i < vals.Length; i++) { var c = i < cells.Length ? cells[i] : ""; vals[i] = c is GC ? ((GC)c).Text : (c ?? "").ToString(); }
            int idx = Rows.Add(vals);
            var row = Rows[idx]; row.Tag = style; row.Cells[0].Tag = tag; // the row's payload lives on its first cell; the row Tag keeps the style
            for (int i = 0; i < vals.Length && i < cells.Length; i++)
            {
                var g = cells[i] as GC;
                if (g != null && g.Color.HasValue) row.Cells[i].Style.ForeColor = g.Color.Value;
                else if ((bool)Columns[i].Tag && vals[i] is string && ((string)vals[i]).StartsWith("-") && ((string)vals[i]).Contains("$")) row.Cells[i].Style.ForeColor = Theme.Bad;
                if (g != null && g.Bold) row.Cells[i].Style.Font = Theme.F(10f, FontStyle.Bold);
            }
            StyleRow(row, style);
            if (AutoHeight) Height = PreferredHeight();
        }
        void StyleRow(DataGridViewRow r, RS s)
        {
            if (s == RS.Total) { r.DefaultCellStyle.BackColor = Theme.Soft; r.DefaultCellStyle.Font = Theme.F(10f, FontStyle.Bold); r.DefaultCellStyle.SelectionBackColor = Theme.Soft; }
            else if (s == RS.Sub) { r.DefaultCellStyle.ForeColor = Theme.Muted; r.DefaultCellStyle.Font = Theme.F(9.5f); r.DefaultCellStyle.SelectionBackColor = Theme.Surface; r.DefaultCellStyle.SelectionForeColor = Theme.Muted; }
        }
        public int PreferredHeight() { return ColumnHeadersHeight + Rows.Count * RowTemplate.Height + 2; }
        public override Size GetPreferredSize(Size proposed) { return AutoHeight ? new Size(proposed.Width, PreferredHeight()) : base.GetPreferredSize(proposed); }

        public object SelectedTag { get { return CurrentRow != null && CurrentRow.Cells.Count > 0 ? CurrentRow.Cells[0].Tag : null; } }
        protected override void OnCellDoubleClick(DataGridViewCellEventArgs e) { base.OnCellDoubleClick(e); Fire(e.RowIndex); }
        protected override void OnKeyDown(KeyEventArgs e) { if (e.KeyCode == Keys.Enter && CurrentRow != null) { Fire(CurrentRow.Index); e.Handled = true; } else base.OnKeyDown(e); }
        void Fire(int row) { if (row < 0 || row >= Rows.Count) return; var h = Activated; var tag = Rows[row].Cells[0].Tag; if (h != null && tag != null) h(tag); }
        public new void ClearSelection() { base.ClearSelection(); }
    }
}
