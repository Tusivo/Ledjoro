using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Printing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Ledjoro
{
    /// <summary>Card used for every signed-out screen: logo, title, then content. Tag carries the preferred width in px.</summary>
    abstract class AuthCard : Card
    {
        protected AuthCard(string title, int widthPx = 460)
        {
            Tag = widthPx; Padding = new Padding(Theme.Px(26)); Gap = Theme.Px(12); StretchButtons = true;
            var brand = new Pnl { Fill = K.Surface, Height = Theme.Px(34) };
            var logo = new PictureBox { Image = App.Logo(Theme.Px(28)), SizeMode = PictureBoxSizeMode.AutoSize, Location = new Point(0, 2), BackColor = Color.Transparent };
            var name = new Lbl("Ledjoro", LS.H2) { Location = new Point(Theme.Px(38), 3) }; name.Fit();
            brand.Controls.Add(logo); brand.Controls.Add(name);
            Add(brand, Theme.Px(6)); Add(new Lbl(title, LS.H1), Theme.Px(10));
        }
    }

    static class AuthText
    {
        public const string Save = "SAVE THIS NOW. Store your setup token, username, password and recovery key in a password vault, and print the sign-in sheet that follows or save it to a flash drive, and keep it in a safe place, such as a locked cabinet or a safe. Never email it or leave it on a shared computer. There is no \"forgot password\" email: without these details nobody can open your books.";
    }

    sealed class LoginScreen : AuthCard
    {
        public LoginScreen() : base("Sign in")
        {
            var user = new Input("Username"); user.Width = Theme.Px(400);
            var pass = new Input("Password") { Password = true }; pass.Width = Theme.Px(400);
            var err = new Notice(NK.Error);
            var go = new Btn("Sign In", BK.Primary);
            Func<Task> submit = async () =>
            {
                err.Reset(); go.Enabled = false;
                try { await App.Api.Post("api/login", Json.O("username", user.Text, "password", pass.Text)); await App.Main.Boot(); }
                catch (ApiException e) { err.Set(e.Message); pass.Text = ""; pass.Focus(); }
                finally { go.Enabled = true; }
            };
            go.Click += (s, e) => Ui.Run(submit);
            Action enter = () => Ui.Run(submit);
            user.Tb.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; pass.Focus(); } };
            pass.Tb.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; enter(); } };
            Add(new Field("Username", user, 400)); Add(new Field("Password", pass, 400)); Add(err); Add(go, Theme.Px(14));
            if (WebAuthn.Available)
            {
                var pk = new Btn("Sign in with a Passkey", BK.Normal);
                pk.Click += (s, e) => Ui.Run(async () =>
                {
                    err.Reset(); pk.Enabled = go.Enabled = false;
                    try { await PasskeyFlow.SignIn(); }
                    catch (PasskeyException x) { if (!x.Cancelled) err.Set(x.Message); }
                    catch (ApiException x) { err.Set(x.Message); }
                    finally { pk.Enabled = go.Enabled = true; }
                });
                Add(pk, Theme.Px(8));
            }
            var link = Add(new Lbl("Forgot your password? Use your recovery key", LS.Link) { Underline = true });
            link.Click += (s, e) => App.Main.ShowAuth(new RecoverScreen());
            var sw = Add(new Lbl(Companies.List.Count > 1 ? "Open a different company" : "Add another company", LS.Link) { Underline = true }); sw.Click += (s, e) => App.Main.ShowPicker();
            HandleCreated += (s, e) => BeginInvoke((Action)(() => user.Focus()));
        }
    }

    sealed class RecoverScreen : AuthCard
    {
        public RecoverScreen(RecoveryInfo pre = null) : base("Reset your password")
        {
            var user = new Input("Username"); user.Width = Theme.Px(400);
            var key = new Input("XXXXX-XXXXX-XXXXX-XXXXX"); key.AsRecoveryKey(); key.Width = Theme.Px(400); key.Tb.Font = Theme.Mono(10.5f);
            var pw = new Input("At least 12 characters") { Password = true }; pw.Width = Theme.Px(400);
            var err = new Notice(NK.Error);
            Add(new Lbl("Enter the recovery key you saved when the account was created, and choose a new password. A new recovery key will be issued.", LS.Muted, true));
            var note = new Notice(NK.Ok);
            if (RecoveryQr.CameraAvailable)
            {
                var scan = new Btn("Scan the QR code with my Camera", BK.Normal);
                scan.Click += (s, e) => Ui.Run(async () =>
                {
                    err.Reset(); note.Reset();
                    var info = RecoveryQr.Parse(ScanDialog.Scan()); if (info == null) return;
                    var cur = Companies.Current;
                    if (info.Company.Length > 0 && cur != null && !string.Equals(cur.Name, info.Company, StringComparison.OrdinalIgnoreCase))
                    {
                        var match = Companies.List.FirstOrDefault(c => string.Equals(c.Name, info.Company, StringComparison.OrdinalIgnoreCase));
                        if (match != null)
                        {
                            if (Msg.Confirm("This code belongs to another company", "The code is for \u201c" + match.Name + "\u201d, but \u201c" + cur.Name + "\u201d is open. Switch to \u201c" + match.Name + "\u201d to reset its password?", "Switch"))
                            { await App.Main.OpenCompany(match); App.Main.ShowAuth(new RecoverScreen(info)); }
                            return;
                        }
                        err.Set("This code is for the company \u201c" + info.Company + "\u201d, which is not in your list. You are in \u201c" + cur.Name + "\u201d. Open the right company first (use the company menu or Add existing), then scan again."); return;
                    }
                    user.Text = info.User; key.Text = info.Key; note.Set("Code read for " + info.User + (info.Company.Length > 0 ? " in " + info.Company : "") + ". Now choose a new password below."); pw.Focus();
                });
                Add(scan);
            }
            Add(note);
            if (pre != null) { user.Text = pre.User; key.Text = pre.Key; note.Set("Code read for " + pre.User + (pre.Company.Length > 0 ? " in " + pre.Company : "") + ". Now choose a new password below."); }
            Add(new Field("Username", user, 400)); Add(new Field("Recovery key", key, 400)); Add(new Field("New password (12+ characters)", pw, 400)); Add(err);
            var go = Add(new Btn("Reset Password", BK.Primary), Theme.Px(14));
            go.Click += (s, e) => Ui.Run(async () =>
            {
                err.Reset(); go.Enabled = false;
                try
                {
                    var r = await App.Api.Post("api/recover", Json.O("username", user.Text, "recovery_key", key.Text, "new_password", pw.Text));
                    await App.Api.RefreshSession();
                    App.Main.ShowAuth(new SheetScreen("Password reset - save your new details", App.Api.Company, user.Text.Trim(), pw.Text, r["recovery_key"].Str, null, "Continue to Ledjoro", () => Ui.Run(App.Main.Boot)));
                }
                catch (ApiException ex) { err.Set(ex.Message); }
                finally { go.Enabled = true; }
            });
            var back = Add(new Lbl("Back to sign in", LS.Link) { Underline = true });
            back.Click += (s, e) => App.Main.ShowAuth(new LoginScreen());
        }
    }

    sealed class SetupScreen : AuthCard
    {
        public SetupScreen(string token) : base("First-time setup")
        {
            Add(new Notice(NK.Red, AuthText.Save));
            Add(new Lbl("Welcome. Create the administrator account for this computer. Everything stays on this PC.", LS.Muted, true));
            var company = new Input("Your company") { Text = Companies.SuggestedName() }; var name = new Input("Your name"); var user = new Input("Username"); var pw = new Input("At least 12 characters") { Password = true };
            foreach (var i in new[] { company, name, user, pw }) i.Width = Theme.Px(400);
            var err = new Notice(NK.Error);
            Add(new Field("Company name", company, 400)); Add(new Field("Your name", name, 400)); Add(new Field("Username", user, 400)); Add(new Field("Password (12+ characters)", pw, 400)); Add(err);
            var go = Add(new Btn("Create Administrator", BK.Primary), Theme.Px(6));
            go.Click += (s, e) => Ui.Run(async () =>
            {
                err.Reset(); go.Enabled = false;
                try
                {
                    var r = await App.Api.Post("api/setup", Json.O("token", token, "company_name", company.Text, "display_name", name.Text, "username", user.Text, "password", pw.Text));
                    await App.Api.RefreshSession();
                    string password = pw.Text; pw.Text = "";
                    App.Main.ShowAuth(new SheetScreen("Account created - save your sign-in details", company.Text.Trim(), user.Text.Trim(), password, r["recovery_key"].Str, token, "Continue to Ledjoro", () => Ui.Run(App.Main.Boot)));
                }
                catch (ApiException ex) { err.Set(ex.Message); }
                finally { go.Enabled = true; }
            });
        }
    }

    /// <summary>Rows of "label / value" where secrets stay masked on screen until revealed (and are always shown when printed).</summary>
    sealed class SheetRows : Control
    {
        public class Row { public string Label, Value; public bool Secret, Mono, Big; }
        public readonly List<Row> Rows = new List<Row>(); public bool Reveal;
        public SheetRows() { SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true); TabStop = false; }
        int RowH(Row r) { return Theme.Px(r.Big ? 44 : 38); }
        public override Size GetPreferredSize(Size proposed) { return new Size(proposed.Width, Rows.Sum(RowH) + 2); }
        protected override void OnPaint(PaintEventArgs e)
        {
            Theme.Smooth(e.Graphics); e.Graphics.Clear(Theme.Surface);
            var outer = new Rectangle(0, 0, Width - 1, Height - 1);
            using (var p = Theme.Round(outer, Theme.Px(8))) using (var pen = new Pen(Theme.Line)) e.Graphics.DrawPath(pen, p);
            int y = 0, lw = Theme.Px(150);
            foreach (var r in Rows)
            {
                int h = RowH(r);
                using (var b = new SolidBrush(Theme.Soft)) e.Graphics.FillRectangle(b, 1, y + 1, lw, h - (y + h >= Height - 1 ? 2 : 0));
                using (var pen = new Pen(Theme.Line)) { if (y > 0) e.Graphics.DrawLine(pen, 0, y, Width, y); e.Graphics.DrawLine(pen, lw, y, lw, y + h); }
                TextRenderer.DrawText(e.Graphics, r.Label, Theme.F(9f, FontStyle.Bold), new Rectangle(Theme.Px(12), y, lw - Theme.Px(12), h), Theme.Muted, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                string text = r.Secret && !Reveal ? new string('•', 14) : r.Value;
                var f = r.Mono ? Theme.Mono(r.Big ? 12.5f : 10f, r.Big ? FontStyle.Bold : FontStyle.Regular) : Theme.F(10f);
                TextRenderer.DrawText(e.Graphics, text, f, new Rectangle(lw + Theme.Px(12), y, Width - lw - Theme.Px(20), h), Theme.Ink, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                y += h;
            }
        }
    }

    /// <summary>The sign-in sheet: a bright red reminder, every detail needed to get back in, a Print button, and a gate before continuing.</summary>
    sealed class SheetScreen : AuthCard
    {
        readonly SheetRows rows = new SheetRows(); QrBox qr; bool done; Btn goBtn; Check ackBox; Notice mustSave;
        const string Guard = "Your sign-in sheet holds your password and recovery key, and this is the only time they are shown. Print it or save it to a flash drive first (the buttons are on this screen). Then you can continue, and close Ledjoro if you wish.";
        /// <summary>True once the sheet has been printed or saved; only then can the person continue or close the window.</summary>
        public bool Done { get { return done; } }
        public void MarkDone() { done = true; if (App.Main != null && App.Main.CloseGuard == Guard) App.Main.CloseGuard = null; if (mustSave != null) mustSave.Visible = false; if (goBtn != null) goBtn.Enabled = ackBox != null && ackBox.Checked; }
        protected override void Dispose(bool disposing) { if (disposing && App.Main != null && App.Main.CloseGuard == Guard) App.Main.CloseGuard = null; base.Dispose(disposing); }
        readonly string title, company, username, password, recovery, token;

        public SheetScreen(string title, string company, string username, string password, string recoveryKey, string setupToken, string continueLabel, Action onContinue) : base(title, 640)
        {
            this.title = title; this.company = company; this.username = username; this.password = password; recovery = recoveryKey; token = setupToken;
            Add(new Notice(NK.Red, AuthText.Save));
            rows.Rows.Add(new SheetRows.Row { Label = "Program", Value = "Ledjoro on this computer (no web address needed)" });
            if (!string.IsNullOrEmpty(company)) rows.Rows.Add(new SheetRows.Row { Label = "Company", Value = company });
            rows.Rows.Add(new SheetRows.Row { Label = "Username", Value = username, Mono = true });
            if (!string.IsNullOrEmpty(password)) rows.Rows.Add(new SheetRows.Row { Label = "Password", Value = password, Secret = true, Mono = true });
            rows.Rows.Add(new SheetRows.Row { Label = "Recovery key", Value = recoveryKey, Secret = true, Mono = true, Big = true });
            if (!string.IsNullOrEmpty(setupToken)) rows.Rows.Add(new SheetRows.Row { Label = "Setup token", Value = setupToken + "  (used)", Mono = true });
            rows.Rows.Add(new SheetRows.Row { Label = "Created", Value = DateTime.Now.ToString("g") });
            Add(rows);
            if (!string.IsNullOrEmpty(recoveryKey)) { qr = new QrBox(RecoveryQr.Build(company, username, recoveryKey)); Add(qr); }
            Add(new Lbl("The recovery key can reset your password once (a new key is issued after each use), and it also opens your backups, so a forgotten backup passphrase never locks you out. Anyone holding this sheet can open your books, so treat it like a bank card PIN. Shred old copies whenever you change the password or key.", LS.Muted, true));
            var row = new Flow(); var print = new Btn("Print This Page", BK.Primary); var save = new Btn("Save to a Flash Drive", BK.Primary); var show = new Btn("Show Secrets", BK.Normal, null, true);
            print.Click += (s, e) => { if (PrintSheet(this.title, this.company, this.username, this.password, recovery, token)) MarkDone(); };
            save.Click += (s, e) => { if (SaveSheet(this.title, this.company, this.username, this.password, recovery, token)) MarkDone(); };
            show.Click += (s, e) => { rows.Reveal = !rows.Reveal; if (qr != null) { qr.Revealed = rows.Reveal; qr.Invalidate(); } show.Text = rows.Reveal ? "Hide Secrets" : "Show Secrets"; rows.Invalidate(); };
            row.Add(print); row.Add(save); row.Add(show); Add(row);
            mustSave = new Notice(NK.Warn, "Before you can continue: print this page, or save it to a flash drive. This is the only time your password and recovery key are shown."); Add(mustSave);
            var ack = Add(new Check("I have stored it somewhere safe.")); ackBox = ack;
            var go = new Btn(continueLabel, BK.Primary) { Enabled = false }; goBtn = go;
            ack.Changed += (s, e) => go.Enabled = ack.Checked && done;
            go.Click += (s, e) => { if (done) onContinue(); };
            Add(go, 0);
            if (App.Main != null) App.Main.CloseGuard = Guard;
        }

        /// <summary>Draws the sheet on a page (used for printing, and by the tests).</summary>
        public static void DrawSheet(Graphics g, Rectangle area, string title, string company, string username, string password, string recovery, string token)
        {
            int y = area.Top;
                g.DrawString("Ledjoro — " + title, new Font("Segoe UI", 16, FontStyle.Bold), Brushes.Black, area.Left, y); y += 40;
                var warn = new Rectangle(area.Left, y, area.Width, 110);
                g.FillRectangle(new SolidBrush(Theme.AlertRed), warn);
                g.DrawString(AuthText.Save, new Font("Segoe UI", 10, FontStyle.Bold), Brushes.White, new RectangleF(warn.Left + 10, warn.Top + 8, warn.Width - 20, warn.Height - 12));
                y += 128;
                var items = new List<string[]> { new[] { "Program", "Ledjoro on this computer" }, new[] { "Company", company }, new[] { "Username", username } };
                if (!string.IsNullOrEmpty(password)) items.Add(new[] { "Password", password });
                items.Add(new[] { "Recovery key", recovery });
                if (!string.IsNullOrEmpty(token)) items.Add(new[] { "Setup token (used)", token });
                items.Add(new[] { "Created", DateTime.Now.ToString("g") });
                foreach (var it in items)
                {
                    g.DrawString(it[0], new Font("Segoe UI", 10, FontStyle.Bold), Brushes.DimGray, area.Left, y);
                    g.DrawString(it[1] ?? "", new Font("Consolas", it[0] == "Recovery key" ? 13 : 11, it[0] == "Recovery key" ? FontStyle.Bold : FontStyle.Regular), Brushes.Black, area.Left + 190, y - 1);
                    y += it[0] == "Recovery key" ? 40 : 30;
                    g.DrawLine(Pens.LightGray, area.Left, y - 6, area.Right, y - 6);
                }
                y += 16;
                g.DrawString("Treat this sheet like a bank card PIN. Store it in a locked cabinet or a safe, never email or photograph it, and shred old copies when you change your password or recovery key.", new Font("Segoe UI", 10), Brushes.Black, new RectangleF(area.Left, y, area.Width, 80));
                if (!string.IsNullOrEmpty(recovery))
                {   // the recovery QR code, big enough for a webcam to read from a printed page
                    y += 70;
                    RecoveryQr.Draw(g, QrEncoder.Encode(RecoveryQr.Build(company, username, recovery)), area.Left, y, 230);
                    g.DrawString("Recovery QR code\n\nIf you forget your password: on the Ledjoro sign-in screen choose \u201cForgot your password?\u201d, then \u201cScan the QR code with my camera\u201d, and hold this code up to your webcam. It fills in your username and recovery key; you then choose a new password.\n\nThe code contains your recovery key. Keep this page private.",
                        new Font("Segoe UI", 10), Brushes.Black, new RectangleF(area.Left + 250, y, area.Width - 250, 230));
                }
        }

        /// <returns>True when the page was sent to a printer.</returns>
        public static bool PrintSheet(string title, string company, string username, string password, string recovery, string token)
        {
            var doc = new PrintDocument { DocumentName = "Ledjoro sign-in details" };
            doc.PrintPage += (s, e) => { DrawSheet(e.Graphics, e.MarginBounds, title, company, username, password, recovery, token); e.HasMorePages = false; };
            try
            {
                using (var dlg = new PrintDialog { Document = doc, UseEXDialog = true }) { if (dlg.ShowDialog(App.Main) == DialogResult.OK) { doc.Print(); return true; } }
            }
            catch (Exception ex) { Msg.Info("The page could not be printed", Say.Plain(ex) + " You can save it to a flash drive instead.", true); }
            return false;
        }

        /// <summary>Saves the sheet as a picture (the same page that would be printed, full letter-size sharpness) wherever the person chooses, such as a flash drive.</summary>
        /// <returns>True when the file was written.</returns>
        public static bool SaveSheet(string title, string company, string username, string password, string recovery, string token, string pathForTest = null)
        {
            try
            {
                string target = pathForTest;
                if (target == null)
                {
                    using (var d = new SaveFileDialog { Title = "Save the sign-in sheet (choose your flash drive)", FileName = "Ledjoro sign-in sheet.png", Filter = "Picture of the sheet (*.png)|*.png" })
                    { if (d.ShowDialog(App.Main) != DialogResult.OK) return false; target = d.FileName; }
                }
                using (var page = new Bitmap(2550, 3300))
                {
                    using (var g = Graphics.FromImage(page)) { g.Clear(Color.White); g.ScaleTransform(3, 3); DrawSheet(g, new Rectangle(100, 100, 650, 900), title, company, username, password, recovery, token); }
                    page.Save(target, System.Drawing.Imaging.ImageFormat.Png);
                }
                if (pathForTest == null) App.Toast("Saved. Keep that flash drive in a safe place: anyone who has it can open your books.");
                return true;
            }
            catch (Exception ex) { Msg.Info("The sheet could not be saved", Say.Plain(ex) + " Choose a different place, or print it instead.", true); return false; }
        }
    }

    sealed class ChangePasswordForm : Stack
    {
        public ChangePasswordForm(bool forced, Action done)
        {
            Fill = K.Surface; Gap = Theme.Px(12);
            var cur = new Input("") { Password = true }; var nw = new Input("") { Password = true }; var err = new Notice(NK.Error);
            if (forced) Add(new Notice(NK.Warn, "You must choose a new password before continuing."));
            Add(new Field("Current password", cur)); Add(new Field("New password (12+ characters)", nw)); Add(err);
            var go = Add(new Btn("Change Password", BK.Primary), 0);
            go.Click += (s, e) => Ui.Run(async () =>
            {
                err.Reset(); go.Enabled = false;
                try { await App.Api.Post("api/password", Json.O("current", cur.Text, "next", nw.Text)); App.Toast("Password changed"); done(); }
                catch (ApiException ex) { err.Set(ex.Message); }
                finally { go.Enabled = true; }
            });
        }
    }

    sealed class ChangePasswordScreen : AuthCard
    {
        public ChangePasswordScreen(bool forced) : base("Change Password") { Add(new ChangePasswordForm(forced, () => Ui.Run(App.Main.Boot))); }
    }
}
