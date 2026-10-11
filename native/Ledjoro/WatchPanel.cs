using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Ledjoro
{
    /// <summary>
    /// Bank statement files found in the watched download folder. On Import &amp; Convert it also holds the settings
    /// (which folder, on or off); on Banking it only appears when there is something waiting.
    /// </summary>
    sealed class WatchPanel : Stack
    {
        readonly bool settings; Jv st, accts; string shownKey = ""; System.Windows.Forms.Timer timer; bool busy;
        Check enable; Input folder; Lbl state; Stack list; Card setup;
        public event Action Imported;
        public Jv Status { get { return st; } }
        public Stack FileList { get { return list; } }

        public WatchPanel(bool withSettings)
        {
            settings = withSettings; Gap = Theme.Px(14); Fill = K.Bg;
            Visible = withSettings;
        }

        public async Task Load()
        {
            accts = await Lists.Accounts();
            st = await App.Api.Get("api/watch");
            Build();
            timer = new System.Windows.Forms.Timer { Interval = 5000 };
            timer.Tick += async (a, b) => { if (busy || IsDisposed) return; try { var n = await App.Api.Get("api/watch"); st = n; Draw(); } catch (Exception) { } };
            timer.Start();
        }
        protected override void Dispose(bool disposing) { if (disposing && timer != null) { timer.Stop(); timer.Dispose(); } base.Dispose(disposing); }

        void Build()
        {
            if (settings)
            {
                setup = new Card();
                setup.Add(new Lbl("Watch a Folder for Bank Downloads", LS.H3), Theme.Px(2));
                setup.Add(new Lbl("Download your bank statements as usual (the QuickBooks QBO, Quicken QFX or OFX option on your bank's site) and save them into one folder. Ledjoro notices new ones there, shows what is in them, and imports them when you press Import. Files are never moved, changed or deleted. CSV downloads are not picked up automatically, because every bank lays them out differently: use Choose File above for those.", LS.Muted, true), Theme.Px(6));
                setup.Add(new Lbl("We recommend a folder just for bank statements.", LS.Bold), Theme.Px(10));
                setup.Add(new Lbl("A folder of its own keeps your statements together and easy to find, and means Ledjoro only ever looks at files you put there on purpose. Your Downloads folder, by contrast, is full of everything else you have ever saved.", LS.Muted, true), Theme.Px(4));
                var make = new Flow(); var created = new Lbl("", LS.Muted, true);
                make.Add(Ui.BA("Create a Folder for Me", BK.Primary, async () =>
                {
                    string path = DedicatedFolder();
                    try { System.IO.Directory.CreateDirectory(path); }
                    catch (Exception ex) { App.Toast(Say.Plain(ex) + " Choose a folder yourself instead.", true); return; }
                    folder.Text = path; enable.Checked = true; busy = true;
                    try { st = await App.Api.Post("api/watch", Json.O("enabled", true, "folder", path)); Draw(true); created.Text = "Created " + path + ". From now on, save your bank downloads there. When your browser asks where to save a file, choose this folder (or set the browser to ask every time)."; Vx.Relayout(setup); }
                    catch (ApiException e) { App.Toast(e.Message, true); }
                    finally { busy = false; }
                }));
                make.Add(Ui.B("How to Download From Your Bank", BK.Normal, BankHelp.Show));
                setup.Add(make, Theme.Px(8)); setup.Add(created, Theme.Px(6));
                enable = new Check("Look for new bank statements in this folder", st["enabled"].Bool);
                folder = new Input(DedicatedFolder(), 480) { Text = st["folder"].Str };
                var browse = Ui.B("Choose Folder…", BK.Normal, () =>
                {
                    using (var d = new FolderBrowserDialog { Description = "Choose the folder your bank downloads are saved in (usually Downloads)." })
                        if (d.ShowDialog(App.Main) == DialogResult.OK) folder.Text = d.SelectedPath;
                });
                var err = new Notice(NK.Error); state = new Lbl("", LS.Muted, true);
                setup.Add(enable, Theme.Px(10));
                var row = new Flow { Fill = Pnl.K.Surface }; row.Add(Ui.F("Folder", folder, 480)); row.Add(Ui.F(" ", browse, 160)); setup.Add(row, Theme.Px(8));
                setup.Add(state, Theme.Px(6)); setup.Add(err, Theme.Px(6));
                var btns = new Flow();
                btns.Add(Ui.BA("Save Folder", BK.Primary, async () =>
                {
                    err.Reset();
                    if (enable.Checked && IsGeneralFolder(folder.Text) && !Msg.Confirm("Use this folder?", "This folder holds lots of other files, so Ledjoro would be looking through everything in it. A folder just for bank statements is tidier and safer. You can press Create a Folder for Me instead, or use this one anyway.", "Use It Anyway")) return;
                    busy = true;
                    try { st = await App.Api.Post("api/watch", Json.O("enabled", enable.Checked, "folder", folder.Text.Trim())); Draw(true); App.Toast(enable.Checked ? "Watching " + st["folder"].Str : "Watching is off"); }
                    catch (ApiException e) { err.Set(e.Message); }
                    finally { busy = false; }
                }));
                btns.Add(Ui.BA("Check Now", BK.Normal, async () =>
                {
                    err.Reset(); busy = true;
                    try { await App.Api.Post("api/watch/scan"); st = await App.Api.Get("api/watch"); Draw(true); App.Toast(st["files"].Count == 0 ? "No new statements in the folder." : st["files"].Count + " file(s) waiting below."); }
                    catch (ApiException e) { err.Set(e.Message); }
                    finally { busy = false; }
                }));
                btns.Add(Ui.BA("Look at Older Files Too", BK.Normal, async () =>
                {
                    err.Reset(); busy = true;
                    try { await App.Api.Post("api/watch/rescan"); st = await App.Api.Get("api/watch"); Draw(true); App.Toast("Looked at the newest older files in the folder."); }
                    catch (ApiException e) { err.Set(e.Message); }
                    finally { busy = false; }
                }));
                setup.Add(btns, Theme.Px(6));
                Add(setup);
            }
            list = new Stack { Gap = Theme.Px(12), Fill = K.Bg }; Add(list);
            Draw(true);
        }

        /// <summary>The folder Ledjoro suggests for bank downloads: its own folder inside Documents.</summary>
        public static string DedicatedFolder() { return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Ledjoro Bank Statements"); }
        /// <summary>True for folders that hold all sorts of other things (Downloads, Desktop, Documents, the user's own folder).</summary>
        public static bool IsGeneralFolder(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            string p = path.Trim().TrimEnd('\\', '/').ToLowerInvariant();
            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).TrimEnd('\\').ToLowerInvariant();
            string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments).TrimEnd('\\').ToLowerInvariant();
            string desk = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory).TrimEnd('\\').ToLowerInvariant();
            return p == profile || p == docs || p == desk || p == profile + "\\downloads";
        }

        string KeyOf() { return st["enabled"].Bool + "|" + string.Join(",", st["files"].Items.Select(f => f["hash"].Str + f["status"].Str)); }

        void Draw(bool force = false)
        {
            if (state != null)
                state.Text = !st["enabled"].Bool ? "Not watching any folder yet." : "Watching " + st["folder"].Str + ". New statements that arrive from now on appear below.";
            string key = KeyOf();
            if (!force && key == shownKey) return;
            shownKey = key; list.ClearItems();
            var files = st["files"].Items.ToList();
            if (!settings)
            {
                Visible = (st["enabled"].Bool && files.Count > 0) || (!st["enabled"].Bool && !tipDismissed);
                if (!st["enabled"].Bool && !tipDismissed) { list.Add(SetupTip()); }
            }
            foreach (var f in files) list.Add(FileCard(f));
            Vx.Relayout(list); Vx.Relayout(this);
            var p = Parent; if (p != null) p.PerformLayout();
        }

        static bool tipDismissed;
        /// <summary>On the Banking page, until a folder is set up: a short invitation to make one.</summary>
        Control SetupTip()
        {
            var c = new Card(); c.Padding = new Padding(Theme.Px(18), Theme.Px(12), Theme.Px(18), Theme.Px(12)); c.Gap = Theme.Px(6);
            c.Add(new Lbl("Save time: let Ledjoro watch a folder for your bank downloads", LS.Bold));
            c.Add(new Lbl("Make a folder just for bank statements, save each download there, and Ledjoro will offer to import it. It only takes a minute to set up.", LS.Muted, true));
            var row = new Flow();
            row.Add(Ui.B("Set Up a Folder", BK.Primary, () => App.Main.Go("#/import")));
            row.Add(Ui.B("How to Download From Your Bank", BK.Normal, BankHelp.Show));
            row.Add(Ui.B("Not Now", BK.Normal, () => { tipDismissed = true; Draw(true); }));
            c.Add(row, Theme.Px(4)); return c;
        }

        Control FileCard(Jv f)
        {
            var c = new Card(); c.Padding = new Padding(Theme.Px(18), Theme.Px(14), Theme.Px(18), Theme.Px(14)); c.Gap = Theme.Px(8);
            string hash = f["hash"].Str;
            if (f["status"].Str == "unreadable")
            {
                c.Add(new Lbl(f["name"].Str, LS.Bold));
                c.Add(new Notice(NK.Warn, f["error"].Str));
                c.Add(Ui.BA("Ignore", BK.Normal, async () => { await App.Api.Post("api/watch/files/" + hash + "/ignore"); st = await App.Api.Get("api/watch"); Draw(true); }), 0);
                return c;
            }
            string who = f["org"].Str.Length > 0 ? f["org"].Str : "A bank";
            who += f["tail"].Str.Length > 0 ? ", " + (f["kind"].Str == "credit_card" ? "card" : "account") + " ending " + f["tail"].Str : "";
            c.Add(new Lbl("New bank statement: " + who, LS.H3));
            c.Add(new Lbl(f["name"].Str + "   ·   " + f["count"].Str + " transaction" + (f["count"].Int == 1 ? "" : "s") + ", " + Fmt.Show(f["from"].Str) + " to " + Fmt.Show(f["to"].Str), LS.Muted, true));
            var combo = Ui.Cb(Ui.AccountItems(accts, a => a["subtype"].Str == "bank" || a["subtype"].Str == "credit_card", "Choose the account…"), f["suggested_account_id"].IsNull ? "" : f["suggested_account_id"].Str, 340);
            c.Add(Ui.F("Import these transactions into", combo, 340), Theme.Px(4));
            var row = new Flow();
            row.Add(Ui.BA("Import", BK.Primary, async () =>
            {
                if (combo.Value.Length == 0) { App.Toast("Choose which account this statement belongs to first.", true); return; }
                busy = true;
                try
                {
                    var r = await App.Api.Post("api/watch/files/" + hash + "/import", Json.O("bank_account_id", long.Parse(combo.Value)));
                    Lists.Bust(); App.Toast("Imported " + r["bank_added"].Str + " new transaction" + (r["bank_added"].Int == 1 ? "" : "s") + (r["bank_duplicates"].Int > 0 ? " (" + r["bank_duplicates"].Str + " were already there)" : ""));
                    st = await App.Api.Get("api/watch"); Draw(true); var h = Imported; if (h != null) h();
                }
                catch (ApiException e) { App.Toast(e.Message, true); st = await App.Api.Get("api/watch"); Draw(true); }
                finally { busy = false; }
            }));
            row.Add(Ui.BA("Ignore", BK.Normal, async () => { await App.Api.Post("api/watch/files/" + hash + "/ignore"); st = await App.Api.Get("api/watch"); Draw(true); }));
            c.Add(row, Theme.Px(6));
            return c;
        }
    }
}
