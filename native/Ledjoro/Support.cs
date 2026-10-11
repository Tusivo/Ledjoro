using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Printing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Ledjoro
{
    /// <summary>Lists that many screens need (accounts, contacts, items), fetched once and refreshed after changes.</summary>
    public static class Lists
    {
        static Jv accounts, contacts, items, roles, taxRates;
        public static async Task<Jv> Accounts() { if (accounts == null) accounts = await App.Api.Get("api/accounts"); return accounts; }
        public static async Task<Jv> Contacts() { if (contacts == null) contacts = await App.Api.Get("api/contacts"); return contacts; }
        public static async Task<Jv> Items() { if (items == null) items = await App.Api.Get("api/items"); return items; }
        public static async Task<Jv> Roles() { if (roles == null) roles = await App.Api.Get("api/roles"); return roles; }
        public static async Task<Jv> TaxRates() { if (taxRates == null) taxRates = await App.Api.Get("api/salestax/rates"); return taxRates; }
        public static void Bust() { accounts = contacts = items = roles = taxRates = null; }
    }

    /// <summary>Equal-width columns that collapse to a single column when the window is narrow.</summary>
    public sealed class Cols : Pnl
    {
        public int Gap = Theme.Px(16), MinColWidth = Theme.Px(400);
        public T Add<T>(T c) where T : Control { Controls.Add(c); PerformLayout(); return c; }
        int N(int width) { return Math.Max(1, Math.Min(Math.Max(1, Controls.Count), (width + Gap) / (MinColWidth + Gap))); }
        public override Size GetPreferredSize(Size proposed)
        {
            int w = proposed.Width > 0 && proposed.Width < 100000 ? proposed.Width : Theme.Px(900), n = N(w), cw = (w - Gap * (n - 1)) / n, h = 0, rowH = 0, i = 0;
            foreach (Control c in Controls) { if (i > 0 && i % n == 0) { h += rowH + Gap; rowH = 0; } rowH = Math.Max(rowH, c.GetPreferredSize(new Size(cw, 0)).Height); i++; }
            return new Size(w, h + rowH);
        }
        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            int n = N(ClientSize.Width), cw = (ClientSize.Width - Gap * (n - 1)) / n, y = 0, x = 0, rowH = 0, i = 0;
            foreach (Control c in Controls)
            {
                if (i > 0 && i % n == 0) { y += rowH + Gap; rowH = 0; x = 0; }
                int h = c.GetPreferredSize(new Size(cw, 0)).Height; c.SetBounds(x, y, cw, h); rowH = Math.Max(rowH, h); x += cw + Gap; i++;
            }
        }
    }

    /// <summary>A clickable "label ... pill" line used in the dashboard's attention list.</summary>
    public sealed class AttnRow : Pnl
    {
        readonly Lbl label; readonly Pill pill; bool hot;
        public AttnRow(string text, string count, PK kind, Action click)
        {
            label = new Lbl(text); pill = new Pill(count, kind); Controls.Add(label); Controls.Add(pill); Cursor = Cursors.Hand;
            foreach (Control c in Controls) { c.Cursor = Cursors.Hand; c.Click += (s, e) => click(); }
            Click += (s, e) => click(); MouseEnter += (s, e) => { hot = true; Invalidate(); }; MouseLeave += (s, e) => { hot = false; Invalidate(); };
        }
        public override Size GetPreferredSize(Size proposed) { return new Size(proposed.Width, Theme.Px(38)); }
        protected override void OnPaintBackground(PaintEventArgs e) { e.Graphics.Clear(hot ? Theme.Soft : Back); using (var p = new Pen(Theme.Line)) e.Graphics.DrawLine(p, 0, Height - 1, Width, Height - 1); }
        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            var ps = pill.GetPreferredSize(Size.Empty); pill.SetBounds(Width - ps.Width - Theme.Px(6), (Height - ps.Height) / 2, ps.Width, ps.Height);
            var ls = label.GetPreferredSize(Size.Empty); label.SetBounds(Theme.Px(6), (Height - ls.Height) / 2, Math.Max(10, Width - ps.Width - Theme.Px(24)), ls.Height);
        }
    }

    /// <summary>A stat tile: big number and a caption.</summary>
    public sealed class StatCard : Card
    {
        public StatCard(string value, string caption, bool negative = false)
        {
            Padding = new Padding(Theme.Px(16), Theme.Px(14), Theme.Px(16), Theme.Px(14)); Gap = Theme.Px(2);
            Add(new Lbl(value, LS.Big) { Tint = negative ? Theme.Bad : (Color?)null }, 0); Add(new Lbl(caption, LS.Muted, true), 0);
        }
        public override Size GetPreferredSize(Size proposed) { return base.GetPreferredSize(new Size(Theme.Px(236), 0)); }
    }

    /// <summary>One line of an invoice/bill/journal editor: weighted columns of ordinary controls, so everything follows the theme.</summary>
    public class RowLayout : Pnl
    {
        public float[] Weights; public int Gap = Theme.Px(8), RowHeight = Theme.Px(34);
        public RowLayout(params float[] weights) { Weights = weights; }
        public override Size GetPreferredSize(Size proposed) { return new Size(proposed.Width, RowHeight + Theme.Px(4)); }
        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            var kids = Controls.Cast<Control>().ToList(); float total = Weights.Take(kids.Count).Sum();
            int avail = Math.Max(0, ClientSize.Width - Gap * (kids.Count - 1)), x = 0;
            for (int i = 0; i < kids.Count; i++)
            {
                int w = i == kids.Count - 1 ? ClientSize.Width - x : (int)(avail * Weights[i] / total);
                kids[i].SetBounds(x, 0, w, RowHeight); x += w + Gap;
            }
        }
    }

    /// <summary>Prints the contents of one or more grids (reports, invoices, lists) on a standard page.</summary>
    public static class PrintHelper
    {
        public static void Print(string title, string subtitle, params Grid[] grids)
        {
            int gi = 0, ri = -1; bool first = true;
            var doc = new PrintDocument { DocumentName = title };
            doc.DefaultPageSettings.Margins = new Margins(60, 60, 60, 60);
            doc.PrintPage += (s, e) =>
            {
                var g = e.Graphics; var a = e.MarginBounds; int y = a.Top;
                if (first) { g.DrawString(title, new Font("Segoe UI", 16, FontStyle.Bold), Brushes.Black, a.Left, y); y += 32; if (!string.IsNullOrEmpty(subtitle)) { g.DrawString(subtitle, new Font("Segoe UI", 10), Brushes.DimGray, a.Left, y); y += 26; } y += 6; first = false; }
                var fn = new Font("Segoe UI", 9); var fb = new Font("Segoe UI", 9, FontStyle.Bold); int rh = 22;
                while (gi < grids.Length)
                {
                    var gr = grids[gi]; var cols = gr.Columns.Cast<DataGridViewColumn>().ToList(); float tot = cols.Sum(c => c.FillWeight);
                    var widths = cols.Select(c => (int)(a.Width * c.FillWeight / tot)).ToArray();
                    if (ri < 0) { if (y + rh * 2 > a.Bottom) { e.HasMorePages = true; return; } int x = a.Left; for (int c = 0; c < cols.Count; c++) { var rf = new Rectangle(x, y, widths[c] - 4, rh); g.DrawString(cols[c].HeaderText, fb, Brushes.DimGray, rf, Align(cols[c])); x += widths[c]; } y += rh; g.DrawLine(Pens.Gray, a.Left, y, a.Right, y); ri = 0; }
                    while (ri < gr.Rows.Count)
                    {
                        if (y + rh > a.Bottom) { e.HasMorePages = true; return; }
                        var row = gr.Rows[ri]; bool bold = row.DefaultCellStyle.Font != null && row.DefaultCellStyle.Font.Bold; int x = a.Left;
                        for (int c = 0; c < cols.Count; c++) { var rf = new Rectangle(x, y + 2, widths[c] - 4, rh); g.DrawString(Convert.ToString(row.Cells[c].Value) ?? "", bold ? fb : fn, Brushes.Black, rf, Align(cols[c])); x += widths[c]; }
                        y += rh; ri++;
                    }
                    gi++; ri = -1; y += 16;
                }
                e.HasMorePages = false;
            };
            using (var dlg = new PrintDialog { Document = doc, UseEXDialog = true }) { if (dlg.ShowDialog(App.Main) == DialogResult.OK) doc.Print(); }
        }
        static StringFormat Align(DataGridViewColumn c) { return new StringFormat { Alignment = c.DefaultCellStyle.Alignment == DataGridViewContentAlignment.MiddleRight ? StringAlignment.Far : StringAlignment.Near, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap }; }
    }
}
