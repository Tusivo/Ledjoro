using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Ledjoro
{
    public static class App
    {
        public static Api Api;
        public static Engine Engine;
        public static MainForm Main;
        public static bool SelfTesting;   // the scripted self-test is allowed to close the window whatever is on screen
        public static Icon AppIcon = MakeIcon(32);

        public static void Toast(string text, bool bad = false) { if (Main != null && !Main.IsDisposed) Main.ShowToast(text, bad); }

        /// <summary>The Ledjoro mark, drawn at any size (used for the window icon, the sidebar and the .exe icon): an open ledger with an amber ribbon marker, on a rounded indigo-to-teal tile.</summary>
        public static Bitmap Logo(int px)
        {
            var bmp = new Bitmap(px, px);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias; g.PixelOffsetMode = PixelOffsetMode.HighQuality; g.Clear(Color.Transparent);
                float s = px;
                var tile = new RectangleF(s * 0.02f, s * 0.02f, s * 0.96f, s * 0.96f);
                using (var path = RoundRect(tile, s * 0.22f))
                {
                    using (var fill = new LinearGradientBrush(new PointF(0, 0), new PointF(s, s), Color.FromArgb(67, 56, 202), Color.FromArgb(8, 145, 178))) g.FillPath(fill, path);
                    if (px >= 96)
                    {   // a soft light from the top-left, and a fine inner edge, so the tile has depth without looking busy
                        using (var shine = new LinearGradientBrush(new PointF(0, 0), new PointF(0, s * 0.6f), Color.FromArgb(70, 255, 255, 255), Color.FromArgb(0, 255, 255, 255)))
                        { var clip = g.Clip; g.SetClip(path); g.FillRectangle(shine, 0, 0, s, s * 0.6f); g.Clip = clip; }
                        using (var edge = new Pen(Color.FromArgb(60, 255, 255, 255), Math.Max(1f, s / 64f))) g.DrawPath(edge, path);
                    }
                }
                // an open ledger: two ruled pages with an amount column on each, and an amber ribbon marker (drawn here, not taken from anywhere)
                Func<float, float> X = u => u * s, Y = v => v * s;
                Func<float, float, float, float, float, float, PointF[]> bez = null;
                Action<GraphicsPath, bool, float> page = (gp, left, grow) =>
                {
                    float m = left ? -1 : 1, cx = 0.5f, o = grow;
                    // spine at the centre, outer edge at 0.5 +/- 0.34; the top edge dips towards the spine like a page being held open
                    gp.AddBezier(X(cx), Y(0.33f - o), X(cx + m * 0.09f), Y(0.255f - o), X(cx + m * 0.22f), Y(0.235f - o), X(cx + m * (0.34f + o)), Y(0.285f - o));
                    gp.AddLine(X(cx + m * (0.34f + o)), Y(0.285f - o), X(cx + m * (0.34f + o)), Y(0.715f + o));
                    gp.AddBezier(X(cx + m * (0.34f + o)), Y(0.715f + o), X(cx + m * 0.22f), Y(0.665f + o), X(cx + m * 0.09f), Y(0.685f + o), X(cx), Y(0.775f + o));
                    gp.CloseFigure();
                };
                if (px >= 40)
                {   // the cover showing around the pages
                    using (var cover = new GraphicsPath()) using (var cb = new SolidBrush(Color.FromArgb(70, 255, 255, 255))) { page(cover, true, 0.028f); page(cover, false, 0.028f); g.FillPath(cb, cover); }
                }
                using (var left = new GraphicsPath()) using (var right = new GraphicsPath())
                {
                    page(left, true, 0f); page(right, false, 0f);
                    using (var pb = new LinearGradientBrush(new PointF(X(0.16f), 0), new PointF(X(0.5f), 0), Color.White, Color.FromArgb(226, 232, 240))) g.FillPath(pb, left);
                    using (var pb = new LinearGradientBrush(new PointF(X(0.84f), 0), new PointF(X(0.5f), 0), Color.White, Color.FromArgb(226, 232, 240))) g.FillPath(pb, right);
                }
                if (px >= 28)
                {   // ruled lines and the vertical amount column on each page
                    float lw = Math.Max(1f, s * 0.018f);
                    using (var rule = new Pen(Color.FromArgb(px >= 48 ? 150 : 190, 71, 85, 105), lw)) using (var col = new Pen(Color.FromArgb(200, 14, 165, 233), lw))
                    {
                        int rows = px >= 48 ? 4 : 3;
                        for (int side = 0; side < 2; side++)
                        {
                            float m = side == 0 ? -1 : 1;
                            for (int i = 0; i < rows; i++)
                            {
                                float y = 0.385f + i * (px >= 48 ? 0.092f : 0.12f);
                                g.DrawLine(rule, X(0.5f + m * 0.075f), Y(y), X(0.5f + m * 0.285f), Y(y));
                            }
                            if (px >= 48) g.DrawLine(col, X(0.5f + m * 0.205f), Y(0.34f), X(0.5f + m * 0.205f), Y(0.69f));
                        }
                    }
                }
                using (var ribbon = new GraphicsPath())
                {   // the marker, hanging from the spine past the bottom edge, with a swallowtail end
                    float rw = s * 0.05f, cx = X(0.5f);
                    ribbon.AddPolygon(new[] { new PointF(cx - rw / 2, Y(0.32f)), new PointF(cx + rw / 2, Y(0.32f)), new PointF(cx + rw / 2, Y(0.86f)), new PointF(cx, Y(0.82f)), new PointF(cx - rw / 2, Y(0.86f)) });
                    using (var rb = new SolidBrush(Color.FromArgb(251, 191, 36))) g.FillPath(rb, ribbon);
                }
            }
            return bmp;
        }
        static GraphicsPath RoundRect(RectangleF r, float radius)
        {
            var p = new GraphicsPath(); float d = radius * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90); p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90); p.CloseFigure();
            return p;
        }
        static Icon MakeIcon(int px) { using (var b = Logo(px)) return Icon.FromHandle(b.GetHicon()); }
    }

    /// <summary>One page of the app. Load() fetches data from the engine and builds the controls.</summary>
    public abstract class Screen : Pnl
    {
        public string NavKey = "";
        protected Api A { get { return App.Api; } }
        protected Screen() { Dock = DockStyle.Fill; Fill = K.Bg; }
        public abstract Task Load();
        protected void SetBody(Control c) { SuspendLayout(); foreach (Control x in Controls.Cast<Control>().ToList()) { Controls.Remove(x); x.Dispose(); } c.Dock = DockStyle.Fill; Controls.Add(c); ResumeLayout(true); Theme.Refresh(this); }
        protected static void Nav(string hash) { App.Main.Go(hash); }
    }

    /// <summary>Sidebar navigation with section headings.</summary>
    public sealed class NavList : Control
    {
        public class Entry { public string Key, Label; public bool Group, External; }   // External: opens something outside the program and is never the selected page
        public readonly List<Entry> Entries = new List<Entry>();
        string sel = ""; int hot = -1, scroll;
        public event Action<string> Picked;
        public NavList() { SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.Selectable | ControlStyles.ResizeRedraw, true); TabStop = true; Cursor = Cursors.Default; }
        public string Selected { get { return sel; } set { sel = value; Invalidate(); } }
        int H(Entry e) { return e.Group ? Theme.Px(30) : Theme.Px(34); }
        int Total() { return Entries.Sum(H) + Theme.Px(8); }
        int At(int y) { int cy = -scroll; for (int i = 0; i < Entries.Count; i++) { int h = H(Entries[i]); if (y >= cy && y < cy + h) return i; cy += h; } return -1; }
        protected override void OnMouseWheel(MouseEventArgs e) { base.OnMouseWheel(e); scroll = Math.Max(0, Math.Min(Math.Max(0, Total() - Height), scroll - e.Delta / 2)); Invalidate(); }
        protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); int i = At(e.Y); if (i >= 0 && Entries[i].Group) i = -1; if (i != hot) { hot = i; Cursor = i >= 0 ? Cursors.Hand : Cursors.Default; Invalidate(); } }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hot = -1; Invalidate(); }
        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); Focus(); int i = At(e.Y); if (i >= 0 && !Entries[i].Group) { var h = Picked; if (Entries[i].External) { if (h != null) h(Entries[i].Key); return; } sel = Entries[i].Key; Invalidate(); if (h != null) h(sel); } }
        protected override bool IsInputKey(Keys k) { return k == Keys.Up || k == Keys.Down || base.IsInputKey(k); }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            var items = Entries.Where(x => !x.Group && !x.External).ToList(); int i = items.FindIndex(x => x.Key == sel);
            if (e.KeyCode == Keys.Down && i < items.Count - 1) { sel = items[i + 1].Key; Invalidate(); Picked?.Invoke(sel); e.Handled = true; }
            if (e.KeyCode == Keys.Up && i > 0) { sel = items[i - 1].Key; Invalidate(); Picked?.Invoke(sel); e.Handled = true; }
            base.OnKeyDown(e);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            Theme.Smooth(e.Graphics); e.Graphics.Clear(Theme.Surface);
            int y = -scroll;
            for (int i = 0; i < Entries.Count; i++)
            {
                var en = Entries[i]; int h = H(en); var r = new Rectangle(Theme.Px(10), y, Width - Theme.Px(20), h - 2);
                if (en.Group) TextRenderer.DrawText(e.Graphics, en.Label.ToUpperInvariant(), Theme.F(8f, FontStyle.Bold), new Rectangle(r.X + Theme.Px(10), y + Theme.Px(8), r.Width, h - Theme.Px(8)), Theme.Muted, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.Bottom);
                else
                {
                    bool on = en.Key == sel;
                    if (on || i == hot) using (var p = Theme.Round(r, Theme.Px(7))) using (var b = new SolidBrush(on ? Theme.AccentSoft : Theme.Soft)) e.Graphics.FillPath(b, p);
                    if (on) using (var b = new SolidBrush(Theme.Accent)) e.Graphics.FillRectangle(b, r.X, r.Y + Theme.Px(7), Theme.Px(3), r.Height - Theme.Px(14));
                    int ic = Theme.Px(24), tx = Theme.Px(14); if (NavIcons.Has(en.Key)) { NavIcons.Draw(e.Graphics, en.Key, new Rectangle(r.X + Theme.Px(14), r.Y + (r.Height - ic) / 2, ic, ic)); tx = Theme.Px(14) + ic + Theme.Px(10); }
                    TextRenderer.DrawText(e.Graphics, en.Label, Theme.F(10f, on ? FontStyle.Bold : FontStyle.Regular), new Rectangle(r.X + tx, r.Y, r.Width - tx, r.Height), Theme.Ink, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
                    if (on && Focused) ControlPaint.DrawFocusRectangle(e.Graphics, r);
                }
                y += h;
            }
        }
    }

    /// <summary>A small round badge with the signed-in person's initials; their full name appears when the mouse rests on it.</summary>
    sealed class UserBadge : Control
    {
        readonly ToolTip tip = new ToolTip();
        public UserBadge() { SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true); TabStop = false; BackColor = Color.Transparent; AccessibleRole = AccessibleRole.StaticText; }
        public string Initials
        {
            get
            {
                var words = (Text ?? "").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (words.Length == 0) return "?";
                return (words.Length == 1 ? words[0].Substring(0, 1) : words[0].Substring(0, 1) + words[words.Length - 1].Substring(0, 1)).ToUpperInvariant();
            }
        }
        protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); tip.SetToolTip(this, Text); AccessibleName = "Signed in as " + Text; Invalidate(); if (Parent != null) Parent.PerformLayout(); }
        public override Size GetPreferredSize(Size proposed) { int d = Theme.Px(30); return new Size(d, d); }
        protected override void OnPaint(PaintEventArgs e)
        {
            Theme.Smooth(e.Graphics); e.Graphics.Clear(Pnl.BackOf(this));
            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            using (var b = new SolidBrush(Theme.AccentSoft)) e.Graphics.FillEllipse(b, r);
            using (var p = new Pen(Theme.Line)) e.Graphics.DrawEllipse(p, r);
            TextRenderer.DrawText(e.Graphics, Initials, Theme.F(Initials.Length > 1 ? 9f : 10f, FontStyle.Bold), new Rectangle(0, 0, Width, Height), Theme.Ink, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
        protected override void Dispose(bool disposing) { if (disposing) tip.Dispose(); base.Dispose(disposing); }
    }

    sealed class TopBar : Pnl
    {
        public readonly Lbl Company = new Lbl("", LS.Bold); public readonly UserBadge User = new UserBadge();
        public readonly Btn New, ThemeBtn, SignOut, Back, Fwd; public readonly Btn Search = new Btn("Search  Ctrl+K", BK.Normal, null, true);
        public TopBar(Btn newBtn, Btn themeBtn, Btn signOut, Btn back, Btn fwd)
        {
            Fill = K.Surface; New = newBtn; ThemeBtn = themeBtn; SignOut = signOut; Back = back; Fwd = fwd;
            Controls.AddRange(new Control[] { Company, User, New, Search, ThemeBtn, SignOut, Back, Fwd });
        }
        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            int x = Width - Theme.Px(20);
            foreach (Control c in new Control[] { User, SignOut, ThemeBtn, New, Search })
            {
                if (!c.Visible) continue;
                var s = c.GetPreferredSize(Size.Empty); x -= s.Width; c.SetBounds(x, (Height - s.Height) / 2, s.Width, s.Height); x -= Theme.Px(10);
            }
            int lx = Theme.Px(20);
            foreach (var b in new[] { Back, Fwd }) { int bw = Theme.Px(40), bh = b.GetPreferredSize(Size.Empty).Height; b.SetBounds(lx, (Height - bh) / 2, bw, bh); lx += bw + Theme.Px(6); }
            lx += Theme.Px(10);
            var cs = Company.GetPreferredSize(Size.Empty); Company.SetBounds(lx, (Height - cs.Height) / 2, Math.Max(10, x - lx - Theme.Px(10)), cs.Height);
        }
        protected override void OnPaint(PaintEventArgs e) { using (var p = new Pen(Theme.Line)) e.Graphics.DrawLine(p, 0, Height - 1, Width, Height - 1); }
    }

    sealed class ToastBox : Control
    {
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 4200 }; bool bad;
        public ToastBox() { SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true); Visible = false; TabStop = false; timer.Tick += (s, e) => { timer.Stop(); Visible = false; }; }
        public void Pop(string text, bool isBad, Control host)
        {
            Text = text; bad = isBad;
            int w = Math.Min(Theme.Px(460), host.ClientSize.Width - Theme.Px(40));
            var m = TextRenderer.MeasureText(text, Theme.F(10f), new Size(w - Theme.Px(30), 1000), TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
            Size = new Size(w, m.Height + Theme.Px(24)); Location = new Point(host.ClientSize.Width - w - Theme.Px(20), host.ClientSize.Height - Height - Theme.Px(20));
            Visible = true; BringToFront(); Invalidate(); timer.Stop(); timer.Start();
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            Theme.Smooth(e.Graphics);
            using (var p = Theme.Round(new Rectangle(0, 0, Width - 1, Height - 1), Theme.Px(9))) using (var b = new SolidBrush(bad ? Theme.Bad : Theme.Ink)) e.Graphics.FillPath(b, p);
            TextRenderer.DrawText(e.Graphics, Text, Theme.F(10f), new Rectangle(Theme.Px(15), Theme.Px(9), Width - Theme.Px(30), Height - Theme.Px(12)), bad ? Color.White : Theme.Bg, TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
        }
    }

    /// <summary>Hosts the sign-in / setup screens centred in the window, with a floating theme button.</summary>
    sealed class AuthHost : Pnl
    {
        public Control Child; public readonly Btn ThemeBtn;
        readonly ThemedScrollBar bar = new ThemedScrollBar(); int pos, contentH;
        public AuthHost(Btn themeBtn) { ThemeBtn = themeBtn; Controls.Add(themeBtn); bar.Visible = false; Controls.Add(bar); bar.Scrolled += () => SetPos(bar.Value); }
        void SetPos(int v) { pos = Math.Max(0, Math.Min(Math.Max(0, contentH - ClientSize.Height), v)); bar.Value = pos; PerformLayout(); }
        protected override void OnMouseWheel(MouseEventArgs e) { var he = e as HandledMouseEventArgs; if (he != null) he.Handled = true; if (bar.Visible) SetPos(pos - e.Delta * Theme.Px(60) / 120); }
        public void Show(Control c) { if (Child != null) { Controls.Remove(Child); Child.Dispose(); } Child = c; Controls.Add(c); PerformLayout(); }
        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            var ts = ThemeBtn.GetPreferredSize(Size.Empty); ThemeBtn.SetBounds(ClientSize.Width - ts.Width - Theme.Px(20), Theme.Px(16), ts.Width, ts.Height); ThemeBtn.BringToFront();
            if (Child == null) return;
            int w = Math.Min(Theme.Px(Child.Tag is int ? (int)Child.Tag : 460), ClientSize.Width - Theme.Px(30));
            int h = Child.GetPreferredSize(new Size(w, 0)).Height;
            int top = Math.Max(Theme.Px(60), (ClientSize.Height - h) / 2);
            contentH = top + h + Theme.Px(30); bool need = contentH > ClientSize.Height;
            pos = need ? Math.Max(0, Math.Min(contentH - ClientSize.Height, pos)) : 0;
            Child.SetBounds((ClientSize.Width - w) / 2, top - pos, w, h);
            bar.Visible = need;
            if (need) { bar.SetBounds(ClientSize.Width - ThemedScrollBar.Thickness, 0, ThemedScrollBar.Thickness, ClientSize.Height); bar.Maximum = contentH; bar.LargeChange = ClientSize.Height; bar.Value = pos; bar.BringToFront(); }
        }
    }

    public sealed class MainForm : Form
    {
        /// <summary>While this is set (the sign-in sheet is on screen and not yet printed or saved), the window cannot be closed by the person.</summary>
        public string CloseGuard;
        public bool BlocksClose(CloseReason reason) { return CloseGuard != null && reason == CloseReason.UserClosing; }
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!App.SelfTesting && BlocksClose(e.CloseReason))
            {
                e.Cancel = true;
                Msg.Info("Print or save your sign-in sheet first", CloseGuard);
                return;
            }
            base.OnFormClosing(e);
        }
        readonly Pnl side = new Pnl { Fill = Pnl.K.Surface, Dock = DockStyle.Left }, content = new Pnl { Dock = DockStyle.Fill };
        readonly NavList nav = new NavList { Dock = DockStyle.Fill };
        readonly TopBar top; readonly AuthHost auth; readonly ToastBox toast = new ToastBox();
        readonly Lbl brandName = new Lbl("Ledjoro", LS.H2);
        Screen current; int navToken; Btn themeTop, themeAuth, backBtn, fwdBtn;
        readonly List<string> history = new List<string>(); int hpos = -1; // pages visited, like a browser: back / forward walk along it
        string currentHash = "";
        public string CurrentHash { get { return currentHash; } }
        public Control Content { get { return content; } }
        public Control AuthArea { get { return auth; } }

        public MainForm()
        {
            App.Main = this;
            Text = "Ledjoro"; Icon = App.AppIcon; AutoScaleMode = AutoScaleMode.None; Font = Theme.F(10f); DoubleBuffered = true;
            StartPosition = FormStartPosition.Manual;
            // a window rather than full screen, but roomy: 1920 x 1080 on the screen the mouse is on, centred (never smaller than the minimum, never bigger than the screen)
            var wa = System.Windows.Forms.Screen.FromPoint(Cursor.Position).WorkingArea;
            MinimumSize = new Size(Math.Min(Theme.Px(1040), wa.Width), Math.Min(Theme.Px(660), wa.Height));
            Size = new Size(Math.Min(wa.Width, Math.Max(MinimumSize.Width, 1920)), Math.Min(wa.Height, Math.Max(MinimumSize.Height, 1080)));   // 1920 x 1080 pixels, or the whole screen if that is smaller
            Location = new Point(wa.Left + (wa.Width - Width) / 2, wa.Top + (wa.Height - Height) / 2);

            var newBtn = new Btn("+ New", BK.Normal, null, true); newBtn.Click += (s, e) => ShowNewMenu(newBtn);
            themeTop = ThemeButton(); themeAuth = ThemeButton();
            var signOut = new Btn("Sign Out", BK.Normal, null, true); signOut.Click += (s, e) => Ui.Run(SignOut);
            backBtn = new Btn("\u2190", BK.Normal, (s, e) => Back(), true) { AccessibleName = "Back" }; fwdBtn = new Btn("\u2192", BK.Normal, (s, e) => Forward(), true) { AccessibleName = "Forward" };
            var tip = new ToolTip(); tip.SetToolTip(backBtn, "Back (Alt+Left)"); tip.SetToolTip(fwdBtn, "Forward (Alt+Right)");
            top = new TopBar(newBtn, themeTop, signOut, backBtn, fwdBtn) { Dock = DockStyle.Top, Height = Theme.Px(54) }; top.Search.Click += (se, ev) => SearchDialog.Show();
            top.Company.Cursor = Cursors.Hand; top.Company.Click += (s, e) => ShowCompanyMenu(top.Company);
            UpdateNavButtons();
            auth = new AuthHost(themeAuth) { Dock = DockStyle.Fill };

            side.Width = Theme.Px(236); side.Controls.Add(nav); side.Paint += (s, e) => { using (var p = new Pen(Theme.Line)) e.Graphics.DrawLine(p, side.Width - 1, 0, side.Width - 1, side.Height); };
            var brand = new Pnl { Fill = Pnl.K.Surface, Dock = DockStyle.Top, Height = Theme.Px(64) };
            var logo = new PictureBox { Image = App.Logo(Theme.Px(30)), SizeMode = PictureBoxSizeMode.AutoSize, Location = new Point(Theme.Px(20), Theme.Px(17)), BackColor = Color.Transparent };
            brand.Controls.Add(logo); brand.Controls.Add(brandName); brandName.Fit(); brandName.Location = new Point(Theme.Px(58), Theme.Px(19));
            side.Controls.Add(brand); brand.BringToFront(); nav.BringToFront();

            content.Controls.Add(toast);
            Controls.Add(content); Controls.Add(top); Controls.Add(side); Controls.Add(auth);
            side.Visible = top.Visible = false; content.Visible = false;
            nav.Picked += key => { if (key == "support") Links.Open(Links.Donate); else Go("#/" + key); };
            Theme.Changed += () => { ApplyChrome(); };
            ApplyChrome();
        }

        Btn ThemeButton()
        {
            var b = new Btn("", BK.Normal, null, true);
            b.Click += (s, e) => ShowThemeMenu(b);
            return b;
        }
        public ContextMenuStrip ThemeMenu;
        public void ShowThemeMenu(Control anchor)
        {
            var m = ThemeMenu = new ContextMenuStrip { Renderer = new ThemedRenderer(), Font = Theme.F(10f), ShowImageMargin = false };
            for (int i = 0; i < Theme.Choices.GetLength(0); i++)
            {
                string key = Theme.Choices[i, 0], title = Theme.Choices[i, 1];
                m.Items.Add(new ToolStripMenuItem((key == Theme.Name ? "\u2713  " : "     ") + title, null, (s, e) => { Theme.Use(key); Theme.Save(); }) { Padding = new Padding(8, 5, 8, 5), AccessibleName = title + " theme" });
            }
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add(new ToolStripMenuItem("Text size") { Enabled = false, Padding = new Padding(8, 3, 8, 3), AccessibleName = "Text size" });
            for (int i = 0; i < Theme.TextChoices.GetLength(0); i++)
            {
                string key = Theme.TextChoices[i, 0], title = Theme.TextChoices[i, 1];
                m.Items.Add(new ToolStripMenuItem((key == Theme.Text ? "\u2713  " : "     ") + title, null, (s, e) => Ui.Run(() => ChangeTextSize(key))) { Padding = new Padding(8, 5, 8, 5), AccessibleName = title + " text size" });
            }
            m.Show(anchor, new Point(0, anchor.Height));
        }

        /// <summary>Text size is applied by starting Ledjoro again (every window is laid out at the new size from the start).</summary>
        async Task ChangeTextSize(string key)
        {
            if (key == Theme.Text) return;
            if (!Msg.Confirm("Change the text size?", "Ledjoro will close and open again to use the " + key + " text size, and you will sign in again. Your books are not affected. Anything you are typing right now that is not saved will be lost.", "Close and Reopen")) return;
            Theme.Text = key; Theme.Save();
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Application.ExecutablePath, "--after " + System.Diagnostics.Process.GetCurrentProcess().Id) { UseShellExecute = false }); }
            catch (Exception) { Msg.Info("Ledjoro could not open itself again", "The new text size is saved. Close Ledjoro and open it yourself to see it.", true); return; }
            await Task.FromResult(0); Close();
        }
        void ApplyChrome()
        {
            BackColor = Theme.Bg; string label = "Theme: " + Theme.Title + " \u25BE";
            themeTop.Text = label; themeAuth.Text = label;
            themeTop.AccessibleName = themeAuth.AccessibleName = "Choose theme (current: " + Theme.Title + ")";
            if (IsHandleCreated) Native.TitleBar(Handle, Theme.Dark);
            Theme.Refresh(this); PerformLayout();
        }
        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Native.TitleBar(Handle, Theme.Dark); }

        bool OpenDialogActive() { return Form.ActiveForm != null && Form.ActiveForm != this; }
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == (Keys.Alt | Keys.Left) && top.Visible) { Back(); return true; }
            if (keyData == (Keys.Alt | Keys.Right) && top.Visible) { Forward(); return true; }
            if (keyData == (Keys.Control | Keys.K) && top.Visible && !OpenDialogActive()) { SearchDialog.Show(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }
        protected override void WndProc(ref Message m)
        {   // mouse "back" / "forward" side buttons arrive as WM_APPCOMMAND
            if (m.Msg == 0x0319 && top.Visible)
            {
                int cmd = (int)(((long)m.LParam >> 16) & 0x0FFF);
                if (cmd == 1) { Back(); m.Result = (IntPtr)1; return; }
                if (cmd == 2) { Forward(); m.Result = (IntPtr)1; return; }
            }
            base.WndProc(ref m);
        }

        public string LastToast = "";
        public void ShowToast(string text, bool bad) { LastToast = text; toast.Pop(text, bad, toast.Parent ?? (Control)this); }

        // ---------- companies ----------
        void CloseCompany()
        {
            if (App.Api != null) { try { App.Api.Dispose(); } catch (Exception) { } App.Api = null; }
            if (App.Engine != null) { App.Engine.Dispose(); App.Engine = null; }
        }

        /// <summary>Starts the engine on a company's folder and shows its sign-in (or first-time setup) screen.</summary>
        public async Task OpenCompany(CompanyInfo c, bool boot = true)
        {
            ShowAuth(new StartingCard());
            try
            {
                CloseCompany();
                string dir = Companies.FolderOf(c); Directory.CreateDirectory(dir);
                Engine.CompanyDir = dir; Companies.Current = c; integrityShown = false; currentHash = ""; ClearHistory(); Lists.Bust();
                App.Engine = new Engine(); App.Engine.Start();
                App.Api = new Api(App.Engine.Port, App.Engine.Key);
                App.Api.SessionLost += () => BeginInvoke(new Action(SessionLost));
                await App.Engine.WaitReady(App.Api);
                if (boot) await Boot();
            }
            catch (Exception ex)
            {
                CloseCompany();
                Msg.Info("Ledjoro could not open \"" + c.Name + "\"", Say.Plain(ex), true);
                ShowPicker();
            }
        }

        public void ShowPicker() { CloseCompany(); top.Visible = side.Visible = content.Visible = false; ShowAuth(new CompanyPicker()); }

        public async Task NewCompany()
        {
            string name = null; var input = new Input("for example: Acme Plumbing LLC");
            var dlg = new Dlg("New company", 540);
            dlg.Body.Add(new Lbl("A new company is a separate set of books with its own users, passwords and recovery key, kept in its own folder. You can switch between companies at any time from the company name at the top of the window.", LS.Muted, true));
            dlg.Body.Add(Ui.F("Company name", input, 460)); dlg.Cancel();
            dlg.Primary("Create Company", () => { try { Companies.CreateNew(input.Text); name = input.Text.Trim(); return Task.FromResult(true); } catch (InvalidOperationException e) { throw new ApiException(0, e.Message, Jv.Null); } });
            if (dlg.Run() != DialogResult.OK || name == null) return;
            await OpenCompany(Companies.List.First(c => c.Name == name));
        }

        public async Task AddExistingCompany()
        {
            using (var d = new FolderBrowserDialog { Description = "Choose the company's folder: the one that contains the file ledjoro.db." })
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                try { var c = Companies.AddExisting(d.SelectedPath); await OpenCompany(c); }
                catch (InvalidOperationException e) { Msg.Info("That is not a company folder", e.Message, true); }
            }
        }

        void ShowCompanyMenu(Control anchor)
        {
            var m = new ContextMenuStrip { Renderer = new ThemedRenderer(), Font = Theme.F(10f), ShowImageMargin = false };
            foreach (var c0 in Companies.List)
            {
                var c = c0;
                m.Items.Add(new ToolStripMenuItem((c == Companies.Current ? "\u2713  " : "     ") + c.Name, null, (s, e) => { if (c != Companies.Current) Ui.Run(() => OpenCompany(c)); }) { Padding = new Padding(8, 5, 8, 5) });
            }
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add(new ToolStripMenuItem("New company…", null, (s, e) => Ui.Run(NewCompany)) { Padding = new Padding(8, 5, 8, 5) });
            m.Items.Add(new ToolStripMenuItem("Add an existing company…", null, (s, e) => Ui.Run(AddExistingCompany)) { Padding = new Padding(8, 5, 8, 5) });
            m.Items.Add(new ToolStripMenuItem("Show all companies…", null, (s, e) => ShowPicker()) { Padding = new Padding(8, 5, 8, 5) });
            m.Show(anchor, new Point(0, anchor.Height));
        }

        // ---------- signed-out screens ----------
        public void ShowAuth(Control screen)
        {
            side.Visible = top.Visible = content.Visible = false; auth.Visible = true;
            if (toast.Parent != auth) { toast.Parent?.Controls.Remove(toast); auth.Controls.Add(toast); }
            auth.Show(screen); Theme.Refresh(auth); toast.BringToFront();
        }

        /// <summary>Decide what to show first: setup, sign-in, or the app (when a session already exists).</summary>
        public async Task Boot()
        {
            current = null;
            bool signedIn = await App.Api.RefreshSession();
            if (signedIn) { if (App.Api.User["mustChangePassword"].Bool) ShowAuth(new ChangePasswordScreen(true)); else ShowShell(); }
            else if (App.Api.SetupRequired) ShowAuth(new SetupScreen(App.Engine.SetupToken));
            else ShowAuth(new LoginScreen());
        }

        // ---------- the signed-in app ----------
        bool integrityShown;
        public void ShowShell()
        {
            if (!integrityShown && !App.Api.Integrity.IsNull && !App.Api.Integrity["ok"].Bool)
            {   // the books file was changed by something other than Ledjoro while it was closed: say so once, plainly
                integrityShown = true;
                string list = string.Join("\n\u2022 ", App.Api.Integrity["problems"].Items.Select(x => x.Str));
                BeginInvoke(new Action(() => Msg.Info("The books were changed outside Ledjoro",
                    "When Ledjoro started, it found that the books file does not look like Ledjoro wrote it:\n\n\u2022 " + list + "\n\nSomeone or something edited the file with another program. Check Settings, Audit & integrity, and consider restoring a recent backup.", true)));
            }
            auth.Visible = false; side.Visible = top.Visible = content.Visible = true;
            if (toast.Parent != content) { toast.Parent?.Controls.Remove(toast); content.Controls.Add(toast); }
            Companies.Touch(Companies.Current, App.Api.Company);
            top.Company.Text = App.Api.Company + "  \u25BE"; top.User.Text = App.Api.User["displayName"].Str;
            nav.Entries.Clear();
            Action<string, string[][]> grp = (g, items) => { nav.Entries.Add(new NavList.Entry { Label = g, Group = true }); foreach (var i in items) nav.Entries.Add(new NavList.Entry { Key = i[0], Label = i[1] }); };
            grp("Overview", new[] { new[] { "dashboard", "Dashboard" } });
            grp("Sales", new[] { new[] { "invoices", "Invoices" }, new[] { "customers", "Customers" } });
            grp("Purchases", new[] { new[] { "bills", "Bills" }, new[] { "vendors", "Vendors" } });
            grp("Banking", new[] { new[] { "banking", "Bank Transactions" }, new[] { "reconcile", "Reconcile" } });
            grp("Accounting", new[] { new[] { "accounts", "Chart of Accounts" }, new[] { "journal", "Journal" }, new[] { "assets", "Fixed Assets" }, new[] { "reports", "Reports" }, new[] { "health", "Books Health" } });
            grp("Data", App.Api.Can("import") ? new[] { new[] { "import", "Import & Convert" }, new[] { "quickbooks", "QuickBooks Sync" } } : new string[0][]);
            grp("Admin", new[] { new[] { "settings", "Settings" }, new[] { "about", "About" } });
            nav.Entries.RemoveAll(e => e.Group && nav.Entries.IndexOf(e) == nav.Entries.Count - 1 || (e.Group && e.Label == "Data" && !App.Api.Can("import")));
            nav.Entries.Add(new NavList.Entry { Key = "support", Label = "Support Ledjoro", External = true });
            nav.Invalidate(); Theme.Refresh(this); PerformLayout();
            Go(currentHash.Length > 0 ? currentHash : "#/dashboard");
        }

        async Task SignOut()
        {
            try { await App.Api.Post("api/logout"); } catch (ApiException) { }
            await App.Api.RefreshSession(); currentHash = ""; ClearHistory(); ShowAuth(new LoginScreen());
        }
        public void SessionLost() { currentHash = currentHash.Length > 0 ? currentHash : "#/dashboard"; ShowAuth(new LoginScreen()); App.Toast("You were signed out. Please sign in again."); }

        void ShowNewMenu(Control anchor)
        {
            var m = new ContextMenuStrip { Renderer = new ThemedRenderer(), Font = Theme.F(10f), ShowImageMargin = false };
            Action<string, string> add = (t, h) => m.Items.Add(new ToolStripMenuItem(t, null, (s, e) => Go(h)) { Padding = new Padding(8, 5, 8, 5) });
            if (App.Api.Can("sales.write")) { add("Invoice", "#/invoices/new"); add("Customer", "#/customers?new"); }
            if (App.Api.Can("purchases.write")) { add("Bill", "#/bills/new"); add("Vendor", "#/vendors?new"); }
            if (App.Api.Can("accounting.write")) add("Journal entry", "#/journal/new");
            m.Show(anchor, new Point(0, anchor.Height));
        }

        // ---------- routing ----------
        static readonly Regex DocRe = new Regex(@"^#/document/(\d+)$"), RepRe = new Regex(@"^#/reports(?:/([\w-]+))?$"), SetRe = new Regex(@"^#/settings(?:/(\w+))?$"), DocListRe = new Regex(@"^#/(invoices|bills)(?:/(open|paid|void|all))?$"), BankRe = new Regex(@"^#/banking(?:/(new|categorized|excluded|rules))?$");

        public bool CanBack { get { return hpos > 0; } }
        public bool CanForward { get { return hpos >= 0 && hpos < history.Count - 1; } }
        public void Back() { if (CanBack) { hpos--; Open(history[hpos]); } }
        public void Forward() { if (CanForward) { hpos++; Open(history[hpos]); } }
        void UpdateNavButtons() { if (backBtn != null) { backBtn.Enabled = CanBack; fwdBtn.Enabled = CanForward; } }
        void ClearHistory() { history.Clear(); hpos = -1; UpdateNavButtons(); }

        /// <summary>Go to a page and remember it, so Back returns here.</summary>
        public void Go(string hash)
        {
            if (!App.Api.SignedIn) return;
            if (hpos < 0 || history[hpos] != hash)
            {
                if (hpos < history.Count - 1) history.RemoveRange(hpos + 1, history.Count - hpos - 1); // going somewhere new drops the forward pages
                history.Add(hash); if (history.Count > 100) history.RemoveAt(0);
                hpos = history.Count - 1;
            }
            Open(hash);
        }

        void Open(string hash)
        {
            UpdateNavButtons();
            currentHash = hash; string path = hash.Split('?')[0]; bool wantNew = hash.EndsWith("?new");
            Screen s; string key; Match m;
            if (path == "#/dashboard") { s = new DashboardScreen(); key = "dashboard"; }
            else if ((m = DocListRe.Match(path)).Success) { bool inv = m.Groups[1].Value == "invoices"; s = new DocListScreen(inv ? "invoice" : "bill", m.Groups[2].Success ? m.Groups[2].Value : "open"); key = m.Groups[1].Value; }
            else if (path == "#/invoices/new") { s = new DocFormScreen("invoice"); key = "invoices"; }
            else if ((m = DocRe.Match(path)).Success) { s = new DocDetailScreen(int.Parse(m.Groups[1].Value)); key = "invoices"; }
            else if (path == "#/bills/new") { s = new DocFormScreen("bill"); key = "bills"; }
            else if (path == "#/customers") { s = new ContactsScreen("customer", wantNew); key = "customers"; }
            else if (path == "#/vendors") { s = new ContactsScreen("vendor", wantNew); key = "vendors"; }
            else if ((m = BankRe.Match(path)).Success) { s = new BankingScreen(m.Groups[1].Success ? m.Groups[1].Value : "new"); key = "banking"; }
            else if (path == "#/reconcile") { s = new ReconcileScreen(); key = "reconcile"; }
            else if (path == "#/accounts") { s = new AccountsScreen(); key = "accounts"; }
            else if (path == "#/journal") { s = new JournalScreen(); key = "journal"; }
            else if (path == "#/journal/new") { s = new JournalFormScreen(); key = "journal"; }
            else if (path == "#/assets") { s = new AssetsScreen(); key = "assets"; }
            else if (path == "#/health") { s = new HealthScreen(); key = "health"; }
            else if ((m = RepRe.Match(path)).Success) { s = new ReportsScreen(m.Groups[1].Success ? m.Groups[1].Value : "profit-loss"); key = "reports"; }
            else if (path == "#/quickbooks") { s = new QuickBooksScreen(); key = "quickbooks"; }
            else if (path == "#/about") { s = new AboutScreen(); key = "about"; }
            else if (path == "#/import") { s = new ImportScreen(); key = "import"; }
            else if ((m = SetRe.Match(path)).Success) { s = new SettingsScreen(m.Groups[1].Success ? m.Groups[1].Value : "company"); key = "settings"; }
            else { s = new NotFoundScreen(); key = ""; }
            Show(s, key);
        }

        public string LastError; Task lastTask = Task.FromResult(0);
        public Task GoAsync(string hash) { Go(hash); return lastTask; }
        void Show(Screen s, string key) { lastTask = ShowAsync(s, key); }
        async Task ShowAsync(Screen s, string key)
        {
            int token = ++navToken; LastError = null;
            nav.Selected = key; s.NavKey = key;
            foreach (Control c in content.Controls.Cast<Control>().ToList()) if (c is Screen) { content.Controls.Remove(c); c.Dispose(); }
            current = s; content.Controls.Add(s); s.BringToFront(); toast.BringToFront();
            try { await s.Load(); }
            catch (ApiException e) { if (token == navToken && e.Status != 401) ShowLoadError(s, e.Message); }
            catch (Exception e) { if (token == navToken) ShowLoadError(s, Say.Plain(e)); }
            if (token == navToken) Theme.Refresh(s);
        }
        void ShowLoadError(Screen s, string msg)
        {
            LastError = msg;
            s.Controls.Clear(); var st = new Stack { Dock = DockStyle.Fill, Padding = new Padding(Theme.Px(24)) };
            st.Add(new Notice(NK.Error, msg)); st.Add(new Btn("Try Again", BK.Normal, (a, b) => Go(currentHash)));
            s.Controls.Add(st);
        }
        public void Reload() { if (currentHash.Length > 0) Open(currentHash); }
    }

    sealed class NotFoundScreen : Screen { public override Task Load() { var p = new ScrollPage(); p.Body.Add(new Lbl("That page does not exist.", LS.Muted)); SetBody(p); return Task.FromResult(0); } }
}
