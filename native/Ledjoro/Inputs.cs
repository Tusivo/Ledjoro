using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace Ledjoro
{
    /// <summary>Menu / drop-down skin that follows the theme.</summary>
    public sealed class ThemedRenderer : ToolStripProfessionalRenderer
    {
        public ThemedRenderer() : base(new ThemedColors()) { RoundedEdges = false; }
        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e) { using (var p = new Pen(Theme.Line)) e.Graphics.DrawRectangle(p, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1); }
        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e) { using (var b = new SolidBrush(Theme.Surface)) e.Graphics.FillRectangle(b, e.AffectedBounds); }
        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            var r = new Rectangle(Theme.Px(3), 1, e.Item.Width - Theme.Px(6), e.Item.Height - 2);
            using (var b = new SolidBrush(e.Item.Selected && e.Item.Enabled ? Theme.AccentSoft : Theme.Surface)) e.Graphics.FillRectangle(b, r);
        }
        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e) { e.TextColor = e.Item.Enabled ? Theme.Ink : Theme.Muted; base.OnRenderItemText(e); }
        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e) { using (var p = new Pen(Theme.Line)) e.Graphics.DrawLine(p, 4, e.Item.Height / 2, e.Item.Width - 4, e.Item.Height / 2); }
        sealed class ThemedColors : ProfessionalColorTable
        {
            public override Color ToolStripDropDownBackground { get { return Theme.Surface; } }
            public override Color MenuItemSelected { get { return Theme.AccentSoft; } }
            public override Color ImageMarginGradientBegin { get { return Theme.Surface; } }
            public override Color ImageMarginGradientMiddle { get { return Theme.Surface; } }
            public override Color ImageMarginGradientEnd { get { return Theme.Surface; } }
        }
    }

    /// <summary>Single-line (or multi-line) text field with a themed rounded border.</summary>
    public class Input : Pnl, IThemed
    {
        public readonly TextBox Tb = new TextBox();
        string cue = "", glyph = ""; bool focus, multi, rightAlign;
        public event EventHandler Changed, GlyphClick;
        int widthPx = Theme.Px(250);

        public Input(string cue = "", int widthPx = 250)
        {
            this.widthPx = Theme.Px(widthPx); Fill = K.Surface; Width = this.widthPx; Height = Theme.Px(34);
            Tb.BorderStyle = BorderStyle.None; Tb.Font = Theme.F(10f); Controls.Add(Tb);
            Tb.GotFocus += (s, e) => { focus = true; Invalidate(); };
            Tb.LostFocus += (s, e) => { focus = false; Invalidate(); };
            Tb.TextChanged += (s, e) => { var h = Changed; if (h != null) h(this, e); };
            Tb.HandleCreated += (s, e) => Native.Cue(Tb, this.cue);
            Cue = cue; OnTheme(); TabStop = false; Cursor = Cursors.IBeam;
        }
        public override Color Back { get { return Theme.Soft; } }
        public override string Text { get { return Tb.Text; } set { Tb.Text = value ?? ""; } }
        public string Cue { get { return cue; } set { cue = value ?? ""; Native.Cue(Tb, cue); } }
        public bool Password { get { return Tb.UseSystemPasswordChar; } set { Tb.UseSystemPasswordChar = value; } }
        public bool ReadOnly { get { return Tb.ReadOnly; } set { Tb.ReadOnly = value; } }
        public bool RightAlign { get { return rightAlign; } set { rightAlign = value; Tb.TextAlign = value ? HorizontalAlignment.Right : HorizontalAlignment.Left; } }
        public int MaxLength { get { return Tb.MaxLength; } set { Tb.MaxLength = value; } }
        public string Glyph { get { return glyph; } set { glyph = value ?? ""; PerformLayout(); Invalidate(); } }
        public bool Multi { get { return multi; } set { multi = value; Tb.Multiline = value; Tb.AcceptsReturn = value; Tb.ScrollBars = value ? ScrollBars.Vertical : ScrollBars.None; Height = value ? Theme.Px(84) : Theme.Px(34); PerformLayout(); } }
        public void SelectAllText() { Tb.SelectAll(); }
        /// <summary>Make this box take a recovery key: capitals, and a hyphen after every 5 characters as you type (spaces, dashes and pasted formatting are tidied away).</summary>
        public Input AsRecoveryKey()
        {
            bool fixing = false; int last = 0;
            Tb.CharacterCasing = CharacterCasing.Upper;
            Tb.TextChanged += (s, e) =>
            {
                if (fixing) return;
                string raw = Tb.Text; bool deleting = raw.Length < last;
                var sb = new System.Text.StringBuilder(); int letters = 0, caretIn = Tb.SelectionStart, caretOut = 0;
                for (int i = 0; i < raw.Length && letters < 20; i++)
                {
                    char c = raw[i]; if (!char.IsLetterOrDigit(c) || c > 127) { if (i < caretIn) caretOut = sb.Length; continue; }
                    if (letters > 0 && letters % 5 == 0 && sb.Length > 0 && sb[sb.Length - 1] != '-') sb.Append('-');
                    sb.Append(char.ToUpperInvariant(c)); letters++; if (i < caretIn) caretOut = sb.Length;
                }
                if (!deleting && letters > 0 && letters < 20 && letters % 5 == 0) { sb.Append('-'); if (caretIn >= raw.Length) caretOut = sb.Length; }   // the hyphen appears as soon as a group of 5 is complete
                string t = sb.ToString();
                if (t != raw) { fixing = true; try { Tb.Text = t; Tb.SelectionStart = Math.Min(caretOut, t.Length); } finally { fixing = false; } }
                last = Tb.Text.Length;
            };
            return this;
        }
        public new bool Focus() { return Tb.Focus(); }
        public override Size GetPreferredSize(Size proposed) { return new Size(widthPx, multi ? Theme.Px(84) : Theme.Px(34)); }
        public void OnTheme() { Tb.BackColor = Theme.Soft; Tb.ForeColor = Theme.Ink; Invalidate(); Native.ScrollTheme(Tb, Theme.Dark); }
        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            int pad = Theme.Px(10), gl = glyph.Length > 0 ? Theme.Px(28) : 0;
            if (multi) Tb.SetBounds(pad, Theme.Px(7), Math.Max(10, Width - pad * 2), Height - Theme.Px(14));
            else { int th = Tb.PreferredHeight; Tb.SetBounds(pad, Math.Max(0, (Height - th) / 2), Math.Max(10, Width - pad * 2 - gl), th); }
        }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (glyph.Length > 0 && e.X > Width - Theme.Px(30)) { var h = GlyphClick; if (h != null) h(this, EventArgs.Empty); } else Tb.Focus();
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            Theme.Smooth(e.Graphics);
            using (var p = Theme.Round(new Rectangle(0, 0, Width - 1, Height - 1), Theme.Px(7))) using (var pen = new Pen(focus ? Theme.Accent : Theme.Line, focus ? 2 : 1)) e.Graphics.DrawPath(pen, p);
            if (glyph.Length > 0) TextRenderer.DrawText(e.Graphics, glyph, Theme.F(9f), new Rectangle(Width - Theme.Px(30), 0, Theme.Px(26), Height), Theme.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }

    /// <summary>Text field that formats a US phone number as xxx-xxx-xxxx while you type.</summary>
    public sealed class PhoneInput : Input
    {
        bool busy;
        public PhoneInput(string value = "") : base("603-555-0142", 250)
        {
            Tb.MaxLength = 14; Tb.Text = Fmt.Phone(value);
            Tb.TextChanged += (s, e) =>
            {
                if (busy) return; busy = true;
                string f = Fmt.PhoneTyping(Tb.Text);
                if (f != Tb.Text) { Tb.Text = f; Tb.SelectionStart = f.Length; }
                busy = false;
            };
        }
    }

    /// <summary>Drop-down list drawn by us (the stock combo box cannot follow a dark theme).</summary>
    public sealed class Combo : Control
    {
        public readonly List<KeyValuePair<string, string>> Items = new List<KeyValuePair<string, string>>();
        int sel = -1; bool hover, open; int widthPx; ToolStripDropDown drop;
        public event EventHandler Changed;

        public Combo(int widthPx = 250)
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.Selectable | ControlStyles.ResizeRedraw, true);
            this.widthPx = Theme.Px(widthPx); Width = this.widthPx; Height = Theme.Px(34); TabStop = true; Cursor = Cursors.Hand; AccessibleRole = AccessibleRole.ComboBox;
        }
        public void SetItems(IEnumerable<KeyValuePair<string, string>> items, string selectedValue = null)
        {
            Items.Clear(); Items.AddRange(items); sel = -1;
            if (selectedValue != null) sel = Items.FindIndex(i => i.Key == selectedValue);
            if (sel < 0 && Items.Count > 0) sel = 0;
            Invalidate();
        }
        public static KeyValuePair<string, string> It(string v, string l) { return new KeyValuePair<string, string>(v, l); }
        public string Value { get { return sel >= 0 && sel < Items.Count ? Items[sel].Key : ""; } set { int i = Items.FindIndex(x => x.Key == value); if (i >= 0 && i != sel) { sel = i; Invalidate(); Raise(); } } }
        public string Label { get { return sel >= 0 && sel < Items.Count ? Items[sel].Value : ""; } }
        public int Index { get { return sel; } set { if (value != sel) { sel = value; Invalidate(); Raise(); } } }
        void Raise() { var h = Changed; if (h != null) h(this, EventArgs.Empty); }
        public override Size GetPreferredSize(Size proposed) { return new Size(widthPx, Theme.Px(34)); }
        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = false; Invalidate(); }
        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); Focus(); Open(); }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
        protected override bool IsInputKey(Keys k) { return k == Keys.Up || k == Keys.Down || base.IsInputKey(k); }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter || (e.Alt && e.KeyCode == Keys.Down)) { Open(); e.Handled = true; }
            else if (e.KeyCode == Keys.Down && sel < Items.Count - 1) { Index = sel + 1; e.Handled = true; }
            else if (e.KeyCode == Keys.Up && sel > 0) { Index = sel - 1; e.Handled = true; }
            base.OnKeyDown(e);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            Theme.Smooth(e.Graphics); e.Graphics.Clear(Pnl.BackOf(this));
            using (var p = Theme.Round(new Rectangle(0, 0, Width - 1, Height - 1), Theme.Px(7))) using (var b = new SolidBrush(hover ? Theme.AccentSoft : Theme.Soft)) using (var pen = new Pen(Focused || open ? Theme.Accent : Theme.Line, Focused ? 2 : 1)) { e.Graphics.FillPath(b, p); e.Graphics.DrawPath(pen, p); }
            TextRenderer.DrawText(e.Graphics, Label, Theme.F(10f), new Rectangle(Theme.Px(10), 0, Width - Theme.Px(34), Height), Enabled ? Theme.Ink : Theme.Muted, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            int cx = Width - Theme.Px(18), cy = Height / 2, a = Theme.Px(4);
            using (var pen = new Pen(Theme.Muted, 1.6f)) e.Graphics.DrawLines(pen, new[] { new Point(cx - a, cy - a / 2), new Point(cx, cy + a / 2), new Point(cx + a, cy - a / 2) });
        }
        void Open()
        {
            if (Items.Count == 0 || !Enabled) return;
            int ih = Theme.Px(28);
            var lb = new ListBox { BorderStyle = BorderStyle.None, DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = ih, IntegralHeight = false, BackColor = Theme.Surface, ForeColor = Theme.Ink, Font = Theme.F(10f) };
            foreach (var i in Items) lb.Items.Add(i.Value);
            int tw = Items.Max(i => TextRenderer.MeasureText(i.Value, Theme.F(10f), new Size(2000, 100), TextFormatFlags.NoPadding).Width) + Theme.Px(40);
            int w = Math.Max(Width, Math.Min(tw, Theme.Px(560))), h = Math.Min(Items.Count, 9) * ih + 2;
            lb.Size = new Size(w, h);
            if (sel >= 0) lb.SelectedIndex = sel;
            lb.DrawItem += (s, e) =>
            {
                if (e.Index < 0) return;
                bool on = (e.State & DrawItemState.Selected) != 0;
                using (var b = new SolidBrush(on ? Theme.AccentSoft : Theme.Surface)) e.Graphics.FillRectangle(b, e.Bounds);
                TextRenderer.DrawText(e.Graphics, lb.Items[e.Index].ToString(), Theme.F(10f), new Rectangle(e.Bounds.X + Theme.Px(10), e.Bounds.Y, e.Bounds.Width - Theme.Px(14), e.Bounds.Height), Theme.Ink, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            };
            lb.MouseMove += (s, e) => { int i = lb.IndexFromPoint(e.Location); if (i >= 0 && i != lb.SelectedIndex) lb.SelectedIndex = i; };
            Action choose = () => { if (lb.SelectedIndex >= 0) Index = lb.SelectedIndex; drop.Close(); Focus(); };
            lb.MouseUp += (s, e) => { if (lb.IndexFromPoint(e.Location) >= 0) choose(); };
            lb.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { choose(); e.Handled = true; } else if (e.KeyCode == Keys.Escape) { drop.Close(); Focus(); } };
            var host = new ToolStripControlHost(lb) { Margin = Padding.Empty, Padding = Padding.Empty, AutoSize = false, Size = lb.Size };
            drop = new ToolStripDropDown { Padding = Padding.Empty, DropShadowEnabled = false, Renderer = new ThemedRenderer(), BackColor = Theme.Surface };
            drop.Items.Add(host);
            drop.Closed += (s, e) => { open = false; Invalidate(); };
            open = true; Invalidate();
            drop.Show(this, new Point(0, Height + 1));
            lb.Focus();
            Native.ScrollTheme(lb, Theme.Dark);
        }
    }

    /// <summary>yyyy-MM-dd text field with a calendar drop-down.</summary>
    public sealed class DateInput : Input
    {
        public DateInput(string value = null, int widthPx = 170) : base(Fmt.Pattern.ToLowerInvariant(), widthPx)
        {
            Tb.Text = Fmt.Show(value ?? Fmt.Today()); Glyph = "\u25BE"; Tb.MaxLength = 14;
            GlyphClick += (s, e) => Pick();
            Tb.Leave += (s, e) => { Invalidate(); };
        }
        /// <summary>The date as the books keep it (yyyy-MM-dd), whatever way it is typed or shown; setting accepts either way of writing it.</summary>
        public override string Text { get { return Fmt.ToIso(Tb.Text); } set { Tb.Text = Fmt.Show(Fmt.ToIso(value ?? "")); } }
        public bool Valid { get { DateTime d; return Fmt.TryLocalDate(Tb.Text, out d); } }
        public string Value { get { return Fmt.ToIso(Tb.Text); } }
        void Pick()
        {
            DateTime d; if (!Fmt.TryLocalDate(Tb.Text, out d)) d = DateTime.Today;
            var cal = new MonthCalendar { MaxSelectionCount = 1, SelectionStart = d, SelectionEnd = d, BackColor = Theme.Surface, ForeColor = Theme.Ink, TitleBackColor = Theme.Soft, TitleForeColor = Theme.Ink, TrailingForeColor = Theme.Muted, ShowToday = true, ShowTodayCircle = false };
            var drop = new ToolStripDropDown { Padding = Padding.Empty, DropShadowEnabled = false, Renderer = new ThemedRenderer() };
            drop.Items.Add(new ToolStripControlHost(cal) { Margin = Padding.Empty, Padding = Padding.Empty });
            cal.DateSelected += (s, e) => { Tb.Text = Fmt.Show(Fmt.Date(cal.SelectionStart)); drop.Close(); };
            drop.Show(this, new Point(0, Height + 1));
        }
    }
}
