using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Ledjoro
{
    public sealed class RecoveryInfo { public string Company = "", User = "", Key = ""; }

    /// <summary>
    /// The QR code on the recovery sheet. It carries the same three things the sheet already shows in words (company, username, recovery key),
    /// so showing it to the camera on the reset screen fills them in. It is exactly as secret as the recovery key itself.
    /// The new password is never in it: that is always chosen fresh.
    /// </summary>
    public static class RecoveryQr
    {
        public const string Scheme = "ledjoro:recover?";
        const string OldScheme = "openbooks:recover?";   // sheets printed before the program was renamed still scan

        public static string Build(string company, string user, string key)
        {
            company = (company ?? "").Trim(); if (company.Length > 60) company = company.Substring(0, 60);
            return Scheme + "v=1&c=" + Uri.EscapeDataString(company) + "&u=" + Uri.EscapeDataString((user ?? "").Trim()) + "&k=" + Uri.EscapeDataString((key ?? "").Trim());
        }

        /// <summary>Null unless the text is a Ledjoro recovery code (anything else a camera sees, such as a shop's QR code, is ignored).</summary>
        public static RecoveryInfo Parse(string text)
        {
            if (!string.IsNullOrEmpty(text) && text.StartsWith(OldScheme, StringComparison.Ordinal)) text = Scheme + text.Substring(OldScheme.Length);
            if (string.IsNullOrEmpty(text) || text.Length > 400 || !text.StartsWith(Scheme, StringComparison.Ordinal)) return null;
            var f = new Dictionary<string, string>();
            try { foreach (var part in text.Substring(Scheme.Length).Split('&')) { int i = part.IndexOf('='); if (i > 0) f[part.Substring(0, i)] = Uri.UnescapeDataString(part.Substring(i + 1)); } }
            catch (UriFormatException) { return null; }
            string v, c, u, k;
            if (!f.TryGetValue("v", out v) || v != "1" || !f.TryGetValue("u", out u) || !f.TryGetValue("k", out k)) return null;
            f.TryGetValue("c", out c);
            if (u.Length == 0 || u.Length > 100 || !System.Text.RegularExpressions.Regex.IsMatch(k, "^[0-9A-Za-z-]{16,32}$")) return null;
            return new RecoveryInfo { Company = c ?? "", User = u, Key = k };
        }

        public static bool CameraAvailable
        {
#if HAS_WINRT_CAMERA
            get { return true; }
#else
            get { return false; }
#endif
        }

        /// <summary>Draws the code as crisp black squares on white (always black on white, whatever the theme: that is what cameras read best).</summary>
        public static void Draw(Graphics g, bool[,] m, float x, float y, float size)
        {
            int n = m.GetLength(0); float quiet = 4, unit = size / (n + quiet * 2);
            var old = g.SmoothingMode; g.SmoothingMode = SmoothingMode.None; g.PixelOffsetMode = PixelOffsetMode.Half;
            g.FillRectangle(Brushes.White, x, y, size, size);
            for (int r = 0; r < n; r++) for (int c = 0; c < n; c++) if (m[r, c]) g.FillRectangle(Brushes.Black, x + (c + quiet) * unit, y + (r + quiet) * unit, unit + 0.4f, unit + 0.4f);
            g.SmoothingMode = old;
        }

        /// <summary>The picture's brightness values (0..255), row by row, ready for the reader.</summary>
        public static byte[] Luminance(Bitmap bmp)
        {
            int w = bmp.Width, h = bmp.Height; var lum = new byte[w * h];
            var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                var row = new byte[Math.Abs(data.Stride)];
                for (int y = 0; y < h; y++)
                {
                    System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, w * 4);
                    for (int x = 0; x < w; x++) lum[y * w + x] = (byte)((row[x * 4 + 2] * 299 + row[x * 4 + 1] * 587 + row[x * 4] * 114) / 1000);
                }
            }
            finally { bmp.UnlockBits(data); }
            return lum;
        }
    }

    /// <summary>The recovery QR code on screen, hidden like the other secrets until the person chooses to show it.</summary>
    sealed class QrBox : Control
    {
        readonly bool[,] modules; public bool Revealed; public readonly string Payload;
        public QrBox(string payload)
        {
            Payload = payload; modules = QrEncoder.Encode(payload); TabStop = false;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        }
        public override Size GetPreferredSize(Size proposed) { return new Size(proposed.Width, Theme.Px(250)); }
        protected override void OnPaint(PaintEventArgs e)
        {
            Theme.Smooth(e.Graphics); e.Graphics.Clear(Theme.Surface);
            int box = Theme.Px(230); var qr = new Rectangle(0, Theme.Px(6), box, box);
            if (Revealed) RecoveryQr.Draw(e.Graphics, modules, qr.X, qr.Y, box);
            else
            {
                using (var p = Theme.Round(qr, Theme.Px(10))) using (var b = new SolidBrush(Theme.Soft)) using (var pen = new Pen(Theme.Line)) { e.Graphics.FillPath(b, p); e.Graphics.DrawPath(pen, p); }
                TextRenderer.DrawText(e.Graphics, "QR code hidden", Theme.F(10f, FontStyle.Bold), new Rectangle(qr.X, qr.Y + box / 2 - Theme.Px(24), box, Theme.Px(24)), Theme.Ink, TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding);
                TextRenderer.DrawText(e.Graphics, "Press “Show Secrets”\nto display it", Theme.F(9.5f), new Rectangle(qr.X + 10, qr.Y + box / 2, box - 20, Theme.Px(50)), Theme.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
            }
            string cap = "Recovery QR code. If you ever forget your password: on the sign-in screen choose “Forgot your password?”, then “Scan the QR code with my camera”, and hold this code up to your webcam. It fills in your username and recovery key for you; you then choose a new password.\n\nThe code contains your recovery key, so keep this sheet as private as the key.";
            TextRenderer.DrawText(e.Graphics, cap, Theme.F(9.5f), new Rectangle(box + Theme.Px(20), Theme.Px(6), Width - box - Theme.Px(24), Height - Theme.Px(10)), Theme.Muted, TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
        }
    }

    /// <summary>"Show the QR code to your camera": a live picture, and the first Ledjoro recovery code it can read ends the scan.</summary>
    static class ScanDialog
    {
        /// <summary>The text of a Ledjoro recovery QR code, or null if the person cancelled or the camera could not be used.</summary>
        public static string Scan()
        {
#if HAS_WINRT_CAMERA
            var dlg = new Dlg("Scan your recovery QR code", 700);
            var pic = new PictureBox { SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Black };
            var status = new Lbl("Starting the camera…", LS.Normal, true);
            var hint = new Lbl("Hold the QR code from your recovery sheet about 20 to 30 cm (8 to 12 inches) from the camera, flat and steady, with good light on it. The picture stays on this computer and is not saved.", LS.Muted, true);
            var settings = new Btn("Open camera Settings", BK.Normal, null, true) { Visible = false };
            settings.Click += (s, e) => { try { System.Diagnostics.Process.Start("ms-settings:privacy-webcam"); } catch (Exception) { } };
            dlg.Body.Add(pic, null, Theme.Px(380)); dlg.Body.Add(status); dlg.Body.Add(hint); dlg.Body.Add(settings);
            dlg.Cancel("Cancel");
            string result = null; Webcam cam = null; bool busy = false, saidNotOurs = false; var timer = new System.Windows.Forms.Timer { Interval = 110 };
            timer.Tick += (s, e) =>
            {
                if (busy || cam == null) return;
                Bitmap frame; try { frame = cam.TryGetFrame(); } catch (Exception) { return; }
                if (frame == null) return;
                var old = pic.Image; pic.Image = frame; if (old != null) old.Dispose();
                busy = true; var shot = (Bitmap)frame.Clone(); int w = shot.Width, h = shot.Height;
                Task.Run(() => { using (shot) { try { return QrReader.Decode(RecoveryQr.Luminance(shot), w, h); } catch (Exception) { return null; } } }).ContinueWith(t =>
                {
                    if (dlg.IsDisposed) return;
                    dlg.BeginInvoke(new Action(() =>
                    {
                        busy = false; string text = t.Result; if (text == null) return;
                        if (RecoveryQr.Parse(text) != null) { result = text; timer.Stop(); dlg.DialogResult = DialogResult.OK; dlg.Close(); }
                        else if (!saidNotOurs) { saidNotOurs = true; status.Text = "A QR code was found, but it is not a Ledjoro recovery code. Hold up the code printed on your recovery sheet."; status.Tint = Theme.Warn; }
                    }));
                });
            };
            dlg.Shown += async (s, e) =>
            {
                try { cam = new Webcam(); await cam.StartAsync(); status.Text = "Camera is on. Hold the QR code up to it."; timer.Start(); }
                catch (Exception ex) { status.Text = Webcam.Explain(ex); status.Tint = Theme.Bad; settings.Visible = ex is UnauthorizedAccessException; Vx.Relayout(dlg.Body); }
            };
            dlg.FormClosed += (s, e) => { timer.Stop(); if (cam != null) cam.Dispose(); if (pic.Image != null) pic.Image.Dispose(); };
            dlg.Run();
            return result;
#else
            return null;
#endif
        }
    }
}
