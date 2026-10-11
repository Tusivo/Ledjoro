using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace Ledjoro
{
    public interface IFill { Color Back { get; } }

    /// <summary>A control's own visible flag. Control.Visible is false for every child of a form that has not been shown yet,
    /// which made dialogs measure themselves as empty; layout code must use this instead.</summary>
    public static class Vx
    {
        static readonly System.Reflection.MethodInfo getState = typeof(Control).GetMethod("GetState", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic, null, new[] { typeof(int) }, null);
        /// <summary>A control changed size or visibility: every ancestor has to measure again, not just the parent (cards sit inside pages that size them).</summary>
        public static void Relayout(Control c) { for (var p = c.Parent; p != null; p = p.Parent) p.PerformLayout(); }
        public static bool On(Control c)
        {
            if (c.Visible) return true;
            if (getState == null) return false;
            try { return (bool)getState.Invoke(c, new object[] { 2 }); } catch { return false; }
        }
    }

    /// <summary>Plain container painted in a theme colour.</summary>
    public class Pnl : Panel, IFill
    {
        public enum K { Inherit, Bg, Surface, Soft }
        public K Fill = K.Inherit;
        public Pnl() { DoubleBuffered = true; ResizeRedraw = true; }
        public virtual Color Back
        {
            get
            {
                switch (Fill) { case K.Bg: return Theme.Bg; case K.Surface: return Theme.Surface; case K.Soft: return Theme.Soft; }
                var f = Parent as IFill; return f != null ? f.Back : Theme.Bg;
            }
        }
        protected override void OnPaintBackground(PaintEventArgs e) { e.Graphics.Clear(Back); }
        public static Color BackOf(Control c)
        {
            for (var p = c.Parent; p != null; p = p.Parent) { var f = p as IFill; if (f != null) return f.Back; }
            return Theme.Bg;
        }
    }

    /// <summary>
    /// Vertical stack. Children take the full width and their natural height (wrapped text measures against the real width),
    /// except one "fill" child which takes all remaining height. Predictable, unlike nested TableLayoutPanels.
    /// </summary>
    public class Stack : Pnl
    {
        readonly List<Control> items = new List<Control>();
        readonly Dictionary<Control, int> fixedH = new Dictionary<Control, int>();
        Control fill;
        public int Gap = Theme.Px(10);
        public bool StretchButtons; // sign-in style cards want full-width buttons; everywhere else buttons keep their natural width

        public T Add<T>(T c, int? gap = null, int? height = null) where T : Control
        {
            c.Margin = new Padding(0, 0, 0, gap ?? Gap);
            if (height.HasValue) fixedH[c] = height.Value;
            items.Add(c); Controls.Add(c); PerformLayout();
            return c;
        }
        public T AddFill<T>(T c) where T : Control { fill = c; return Add(c, 0); }
        public void ClearItems()
        {
            foreach (var c in items.ToList()) { Controls.Remove(c); c.Dispose(); }
            items.Clear(); fixedH.Clear(); fill = null; PerformLayout();
        }

        int Pref(Control c, int width) { int h; return fixedH.TryGetValue(c, out h) ? h : c.GetPreferredSize(new Size(width, 0)).Height; }

        public int ContentHeight(int innerWidth)
        {
            int y = 0;
            foreach (var c in items) { if (!Vx.On(c)) continue; y += (c == fill ? Theme.Px(120) : Pref(c, innerWidth)) + c.Margin.Bottom; }
            return y;
        }
        public override Size GetPreferredSize(Size proposed)
        {
            int w = proposed.Width > 0 && proposed.Width < 100000 ? proposed.Width : Theme.Px(600);
            int h = ContentHeight(w - Padding.Horizontal) + Padding.Vertical - (items.Count > 0 && Vx.On(items[items.Count - 1]) ? items[items.Count - 1].Margin.Bottom : 0);
            return new Size(w, Math.Max(0, h));
        }
        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            int w = Math.Max(0, ClientSize.Width - Padding.Horizontal), y = Padding.Top;
            int fixedBelow = 0; bool pastFill = false;
            foreach (var c in items) { if (!Vx.On(c)) continue; if (c == fill) { pastFill = true; continue; } if (pastFill) fixedBelow += Pref(c, w) + c.Margin.Bottom; }
            foreach (var c in items)
            {
                if (!Vx.On(c)) continue;
                int h = c == fill ? Math.Max(Theme.Px(80), ClientSize.Height - Padding.Bottom - y - fixedBelow) : Pref(c, w);
                int cw = c is Btn && !StretchButtons ? Math.Min(w, c.GetPreferredSize(Size.Empty).Width) : w;
                c.SetBounds(Padding.Left, y, cw, h);
                y += h + c.Margin.Bottom;
            }
        }
    }

    /// <summary>Left-to-right flow that wraps; each child keeps its own natural size and is centred vertically within its row.</summary>
    public class Flow : Pnl
    {
        public int HGap = Theme.Px(10), VGap = Theme.Px(8);
        public bool AlignRight;
        public T Add<T>(T c) where T : Control { Controls.Add(c); PerformLayout(); return c; }

        Size Run(int width, bool place)
        {
            int y = Padding.Top, maxX = 0;
            var kids = Controls.Cast<Control>().Where(c => Vx.On(c)).ToList();
            var row = new List<KeyValuePair<Control, Size>>(); int x = Padding.Left, rowH = 0;
            Action flush = () =>
            {
                if (row.Count == 0) return;
                int sx = Padding.Left;
                if (AlignRight) { int total = row.Sum(r => r.Value.Width) + HGap * (row.Count - 1); sx = Math.Max(Padding.Left, width - Padding.Right - total); }
                foreach (var r in row) { if (place) r.Key.SetBounds(sx, y + (rowH - r.Value.Height) / 2, r.Value.Width, r.Value.Height); sx += r.Value.Width + HGap; }
                maxX = Math.Max(maxX, sx - HGap); y += rowH + VGap; row.Clear(); rowH = 0; x = Padding.Left;
            };
            foreach (var c in kids)
            {
                var s = c.GetPreferredSize(new Size(0, 0));
                if (row.Count > 0 && x + s.Width > width - Padding.Right) flush();
                row.Add(new KeyValuePair<Control, Size>(c, s)); x += s.Width + HGap; rowH = Math.Max(rowH, s.Height);
            }
            flush();
            return new Size(maxX + Padding.Right, Math.Max(0, y - VGap) + Padding.Bottom);
        }
        public override Size GetPreferredSize(Size proposed)
        {
            bool fixedW = proposed.Width > 0 && proposed.Width < 100000;
            var s = Run(fixedW ? proposed.Width : 100000, false); return new Size(fixedW ? proposed.Width : s.Width, s.Height);
        }
        protected override void OnLayout(LayoutEventArgs e) { base.OnLayout(e); Run(ClientSize.Width, true); }
    }

    /// <summary>A page that scrolls: a Stack whose height is its content, with a scroll bar drawn in the colours of the current theme.</summary>
    public sealed class ScrollPage : Pnl
    {
        public readonly Stack Body = new Stack();
        readonly ThemedScrollBar bar = new ThemedScrollBar();
        int pos, contentH; bool busy;
        /// <summary>How far down the page is scrolled, in pixels (0 = top).</summary>
        public int ScrollPos { get { return pos; } set { SetPos(value); } }
        /// <summary>Height of everything on the page.</summary>
        public int ContentHeight { get { return contentH; } }
        public bool ScrollBarShown { get { return bar.Visible; } }
        public ScrollPage()
        {
            Body.Padding = new Padding(Theme.Px(24)); Body.Gap = Theme.Px(14); Controls.Add(Body);
            bar.Visible = false; Controls.Add(bar); bar.BringToFront(); bar.Scrolled += () => SetPos(bar.Value);
            Hook(Body);
        }
        void Hook(Control c)
        {
            c.ControlAdded += (s, e) => Hook(e.Control);
            c.Enter += (s, e) => KeepInView((Control)s);
            foreach (Control k in c.Controls) Hook(k);
        }
        void KeepInView(Control c)
        {   // tabbing to something off screen scrolls it into view
            if (busy || !bar.Visible || c is Stack || c is Pnl && !(c is Card) && c.Controls.Count > 0 && !c.CanFocus) return;
            var r = RectangleToClient(c.RectangleToScreen(c.ClientRectangle)); int pad = Theme.Px(16);
            if (r.Top < 0) SetPos(pos + r.Top - pad); else if (r.Bottom > ClientSize.Height) SetPos(pos + (r.Bottom - ClientSize.Height) + pad);
        }
        void SetPos(int v)
        {
            int p = Math.Max(0, Math.Min(Math.Max(0, contentH - ClientSize.Height), v));
            pos = p; bar.Value = p;
            if (Body.Top != -p) { Body.SetBounds(0, -p, Body.Width, Body.Height); }
        }
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            var he = e as HandledMouseEventArgs; if (he != null) he.Handled = true;
            if (bar.Visible) SetPos(pos - e.Delta * Theme.Px(60) / 120);
        }
        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            if (busy || ClientSize.Width <= 0) return;
            busy = true;
            try
            {
                int cw = ClientSize.Width, ch = ClientSize.Height, w = cw, h = Body.GetPreferredSize(new Size(w, 0)).Height;
                bool need = h > ch;
                if (need) { w = cw - ThemedScrollBar.Thickness; h = Body.GetPreferredSize(new Size(w, 0)).Height; }   // the bar takes some width, so wrapped text may get taller
                contentH = h; bar.Visible = need;
                if (need) { bar.SetBounds(cw - ThemedScrollBar.Thickness, 0, ThemedScrollBar.Thickness, ch); bar.Maximum = h; bar.LargeChange = ch; bar.BringToFront(); }
                pos = Math.Max(0, Math.Min(Math.Max(0, h - ch), need ? pos : 0)); bar.Value = pos;
                Body.SetBounds(0, -pos, w, h);
            }
            finally { busy = false; }
        }
    }

    /// <summary>White-on-black (or inverse) panel with a thin border and rounded corners.</summary>
    public class Card : Stack
    {
        Lbl sparkTitle; Action themeHook;
        public Card() { Padding = new Padding(Theme.Px(16)); Gap = Theme.Px(10); Fill = K.Surface; }
        /// <summary>Make this card stand out: a tinted background, a coloured edge and a coloured title (cyan on dark themes, purple on light ones).</summary>
        public override Color Back { get { return sparkTitle != null ? Theme.SparkSoft : base.Back; } }
        public void Highlight(Lbl title)
        {
            sparkTitle = title; Padding = new Padding(Theme.Px(22), Theme.Px(16), Theme.Px(16), Theme.Px(16));
            themeHook = () => { if (sparkTitle != null) sparkTitle.Tint = Theme.Spark; Invalidate(); };
            Theme.Changed += themeHook; themeHook();
        }
        protected override void Dispose(bool disposing) { if (disposing && themeHook != null) { Theme.Changed -= themeHook; themeHook = null; } base.Dispose(disposing); }
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            e.Graphics.Clear(BackOf(this));
            Theme.Smooth(e.Graphics);
            var r = new Rectangle(0, 0, Width - 1, Height - 1); bool hi = sparkTitle != null;
            using (var p = Theme.Round(r, Theme.Px(10))) using (var b = new SolidBrush(hi ? Theme.SparkSoft : Theme.Surface)) using (var pen = new Pen(hi ? Theme.Spark : Theme.Line)) { e.Graphics.FillPath(b, p); e.Graphics.DrawPath(pen, p); }
            if (hi)
            {   // a bar down the left edge, following the rounded corners
                var state = e.Graphics.Save();
                using (var clip = Theme.Round(r, Theme.Px(10))) e.Graphics.SetClip(clip);
                using (var b = new SolidBrush(Theme.Spark)) e.Graphics.FillRectangle(b, 0, 0, Theme.Px(7), Height);
                e.Graphics.Restore(state);
            }
        }
    }

    /// <summary>A labelled input: small caption above the control.</summary>
    public sealed class Field : Pnl
    {
        readonly Lbl cap; readonly Control inner; readonly int width;
        public Field(string caption, Control c, int widthPx = 250)
        {
            cap = new Lbl(caption, LS.Muted); inner = c; width = Theme.Px(widthPx);
            Controls.Add(cap); Controls.Add(c);
        }
        public Control Inner { get { return inner; } }
        public override Color Back { get { var f = Parent as IFill; return f != null ? f.Back : Theme.Bg; } }
        public override Size GetPreferredSize(Size proposed)
        {
            var cs = inner.GetPreferredSize(Size.Empty); int cw = Math.Max(width, 0);
            return new Size(cw, cap.GetPreferredSize(Size.Empty).Height + Theme.Px(4) + cs.Height);
        }
        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            int ch = cap.GetPreferredSize(Size.Empty).Height;
            cap.SetBounds(0, 0, Width, ch);
            inner.SetBounds(0, ch + Theme.Px(4), Width, Height - ch - Theme.Px(4));
        }
    }
}
