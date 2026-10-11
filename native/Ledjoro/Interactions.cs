using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Ledjoro
{
    /// <summary>Scripted "user" for the self-test: finds controls, types into them and clicks them exactly as a person would.</summary>
    static class Drive
    {
        public static IEnumerable<T> All<T>(Control root) where T : Control
        {
            foreach (Control c in root.Controls) { var t = c as T; if (t != null) yield return t; foreach (var x in All<T>(c)) yield return x; }
        }
        public static T Find<T>(Control root, Func<T, bool> pred = null) where T : Control { return All<T>(root).FirstOrDefault(c => c.Visible && (pred == null || pred(c))); }
        public static Btn Button(Control root, string text) { return Find<Btn>(root, b => b.Text == text); }
        public static async Task Settle(int ms = 350) { await Task.Delay(ms); Application.DoEvents(); }
        public static async Task<Dlg> WaitDialog(int ms = 4000)
        {
            for (int t = 0; t < ms; t += 50) { var d = Application.OpenForms.OfType<Dlg>().FirstOrDefault(); if (d != null && d.Visible) { await Settle(150); return d; } await Task.Delay(50); }
            return null;
        }
        public static async Task WaitClosed(Dlg d, int ms = 6000) { for (int t = 0; t < ms && !d.IsDisposed && d.Visible; t += 50) await Task.Delay(50); await Settle(200); }
        public static async Task<bool> Until(Func<bool> cond, int ms = 6000) { for (int t = 0; t < ms; t += 50) { if (cond()) return true; await Task.Delay(50); } return cond(); }
        public static Input Field(Control root, string cue) { return Find<Input>(root, i => i.Cue == cue); }
    }

    static class Interactions
    {
        public static string User = "selftest", Password = "", Recovery = "";

        public static async Task Run(MainForm form, Action<string> note, Func<string, Task> shot, Action<string> fail)
        {
            Action<bool, string> check = (ok, what) => { if (ok) note("ok   " + what); else { fail(what); } };
            var api = App.Api;

            // ---- theme toggle -------------------------------------------------------------------------------------
            var tb = Drive.Find<Btn>(form, b => b.Text.StartsWith("Theme:"));
            check(tb != null, "theme drop-down button exists in the top bar");
            if (tb != null)
            {
                tb.PerformClick(); await Drive.Settle(200);
                check(form.ThemeMenu != null && form.ThemeMenu.Visible && form.ThemeMenu.Items.OfType<ToolStripMenuItem>().Count(i => i.AccessibleName.EndsWith(" theme")) == 3 && form.ThemeMenu.Items.OfType<ToolStripMenuItem>().Count(i => i.AccessibleName.EndsWith(" text size")) == 3, "theme menu opens with three themes and three text sizes"); await shot("36-theme-menu");
                form.ThemeMenu.Close();
                foreach (var name in new[] { "light", "night", "dark" })
                {
                    Theme.Use(name); await Drive.Settle(200);
                    check(Theme.Name == name && tb.Text.StartsWith("Theme: " + Theme.Title), "theme '" + name + "' applies and the button shows it");
                    if (name == "night") await shot("36-theme-" + name);
                }
                check(Theme.Dark, "back on the dark theme");
            }

            // ---- back / forward ---------------------------------------------------------------------------------------------------
            var backB = Drive.Find<Btn>(form, x => x.AccessibleName == "Back"); var fwdB = Drive.Find<Btn>(form, x => x.AccessibleName == "Forward");
            check(backB != null && fwdB != null, "back and forward buttons exist in the top bar");
            if (backB != null && fwdB != null)
            {
                await form.GoAsync("#/dashboard"); await form.GoAsync("#/invoices"); await form.GoAsync("#/document/1"); await form.GoAsync("#/banking/categorized"); await Drive.Settle(300);
                check(backB.Enabled && !fwdB.Enabled, "after visiting pages: Back is on, Forward is off");
                backB.PerformClick(); await Drive.Settle(500); check(form.CurrentHash == "#/document/1", "Back returns to the invoice I opened");
                backB.PerformClick(); await Drive.Settle(500); check(form.CurrentHash == "#/invoices", "Back again returns to the invoice list");
                check(fwdB.Enabled, "Forward is available after going back");
                fwdB.PerformClick(); await Drive.Settle(500); check(form.CurrentHash == "#/document/1", "Forward returns to the invoice");
                await form.GoAsync("#/vendors"); await Drive.Settle(300); check(!fwdB.Enabled, "visiting a new page drops the forward history");
                for (int i = 0; i < 3; i++) { backB.PerformClick(); await Drive.Settle(400); }
                check(form.CurrentHash == "#/dashboard" && fwdB.Enabled, "Back three times from Vendors lands on the dashboard visited before the invoice, with Forward on");
                await form.GoAsync("#/invoices"); await form.GoAsync("#/invoices/paid"); await form.GoAsync("#/invoices/void"); await Drive.Settle(400);
                var tb2 = Drive.Find<TabBar>(form.Content); check(tb2 != null && tb2.Selected == "void", "the Void tab is selected on the Void page");
                backB.PerformClick(); await Drive.Settle(600); tb2 = Drive.Find<TabBar>(form.Content);
                check(form.CurrentHash == "#/invoices/paid" && tb2 != null && tb2.Selected == "paid", "Back from the Void tab returns to the Paid tab");
                backB.PerformClick(); await Drive.Settle(600); tb2 = Drive.Find<TabBar>(form.Content);
                check(form.CurrentHash == "#/invoices" && tb2 != null && tb2.Selected == "open", "Back again returns to the Open tab");
                await form.GoAsync("#/settings/company"); await form.GoAsync("#/settings/users"); backB.PerformClick(); await Drive.Settle(600);
                check(form.CurrentHash == "#/settings/company", "Back from Settings > Users returns to Settings > Company");
                await form.GoAsync("#/dashboard"); await shot("42-nav-buttons");
            }

            // ---- passkeys: the section in My account (Windows itself cannot be driven by a script, so the prompt is not exercised here) -------------
            await form.GoAsync("#/settings/account"); await Drive.Settle(900);
            check(WebAuthn.Available, "this Windows can make passkeys (webauthn.dll API version " + WebAuthn.ApiVersion + ")");
            check(Drive.All<Lbl>(form.Content).Any(l => l.Text == "Passkeys") && Drive.Button(form.Content, "Add a Passkey") != null && Drive.Button(form.Content, "Remove Selected Passkey") == null, "My account has a Passkeys section with Add (and no Remove button while there is nothing to remove)");
            check(Drive.All<Lbl>(form.Content).Any(l => l.Visible && l.Text == "You have no passkeys yet."), "it says plainly when there are no passkeys");
            { var sp0 = Drive.All<ScrollPage>(form.Content).FirstOrDefault(); if (sp0 != null) { sp0.ScrollPos = 100000; await Drive.Settle(300); await shot("43-passkeys-section"); sp0.ScrollPos = 0; } }
            await InteractionsNew.Run(form, note, shot, check);
            var addBtn = Drive.Button(form.Content, "Add a Passkey"); if (addBtn != null) { addBtn.PerformClick(); await Drive.Settle(500); check(Drive.All<Notice>(form.Content).Any(n => n.Visible && n.Message.Contains("current password")), "adding a passkey without the password is explained, and no Windows prompt appears"); }

            // ---- sign out, wrong password, then the recovery-key path ---------------------------------------------
            Drive.Button(form, "Sign Out").PerformClick();
            check(await Drive.Until(() => !api.SignedIn && Drive.Find<Input>(form.AuthArea, i => i.Cue == "Username") != null), "Sign out shows the sign-in screen");
            await shot("30-login");
            Drive.Find<Input>(form.AuthArea, i => i.Cue == "Username").Text = User; Drive.Find<Input>(form.AuthArea, i => i.Cue == "Password").Text = "definitely-wrong-password";
            Drive.Button(form.AuthArea, "Sign In").PerformClick();
            check(await Drive.Until(() => Drive.Find<Notice>(form.AuthArea, n => n.Message.Length > 0) != null), "wrong password shows an error");
            await shot("31-login-error");

            Drive.Find<Lbl>(form.AuthArea, l => l.Text.StartsWith("Forgot your password")).PerformClickLabel();
            check(await Drive.Until(() => Drive.Find<Input>(form.AuthArea, i => i.Cue == "XXXXX-XXXXX-XXXXX-XXXXX") != null), "forgot-password link opens the recovery screen");
            {   // typing the key letter by letter: hyphens appear by themselves, capitals are forced, and backspace can remove a hyphen
                var kb = Drive.Find<Input>(form.AuthArea, i => i.Cue == "XXXXX-XXXXX-XXXXX-XXXXX"); kb.Text = ""; kb.Tb.Focus();
                foreach (char ch in "k7qf9xmwbc4htn8vr3da") { kb.Tb.AppendText(ch.ToString()); }
                check(kb.Text == "K7QF9-XMWBC-4HTN8-VR3DA", "typing a recovery key adds the hyphens by itself (" + kb.Text + ")");
                kb.Text = ""; kb.Tb.AppendText("abcde"); check(kb.Text == "ABCDE-", "the hyphen appears as soon as five characters are typed (" + kb.Text + ")");
                kb.Tb.Text = "ABCDE"; check(kb.Text == "ABCDE", "backspacing over the hyphen is not undone (" + kb.Text + ")");
                kb.Text = "k7qf9 xmwbc-4htn8_vr3da extra"; check(kb.Text == "K7QF9-XMWBC-4HTN8-VR3DA", "a pasted key with spaces or odd dashes is tidied (" + kb.Text + ")");
                kb.Text = "";
            }
            string newPw = "Recovered-" + Guid.NewGuid().ToString("N").Substring(0, 12) + "-Ink";
            Drive.Find<Input>(form.AuthArea, i => i.Cue == "Username").Text = User; Drive.Find<Input>(form.AuthArea, i => i.Cue == "XXXXX-XXXXX-XXXXX-XXXXX").Text = Recovery.ToLowerInvariant(); Drive.Find<Input>(form.AuthArea, i => i.Cue == "At least 12 characters").Text = newPw;
            check((Drive.Button(form.AuthArea, "Scan the QR code with my Camera") != null) == RecoveryQr.CameraAvailable, "the reset screen offers camera scanning exactly when the camera is available");
            Drive.Button(form.AuthArea, "Reset Password").PerformClick();
            var cont = await WaitFor(() => Drive.Button(form.AuthArea, "Continue to Ledjoro"));
            check(cont != null, "recovery key resets the password and shows the new sign-in sheet");
            if (cont != null)
            {
                check(!cont.Enabled, "Continue stays locked until the sheet is acknowledged");
                await shot("32-recovery-sheet");
                Drive.Find<Check>(form.AuthArea).Checked = true; await Drive.Settle(100); check(!cont.Enabled, "ticking the box alone does not unlock Continue (the sheet must be printed or saved)");
                var recSheet = Drive.Find<SheetScreen>(form.AuthArea); if (recSheet != null) recSheet.MarkDone(); await Drive.Settle(100); check(cont.Enabled, "once the sheet is printed or saved, ticking the box unlocks Continue");
                var keyRow = Drive.Find<SheetRows>(form.AuthArea).Rows.First(r => r.Label == "Recovery key"); Recovery = keyRow.Value;
                Password = newPw; cont.PerformClick();
            }
            check(await Drive.Until(() => api.SignedIn && form.Content.Visible), "Continue opens the app, signed in with the new password");

            // ---- new invoice ----------------------------------------------------------------------------------------
            var customers = await api.Get("api/contacts?kind=customer"); string firstCustomer = customers[0]["id"].Str;
            await form.GoAsync("#/invoices/new"); await Drive.Settle();
            var cust = Drive.Find<Combo>(form.Content); cust.Value = firstCustomer;
            Drive.Field(form.Content, "Description").Text = "Self-test consulting"; Drive.Find<Input>(form.Content, i => i.Cue == "1").Text = "2"; Drive.Find<Input>(form.Content, i => i.Cue == "0.00").Text = "125.50";
            await Drive.Settle(150); await shot("33-invoice-filled");
            Drive.Button(form.Content, "Save").PerformClick();
            bool saved = await Drive.Until(() => form.CurrentHash.StartsWith("#/document/"));
            if (!saved) { var msg = Drive.All<Notice>(form.Content).Select(n => n.Message).FirstOrDefault(m => m.Length > 0); note("     (screen says: " + (msg ?? "nothing") + ")"); await shot("33b-invoice-not-saved"); }
            check(saved, "saving an invoice opens it");
            await Drive.Settle(500);
            var docs = await api.Get("api/documents?kind=invoice"); var newest = docs.Items.OrderByDescending(d => d["id"].Long).First();
            check(newest["total_cents"].Long == 25100 && newest["contact_name"].Str.Length > 0, "invoice total is 2 x $125.50 = $251.00");
            long docId = newest["id"].Long;

            // ---- receive a partial payment through the dialog ---------------------------------------------------------
            Drive.Button(form.Content, "Receive payment").Post();
            var dlg = await Drive.WaitDialog();
            check(dlg != null, "Receive payment opens a dialog");
            if (dlg != null)
            {
                var amt = Drive.Find<Input>(dlg, i => i.Cue == "0.00"); check(amt.Text == "251.00", "payment amount defaults to the balance");
                amt.Text = "100.00"; await shot("34-payment-dialog");
                Drive.Button(dlg, "Save Payment").PerformClick(); await Drive.WaitClosed(dlg);
            }
            var after = await api.Get("api/documents/" + docId);
            check(after["paid_cents"].Long == 10000 && after["status"].Str == "open", "partial payment is recorded ($100 of $251)");

            // ---- new customer with phone formatting ---------------------------------------------------------------------
            await form.GoAsync("#/customers"); await Drive.Settle();
            Drive.Button(form.Content, "New Customer").Post();
            dlg = await Drive.WaitDialog(); check(dlg != null, "New customer opens a dialog");
            if (dlg != null)
            {
                Drive.Find<Input>(dlg, i => i.Cue.StartsWith("Business or display name")).Text = "Self Test Co";
                Drive.Find<Input>(dlg, i => i.Cue == "First").Text = "Sam"; Drive.Find<Input>(dlg, i => i.Cue == "Middle").Text = "Q"; Drive.Find<Input>(dlg, i => i.Cue == "Last").Text = "Tester";
                Drive.All<PhoneInput>(dlg).Last().Text = "603 555 0188";
                Drive.Find<Input>(dlg, i => i.Cue == "Street address").Text = "12 Main St"; Drive.Find<Input>(dlg, i => i.Cue.StartsWith("Apartment")).Text = "Suite 4";
                Drive.Find<Input>(dlg, i => i.Cue == "City").Text = "Concord"; Drive.All<Combo>(dlg).First().Value = "NH"; Drive.Find<Input>(dlg, i => i.Cue == "ZIP code").Text = "03301";
                var phone = Drive.Find<PhoneInput>(dlg); phone.Text = "(603) 555 0142"; check(phone.Text == "603-555-0142", "phone field formats as xxx-xxx-xxxx while typing");
                phone.Text = "1 603 555 0143 99"; check(phone.Text == "603-555-0143", "extra digits and a leading 1 are handled");
                phone.Text = "6035550144"; await shot("35-customer-dialog");
                Drive.Button(dlg, "Save").PerformClick(); await Drive.WaitClosed(dlg);
            }
            var cs = await api.Get("api/contacts?kind=customer"); var made = cs.Items.FirstOrDefault(c => c["name"].Str == "Self Test Co");
            check(made != null && made["phone"].Str == "603-555-0144", "customer saved with phone 603-555-0144");
            check(made != null && made["first_name"].Str == "Sam" && made["last_name"].Str == "Tester" && made["phone2"].Str == "603-555-0188" && made["address"].Str == "12 Main St\nSuite 4\nConcord, NH 03301", "first, middle and last name, a second phone and a US address are saved");

            // ---- bank: categorize one transaction -------------------------------------------------------------------------
            await form.GoAsync("#/banking"); await Drive.Settle(500);
            int toReview = (await api.Get("api/bank/transactions?status=new")).Count;
            var grid = Drive.Find<Grid>(form.Content);
            if (toReview > 0 && grid != null)
            {
                grid.ClearSelection(); grid.Rows[0].Selected = true; grid.CurrentCell = grid.Rows[0].Cells[0]; await Drive.Settle(300);
                var combos = Drive.All<Combo>(form.Content).ToList(); var category = combos[0]; category.Index = 1; await Drive.Settle(100);
                Drive.Button(form.Content, "Add").PerformClick(); await Drive.Settle(800);
                check((await api.Get("api/bank/transactions?status=new")).Count == toReview - 1, "categorizing a bank transaction moves it out of 'to review'");
            }
            else check(false, "bank screen has transactions to review");

            // ---- import, then undo ----------------------------------------------------------------------------------------------
            await form.GoAsync("#/import"); await Drive.Settle(500);
            var imp = Drive.Find<ImportScreen>(form.Content) ?? form.Content.Controls.OfType<ImportScreen>().FirstOrDefault();
            string sample = Environment.GetEnvironmentVariable("LEDJORO_SAMPLE_IIF");
            if (string.IsNullOrEmpty(sample) || !File.Exists(sample))
            {   // look for samples\sample.iif in this folder and every folder above it (works wherever the build or the repo sits)
                sample = null; for (var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory); dir != null && sample == null; dir = dir.Parent) { var f = Path.Combine(dir.FullName, "samples", "sample.iif"); if (File.Exists(f)) sample = f; }
                sample = sample ?? "";
            }
            if (File.Exists(sample) && imp != null)
            {
                imp.LoadFile(sample); await imp.PreviewNow(); await Drive.Settle(500); await shot("36-import-preview");
                var go = Drive.Button(form.Content, "Import Now"); check(go != null, "preview offers 'Import now'");
                if (go != null) { go.PerformClick(); await Drive.Settle(1200); }
                var batches = await api.Get("api/import/batches"); check(batches.Count > 0 && batches[0]["live_entries"].Int == 3, "import added 3 journal entries");
                await Drive.Settle(500);
                var bg = Drive.All<Grid>(form.Content).LastOrDefault();
                if (bg != null && bg.Rows.Count > 0)
                {
                    bg.Rows[0].Selected = true; bg.CurrentCell = bg.Rows[0].Cells[0]; await Drive.Settle(150);
                    form.LastToast = ""; Drive.Button(form.Content, "Undo Selected Import").Post();
                    var confirm = await Drive.WaitDialog(); if (confirm == null) note("     (no dialog; last toast: '" + form.LastToast + "'; selected tag null? " + (bg.SelectedTag == null) + "; rows " + bg.Rows.Count + ")");
                    check(confirm != null, "Undo asks for confirmation");
                    if (confirm != null) { Drive.Button(confirm, "Undo Import").PerformClick(); await Drive.WaitClosed(confirm); await Drive.Settle(800); }
                    batches = await api.Get("api/import/batches"); check(batches[0]["undone"].Bool && batches[0]["live_entries"].Int == 0, "undo reverses the import");
                }
                else check(false, "recent imports list shows the new import");
            }
            else check(false, "sample.iif available for the import test");

            // ---- a CSV nobody recognises: the column matcher opens by itself --------------------------------------------------------
            if (imp != null)
            {
                string weird = Path.Combine(Path.GetTempPath(), "ob-weird-" + Guid.NewGuid().ToString("N").Substring(0, 6) + ".csv");
                File.WriteAllText(weird, "Posted,Details,Amt,Running\r\n2026-03-02,COFFEE SHOP,-4.50,95.50\r\n2026-03-03,PAYROLL,1000.00,1095.50\r\n2026-03-04,RENT,-800.00,295.50\r\n");
                await form.GoAsync("#/import"); await Drive.Settle(400);
                imp = Drive.Find<ImportScreen>(form.Content) ?? form.Content.Controls.OfType<ImportScreen>().FirstOrDefault();
                imp.LoadFile(weird); await imp.PreviewNow(); await Drive.Settle(500); await shot("37-import-column-matcher");
                var mp = imp.Mapper;
                check(mp != null, "an unrecognised CSV opens the column matcher");
                if (mp != null)
                {
                    check(mp.Cols.Count == 4 && mp.ColumnFor("date") == 0 && mp.ColumnFor("payee") == 1 && mp.ColumnFor("amount") == 2 && mp.ColumnFor("balance") == 3, "the matcher guesses Date, Payee, Amount and Balance from the column names");
                    var pvb = Drive.Button(form.Content, "Preview"); check(pvb != null && pvb.Visible, "the matcher has its own Preview button");
                    // each column has a list saying what it is: choose "Money out" for the third column ("Amt")
                    mp.Clear("amount");
                    var cardAmt = mp.Cards.First(x => x.Col.Index == 2);
                    cardAmt.Combo.Index = mp.Cards[0].Combo.Items.FindIndex(x => x.Key == "out"); await Drive.Settle(100);
                    check(mp.ColumnFor("out") == 2, "choosing 'Money out' from a column's list assigns it");
                    var cardPayee = mp.Cards.First(x => x.Col.Index == 1);
                    cardPayee.Combo.Index = cardPayee.Combo.Items.FindIndex(x => x.Key == "out"); await Drive.Settle(100);
                    check(mp.ColumnFor("out") == 1 && mp.Cards.First(x => x.Col.Index == 2).Combo.Value == "", "each meaning can be used once: choosing it elsewhere frees the first column");
                    cardPayee.Combo.Index = cardPayee.Combo.Items.FindIndex(x => x.Key == "payee"); await Drive.Settle(100);
                    check(Drive.All<Lbl>(form.Content).Any(l => l.Text.StartsWith("Still needed")), "the screen says plainly what is still needed");
                    mp.Assign("payee", 1); mp.Assign("amount", 2); await Drive.Settle(200);
                    await imp.PreviewNow(); await Drive.Settle(600); await shot("38-import-mapped-preview");
                    check(Drive.Button(form.Content, "Import Now") != null && Drive.All<Lbl>(form.Content).Any(l => l.Text.Contains("will wait under Banking")), "a mapped bank statement previews as lines waiting for review");
                    mp.Clear("date"); await imp.PreviewNow(); await Drive.Settle(300);
                    check(Drive.All<Notice>(form.Content).Any(n => n.Visible && n.Message.Contains("holds the date")), "forgetting the date column is explained in the matcher");
                }
                imp.SetFormat("csv-trialbalance"); await imp.PreviewNow(); await Drive.Settle(300);
                check(imp.Mapper == null && Drive.All<Notice>(form.Content).Any(n => n.Visible && n.Message.Contains("does not look like a trial balance")), "choosing another format closes the matcher and shows that format's error");
                var pv = Drive.Button(form.Content, "Preview"); check(pv != null && pv.Visible && pv.Bottom <= pv.Parent.Height + 1, "Preview button stays visible under an error");
                File.Delete(weird);
            }

            // ---- optional: a real account-register CSV (path in LEDJORO_REGISTER_CSV): preview only ------------------------------
            string regFile = Environment.GetEnvironmentVariable("LEDJORO_REGISTER_CSV");
            if (!string.IsNullOrEmpty(regFile) && File.Exists(regFile))
            {
                await form.GoAsync("#/import"); await Drive.Settle(400);
                imp = Drive.Find<ImportScreen>(form.Content) ?? form.Content.Controls.OfType<ImportScreen>().FirstOrDefault();
                imp.LoadFile(regFile); await imp.PreviewNow(); await Drive.Settle(300);
                check(imp.Mapper != null && imp.Mapper.ColumnFor("out") == 5 && imp.Mapper.ColumnFor("in") == 7 && imp.Mapper.ColumnFor("date") == 0, "opening the register CSV shows the matcher, with Payment (column F) on Money out and Deposit (column H) on Money in");
                await shot("39a-register-matcher");
                {   // the page must be tall enough to scroll to its last control, whatever is open above it
                    var spg = Drive.All<ScrollPage>(form.Content).FirstOrDefault();
                    var lastGrid = Drive.All<Grid>(form.Content).LastOrDefault();
                    if (spg != null && lastGrid != null)
                    {
                        int gridBottom = spg.Body.PointToClient(lastGrid.PointToScreen(new System.Drawing.Point(0, lastGrid.Height))).Y;
                        note("     (page body " + spg.Body.Height + "px, last grid bottom " + gridBottom + "px, scroll min " + spg.ContentHeight + "px, grid rows " + lastGrid.Rows.Count + ", grid height " + lastGrid.Height + ")");
                        check(gridBottom <= spg.Body.Height, "the page is tall enough to scroll down to the last grid (Recent imports)");
                    }
                }
                if (File.Exists(sample)) for (int k = 0; k < 8; k++) await api.Post("api/import/commit", Json.O("filename", "again-" + k + ".iif", "content", File.ReadAllText(sample)));   // a long "Recent imports" list
                await form.GoAsync("#/import"); await Drive.Settle(500);
                imp = Drive.Find<ImportScreen>(form.Content) ?? form.Content.Controls.OfType<ImportScreen>().FirstOrDefault();
                imp.LoadFile(regFile); await imp.PreviewNow(); await Drive.Settle(900);
                var sp = Drive.All<ScrollPage>(form.Content).FirstOrDefault(); if (sp != null) { sp.ScrollPos = Theme.Px(560); await Drive.Settle(300); await shot("39b-matcher-cards"); sp.ScrollPos = 100000; await Drive.Settle(300); await shot("39c-page-bottom"); { var lastBtn = Drive.All<Btn>(sp.Body).LastOrDefault(); int bb = sp.Body.PointToClient(lastBtn.PointToScreen(new System.Drawing.Point(0, lastBtn.Height))).Y; note("     (bottom: last button '" + lastBtn.Text + "' ends at " + bb + "px, body height " + sp.Body.Height + "px, scroll min " + sp.ContentHeight + "px, client height " + sp.ClientSize.Height + "px, scroll pos " + sp.ScrollPos + "; scroll bar " + (sp.ScrollBarShown ? "shown" : "hidden") + ")"); check(bb <= sp.Body.Height, "the last button on the Import page is inside the scrollable area"); check(sp.ScrollPos + sp.ClientSize.Height >= sp.Body.Height - 2, "scrolling to the bottom really reaches the bottom of a page that just grew"); } sp.ScrollPos = 0; }
                check(Drive.All<Notice>(form.Content).Any(n => n.Visible && n.Message.Contains("account this register belongs to")), "register CSV asks which account it belongs to");
                imp.SetRegister("Personal Checking"); await imp.PreviewNow(); await Drive.Settle(600); await shot("39-import-register-preview");
                check(Drive.Button(form.Content, "Import Now") != null, "register CSV previews and offers 'Import now'");
                check(Drive.All<Notice>(form.Content).Any(n => n.Message.Contains("looks like an account register")) || Drive.All<Lbl>(form.Content).Any(l => l.Text.StartsWith("CSV: Account register")), "register CSV was recognised as an account register");
                check(Drive.Button(form.Content, "Match Columns Myself") != null, "the Match columns button is a full-size button in the file card");
                imp.SetFormat("csv-mapped"); await Drive.Settle(800);
                var mm = imp.Mapper;
                if (mm != null && imp.Mapper.ColumnFor("out") == 5)
                {
                    imp.SetRegister("Personal Checking"); await imp.PreviewNow(); await Drive.Settle(900);
                    check(Drive.All<Lbl>(form.Content).Any(l => l.Text.Contains("Cross-check passed")) && Drive.Button(form.Content, "Import Now") != null, "the same register, matched by hand, previews with the balance cross-check");
                    imp.SetFormat(""); await Drive.Settle(300); await imp.PreviewNow(); await Drive.Settle(600);
                }
                else check(false, "matcher guessed the register's columns (Payment = column F)");
                var goReg = Drive.Button(form.Content, "Import Now");
                if (goReg != null)
                {
                    goReg.Post(); await Drive.Until(() => Drive.All<Notice>(form.Content).Any(n => n.Visible && n.Message.StartsWith("Imported:")), 60000); await Drive.Settle(500); await shot("40-import-register-done");
                    var bills = await api.Get("api/documents?kind=bill"); var invs = await api.Get("api/documents?kind=invoice");
                    check(bills.Count >= 1600 && invs.Count >= 700, "register import created bills (" + bills.Count + ") and invoices (" + invs.Count + ")");
                    var hist = await api.Get("api/bank/transactions?status=categorized"); check(hist.Count >= 4300, "register import wrote its bank history (" + hist.Count + " categorized lines)");
                    await form.GoAsync("#/banking"); await Drive.Settle(500); var cat = Drive.Find<TabBar>(form.Content); if (cat != null) { cat.GetType().GetMethod("Pick", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Invoke(cat, new object[] { "categorized" }); await Drive.Settle(1500); await shot("41-bank-history"); }
                }
            }

            // ---- Data & backup screen: three ways to protect a backup, and a restore that reads the file first ----------------------------------------
            await form.GoAsync("#/settings/data"); await Drive.Settle(900);
            check(Drive.All<Combo>(form.Content).Any(c => c.Items.Any(i => i.Key == "recovery") && c.Items.Any(i => i.Key == "passphrase") && c.Items.Any(i => i.Key == "none")), "backups can be locked with the recovery key, a passphrase, or not locked at all");
            check(Drive.All<Lbl>(form.Content).Any(l => l.Text.Contains("recovery key") && l.Text.Contains("opens this backup")), "the screen says in words what protects the backup");
            await shot("44-backup-options");
            var howBox = Drive.All<Combo>(form.Content).First(c => c.Items.Any(i => i.Key == "none")); howBox.Index = howBox.Items.FindIndex(i => i.Key == "none"); await Drive.Settle(200);
            check(Drive.All<Notice>(form.Content).Any(n => n.Visible && n.Message.Contains("will NOT be locked")), "choosing no lock shows a plain warning");
            howBox.Index = howBox.Items.FindIndex(i => i.Key == "recovery"); await Drive.Settle(100);

            // ---- backup and restore through the native API client -------------------------------------------------------------
            var bk = await api.PostForFile("api/backup", Json.O("password", Password, "passphrase", "native-selftest-passphrase-1"));
            check(bk.Bytes.Length > 1000 && System.Text.Encoding.ASCII.GetString(bk.Bytes, 0, 5) == "OBBK2" && bk.FileName.EndsWith(".obbak"), "backup downloads an encrypted .obbak file");
            await api.Post("api/contacts", Json.O("kind", "customer", "name", "Added After Backup"));
            var res = await api.Post("api/restore", Json.O("file_b64", Convert.ToBase64String(bk.Bytes), "passphrase", "native-selftest-passphrase-1", "password", Password, "confirm", "RESTORE"));
            check(res["safety_copy"].Str.StartsWith("pre-restore-"), "restore reports a safety copy");
            check(!await api.RefreshSession(), "restore signs everyone out");
            await api.Post("api/login", Json.O("username", User, "password", Password)); await api.RefreshSession();
            Lists.Bust();
            check(!(await api.Get("api/contacts?kind=customer")).Items.Any(c => c["name"].Str == "Added After Backup"), "restore rolled back changes made after the backup");
            await form.Boot(); await Drive.Settle(500);
            check(api.SignedIn && form.Content.Visible, "app returns to the signed-in state after a restore");

            // ---- several companies: separate folders, separate books, separate passwords; switching back finds everything as it was ----------------------
            var mainCo = Companies.Current; var mainInvoices = (await App.Api.Get("api/documents?kind=invoice")).Count; string mainName = App.Api.Company;
            check(mainInvoices > 0 && mainCo != null, "the first company has its books (" + mainInvoices + " invoices)");
            CompanyInfo second;
            try { Companies.CreateNew("  "); check(false, "an empty company name is refused"); } catch (InvalidOperationException ex) { check(ex.Message.Contains("Type a name"), "an empty company name is refused in plain words"); }
            try { Companies.CreateNew(mainName.ToUpperInvariant()); check(false, "a duplicate company name is refused"); } catch (InvalidOperationException ex) { check(ex.Message.Contains("already have a company"), "a duplicate company name is refused in plain words"); }
            second = Companies.CreateNew("Second: Test/Co?");
            check(Directory.Exists(Companies.FolderOf(second)) && Companies.FolderOf(second) != Companies.FolderOf(mainCo) && Companies.FolderOf(second).Contains("Companies"), "a new company gets its own folder (" + Path.GetFileName(Companies.FolderOf(second)) + ")");
            await form.OpenCompany(second); await Drive.Settle(500);
            check(App.Api.SetupRequired && Engine.CompanyFolder() == Companies.FolderOf(second) && File.Exists(Path.Combine(Companies.FolderOf(second), "ledjoro.db")), "opening it starts its own books and asks to set up an administrator");
            string pw2 = "Second-" + Guid.NewGuid().ToString("N").Substring(0, 14) + "-Quill";
            await App.Api.Post("api/setup", Json.O("token", App.Engine.SetupToken, "company_name", "Second Test Co", "display_name", "Second Owner", "username", "owner2", "password", pw2));
            await App.Api.RefreshSession();
            check(App.Api.Company == "Second Test Co" && (await App.Api.Get("api/documents?kind=invoice")).Count == 0 && (await App.Api.Get("api/contacts?kind=customer")).Count == 0, "the second company starts empty and separate");
            await App.Api.Post("api/contacts", Json.O("kind", "customer", "name", "Only In Second"));
            form.ShowShell(); await Drive.Settle(600);
            check(Companies.List.Any(c => c.Id == second.Id && c.Name == "Second Test Co"), "the company list remembers its real name after sign-in");
            await form.OpenCompany(mainCo); await Drive.Settle(500);
            check(!App.Api.SetupRequired && !App.Api.SignedIn, "switching back asks for the first company's own sign-in");
            try { await App.Api.Post("api/login", Json.O("username", "owner2", "password", pw2)); check(false, "the second company's password does not work in the first"); } catch (ApiException) { check(true, "the second company's password does not work in the first"); }
            await App.Api.Post("api/login", Json.O("username", User, "password", Password)); await App.Api.RefreshSession();
            check((await App.Api.Get("api/documents?kind=invoice")).Count == mainInvoices && !(await App.Api.Get("api/contacts?kind=customer")).Items.Any(c => c["name"].Str == "Only In Second"), "the first company is exactly as it was, with nothing from the second");
            var reg = File.ReadAllText(Path.Combine(Engine.DataDir(), "companies.json"));
            check(reg.Contains("Second Test Co") && reg.Contains(mainName), "companies.json lists both companies");
            Companies.Load(); check(Companies.List.Count == 2, "the list is rebuilt the same way on the next start");
            File.Delete(Path.Combine(Engine.DataDir(), "companies.json")); Companies.Load();
            check(Companies.List.Count == 2, "even with the list file deleted, both company folders are found again");
            var again = Companies.List.First(c => c.Id != "main" && Companies.FolderOf(c) == Companies.FolderOf(second));
            Companies.Remove(again); check(Companies.List.Count == 1 && Directory.Exists(Companies.FolderOf(second)), "removing a company from the list does not delete its files");
            try { Companies.Remove(Companies.List[0]); check(false, "the last company cannot be removed"); } catch (InvalidOperationException ex) { check(ex.Message.Contains("only company"), "the last company cannot be removed, and it says why"); }
            try { Companies.AddExisting(Path.GetTempPath()); check(false, "a folder with no books is refused"); } catch (InvalidOperationException ex) { check(ex.Message.Contains("ledjoro.db"), "a folder with no books is refused, and it says what to look for"); }
            check(Companies.AddExisting(Companies.FolderOf(second)).Name.Length > 0 && Companies.List.Count == 2, "an existing company folder can be added back");
            Companies.Current = mainCo; Engine.CompanyDir = Companies.FolderOf(mainCo);

            // ---- the company picker (shown at start-up when there are several companies, and from the sign-in screen)
            form.ShowPicker(); await Drive.Settle(700); await shot("45-company-picker");
            var pickGrid = Drive.All<Grid>(form.AuthArea).FirstOrDefault();
            check(pickGrid != null && pickGrid.Rows.Count == 2 && Drive.Button(form.AuthArea, "Open") != null && Drive.Button(form.AuthArea, "New Company…") != null && Drive.Button(form.AuthArea, "Add Existing…") != null, "the picker lists both companies with Open, New company and Add existing");
        }

        static async Task<T> WaitFor<T>(Func<T> get, int ms = 8000) where T : class { for (int t = 0; t < ms; t += 50) { var v = get(); if (v != null) return v; await Task.Delay(50); } return null; }
    }

    static class LabelExt
    {
        /// <summary>Clicks a link label the way the mouse would.</summary>
        /// <summary>Click without waiting: buttons that open a modal dialog would otherwise block the test inside the click.</summary>
        public static void Post(this Btn b) { App.Main.BeginInvoke(new Action(() => b.PerformClick())); }
        public static void PerformClickLabel(this Lbl l) { var m = typeof(Control).GetMethod("OnClick", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic); m.Invoke(l, new object[] { EventArgs.Empty }); }
    }
}
