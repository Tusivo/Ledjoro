using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Ledjoro
{
    /// <summary>Shown after a restore: everyone is signed out and must sign in with the restored accounts.</summary>
    sealed class RestoreDoneCard : AuthCard
    {
        public RestoreDoneCard(string message, string safety) : base("Restore complete", 600)
        {
            Add(new Notice(NK.Ok, message));
            Add(new Lbl("Everyone has been signed out. Sign in with the username and password that were in use when the backup was made.", LS.Normal, true));
            Add(new Lbl("A safety copy of the data you had before is saved as " + safety + " in your Ledjoro data folder, locked with the same passphrase, in case you need to go back.", LS.Muted, true));
            Add(Ui.B("Go to Sign-in", BK.Primary, () => Ui.Run(App.Main.Boot)), 0);
        }
    }

    sealed class SettingsScreen : Screen
    {
        string tab; ScrollPage page; Stack body; TabBar tabs;
        static readonly Dictionary<string, string> Perm = new Dictionary<string, string> {
            { "sales.write", "Create/edit sales (invoices, customers, items)" }, { "purchases.write", "Create/edit purchases (bills, vendors)" }, { "banking.write", "Categorize bank transactions, rules" },
            { "accounting.write", "Chart of accounts and journal entries" }, { "import", "Import files" }, { "export", "Export data" }, { "settings.manage", "Company settings, backup and restore" },
            { "audit.view", "View audit log" }, { "users.manage", "Manage users and roles" } };

        public SettingsScreen(string tab) { this.tab = tab; }

        public override async Task Load()
        {
            var avail = new List<KeyValuePair<string, string>> { Ui.It("company", "Company") };
            if (A.Can("settings.manage")) avail.Add(Ui.It("data", "Data & Backup"));
            if (A.Can("users.manage")) { avail.Add(Ui.It("users", "Users")); avail.Add(Ui.It("roles", "Roles")); }
            if (A.Can("audit.view")) avail.Add(Ui.It("audit", "Audit & Integrity"));
            avail.Add(Ui.It("account", "My Account"));
            if (!avail.Any(a => a.Key == tab)) tab = "company";
            page = new ScrollPage(); page.Body.Add(new Header("Settings"));
            tabs = page.Body.Add(new TabBar()); tabs.Set(avail, tab); tabs.Changed += k => Nav("#/settings/" + k);
            body = new Stack { Gap = Theme.Px(14) }; page.Body.Add(body);
            SetBody(page); await Fill();
        }

        async Task Fill()
        {
            body.ClearItems();
            switch (tab)
            {
                case "company": await Company(); break;
                case "data": Data(); break;
                case "users": await Users(); break;
                case "roles": await Roles(); break;
                case "audit": await Audit(); break;
                default: Account(); break;
            }
            body.PerformLayout(); page.PerformLayout(); Theme.Refresh(body);
        }

        // ---------------------------------------------------------------- company
        async Task Company()
        {
            var s = await A.Get("api/settings");
            var name = new Input("Company name") { Text = s["company_name"].Str, Width = Theme.Px(420) }; var close = new DateInput(s["closing_date"].Str, 200); close.Cue = "none";
            var err = new Notice(NK.Error); var card = new Card();
            card.Add(Ui.F("Company name", name, 420)); card.Add(Ui.F("Books closed through (no entries on or before this date)", close, 420)); card.Add(err);
            if (A.Can("settings.manage")) card.Add(Ui.BA("Save", BK.Primary, async () =>
            {
                err.Reset();
                try
                {
                    if (close.Text.Trim().Length > 0 && !close.Valid) throw new ApiException(0, Say.Date("closing date") + " Or leave it empty if the books are not closed.", Jv.Null);
                    await A.Put("api/settings", Json.O("company_name", name.Text, "closing_date", close.Text.Trim())); A.Company = name.Text; App.Toast("Saved");
                }
                catch (ApiException e) { err.Set(e.Message); }
            }), 0);
            else card.Add(new Lbl("Only administrators can change these.", LS.Muted));
            body.Add(card);
        }

        // ---------------------------------------------------------------- automatic backups
        public Check AutoOn; public Input AutoFolder; public Combo AutoEvery, AutoKeep, AutoMode; public Btn AutoSaveBtn, AutoRunBtn; public Lbl AutoStatus;
        void AutoBackupCard()
        {
            var c = new Card(); c.Add(new Lbl("Automatic backups", LS.H3));
            c.Add(new Lbl("While Ledjoro is open it makes a backup for you when one is due, so you never have to remember. Old automatic backups are removed once there are more than you want to keep. Backups you save by hand are never touched.", LS.Muted, true), Theme.Px(6));
            AutoOn = new Check("Back up automatically");
            AutoEvery = Ui.Cb(new[] { Ui.It("1", "Every day"), Ui.It("7", "Every week"), Ui.It("30", "Every month") }, "1", 200);
            AutoKeep = Ui.Cb(new[] { Ui.It("7", "Keep the last 7"), Ui.It("14", "Keep the last 14"), Ui.It("30", "Keep the last 30"), Ui.It("60", "Keep the last 60"), Ui.It("90", "Keep the last 90") }, "14", 200);
            AutoMode = Ui.Cb(new[] { Ui.It("recovery", "Locked with my recovery key (recommended)"), Ui.It("none", "Not locked (anyone can read them)") }, "recovery", 400);
            AutoFolder = new Input("Backups", 400);
            var browse = Ui.B("Choose Folder…", BK.Normal, () =>
            {
                using (var d = new FolderBrowserDialog { Description = "Choose where automatic backups are saved (a USB drive or a folder that syncs online keeps them safe if this computer is lost)." })
                    if (d.ShowDialog(App.Main) == DialogResult.OK) AutoFolder.Text = d.SelectedPath;
            });
            AutoStatus = new Lbl("", LS.Muted, true); var err = new Notice(NK.Error); var ok = new Notice(NK.Ok);
            c.Add(AutoOn, Theme.Px(10));
            var r1 = new Flow { Fill = Pnl.K.Surface }; r1.Add(Ui.F("How often", AutoEvery, 200)); r1.Add(Ui.F("How many to keep", AutoKeep, 200)); c.Add(r1, Theme.Px(8));
            c.Add(Ui.F("Protection", AutoMode, 400), Theme.Px(8));
            var r2 = new Flow { Fill = Pnl.K.Surface }; r2.Add(Ui.F("Save to this folder", AutoFolder, 400)); r2.Add(Ui.F(" ", browse, 150)); c.Add(r2, Theme.Px(8)); c.Add(new Lbl("A plain name such as Backups is a folder inside this company's folder, so it moves with the program. For a USB drive or a synced folder, choose it with the button.", LS.Muted, true), Theme.Px(4));
            c.Add(AutoStatus, Theme.Px(8)); c.Add(err, Theme.Px(6)); c.Add(ok, Theme.Px(6));
            Func<Jv, Task> show = async st =>
            {
                AutoOn.Checked = st["enabled"].Bool; AutoEvery.Value = st["every_days"].Str; AutoKeep.Value = st["keep"].Str; AutoMode.Value = st["mode"].Str; AutoFolder.Text = st["folder"].Str;
                string last = st["last_auto"].Str, e = st["last_error"].Str;
                AutoStatus.Text = (e.Length > 0 ? "The last attempt failed: " + e + "\n" : "") + (last.Length == 0 ? "No automatic backup has been made yet." : "Last automatic backup: " + Fmt.Age(DateTime.Parse(last, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal)) + ".") + "  Saved in: " + st["folder_full"].Str;
                AutoStatus.Tint = e.Length > 0 ? Theme.Bad : (System.Drawing.Color?)null; Vx.Relayout(c); await Task.FromResult(0);
            };
            Func<Task<Jv>> save = () => A.Post("api/backup/auto", Json.O("enabled", AutoOn.Checked, "every_days", int.Parse(AutoEvery.Value), "keep", int.Parse(AutoKeep.Value), "mode", AutoMode.Value, "folder", AutoFolder.Text.Trim().Length == 0 ? "Backups" : AutoFolder.Text.Trim()));
            var row = new Flow();
            AutoSaveBtn = Ui.BA("Save These Settings", BK.Primary, async () => { err.Reset(); ok.Reset(); try { await show(await save()); ok.Set(AutoOn.Checked ? "Automatic backups are on." : "Automatic backups are off."); } catch (ApiException e) { err.Set(e.Message); } });
            AutoRunBtn = Ui.BA("Back Up Now", BK.Normal, async () =>
            {
                err.Reset(); ok.Reset();
                try { await show(await save()); var r = await A.Post("api/backup/auto/run"); await show(await A.Get("api/backup/auto")); ok.Set("Saved " + r["file"].Str + " in " + r["folder"].Str + "."); }
                catch (ApiException e) { err.Set(e.Message); await show(await A.Get("api/backup/auto")); }
            });
            row.Add(AutoSaveBtn); row.Add(AutoRunBtn); c.Add(row, 0);
            body.Add(c);
            Ui.Run(async () => await show(await A.Get("api/backup/auto")));
        }

        // ---------------------------------------------------------------- data & backup
        void Data()
        {
            var b = new Card();
            b.Add(new Lbl("Back up my books", LS.H3));
            var status = new Lbl("", LS.Normal, true); b.Add(status, Theme.Px(6));
            b.Add(new Lbl("Saves one file containing all of your books, checked for damage first. Keep it somewhere other than this computer (a USB drive, or a folder that syncs online) so it still exists if this computer is lost.", LS.Muted, true), Theme.Px(10));
            var how = Ui.Cb(new[] { Ui.It("recovery", "Locked with my recovery key (recommended)"), Ui.It("passphrase", "Locked with a passphrase I choose"), Ui.It("none", "Not locked (anyone can read it)") }, "recovery", 470);
            b.Add(Ui.F("How should this backup be protected?", how, 470), Theme.Px(6));
            var help = new Lbl("", LS.Muted, true); b.Add(help, Theme.Px(8));
            var warn = new Notice(NK.Warn, ""); b.Add(warn, Theme.Px(8));
            var pw = new Input("") { Password = true }; var p1 = new Input("") { Password = true }; var p2 = new Input("") { Password = true }; var rk = new Input("XXXXX-XXXXX-XXXXX-XXXXX", 360).AsRecoveryKey();
            var err = new Notice(NK.Error); var ok = new Notice(NK.Ok);
            var passFlow = new Flow { Fill = Pnl.K.Surface }; passFlow.Add(Ui.F("Passphrase (at least 12 characters)", p1)); passFlow.Add(Ui.F("Type it again", p2)); b.Add(passFlow, Theme.Px(8));
            var rkField = Ui.F("Your current recovery key (typed once, to link it to your backups)", rk, 460); b.Add(rkField, Theme.Px(8));
            b.Add(Ui.F("Your current password (asked because a backup holds every record)", pw, 460), Theme.Px(8));
            b.Add(err, Theme.Px(6)); b.Add(ok, Theme.Px(6));
            bool needLink = false;
            Action refresh = () =>
            {
                string m = how.Value;
                passFlow.Visible = m == "passphrase"; rkField.Visible = needLink && m != "none";
                warn.Set(m == "none" ? "This backup will NOT be locked. Anyone who finds or copies the file can read every record in your books, including your customers and vendors. Only choose this for a file you will keep somewhere private." : "");
                help.Text = m == "recovery"
                    ? "Your recovery key (the one shown when your account was made: XXXXX-XXXXX-XXXXX-XXXXX) opens this backup, so there is no extra password to remember. Keep that key safe. A backup opens with the recovery key that was current when it was made."
                    : m == "passphrase" ? "You choose a passphrase, and your recovery key ALSO opens the backup, so forgetting the passphrase will not lock you out of your own backup."
                    : "Choose this only if you are sure. A locked backup is much safer if the file is ever lost, emailed or left on a shared computer.";
                Vx.Relayout(b);
            };
            how.Changed += (s2, e2) => refresh();
            Ui.Run(async () =>
            {
                var st = await A.Get("api/backup/status"); string lb = st["last_backup"].Str; needLink = !st["recovery_linked_me"].Bool;
                status.Text = lb.Length == 0 ? "You have never made a backup from this app." : "Last backup: " + Fmt.Age(DateTime.Parse(lb, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal));
                if (lb.Length == 0) status.Tint = Theme.Bad;
                refresh();
            });
            refresh();
            b.Add(Ui.BA("Save Backup", BK.Primary, async () =>
            {
                err.Reset(); ok.Reset(); string m = how.Value;
                if (pw.Text.Length == 0) { err.Set("Type your current password first. It is asked for because a backup contains every record in your books."); return; }
                if (m == "passphrase" && p1.Text != p2.Text) { err.Set("The two passphrases are not the same. Type them again so they match exactly."); return; }
                if (m == "recovery" && needLink && rk.Text.Trim().Length == 0) { err.Set("Type your current recovery key once so Ledjoro can link it to your backups (it looks like XXXXX-XXXXX-XXXXX-XXXXX). If you cannot find it, make a new one under My account."); return; }
                try
                {
                    var body2 = Json.O("password", pw.Text, "mode", m); if (m == "passphrase") body2["passphrase"] = p1.Text; if (needLink && m != "none" && rk.Text.Trim().Length > 0) body2["recovery_key"] = rk.Text.Trim();
                    var file = await A.PostForFile("api/backup", body2);
                    using (var d = new SaveFileDialog { FileName = file.FileName, Title = "Save Backup", Filter = "Ledjoro backup|*.obbak" })
                    {
                        if (d.ShowDialog(App.Main) != DialogResult.OK) { err.Set("The backup was made but you did not choose where to save it, so it was thrown away. Press Save Backup again and pick a folder."); return; }
                        File.WriteAllBytes(d.FileName, file.Bytes); pw.Text = p1.Text = p2.Text = rk.Text = ""; needLink = false; refresh();
                        ok.Set("Backup saved as " + Path.GetFileName(d.FileName) + ". " + (m == "none" ? "It is not locked, so keep it somewhere private." : m == "recovery" ? "Your recovery key opens it, so keep that key safe." : "Your passphrase or your recovery key opens it; keep at least one of them safe."));
                        status.Text = "Last backup: today"; status.Tint = null;
                    }
                }
                catch (ApiException e) { err.Set(e.Message); }
                catch (IOException e) { err.Set(Say.Plain(e) + " Try a different folder, such as Documents or a USB drive."); }
                catch (UnauthorizedAccessException) { err.Set("Windows does not allow Ledjoro to save into that folder. Choose a folder you own, such as Documents."); }
            }), 0);
            body.Add(b);

            AutoBackupCard();

            // ---- restore
            var r = new Card(); r.Add(new Lbl("Restore from a backup", LS.H3));
            r.Add(new Notice(NK.Warn, "This replaces everything. All current books, users and settings are swapped for the contents of the backup, and everyone is signed out. Your current data is first saved as a safety copy so you can go back."), Theme.Px(10));
            string path = ""; bool locked = true; var fileLbl = new Lbl("No file chosen", LS.Muted); var pick = new Flow { Fill = Pnl.K.Surface }; var kind = new Lbl("", LS.Muted, true);
            var bp = new Input("") { Password = true }; var rpw = new Input("") { Password = true }; var word = new Input("RESTORE") { Width = Theme.Px(150) }; var rerr = new Notice(NK.Error);
            var secretField = Ui.F("Passphrase or recovery key", bp, 420);
            pick.Add(Ui.B("Choose Backup File…", BK.Normal, () =>
            {
                using (var d = new OpenFileDialog { Title = "Choose a backup", Filter = "Ledjoro backup|*.obbak|All files|*.*" })
                {
                    if (d.ShowDialog(App.Main) != DialogResult.OK) return;
                    path = d.FileName; fileLbl.Text = Path.GetFileName(path); fileLbl.Tint = null; rerr.Reset();
                    try
                    {   // read just the start of the file to see whether it needs a passphrase or recovery key
                        var head = new byte[6]; int n; using (var fs = File.OpenRead(path)) n = fs.Read(head, 0, 6);
                        string magic = System.Text.Encoding.ASCII.GetString(head, 0, Math.Min(5, n));
                        if (magic == "OBBK2" && n == 6 && head[5] == 0) { locked = false; kind.Text = "This backup is not locked, so no passphrase or recovery key is needed."; }
                        else if (magic == "OBBK2" || magic == "OBBK1") { locked = true; kind.Text = "This backup is locked. Type the passphrase it was made with, or the company's recovery key."; }
                        else { locked = true; kind.Text = ""; rerr.Set("That file does not look like a Ledjoro backup. Ledjoro backups end in .obbak and are saved from this screen. Check you picked the right file."); }
                    }
                    catch (Exception e) { rerr.Set(Say.Plain(e)); }
                    secretField.Visible = locked; Vx.Relayout(r);
                }
            })); pick.Add(fileLbl);
            r.Add(pick, Theme.Px(10)); r.Add(kind, Theme.Px(8));
            var rf = new Flow { Fill = Pnl.K.Surface }; rf.Add(secretField); rf.Add(Ui.F("Your current password", rpw)); rf.Add(Ui.F("Type RESTORE to confirm", word, 170)); r.Add(rf, Theme.Px(8)); r.Add(rerr, Theme.Px(6));
            var go = new Btn("Restore From Backup", BK.Danger) { Enabled = false }; word.Changed += (a, e) => go.Enabled = word.Text.Trim() == "RESTORE";
            go.Click += (a, e) => Ui.Run(async () =>
            {
                rerr.Reset();
                if (path.Length == 0) { rerr.Set("Choose a backup file first (the files ending in .obbak)."); return; }
                var fi = new FileInfo(path); if (fi.Length > 48L * 1024 * 1024) { rerr.Set("That file is " + (fi.Length / 1048576) + " MB, which is more than the 48 MB Ledjoro can restore in one go. It is probably not a Ledjoro backup."); return; }
                if (locked && bp.Text.Trim().Length == 0) { rerr.Set("This backup is locked. Type the passphrase it was made with, or the company's recovery key."); return; }
                if (rpw.Text.Length == 0) { rerr.Set("Type your current password. It is asked for because a restore replaces every record in the books."); return; }
                if (!Msg.Confirm("Replace all current data?", "This replaces ALL current data (books, users and settings) with the contents of the backup, and signs everyone out. A safety copy of the current data is saved first.", "Restore", true)) return;
                go.Enabled = false; go.Text = "Restoring…";
                try
                {
                    var res = await A.Post("api/restore", Json.O("file_b64", Convert.ToBase64String(File.ReadAllBytes(path)), "secret", bp.Text, "password", rpw.Text, "confirm", word.Text.Trim()));
                    Lists.Bust(); await A.RefreshSession();
                    App.Main.ShowAuth(new RestoreDoneCard("Restored “" + res["company"].Str + "”: " + res["entries"].Long.ToString("N0") + " journal entries and " + res["users"].Str + " user account(s).", res["safety_copy"].Str));
                }
                catch (ApiException ex) { rerr.Set(ex.Message); go.Text = "Restore From Backup"; go.Enabled = word.Text.Trim() == "RESTORE"; }
            });
            r.Add(go, 0); body.Add(r);
        }

        // ---------------------------------------------------------------- users & roles
        async Task Users()
        {
            var users = await A.Get("api/users"); var roles = await Lists.Roles();
            body.Add(Ui.B("Add User", BK.Primary, () => UserDialog(roles, null)));
            var g = new Grid(); g.Cols(Grid.Col("Username", 1.6f), Grid.Col("Name", 2f), Grid.Col("Role", 1.4f), Grid.Col("Status", 1.6f));
            foreach (var u in users.Items) g.Add(u, u["username"].Str, u["display_name"].Str, u["role"].Str, !u["active"].Bool ? new GC("disabled", Theme.Bad) : u["must_change_password"].Bool ? new GC("must change password", Theme.Warn) : new GC("active", Theme.Good));
            g.Activated += tag => UserDialog(roles, tag as Jv); body.Add(g, null, Theme.Px(380));
        }

        void UserDialog(Jv roles, Jv u)
        {
            var dlg = new Dlg(u == null ? "Add User" : "Edit " + u["username"].Str, 580);
            var user = new Input("Username") { Text = u == null ? "" : u["username"].Str, ReadOnly = u != null }; var name = new Input("Display name") { Text = u == null ? "" : u["display_name"].Str };
            var role = Ui.Cb(roles.Items.Select(r => Ui.It(r["id"].Str, r["name"].Str)), u == null ? roles[0]["id"].Str : u["role_id"].Str); var pw = new Input("") { Password = true }; var active = new Check("Active", u == null || u["active"].Bool);
            var f = new Flow { Fill = Pnl.K.Surface }; f.Add(Ui.F("Username", user)); f.Add(Ui.F("Display name", name)); f.Add(Ui.F("Role", role)); f.Add(Ui.F(u == null ? "Temporary password" : "Reset password (optional)", pw));
            dlg.Body.Add(f); if (u != null) dlg.Body.Add(active); dlg.Body.Add(new Lbl("The user must choose a new password at first sign-in.", LS.Muted, true));
            dlg.Cancel();
            dlg.Primary("Save", async () =>
            {
                if (u == null) await A.Post("api/users", Json.O("username", user.Text, "display_name", name.Text, "role_id", long.Parse(role.Value), "password", pw.Text));
                else { var body2 = Json.O("display_name", name.Text, "role_id", long.Parse(role.Value), "active", active.Checked); if (pw.Text.Length > 0) body2["password"] = pw.Text; await A.Patch("api/users/" + u["id"].Str, body2); }
                Lists.Bust(); App.Toast("Saved"); await Fill(); return true;
            });
            dlg.Run();
        }

        async Task Roles()
        {
            var roles = await Lists.Roles();
            body.Add(Ui.B("New Custom Role", BK.Primary, () => RoleDialog(null)));
            var g = new Grid(); g.Cols(Grid.Col("Role", 1.5f), Grid.Col("Permissions", 5f), Grid.Col("", 1f));
            foreach (var r in roles.Items) g.Add(r, r["name"].Str, string.Join(", ", r["permissions"].Items.Select(p => p.Str)), r["builtin"].Bool ? "built-in" : "");
            g.Activated += tag => { var r = tag as Jv; if (r["builtin"].Bool) App.Toast("Built-in roles are fixed so everyone agrees what they mean. To change what people can do, make a new custom role."); else RoleDialog(r); };
            body.Add(g, null, Theme.Px(300));
        }

        void RoleDialog(Jv r)
        {
            var dlg = new Dlg(r == null ? "New Custom Role" : "Edit " + r["name"].Str, 600);
            var name = new Input("Role name") { Text = r == null ? "" : r["name"].Str, ReadOnly = r != null }; dlg.Body.Add(Ui.F("Role name", name));
            dlg.Body.Add(new Lbl("Everyone can view data; tick what else this role may do.", LS.Muted, true));
            var boxes = Perm.Select(p => new { p.Key, Box = new Check(p.Value, r != null && r["permissions"].Items.Any(x => x.Str == p.Key)) }).ToList();
            foreach (var b in boxes) dlg.Body.Add(b.Box, Theme.Px(4));
            dlg.Cancel();
            dlg.Primary("Save", async () =>
            {
                var perms = new List<string> { "view" }; perms.AddRange(boxes.Where(b => b.Box.Checked).Select(b => b.Key));
                if (r == null) await A.Post("api/roles", Json.O("name", name.Text, "permissions", perms)); else await A.Patch("api/roles/" + r["id"].Str, Json.O("permissions", perms));
                Lists.Bust(); App.Toast("Saved"); await Fill(); return true;
            });
            dlg.Run();
        }

        // ---------------------------------------------------------------- audit
        async Task Audit()
        {
            var rows = await A.Get("api/audit?limit=200"); var res = new Notice(NK.Ok);
            body.Add(Ui.BA("Verify Integrity", BK.Normal, async () =>
            {
                var v = await A.Get("api/audit/verify"); bool ok = v["audit_chain"]["ok"].Bool && v["ledger"]["ok"].Bool; res.Kind = ok ? NK.Ok : NK.Error;
                res.Set((v["audit_chain"]["ok"].Bool ? "Audit chain intact (" + v["audit_chain"]["checked"].Str + " records). " : "AUDIT CHAIN BROKEN at record " + v["audit_chain"]["brokenAtId"].Str + ". ") + (v["ledger"]["ok"].Bool ? "Every journal entry balances." : "Ledger integrity problem detected."));
            }));
            body.Add(res);
            var g = new Grid(); g.Cols(Grid.Col("When", 1.7f), Grid.Col("User", 1.2f), Grid.Col("Action", 1.6f), Grid.Col("Detail", 3.4f));
            foreach (var a in rows.Items) g.Add(null, Fmt.FromUnixMs(a["ts"].Long).ToLocalTime().ToString("g"), a["username"].Str, a["action"].Str, a["detail"].Str);
            body.Add(g, null, Theme.Px(420));
        }

        // ---------------------------------------------------------------- my account
        void Account()
        {
            var c = new Card(); c.Add(new Lbl("Change my password", LS.H3)); c.Add(new ChangePasswordForm(false, () => { }), 0); body.Add(c);
            var k = new Card(); k.Add(new Lbl("Recovery key", LS.H3));
            k.Add(new Lbl("If you ever lose your password, your recovery key lets you set a new one from the sign-in screen. Generating a new key makes the old one stop working.", LS.Muted, true), Theme.Px(8));
            var pw = new Input("") { Password = true }; var err = new Notice(NK.Error); k.Add(Ui.F("Current password", pw), Theme.Px(8)); k.Add(err, Theme.Px(6));
            k.Add(Ui.BA("Generate a New Recovery Key", BK.Normal, async () =>
            {
                err.Reset();
                try
                {
                    var r = await A.Post("api/recovery-key", Json.O("password", pw.Text)); pw.Text = "";
                    var dlg = new Dlg("Your new recovery key", 600); dlg.Body.Add(new Notice(NK.Red, AuthText.Save));
                    var rows = new SheetRows(); rows.Rows.Add(new SheetRows.Row { Label = "Company", Value = A.Company }); rows.Rows.Add(new SheetRows.Row { Label = "Username", Value = A.User["username"].Str, Mono = true }); rows.Rows.Add(new SheetRows.Row { Label = "Recovery key", Value = r["recovery_key"].Str, Mono = true, Big = true, Secret = true });
                    dlg.Body.Add(rows, null, Theme.Px(38 * 2 + 44 + 2)); var qrb = new QrBox(RecoveryQr.Build(A.Company, A.User["username"].Str, r["recovery_key"].Str)); dlg.Body.Add(qrb, null, Theme.Px(250)); var reveal = new Btn("Show Key", BK.Normal, null, true); reveal.Click += (a, b) => { rows.Reveal = !rows.Reveal; qrb.Revealed = rows.Reveal; qrb.Invalidate(); reveal.Text = rows.Reveal ? "Hide key" : "Show key"; rows.Invalidate(); };
                    var bar = new Flow { Fill = Pnl.K.Surface }; bar.Add(Ui.B("Print this Page", BK.Normal, () => SheetScreen.PrintSheet("New recovery key", A.Company, A.User["username"].Str, null, r["recovery_key"].Str, null))); bar.Add(reveal); dlg.Body.Add(bar);
                    dlg.Primary("Done", () => Task.FromResult(true)); dlg.Run();
                }
                catch (ApiException e) { err.Set(e.Message); }
            }), 0);
            body.Add(k);
            body.Add(new PasskeyCard());
        }
    }
}
