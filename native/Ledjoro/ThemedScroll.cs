using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Ledjoro
{
    /// <summary>
    /// A vertical scroll bar drawn by Ledjoro in the colours of the chosen theme (the standard Windows one can only be light or dark).
    /// It is a thin rounded handle on a quiet track that follows the page's background; the handle brightens when the mouse is over it.
    /// It only holds numbers (how much there is, how much is visible, where we are): whoever owns the content does the moving.
    /// </summary>
    public sealed class ThemedScrollBar : Control
    {
        int max, view, value; bool hot, drag; int dragOffset; readonly System.Windows.Forms.Timer repeat = new System.Windows.Forms.Timer { Interval = 350 };
        int pageDir; int pageTarget;
        public event Action Scrolled;
        public static int Thickness { get { return Theme.Px(14); } }

        public ThemedScrollBar()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true); SetStyle(ControlStyles.Selectable, false);
            TabStop = false; Width = Thickness; Cursor = Cursors.Default;
            repeat.Tick += (s, e) => { repeat.Interval = 60; Page(); };
        }

        /// <summary>Total size of the content, the size of the visible part, and the current position (all in pixels).</summary>
        public int Maximum { get { return max; } set { max = Math.Max(0, value); Clamp(); Invalidate(); } }
        public int LargeChange { get { return view; } set { view = Math.Max(1, value); Clamp(); Invalidate(); } }
        public int Value { get { return value; } set { int v = Math.Max(0, Math.Min(Range, value)); if (v != this.value) { this.value = v; Invalidate(); } } }
        public int Range { get { return Math.Max(0, max - view); } }
        void Clamp() { value = Math.Max(0, Math.Min(Range, value)); }

        Rectangle Track { get { return new Rectangle(Theme.Px(3), Theme.Px(2), Math.Max(2, Width - Theme.Px(6)), Math.Max(2, Height - Theme.Px(4))); } }
        Rectangle Thumb
        {
            get
            {
                var t = Track; if (max <= view || Range == 0) return Rectangle.Empty;
                int h = Math.Max(Theme.Px(32), (int)((long)t.Height * view / max)); h = Math.Min(h, t.Height);
                int y = t.Top + (int)((long)(t.Height - h) * value / Range);
                return new Rectangle(t.Left, y, t.Width, h);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Theme.Smooth(e.Graphics);
            e.Graphics.Clear(Pnl.BackOf(this));
            var th = Thumb; if (th.IsEmpty) return;
            var color = drag || hot ? Theme.Muted : Mix(Theme.Line, Theme.Muted);
            using (var p = Theme.Round(th, th.Width / 2)) using (var b = new SolidBrush(color)) e.Graphics.FillPath(b, p);
        }

        static Color Mix(Color a, Color b) { return Color.FromArgb((a.R + b.R) / 2, (a.G + b.G) / 2, (a.B + b.B) / 2); }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (drag) { var t = Track; var th = Thumb; int span = t.Height - th.Height; if (span > 0) Value = (int)((long)(e.Y - dragOffset - t.Top) * Range / span); Fire(); return; }
            bool over = Thumb.Contains(e.Location); if (over != hot) { hot = over; Invalidate(); }
        }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); if (hot && !drag) { hot = false; Invalidate(); } }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left || Range == 0) return;
            var th = Thumb;
            if (th.Contains(e.Location)) { drag = true; dragOffset = e.Y - th.Top; Capture = true; Invalidate(); return; }
            pageDir = e.Y < th.Top ? -1 : 1; pageTarget = e.Y; Page(); repeat.Interval = 350; repeat.Start();   // a click on the track moves a page, and repeats while held
        }
        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); repeat.Stop(); if (drag) { drag = false; Capture = false; hot = Thumb.Contains(e.Location); Invalidate(); } }
        void Page()
        {
            var th = Thumb; if (th.IsEmpty || (pageDir < 0 && pageTarget >= th.Top) || (pageDir > 0 && pageTarget <= th.Bottom)) { repeat.Stop(); return; }   // reached the click
            Value = value + pageDir * (int)(view * 0.9); Fire();
        }
        void Fire() { var h = Scrolled; if (h != null) h(); }
        protected override void Dispose(bool disposing) { if (disposing) repeat.Dispose(); base.Dispose(disposing); }
    }
}
