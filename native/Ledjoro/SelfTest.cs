using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Ledjoro
{
    /// <summary>
    /// Developer self-test (Ledjoro.exe --selftest outdir): signs in to a throwaway engine, opens every screen, records any failure,
    /// and saves a screenshot of each so the look can be checked without clicking through by hand.
    /// </summary>
    static class SelfTest
    {
        public static async Task Run(MainForm form, string dir)
        {
            Directory.CreateDirectory(dir);
            var log = new StringBuilder(); int failures = 0;
            Action<string> note = s => { log.AppendLine(s); };
            Func<Bitmap, bool> IsBlack = b => { for (int x = 5; x < b.Width; x += 97) for (int y = 5; y < b.Height; y += 89) if (b.GetPixel(x, y).GetBrightness() > 0.02f) return false; return true; };
            Func<string, Task> shot = async name =>
            {
                await Task.Delay(450); Application.DoEvents();
                try
                {
                    form.Activate(); form.BringToFront(); await Task.Delay(120); Application.DoEvents();
                    var r = form.Bounds; using (var bmp = new Bitmap(r.Width, r.Height)) using (var g = Graphics.FromImage(bmp)) { g.CopyFromScreen(r.Location, Point.Empty, r.Size); if (IsBlack(bmp)) form.DrawToBitmap(bmp, new Rectangle(0, 0, r.Width, r.Height)); bmp.Save(Path.Combine(dir, name + ".png")); }   // a locked or switched-off display captures as black: draw the window itself instead
                }
                catch (Exception e) { note("  screenshot failed: " + e.Message); }
            };
            try
            {
                {   // theme: light by default, and text added to shell.json by hand must not reset the choice
                    if (Theme.Name == "light") note("ok   the default theme is Light"); else { failures++; note("FAIL the default theme is Light (it was " + Theme.Name + ")"); }
                    string pref = Path.Combine(Engine.DataDir(), "shell.json"); string keep = File.Exists(pref) ? File.ReadAllText(pref) : null;
                    var cases = new[] {
                        new[] { "{\"theme\":\"night\"}\r\nsome note I typed", "night" },
                        new[] { "{\"theme\":\"solarized-dark\"}", "night" },
                        new[] { "random words only", "light" },
                        new[] { "// comment\n{ \"theme\" : \"dark\", }\ntrailing", "dark" },
                        new[] { "{\"theme\":\"light\"} and more", "light" } };
                    foreach (var t in cases)
                    {
                        File.WriteAllText(pref, t[0]); Theme.Name = "light"; Theme.Load();
                        if (Theme.Name == t[1]) note("ok   shell.json with extra text keeps the theme (" + t[1] + ")"); else { failures++; note("FAIL shell.json with extra text gave " + Theme.Name + ", wanted " + t[1]); }
                    }
                    if (keep != null) File.WriteAllText(pref, keep); else File.Delete(pref);
                    Theme.Name = "light"; Theme.Load();
                    if (Engine.DataDir().Contains("Ledjoro-Data") || Environment.GetEnvironmentVariable("LEDJORO_DATA") != null) note("ok   data folder: " + Engine.DataDir()); else { failures++; note("FAIL data folder is not the portable folder: " + Engine.DataDir()); }
                }
                {   // the recovery QR code: payload, reading it back from a picture, and from a printed page
                    string company = "Acme & Sons, LLC (S\u00e3o Paulo)", user = "user.name", key = "7K2QF-9XMWB-C4HTN-8VR3D";
                    string payload = RecoveryQr.Build(company, user, key); var info = RecoveryQr.Parse(payload);
                    if (info != null && info.Company == company && info.User == user && info.Key == key) note("ok   QR payload round trip (company, user, key)"); else { failures++; note("FAIL QR payload round trip"); }
                    if (RecoveryQr.Parse("https://example.com/shop?id=1") == null && RecoveryQr.Parse("ledjoro:recover?v=1&u=x&k=short") == null && RecoveryQr.Parse("ledjoro:recover?v=2&u=x&k=" + key) == null && RecoveryQr.Parse(null) == null) note("ok   other QR codes and bad payloads are ignored"); else { failures++; note("FAIL other QR codes are not ignored"); }
                    using (var bmp = new Bitmap(520, 520))
                    {
                        using (var g = Graphics.FromImage(bmp)) { g.Clear(Color.White); RecoveryQr.Draw(g, QrEncoder.Encode(payload), 10, 10, 500); }
                        if (QrReader.Decode(RecoveryQr.Luminance(bmp), bmp.Width, bmp.Height) == payload) note("ok   the QR drawn for the screen reads back"); else { failures++; note("FAIL the QR drawn for the screen does not read back"); }
                    }
                    using (var page = new Bitmap(2550, 3300))
                    {   // the printed sheet at 300 dots per inch (the printer works in hundredths of an inch)
                        using (var g = Graphics.FromImage(page)) { g.Clear(Color.White); g.ScaleTransform(3, 3); SheetScreen.DrawSheet(g, new Rectangle(100, 100, 650, 900), "Account created", company, user, "a-long-password-for-printing", key, "SETUPTOKEN"); }
                        if (QrReader.Decode(RecoveryQr.Luminance(page), page.Width, page.Height) == payload) note("ok   the QR on the printed sheet reads back"); else { failures++; note("FAIL the QR on the printed sheet does not read back"); }
                    }
                    note("     (camera for scanning is " + (RecoveryQr.CameraAvailable ? "available" : "not compiled in") + ")");
                }
                {   // errors are explained in plain words: what happened, why, what to do
                    Action<string, string, string> says = (what, got, want) => { if (got.IndexOf(want, StringComparison.OrdinalIgnoreCase) >= 0 && got.Length > 40) note("ok   plain error: " + what); else { failures++; note("FAIL plain error (" + what + ") was: " + got); } };
                    says("no access to a file", Say.Plain(new UnauthorizedAccessException("Access to the path is denied.")), "did not allow Ledjoro");
                    says("disk full", Say.Plain(new IOException("There is not enough space on the disk.", unchecked((int)0x80070070))), "disk is full");
                    says("file in use", Say.Plain(new IOException("in use", unchecked((int)0x80070020))), "Another program is using that file");
                    says("file missing", Say.Plain(new FileNotFoundException("gone.db")), "could not find");
                    says("engine unreachable", Say.Plain(new ApiException(0, Say.EngineUnreachable, Jv.Null)), "could not talk to its own engine");
                    says("a server status with no message", Say.Plain(new ApiException(500, "", Jv.Null)), "problem inside its own engine");
                    says("signed out", Say.Plain(new ApiException(401, "", Jv.Null)), "not signed in");
                    says("anything unexpected", Say.Plain(new NullReferenceException("Object reference not set")), "did not expect");
                    says("engine stopped, with the engine's own reason", Say.EngineStopped("2026-10-09T00:00:00.000Z problem: The books file is open in another program (or another copy of Ledjoro). Close it and start Ledjoro again."), "Why: The books file is open in another program");
                    says("engine slow", Say.EngineStopped("", true), "did not finish starting");
                }
                form.Size = new Size(Theme.Px(1320), Theme.Px(860)); form.Location = new Point(10, 10); Application.DoEvents();
                await App.Api.RefreshSession();
                note("setup required: " + App.Api.SetupRequired);
                if (App.Api.SetupRequired)
                {
                    form.ShowAuth(new SetupScreen(App.Engine.SetupToken)); await shot("00-setup");
                    string pw = "Harbor-" + Guid.NewGuid().ToString("N").Substring(0, 14) + "-Quill";
                    var r = await App.Api.Post("api/setup", Json.O("token", App.Engine.SetupToken, "company_name", "Birchwood Design Studio LLC", "display_name", "Self Test", "username", "selftest", "password", pw));
                    await App.Api.RefreshSession();
                    Interactions.Password = pw; Interactions.Recovery = r["recovery_key"].Str;
                    var sheet = new SheetScreen("Account created - save your sign-in details", "Birchwood Design Studio LLC", "selftest", pw, r["recovery_key"].Str, App.Engine.SetupToken, "Continue to Ledjoro", () => { });
                    form.ShowAuth(sheet);
                    if (form.BlocksClose(System.Windows.Forms.CloseReason.UserClosing) && !form.BlocksClose(System.Windows.Forms.CloseReason.WindowsShutDown)) note("ok   the window cannot be closed on the sign-in sheet (X, Alt+F4), but Windows can still shut down"); else { failures++; note("FAIL the sign-in sheet does not block closing the window"); }
                    var goSheet = Drive.Button(form.AuthArea, "Continue to Ledjoro"); var ackSheet = Drive.All<Check>(form.AuthArea).FirstOrDefault();
                    if (ackSheet != null) ackSheet.Checked = true;
                    if (goSheet != null && !goSheet.Enabled && !sheet.Done) note("ok   Continue stays off until the sheet is printed or saved, even after ticking the box"); else { failures++; note("FAIL Continue is available before the sheet was printed or saved"); }
                    string tmpPng = Path.Combine(Path.GetTempPath(), "ledjoro-sheet-test-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".png");
                    if (SheetScreen.SaveSheet("Account created", "Birchwood Design Studio LLC", "selftest", pw, r["recovery_key"].Str, App.Engine.SetupToken, tmpPng) && new FileInfo(tmpPng).Length > 20000) { note("ok   saving the sheet to a file works (" + new FileInfo(tmpPng).Length / 1024 + " KB picture)"); sheet.MarkDone(); } else { failures++; note("FAIL saving the sheet to a file"); }
                    try { File.Delete(tmpPng); } catch (IOException) { }
                    if (goSheet != null && goSheet.Enabled && !form.BlocksClose(System.Windows.Forms.CloseReason.UserClosing)) note("ok   after saving, Continue is available and the window can be closed again"); else { failures++; note("FAIL after saving, Continue/closing are still blocked"); }
                    await shot("01-sheet");
                    var sheetQr = Drive.All<QrBox>(form.AuthArea).FirstOrDefault(); var showBtn = Drive.Button(form.AuthArea, "Show Secrets");
                    if (sheetQr != null && !sheetQr.Revealed && showBtn != null) { showBtn.PerformClick(); await Task.Delay(150); if (sheetQr.Revealed) note("ok   the sheet shows a QR code, hidden until \"Show Secrets\""); else { failures++; note("FAIL the sheet's QR code did not appear on Show Secrets"); } showBtn.PerformClick(); await Task.Delay(100); if (!sheetQr.Revealed) note("ok   and hides again"); else { failures++; note("FAIL the sheet's QR code did not hide again"); } showBtn.PerformClick(); await Task.Delay(150); await shot("01b-sheet-qr-shown"); showBtn.PerformClick(); await Task.Delay(100); }
                    else { failures++; note("FAIL the sign-in sheet has no hidden QR code"); }
                }
                form.ShowShell();
                var routes = new[] { "#/dashboard", "#/invoices", "#/document/1", "#/invoices/new", "#/bills", "#/bills/new", "#/customers", "#/vendors", "#/banking", "#/accounts", "#/journal", "#/journal/new",
                    "#/assets", "#/health", "#/reports/profit-loss", "#/reports/balance-sheet", "#/reports/trial-balance", "#/reports/ar-aging", "#/reports/ap-aging", "#/reports/sales-by-customer", "#/reports/general-ledger", "#/reports/cash-forecast",
                    "#/import", "#/settings/company", "#/settings/data", "#/settings/users", "#/settings/roles", "#/settings/audit", "#/settings/account" };
                foreach (var route in routes)
                {
                    string name = route.Replace("#/", "").Replace("/", "-");
                    try { await form.GoAsync(route); string err = form.LastError; if (err != null) { failures++; note("FAIL " + route + ": " + err); } else note("ok   " + route); await shot("10-" + name); }
                    catch (Exception e) { failures++; note("EXC  " + route + ": " + e.Message); }
                }
                Theme.Set(false); await Task.Delay(300);
                foreach (var route in new[] { "#/dashboard", "#/reports/cash-forecast", "#/settings/data" })
                { await form.GoAsync(route); await shot("20-light-" + route.Replace("#/", "").Replace("/", "-")); note("ok   light " + route); }
                Theme.Set(true);
                note("--- interaction tests");
                int before = failures;
                await Interactions.Run(form, note, shot, what => { failures++; note("FAIL " + what); });
                note("interaction failures: " + (failures - before));
            }
            catch (Exception e) { failures++; note("SELFTEST ERROR: " + e); }
            File.WriteAllText(Path.Combine(dir, "result.txt"), log + "\nfailures: " + failures + "\n");
            Environment.ExitCode = failures == 0 ? 0 : 1;
            form.Close();
        }
    }
}
