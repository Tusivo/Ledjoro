using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace Ledjoro
{
    public enum LS { Normal, Muted, Bold, Small, H1, H2, H3, Mono, Good, Bad, Warn, Link, Big }

    /// <summary>Text label that paints itself (so it always follows the theme) and measures wrapped text against its real width.</summary>
    public sealed class Lbl : Control
    {
        public LS Style; public bool Wrap, RightAlign, Underline, VCenter;
        public Color? Tint;
        bool hover;

        public Lbl(string text, LS style = LS.Normal, bool wrap = false)
        {
            SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Color.Transparent; Text = text ?? ""; Style = style; Wrap = wrap; TabStop = false;
            if (style == LS.Link) { Cursor = Cursors.Hand; TabStop = true; }
        }

        Font Fnt()
        {
            switch (Style)
            {
                case LS.Bold: return Theme.F(10f, FontStyle.Bold);
                case LS.Small: return Theme.F(9f);
                case LS.H1: return Theme.F(18f, FontStyle.Bold);
                case LS.H2: return Theme.F(12.5f, FontStyle.Bold);
                case LS.H3: return Theme.F(8.5f, FontStyle.Bold);
                case LS.Mono: return Theme.Mono(10.5f);
                case LS.Big: return Theme.F(20f, FontStyle.Bold);
                default: return Theme.F(10f);
            }
        }
        Color Col()
        {
            if (Tint.HasValue) return Tint.Value;
            switch (Style)
            {
                case LS.Muted: case LS.H3: case LS.Small: return Theme.Muted;
                case LS.Good: return Theme.Good;
                case LS.Bad: return Theme.Bad;
                case LS.Warn: return Theme.Warn;
                default: return Theme.Ink;
            }
        }
        string Shown() { return Style == LS.H3 ? Text.ToUpperInvariant() : Text; }
        TextFormatFlags Flags()
        {
            var f = TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | (RightAlign ? TextFormatFlags.Right : TextFormatFlags.Left);
            return f | (Wrap ? TextFormatFlags.WordBreak : TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis) | (VCenter && !Wrap ? TextFormatFlags.VerticalCenter : 0);
        }

        public override Size GetPreferredSize(Size proposed)
        {
            var f = Fnt();
            if (Wrap)
            {
                int w = proposed.Width > 0 && proposed.Width < 100000 ? proposed.Width : Theme.Px(520);
                var s = TextRenderer.MeasureText(Shown(), f, new Size(w, 100000), Flags());
                return new Size(w, s.Height + 2);
            }
            var m = TextRenderer.MeasureText(Shown(), f, new Size(100000, 1000), Flags() & ~TextFormatFlags.EndEllipsis);
            return new Size(m.Width + 2, m.Height + 2);
        }
        public Lbl Fit() { Size = GetPreferredSize(Size.Empty); return this; }
        protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Invalidate(); Vx.Relayout(this); }
        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); if (Style == LS.Link) { hover = true; Invalidate(); } }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = false; Invalidate(); }
        protected override void OnKeyDown(KeyEventArgs e) { if (Style == LS.Link && (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space)) OnClick(EventArgs.Empty); base.OnKeyDown(e); }
        protected override void OnPaint(PaintEventArgs e)
        {
            var f = Fnt();
            if ((Style == LS.Link && hover) || Underline) f = new Font(f, f.Style | FontStyle.Underline);
            TextRenderer.DrawText(e.Graphics, Shown(), f, new Rectangle(0, 0, Width, Height), Col(), Flags());
            if (Focused && Style == LS.Link) ControlPaint.DrawFocusRectangle(e.Graphics, new Rectangle(0, 0, Width, Height));
        }
    }

    public enum PK { Neutral, Good, Bad, Warn, Accent }
    public sealed class Pill : Control
    {
        public PK Kind;
        public Pill(string text, PK kind = PK.Neutral)
        {
            SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
            BackColor = Color.Transparent; Text = text; Kind = kind; TabStop = false;
        }
        public override Size GetPreferredSize(Size proposed)
        {
            var m = TextRenderer.MeasureText(Text, Theme.F(8.5f, FontStyle.Bold), new Size(1000, 100), TextFormatFlags.NoPadding);
            return new Size(m.Width + Theme.Px(18), m.Height + Theme.Px(6));
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            Color bg, fg;
            switch (Kind) { case PK.Good: bg = Theme.GoodSoft; fg = Theme.Good; break; case PK.Bad: bg = Theme.BadSoft; fg = Theme.Bad; break; case PK.Warn: bg = Theme.WarnSoft; fg = Theme.Warn; break; case PK.Accent: bg = Theme.AccentSoft; fg = Theme.Ink; break; default: bg = Theme.Soft; fg = Theme.Muted; break; }
            Theme.Smooth(e.Graphics);
            using (var p = Theme.Round(new Rectangle(0, 0, Width - 1, Height - 1), Height / 2)) using (var b = new SolidBrush(bg)) e.Graphics.FillPath(b, p);
            TextRenderer.DrawText(e.Graphics, Text, Theme.F(8.5f, FontStyle.Bold), new Rectangle(0, 0, Width, Height), fg, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }

    public enum BK { Normal, Primary, Danger, Promo }
    public sealed class Btn : Control
    {
        public BK Kind; public bool Small;
        bool hover, down;
        public Btn(string text, BK kind = BK.Normal, EventHandler onClick = null, bool small = false)
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.Selectable | ControlStyles.StandardClick | ControlStyles.SupportsTransparentBackColor, true);
            Text = text; Kind = kind; Small = small; TabStop = true; Cursor = Cursors.Hand; BackColor = Color.Transparent;
            AccessibleName = text; AccessibleRole = AccessibleRole.PushButton;
            if (onClick != null) Click += onClick;
        }
        Font Fnt() { return Theme.F(Small ? 9f : 10f, Kind == BK.Primary || Kind == BK.Promo ? FontStyle.Bold : FontStyle.Regular); }
        public override Size GetPreferredSize(Size proposed)
        {
            var m = TextRenderer.MeasureText(Text, Fnt(), new Size(1000, 100), TextFormatFlags.NoPadding);
            return new Size(m.Width + Theme.Px(Small ? 22 : 34), Theme.Px(Small ? 28 : 34));
        }
        public void PerformClick() { if (Enabled) OnClick(EventArgs.Empty); }
        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = false; down = false; Invalidate(); }
        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); down = true; Focus(); Invalidate(); }
        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); down = false; Invalidate(); }
        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
        protected override bool IsInputKey(Keys k) { return k == Keys.Enter || k == Keys.Space || base.IsInputKey(k); }
        protected override void OnKeyDown(KeyEventArgs e) { if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter) { PerformClick(); e.Handled = true; } base.OnKeyDown(e); }
        protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); AccessibleName = Text; Invalidate(); if (Parent != null) Parent.PerformLayout(); }
        protected override void OnPaint(PaintEventArgs e)
        {
            Theme.Smooth(e.Graphics);
            e.Graphics.Clear(Pnl.BackOf(this));
            Color fill, border, text;
            switch (Kind)
            {
                case BK.Primary: fill = Theme.Accent; border = Theme.Accent; text = Theme.AccentInk; break;
                case BK.Promo: fill = hover ? Color.FromArgb(Math.Min(255, Theme.Promo.R + 25), Math.Min(255, Theme.Promo.G + 25), Math.Min(255, Theme.Promo.B + 25)) : Theme.Promo; border = Theme.Promo; text = Theme.PromoInk; break;
                case BK.Danger: fill = hover ? Theme.BadSoft : Theme.Surface; border = Theme.Bad; text = Theme.Bad; break;
                default: fill = hover ? Theme.AccentSoft : Theme.Surface; border = Theme.Line; text = Theme.Ink; break;
            }
            if (Kind == BK.Primary && (hover || down)) fill = Color.FromArgb(down ? 200 : 225, fill);
            if (down && Kind != BK.Primary) fill = Theme.Soft;
            if (!Enabled) { text = Theme.Muted; if (Kind == BK.Primary) { fill = Theme.Soft; border = Theme.Line; } }
            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            using (var p = Theme.Round(r, Theme.Px(7))) using (var b = new SolidBrush(fill)) using (var pen = new Pen(Focused && Enabled ? Theme.Accent : border, Focused ? 2 : 1))
            { e.Graphics.FillPath(b, p); e.Graphics.DrawPath(pen, p); }
            TextRenderer.DrawText(e.Graphics, Text, Fnt(), new Rectangle(0, 0, Width, Height), text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
        }
    }

    public sealed class Check : Control
    {
        bool chk; public event EventHandler Changed;
        public Check(string text, bool isChecked = false)
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.Selectable | ControlStyles.SupportsTransparentBackColor, true);
            Text = text; chk = isChecked; TabStop = true; Cursor = Cursors.Hand; BackColor = Color.Transparent;
            AccessibleName = text; AccessibleRole = AccessibleRole.CheckButton;
        }
        public bool Checked { get { return chk; } set { if (chk != value) { chk = value; Invalidate(); var h = Changed; if (h != null) h(this, EventArgs.Empty); } } }
        public override Size GetPreferredSize(Size proposed)
        {
            int w = proposed.Width > 0 && proposed.Width < 100000 ? proposed.Width : 100000;
            int tw = w == 100000 ? 100000 : w - Theme.Px(28);
            var m = TextRenderer.MeasureText(Text, Theme.F(10f), new Size(tw, 100000), TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
            return new Size(w == 100000 ? m.Width + Theme.Px(28) : w, Math.Max(Theme.Px(24), m.Height + 4));
        }
        protected override void OnClick(EventArgs e) { Checked = !chk; Focus(); base.OnClick(e); }
        protected override bool IsInputKey(Keys k) { return k == Keys.Space || base.IsInputKey(k); }
        protected override void OnKeyDown(KeyEventArgs e) { if (e.KeyCode == Keys.Space) { Checked = !chk; e.Handled = true; } base.OnKeyDown(e); }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
        protected override void OnPaint(PaintEventArgs e)
        {
            Theme.Smooth(e.Graphics);
            int s = Theme.Px(17), y = Theme.Px(3);
            var box = new Rectangle(0, y, s, s);
            using (var p = Theme.Round(box, Theme.Px(4))) using (var b = new SolidBrush(chk ? Theme.Accent : Theme.Soft)) using (var pen = new Pen(Focused ? Theme.Accent : Theme.Line, Focused ? 2 : 1)) { e.Graphics.FillPath(b, p); e.Graphics.DrawPath(pen, p); }
            if (chk) using (var pen = new Pen(Theme.AccentInk, 2f)) { e.Graphics.DrawLines(pen, new[] { new Point(box.X + s / 5 + 1, box.Y + s / 2), new Point(box.X + s * 2 / 5 + 1, box.Y + s * 7 / 10), new Point(box.X + s * 4 / 5, box.Y + s / 4) }); }
            TextRenderer.DrawText(e.Graphics, Text, Theme.F(10f), new Rectangle(s + Theme.Px(9), 0, Width - s - Theme.Px(9), Height), Enabled ? Theme.Ink : Theme.Muted, TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
        }
    }

    /// <summary>Row of text tabs with an underline on the active one.</summary>
    public sealed class TabBar : Control
    {
        public readonly List<KeyValuePair<string, string>> Items = new List<KeyValuePair<string, string>>();
        string sel = ""; int hot = -1; public event Action<string> Changed;
        public TabBar() { SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.Selectable | ControlStyles.ResizeRedraw, true); TabStop = true; Cursor = Cursors.Hand; }
        public string Selected { get { return sel; } set { sel = value; Invalidate(); } }
        public void Set(IEnumerable<KeyValuePair<string, string>> items, string selected) { Items.Clear(); Items.AddRange(items); sel = selected; Invalidate(); }
        public override Size GetPreferredSize(Size proposed) { return new Size(proposed.Width, Theme.Px(38)); }
        int TabW(string s) { return TextRenderer.MeasureText(s, Theme.F(10f, FontStyle.Bold), new Size(1000, 100), TextFormatFlags.NoPadding).Width + Theme.Px(16); }
        int IndexAt(int x) { int cx = 0; for (int i = 0; i < Items.Count; i++) { int w = TabW(Items[i].Value); if (x >= cx && x < cx + w) return i; cx += w; } return -1; }
        protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); int h = IndexAt(e.X); if (h != hot) { hot = h; Invalidate(); } }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hot = -1; Invalidate(); }
        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); int i = IndexAt(e.X); if (i >= 0) Pick(Items[i].Key); }
        void Pick(string key) { if (key == sel) return; sel = key; Invalidate(); var h = Changed; if (h != null) h(key); }
        protected override bool IsInputKey(Keys k) { return k == Keys.Left || k == Keys.Right || base.IsInputKey(k); }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            int i = Items.FindIndex(x => x.Key == sel);
            if (e.KeyCode == Keys.Right && i < Items.Count - 1) { Pick(Items[i + 1].Key); e.Handled = true; }
            if (e.KeyCode == Keys.Left && i > 0) { Pick(Items[i - 1].Key); e.Handled = true; }
            base.OnKeyDown(e);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Pnl.BackOf(this));
            using (var pen = new Pen(Theme.Line)) e.Graphics.DrawLine(pen, 0, Height - 1, Width, Height - 1);
            int x = 0;
            for (int i = 0; i < Items.Count; i++)
            {
                bool on = Items[i].Key == sel; int w = TabW(Items[i].Value);
                TextRenderer.DrawText(e.Graphics, Items[i].Value, Theme.F(10f, on ? FontStyle.Bold : FontStyle.Regular), new Rectangle(x, 0, w, Height - 3), on || i == hot ? Theme.Ink : Theme.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
                if (on) using (var b = new SolidBrush(Theme.Accent)) e.Graphics.FillRectangle(b, x + Theme.Px(4), Height - 3, w - Theme.Px(8), 3);
                if (on && Focused) ControlPaint.DrawFocusRectangle(e.Graphics, new Rectangle(x + 2, 2, w - 4, Height - 7));
                x += w;
            }
        }
    }

    /// <summary>Horizontal bars with a label on the left and a value on the right.</summary>
    public sealed class BarChart : Control
    {
        public class Row { public string Label, ValueText; public long Value; public bool Negative; }
        public readonly List<Row> Rows = new List<Row>();
        public BarChart() { SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true); BackColor = Color.Transparent; }
        public void Add(string label, long value, string text, bool negative = false) { Rows.Add(new Row { Label = label, Value = value, ValueText = text, Negative = negative }); Parent?.PerformLayout(); Invalidate(); }
        public override Size GetPreferredSize(Size proposed) { return new Size(proposed.Width, Rows.Count * Theme.Px(26) + 2); }
        protected override void OnPaint(PaintEventArgs e)
        {
            Theme.Smooth(e.Graphics);
            long max = Math.Max(1, Rows.Select(r => Math.Abs(r.Value)).DefaultIfEmpty(1).Max());
            int rh = Theme.Px(26), lab = Theme.Px(78), val = Theme.Px(110), y = 0;
            foreach (var r in Rows)
            {
                TextRenderer.DrawText(e.Graphics, r.Label, Theme.F(9.5f), new Rectangle(0, y, lab, rh), Theme.Muted, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                var track = new Rectangle(lab, y + rh / 2 - Theme.Px(6), Math.Max(10, Width - lab - val - Theme.Px(8)), Theme.Px(12));
                using (var p = Theme.Round(track, track.Height / 2)) using (var b = new SolidBrush(Theme.Soft)) e.Graphics.FillPath(b, p);
                int w = Math.Max(track.Height, (int)(track.Width * (Math.Abs(r.Value) / (double)max)));
                var bar = new Rectangle(track.X, track.Y, Math.Min(w, track.Width), track.Height);
                using (var p = Theme.Round(bar, bar.Height / 2)) using (var b = new SolidBrush(r.Negative ? Theme.Bad : Theme.Accent)) e.Graphics.FillPath(b, p);
                TextRenderer.DrawText(e.Graphics, r.ValueText, Theme.F(9.5f), new Rectangle(Width - val, y, val, rh), Theme.Ink, TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                y += rh;
            }
        }
    }
}
