using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Ledjoro
{
    public enum NK { Error, Ok, Warn, Red }

    /// <summary>Coloured message box inside a page or dialog (errors, confirmations, warnings, the bright red save-this-now notice).</summary>
    public sealed class Notice : Control
    {
        public NK Kind; string body = "";
        public Notice(NK kind = NK.Error, string text = "")
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor | ControlStyles.ResizeRedraw, true);
            BackColor = Color.Transparent; Kind = kind; TabStop = false; Set(text);
        }
        public void Set(string text) { body = text ?? ""; Visible = body.Length > 0; Invalidate(); Vx.Relayout(this); }
        public void Reset() { Set(""); }
        public string Message { get { return body; } }
        Color Fg() { return Kind == NK.Red ? Color.White : Kind == NK.Ok ? Theme.Good : Kind == NK.Warn ? Theme.Warn : Theme.Bad; }
        Color Bgc() { return Kind == NK.Red ? Theme.AlertRed : Kind == NK.Ok ? Theme.GoodSoft : Kind == NK.Warn ? Theme.WarnSoft : Theme.BadSoft; }
        public override Size GetPreferredSize(Size proposed)
        {
            int w = proposed.Width > 0 && proposed.Width < 100000 ? proposed.Width : Theme.Px(500);
            var m = TextRenderer.MeasureText(body, Kind == NK.Red ? Theme.F(10f, FontStyle.Bold) : Theme.F(10f), new Size(w - Theme.Px(28), 100000), TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
            return new Size(w, m.Height + Theme.Px(26));
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            Theme.Smooth(e.Graphics);
            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            using (var p = Theme.Round(r, Theme.Px(8))) using (var b = new SolidBrush(Bgc())) { e.Graphics.FillPath(b, p); if (Kind == NK.Red) using (var pen = new Pen(Theme.AlertBorder, 2)) e.Graphics.DrawPath(pen, p); }
            TextRenderer.DrawText(e.Graphics, body, Kind == NK.Red ? Theme.F(10f, FontStyle.Bold) : Theme.F(10f), new Rectangle(Theme.Px(14), Theme.Px(10), Width - Theme.Px(28), Height - Theme.Px(12)), Fg(), TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
        }
    }

    /// <summary>Page title with an optional subtitle on the left and action buttons on the right.</summary>
    public sealed class Header : Pnl
    {
        readonly Lbl title, sub; readonly Flow actions = new Flow { HGap = Theme.Px(8) };
        public Header(string t, string s = null, params Control[] buttons)
        {
            title = new Lbl(t, LS.H1); Controls.Add(title);
            if (!string.IsNullOrEmpty(s)) { sub = new Lbl(s, LS.Muted, true); Controls.Add(sub); }
            Controls.Add(actions); foreach (var b in buttons.Where(x => x != null)) actions.Add(b);
        }
        public void AddAction(Control c) { actions.Add(c); PerformLayout(); }
        int Aw() { return actions.Controls.Count == 0 ? 0 : actions.GetPreferredSize(new Size(100000, 0)).Width; }
        public override Size GetPreferredSize(Size proposed)
        {
            int w = proposed.Width > 0 && proposed.Width < 100000 ? proposed.Width : Theme.Px(600);
            int lw = Math.Max(Theme.Px(200), w - Aw() - Theme.Px(16));
            int h = title.GetPreferredSize(Size.Empty).Height + (sub != null ? sub.GetPreferredSize(new Size(lw, 0)).Height + Theme.Px(2) : 0);
            return new Size(w, Math.Max(h, actions.Controls.Count > 0 ? Theme.Px(36) : 0));
        }
        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            int aw = Aw(), lw = Math.Max(Theme.Px(200), Width - aw - Theme.Px(16));
            int th = title.GetPreferredSize(Size.Empty).Height;
            title.SetBounds(0, 0, lw, th);
            if (sub != null) sub.SetBounds(0, th + Theme.Px(2), lw, sub.GetPreferredSize(new Size(lw, 0)).Height);
            actions.SetBounds(Width - aw, 0, aw, actions.GetPreferredSize(new Size(aw, 0)).Height);
        }
    }

    public static class Ui
    {
        public static int Px(int n) { return Theme.Px(n); }
        public static Lbl L(string t, LS s = LS.Normal, bool wrap = false) { return new Lbl(t, s, wrap); }
        public static Btn B(string t, BK k = BK.Normal, Action click = null, bool small = false) { var b = new Btn(t, k, null, small); if (click != null) b.Click += (s, e) => click(); return b; }
        public static Btn BA(string t, BK k, Func<Task> click, bool small = false) { var b = new Btn(t, k, null, small); b.Click += (s, e) => Run(click); return b; }
        public static Field F(string caption, Control c, int width = 250) { return new Field(caption, c, width); }
        public static Combo Cb(IEnumerable<KeyValuePair<string, string>> items, string selected = null, int width = 250) { var c = new Combo(width); c.SetItems(items, selected); return c; }
        public static KeyValuePair<string, string> It(string v, string l) { return new KeyValuePair<string, string>(v, l); }

        /// <summary>Runs async UI work, turning any failure into a message instead of crashing the app.</summary>
        public static async void Run(Func<Task> work)
        {
            try { await work(); }
            catch (ApiException e) { if (e.Status != 401) App.Toast(e.Message, true); }
            catch (Exception e) { App.Toast(Say.Plain(e), true); }
        }

        public static IEnumerable<KeyValuePair<string, string>> AccountItems(Jv accounts, Func<Jv, bool> filter = null, string blank = null)
        {
            if (blank != null) yield return It("", blank);
            foreach (var a in accounts.Items)
                if (a["active"].Bool && (filter == null || filter(a))) yield return It(a["id"].Str, (a["number"].Str.Length > 0 ? a["number"].Str + " · " : "") + a["name"].Str);
        }
        public static IEnumerable<KeyValuePair<string, string>> ContactItems(Jv contacts, string kind, string blank = null)
        {
            if (blank != null) yield return It("", blank);
            foreach (var c in contacts.Items) if (c["active"].Bool && c["kind"].Str == kind) yield return It(c["id"].Str, c["name"].Str);
        }
    }

    /// <summary>Modal dialog with themed title bar, body, error line and right-aligned buttons.</summary>
    public sealed class Dlg : Form
    {
        public readonly Stack Body = new Stack();
        readonly Stack root = new Stack(); readonly Notice err = new Notice(NK.Error); readonly Flow buttons = new Flow { AlignRight = true };
        readonly List<Btn> all = new List<Btn>(); Btn primary; readonly int widthPx; public int FixedBodyHeight;

        public Dlg(string title, int widthPx = 560)
        {
            this.widthPx = widthPx;
            Text = title; FormBorderStyle = FormBorderStyle.FixedDialog; StartPosition = FormStartPosition.CenterParent; MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
            KeyPreview = true; Font = Theme.F(10f); AutoScaleMode = AutoScaleMode.None; Icon = App.AppIcon; BackColor = Theme.Surface;
            root.Fill = Pnl.K.Surface; root.Padding = new Padding(Theme.Px(22)); root.Dock = DockStyle.Fill; Body.Fill = Pnl.K.Surface; Body.Gap = Theme.Px(12);
            root.Add(new Lbl(title, LS.H2), Theme.Px(14)); root.Add(Body, Theme.Px(12)); root.Add(err, Theme.Px(12)); root.Add(buttons, 0);
            Controls.Add(root);
        }

        public Btn Primary(string text, Func<Task<bool>> action, BK kind = BK.Primary) { primary = Add(text, kind, action); return primary; }
        public Btn Cancel(string text = "Cancel") { return Add(text, BK.Normal, async () => { await Task.FromResult(0); return true; }, true); }
        Btn Add(string text, BK kind, Func<Task<bool>> action, bool cancel = false)
        {
            var b = new Btn(text, kind); all.Add(b);
            b.Click += async (s, e) =>
            {
                if (cancel) { DialogResult = DialogResult.Cancel; Close(); return; }
                err.Reset(); foreach (var x in all) x.Enabled = false;
                try { if (await action()) { DialogResult = DialogResult.OK; Close(); } }
                catch (ApiException ex) { err.Set(ex.Message); }
                catch (Exception ex) { err.Set(Say.Plain(ex)); }
                finally { if (!IsDisposed) foreach (var x in all) x.Enabled = true; }
            };
            buttons.Add(b); return b;
        }
        public void SetError(string m) { err.Set(m); }

        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Native.TitleBar(Handle, Theme.Dark); }
        protected override bool ProcessCmdKey(ref Message m, Keys k)
        {
            if (k == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); return true; }
            if (k == Keys.Enter && primary != null && primary.Enabled && !(ActiveControl is TextBox && ((TextBox)ActiveControl).Multiline)) { primary.PerformClick(); return true; }
            return base.ProcessCmdKey(ref m, k);
        }

        public DialogResult Run()
        {
            int cw = Theme.Px(widthPx); ClientSize = new Size(cw, 100);
            int h = root.GetPreferredSize(new Size(cw, 0)).Height;
            if (FixedBodyHeight > 0) h += FixedBodyHeight;
            var wa = System.Windows.Forms.Screen.FromControl(App.Main ?? (Control)this).WorkingArea;
            ClientSize = new Size(cw, Math.Min(h, wa.Height - Theme.Px(60)));
            Theme.Refresh(this);
            return ShowDialog(App.Main);
        }
    }

    public static class Msg
    {
        public static void Info(string title, string text, bool bad = false)
        {
            var d = new Dlg(title, 520); d.Body.Add(new Lbl(text, bad ? LS.Bad : LS.Normal, true)); d.Primary("OK", () => Task.FromResult(true)); d.Run();
        }
        public static bool Confirm(string title, string text, string yes = "OK", bool danger = false)
        {
            var d = new Dlg(title, 520); d.Body.Add(new Lbl(text, LS.Normal, true));
            d.Cancel(); d.Primary(yes, () => Task.FromResult(true), danger ? BK.Danger : BK.Primary);
            return d.Run() == DialogResult.OK;
        }
    }
}
