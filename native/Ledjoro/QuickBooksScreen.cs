using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Ledjoro
{
    /// <summary>A thin progress bar in the theme colours.</summary>
    sealed class Meter : Control
    {
        int pct;
        public Meter() { SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.ResizeRedraw, true); Height = Theme.Px(10); TabStop = false; }
        public int Percent { get { return pct; } set { pct = Math.Max(0, Math.Min(100, value)); Invalidate(); } }
        protected override void OnPaint(PaintEventArgs e)
        {
            Theme.Smooth(e.Graphics); e.Graphics.Clear(Pnl.BackOf(this));
            var r = new Rectangle(0, 1, Width - 1, Height - 3);
            using (var p = Theme.Round(r, r.Height / 2)) using (var b = new SolidBrush(Theme.Soft)) e.Graphics.FillPath(b, p);
            int w = (int)(r.Width * pct / 100.0); if (w < r.Height) w = pct > 0 ? r.Height : 0;
            if (w > 0) using (var p = Theme.Round(new Rectangle(r.X, r.Y, w, r.Height), r.Height / 2)) using (var b = new SolidBrush(Theme.Accent)) e.Graphics.FillPath(b, p);
        }
        public override Size GetPreferredSize(Size proposed) { return new Size(Math.Max(100, proposed.Width), Theme.Px(10)); }
    }

    /// <summary>
    /// QuickBooks Sync: copy accounts, customers, vendors, items and transactions out of QuickBooks Desktop through Intuit's
    /// QuickBooks Web Connector. Read-only, and nothing reaches the books until the person has seen a preview and agreed.
    /// </summary>
    sealed class QuickBooksScreen : Screen
    {
        ScrollPage page; Stack box; Jv st; string shownKey = ""; string password = "";
        System.Windows.Forms.Timer timer; Lbl phaseLbl; Meter meter; bool busy;
        public string Key { get { return shownKey; } }
        public Jv Status { get { return st; } }

        public override async Task Load()
        {
            page = new ScrollPage(); var s = page.Body;
            s.Add(new Header("QuickBooks Sync", "Bring your books across from QuickBooks Desktop. Read-only: nothing in QuickBooks is changed."));
            box = new Stack { Gap = Theme.Px(20) }; s.Add(box);
            SetBody(page);
            st = await A.Get("api/qbwc/status"); Render(true);
            timer = new System.Windows.Forms.Timer { Interval = 2000 };
            timer.Tick += async (a, b) => { if (busy || IsDisposed) return; try { var n = await A.Get("api/qbwc/status"); st = n; Render(false); } catch (Exception) { } };
            timer.Start();
        }
        protected override void Dispose(bool disposing) { if (disposing && timer != null) { timer.Stop(); timer.Dispose(); } base.Dispose(disposing); }

        string KeyOf(Jv s) { return (s["enabled"].Bool ? "on" : "off") + "|" + (s["running"].Bool ? "run" : "stop") + "|" + s["phase"].Str + "|" + (s["pending"].IsNull ? "-" : s["pending"]["finished"].Str) + "|" + s["port_changed"].Bool + "|" + password.Length + "|" + s["error"].Str; }

        void Render(bool force)
        {
            string key = KeyOf(st);
            if (!force && key == shownKey) { UpdateLive(); return; }
            shownKey = key; box.ClearItems();
            if (!st["enabled"].Bool) { Intro(); } else { Connected(); }
            UpdateLive(); Vx.Relayout(box); page.PerformLayout();
        }

        void UpdateLive()
        {
            if (phaseLbl == null) return;
            string ph = st["phase"].Str; int pct = st["progress"].Int;
            phaseLbl.Text = ph == "syncing" ? st["step"].Str + "…  " + pct + "%" : ph == "ready" ? "Finished reading QuickBooks. Review what was found below." : ph == "error" ? "The last attempt did not finish: see the message above. When that is sorted out, press Update Selected in the Web Connector again.  " + LastContact() : LastContact();
            if (meter != null) { meter.Visible = ph == "syncing"; meter.Percent = pct; }
        }
        string LastContact()
        {
            long ms = st["last_contact"].Long;
            return ms == 0 ? "The Web Connector has not contacted Ledjoro yet." : "The Web Connector last contacted Ledjoro " + Fmt.Age(Fmt.FromUnixMs(ms)) + ".";
        }

        // ---- not switched on yet
        void Intro()
        {
            var c = new Card(); Roomy(c); c.Add(new Lbl("Connect to QuickBooks Desktop", LS.H3), Theme.Px(4));
            c.Add(new Lbl("QuickBooks Desktop comes with a free helper from Intuit called the QuickBooks Web Connector. Once you link it to Ledjoro, it can copy your chart of accounts, customers, vendors, products and services, invoices, bills, payments, deposits, checks and journal entries straight into Ledjoro, with no exporting or file juggling.", LS.Normal, true), Theme.Px(6));
            c.Add(new Lbl("It only reads. Nothing in QuickBooks is changed, and nothing reaches your Ledjoro books until you have looked at a preview and pressed Import. The link works only on this computer and only while you have it switched on.", LS.Normal, true), Theme.Px(8));
            c.Add(new Lbl("You need QuickBooks Desktop on this same computer, with your company file open and you signed in as an administrator. For QuickBooks Online, or a computer without QuickBooks, export a file from QuickBooks and use Import & Convert instead.", LS.Muted, true), Theme.Px(8));
            var err = new Notice(NK.Error); c.Add(err, Theme.Px(8));
            c.Add(Ui.BA("Set up the Connection", BK.Primary, async () =>
            {
                busy = true;
                try { var r = await A.Post("api/qbwc/enable"); password = r["password"].Str; st = await A.Get("api/qbwc/status"); Render(true); }
                catch (ApiException e) { err.Set(e.Message); }
                finally { busy = false; }
            }), 0);
            box.Add(c);
        }

        // ---- switched on
        static void Roomy(Card c) { c.Padding = new Padding(Theme.Px(24), Theme.Px(22), Theme.Px(24), Theme.Px(24)); c.Gap = Theme.Px(14); }

        void Connected()
        {
            if (st["error"].Str.Length > 0 && st["phase"].Str == "error") box.Add(new Notice(NK.Error, st["error"].Str));
            if (st["port_changed"].Bool) box.Add(new Notice(NK.Warn, "Ledjoro had to change the connection's port number, so the connection file you saved earlier no longer matches. Save it again below and add it to the Web Connector again (remove the old Ledjoro entry there first)."));
            if (!st["running"].Bool) box.Add(new Notice(NK.Warn, "The connection is switched on but not listening. Turn it off and on again."));

            var live = new Card(); Roomy(live); live.Add(new Lbl("Status", LS.H3));
            phaseLbl = new Lbl("", LS.Normal, true); live.Add(phaseLbl, Theme.Px(4));
            meter = new Meter { Visible = false }; live.Add(meter, Theme.Px(6));
            box.Add(live);

            if (!st["pending"].IsNull) PendingCard();

            var steps = new Card(); Roomy(steps); steps.Add(new Lbl("Link Ledjoro to the Web Connector (once)", LS.H3), Theme.Px(6));
            // each step is its own little group (text, then its button), with clear space between the groups
            Action<string, string, Control> step = (n, t, action) =>
            {
                var g = new Stack { Fill = Pnl.K.Surface, Gap = Theme.Px(10) };
                g.Add(new Lbl(n + "   " + t, LS.Normal, true));
                if (action != null) { var row = new Flow { Fill = Pnl.K.Surface }; row.Add(action); g.Add(row); }
                steps.Add(g, Theme.Px(20));
            };
            var saveRow = new Flow { Fill = Pnl.K.Surface }; saveRow.Add(Ui.BA("Save Connection File", BK.Primary, SaveQwc));
            step("1.", "Save Ledjoro's connection file (it has no password in it). The Web Connector and QuickBooks Desktop must be on this same computer.", saveRow);
            var openRow = new Flow { Fill = Pnl.K.Surface }; openRow.Add(Ui.B("Open the Web Connector", BK.Primary, () => OpenWebConnector(false))); openRow.Add(Ui.B("Open as Administrator", BK.Normal, () => OpenWebConnector(true))); openRow.Add(Ui.B("Check That QuickBooks Is Open", BK.Normal, CheckQuickBooks));
            step("2.", "First open QuickBooks Desktop and your company file, signed in as the Admin user, and leave it open (File menu: Switch to Single-user Mode). Then open the QuickBooks Web Connector. If QuickBooks is not open, the Web Connector fails with \u201CCould not start QuickBooks\u201D. If QuickBooks was started with \u201CRun as administrator\u201D, use Open as Administrator here so both run at the same level.", openRow);
            step("3.", "In the Web Connector choose \u201CAdd an Application\u201D, pick the file you just saved, and agree when QuickBooks asks whether Ledjoro may connect (choose to allow it, and to let it access the company file). QuickBooks will describe the access as reading and changing data: that is because the Web Connector itself has to record a small identifier in the company file. Ledjoro only ever asks questions and never changes anything in QuickBooks.", null);
            if (password.Length > 0)
            {
                var pw = new Flow { Fill = Pnl.K.Surface, HGap = Theme.Px(14) }; pw.Add(new Lbl(password, LS.Mono)); pw.Add(Ui.B("Copy", BK.Primary, () => { try { Clipboard.SetText(password); App.Toast("Password copied"); } catch (Exception) { App.Toast("Could not copy. Select and copy it by hand.", true); } }, true));
                step("4.", "When the Web Connector asks for a password, type this one. It is shown only now, so keep it until you have done this:", pw);
            }
            else
            {
                step("4.", "The Web Connector asks for a password. Ledjoro does not keep it (only a scrambled copy), so if you no longer have it, make a new one:", Ui.BA("Make a New Password", BK.Primary, async () =>
                {
                    if (!Msg.Confirm("Make a New Password?", "The old password stops working. You will type the new one into the Web Connector the next time it asks.", "Make a New Password")) return;
                    var r = await A.Post("api/qbwc/password"); password = r["password"].Str; st = await A.Get("api/qbwc/status"); Render(true);
                }));
            }
            step("5.", "Tick Ledjoro in the Web Connector and press \u201CUpdate Selected\u201D. Ledjoro reads your QuickBooks data (this can take a few minutes for a big company), and the results appear here for you to review. Do this again whenever you want fresh data; later syncs only ask for what has changed.", null);
            box.Add(steps);

            box.Add(HelpCard());
            var off = new Card(); Roomy(off); off.Add(new Lbl("Switch it off", LS.H3));
            off.Add(new Lbl("Turning the link off closes the connection completely. It does not delete anything you have already imported.", LS.Muted, true), Theme.Px(4));
            off.Add(Ui.BA("Turn Off the QuickBooks Link", BK.Danger, async () =>
            {
                if (!Msg.Confirm("Turn Off the QuickBooks Link?", "The Web Connector will no longer be able to reach Ledjoro. Your books are not changed. You can set it up again later.", "Turn Off")) return;
                await A.Post("api/qbwc/disable"); password = ""; st = await A.Get("api/qbwc/status"); Render(true);
            }), 0);
            box.Add(off);
        }

        /// <summary>What to try when the Web Connector or QuickBooks refuses to link (the usual cause is QuickBooks' own security settings, not Ledjoro).</summary>
        Card HelpCard()
        {
            var c = new Card(); Roomy(c); c.Add(new Lbl("Having trouble linking?", LS.H3));
            var text = new Lbl("If the Web Connector says \u201CQBWC1039\u201D when you add Ledjoro (for example \u201CCould not start QuickBooks\u201D or \u201CUnable to add FileID to company file\u201D), the problem is between QuickBooks and the Web Connector, before Ledjoro is involved. Its log (Help menu, View Log) says which. These usually fix it:\n\n" +
                "1.  Open QuickBooks Desktop and your company file in single-user mode (File menu: Switch to Single-user Mode), signed in as the Admin user.\n" +
                "2.  Close the Web Connector, then open QuickBooks and the Web Connector the same way: either both normally, or both with \u201CRun as administrator\u201D (right-click the program). A mismatch between the two causes this error.\n" +
                "3.  In QuickBooks, open Edit, Preferences, Integrated Applications, Company Preferences. Remove any older Ledjoro entry, and make sure \u201CDon't allow any applications to access this company file\u201D is not ticked.\n" +
                "4.  If QuickBooks reports that the program \u201Crequested read-only access\u201D (log code 3263), remove Ledjoro from the Web Connector, save the connection file again from step 1 above (this version asks for standard access) and add it again.\n" +
                "5.  If it still fails, open the Web Connector's log and look at the last lines: the Web Connector's Help menu has \u201CView Log\u201D, or open  %LocalAppData%\\Intuit\\QBWebConnector\\log\\QWCLog.txt  in Notepad. It says exactly why QuickBooks refused.", LS.Normal, true) { Visible = false };
            string closed = "Show the fixes to try", opened = "Hide the fixes";
            var toggle = new Lbl(closed, LS.Link) { Underline = true };
            toggle.Click += (a, b) => { text.Visible = !text.Visible; toggle.Text = text.Visible ? opened : closed; Vx.Relayout(box); page.PerformLayout(); };
            c.Add(toggle, Theme.Px(4)); c.Add(text, Theme.Px(8));
            return c;
        }

        void PendingCard()
        {
            var c = new Card(); Roomy(c); c.Add(new Lbl("Ready to review", LS.H3));
            c.Add(new Lbl("This is what Ledjoro read from QuickBooks. Nothing is in your books yet.", LS.Muted, true), Theme.Px(4));
            var holder = new Stack { Gap = Theme.Px(4) }; c.Add(holder, Theme.Px(6));
            var importRow = new Flow(); c.Add(importRow, Theme.Px(10));
            box.Add(c);
            Ui.Run(async () =>
            {
                Jv p;
                try { p = await A.Get("api/qbwc/preview"); } catch (ApiException e) { holder.Add(new Notice(NK.Error, e.Message)); Vx.Relayout(box); return; }
                var sm = p["summary"];
                Action<string> bullet = t => holder.Add(new Lbl("•  " + t, LS.Normal, true));
                bullet("Accounts: " + sm["accounts"]["new"].Str + " new, " + sm["accounts"]["existing"].Str + " already in Ledjoro");
                bullet("Customers and vendors: " + sm["contacts"]["new"].Str + " new, " + sm["contacts"]["existing"].Str + " already in Ledjoro");
                bullet("Products and services: " + sm["items"]["new"].Str + " new, " + sm["items"]["existing"].Str + " already in Ledjoro");
                bullet("Transactions: " + sm["journal_entries"]["new"].Str + " new, " + sm["journal_entries"]["duplicate"].Str + " already imported before");
                foreach (var w in p["warnings"].Items) holder.Add(new Notice(NK.Warn, w.Str));
                int nn = p["notes"].Count;
                if (nn > 0)
                {
                    var noteText = new Lbl("•  " + string.Join("\n•  ", p["notes"].Items.Select(x => x.Str)), LS.Muted, true) { Visible = false };
                    string closed = "Show " + nn + " note" + (nn == 1 ? "" : "s") + " (nothing here needs fixing)";
                    var toggle = new Lbl(closed, LS.Link) { Underline = true };
                    toggle.Click += (a, b) => { noteText.Visible = !noteText.Visible; toggle.Text = noteText.Visible ? "Hide the notes" : closed; Vx.Relayout(box); page.PerformLayout(); };
                    holder.Add(toggle); holder.Add(noteText);
                }
                int ec = p["errorCount"].Int;
                if (ec > 0) holder.Add(new Notice(NK.Warn, ec + " record(s) cannot be imported as they are (for example an account they use does not exist) and will be skipped:\n• " + string.Join("\n• ", p["errors"].Items.Take(8).Select(x => x.Str))));
                importRow.Add(Ui.BA("Import into my Books", BK.Primary, async () =>
                {
                    busy = true;
                    try
                    {
                        var r = await A.Post("api/qbwc/commit"); Lists.Bust();
                        App.Toast("Imported: " + r["journal_entries"].Str + " transactions, " + r["accounts"].Str + " accounts, " + r["contacts"].Str + " people");
                        st = await A.Get("api/qbwc/status"); Render(true);
                        box.Add(new Notice(NK.Ok, "Imported " + r["journal_entries"].Str + " transactions, " + r["accounts"].Str + " accounts, " + r["contacts"].Str + " customers and vendors, and " + r["items"].Str + " items. You can undo this under Import & Convert, Recent imports."), Theme.Px(0)); Vx.Relayout(box); page.PerformLayout();
                    }
                    catch (ApiException e) { App.Toast(e.Message, true); }
                    finally { busy = false; }
                }));
                importRow.Add(Ui.BA("Throw this Away", BK.Normal, async () => { await A.Post("api/qbwc/discard"); st = await A.Get("api/qbwc/status"); Render(true); }));
                Vx.Relayout(box); page.PerformLayout();
            });
        }

        async Task SaveQwc()
        {
            try
            {
                var file = await A.PostForFile("api/qbwc/qwc", Json.O("read_only", false));
                using (var d = new SaveFileDialog { FileName = file.FileName, Title = "Save the Ledjoro connection file", Filter = "QuickBooks Web Connector file|*.qwc", InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) })
                {
                    if (d.ShowDialog(App.Main) != DialogResult.OK) return;
                    File.WriteAllBytes(d.FileName, file.Bytes); App.Toast("Saved " + Path.GetFileName(d.FileName));
                }
            }
            catch (ApiException e) { App.Toast(e.Message, true); }
            catch (IOException e) { App.Toast(Say.Plain(e) + " Try a different folder, such as Documents.", true); }
            catch (UnauthorizedAccessException) { App.Toast("Windows does not allow Ledjoro to save into that folder. Choose a folder you own, such as Documents.", true); }
        }

        /// <summary>Where the QuickBooks Web Connector program is on this computer, or null. QuickBooks Desktop puts it in its Common Files folder and records the place in the registry.</summary>
        public static string FindWebConnector()
        {
            // 1. the place QuickBooks recorded when it installed the Web Connector (both the 32-bit and the 64-bit view of the registry)
            foreach (var view in new[] { Microsoft.Win32.RegistryView.Registry32, Microsoft.Win32.RegistryView.Registry64 })
            {
                try
                {
                    using (var hk = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, view))
                    using (var k = hk.OpenSubKey(@"SOFTWARE\Intuit\QBWebConnector") ?? hk.OpenSubKey(@"SOFTWARE\WOW6432Node\Intuit\QBWebConnector"))
                    {
                        string p = k == null ? null : k.GetValue("Path") as string;
                        if (!string.IsNullOrEmpty(p) && File.Exists(p)) return p;
                    }
                }
                catch (Exception) { /* not readable: try the usual places */ }
            }
            // 2. the usual places
            var roots = new[] { Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFilesX86), Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles),
                                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) };
            foreach (var r in roots.Where(x => !string.IsNullOrEmpty(x)).Distinct())
                foreach (var sub in new[] { @"Intuit\QuickBooks\QBWebConnector", @"QuickBooks\QBWebConnector" })
                {
                    string exe = Path.Combine(r, sub, "QBWebConnector.exe");
                    if (File.Exists(exe)) return exe;
                }
            // 3. a Start-menu shortcut called "Web Connector"
            foreach (var menu in new[] { Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), Environment.GetFolderPath(Environment.SpecialFolder.StartMenu) })
            {
                try { var lnk = Directory.GetFiles(menu, "*Web Connector*.lnk", SearchOption.AllDirectories).FirstOrDefault(); if (lnk != null) return lnk; }
                catch (Exception) { /* no access to that part of the menu */ }
            }
            return null;
        }

        /// <summary>What state QuickBooks is in right now, in plain words: not open, open normally, or open as administrator (which the Web Connector must match).</summary>
        public static string QuickBooksState(out bool running, out bool elevated)
        {
            running = false; elevated = false;
            var names = new[] { "QBW32", "QBW", "QBW32EnterpriseProfessional" };
            var list = names.SelectMany(n => { try { return System.Diagnostics.Process.GetProcessesByName(n); } catch (Exception) { return new System.Diagnostics.Process[0]; } }).ToList();
            if (list.Count == 0) return "QuickBooks is not open right now. Open QuickBooks and your company file first (signed in as the Admin user), leave it open, and then add Ledjoro in the Web Connector. This is the usual reason for \u201CCould not start QuickBooks\u201D.";
            running = true;
            var titles = list.Select(p => { try { return p.MainWindowTitle ?? ""; } catch (Exception) { return ""; } }).Where(t => t.Length > 0).ToList();
            if (titles.Count > 1 && titles.Any(t => t.IndexOf("(Secondary)", StringComparison.OrdinalIgnoreCase) >= 0))
                return "QuickBooks is open twice: one main window with your company file, and a second \u201C(Secondary)\u201D window. With two windows the Web Connector cannot tell which one to use and reports \u201Cthe company data file is not open\u201D. Close both, open QuickBooks once with your company file (File menu: Switch to Single-user Mode), and try again.";
            if (titles.Count == 0)
                return "QuickBooks is running but has no window open. Close it completely (Task Manager, end QuickBooks), open it again from the Start menu and open your company file.";
            if (!titles.Any(t => t.IndexOf(" - Intuit QuickBooks", StringComparison.OrdinalIgnoreCase) >= 0 && t.Trim().Length > "Intuit QuickBooks".Length + 6 && !t.TrimStart().StartsWith("Intuit QuickBooks", StringComparison.OrdinalIgnoreCase)))
                return "QuickBooks is open, but no company file seems to be open in it. Open your company file (File menu, Open or Restore Company), wait until it is fully showing, and try again.";
            foreach (var p in list)
            {
                try { var m = p.MainModule; }   // an administrator's program cannot be inspected by an ordinary one: that tells us how it was started
                catch (System.ComponentModel.Win32Exception) { elevated = true; }
                catch (InvalidOperationException) { }
            }
            return elevated
                ? "QuickBooks is open, and it was started as administrator. Use Open as Administrator to start the Web Connector, so both run at the same level, then add Ledjoro."
                : "QuickBooks is open and running normally. Use the plain Open the Web Connector button (not the administrator one), make sure a company file is open in it, and then add Ledjoro.";
        }

        static void CheckQuickBooks() { bool r, e; string text = QuickBooksState(out r, out e); Msg.Info(r ? "QuickBooks is open" : "QuickBooks is not open", text); }

        static void OpenWebConnector(bool asAdmin)
        {
            string path = FindWebConnector();
            if (path != null)
            {
                try { var psi = asAdmin ? new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true, Verb = "runas" } : new System.Diagnostics.ProcessStartInfo("explorer.exe", "\"" + path + "\"") { UseShellExecute = true }; System.Diagnostics.Process.Start(psi); return; }   // via Explorer so it does not inherit Ledjoro\u2019s process environment: QuickBooks then treats it like a normal Start-menu launch
                catch (System.ComponentModel.Win32Exception wx) when (wx.NativeErrorCode == 1223) { return; }   // the person declined the Windows permission prompt
                catch (Exception ex) { Msg.Info("The Web Connector would not open", "Windows found the Web Connector but could not start it: " + Say.Plain(ex) + " Try opening it from the Windows Start menu (search for \u201CWeb Connector\u201D).", true); return; }
            }
            Msg.Info("The Web Connector was not found", "Ledjoro could not find the QuickBooks Web Connector. Open it from the Windows Start menu (search for \u201CWeb Connector\u201D). It is installed together with QuickBooks Desktop; if it is missing, run the QuickBooks installer's repair.");
        }
    }
}
