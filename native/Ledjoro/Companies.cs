using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Ledjoro
{
    /// <summary>One company: its own folder holding its own books, users, passwords and recovery key. Nothing is shared between companies.</summary>
    public sealed class CompanyInfo
    {
        public string Id = "", Name = "", Path = "";   // Path: relative to the Ledjoro-Data folder (so the whole folder can be moved), or absolute
        public long Opened;                            // when it was last opened (unix ms), 0 = never
    }

    /// <summary>
    /// The list of companies, kept in Ledjoro-Data\companies.json. The first company lives in Ledjoro-Data itself (so books made by earlier
    /// versions keep working); others live in Ledjoro-Data\Companies\&lt;name&gt;. Folders found in Companies\ are picked up automatically, so copying a
    /// company folder in from another computer just works.
    /// </summary>
    public static class Companies
    {
        public static readonly List<CompanyInfo> List = new List<CompanyInfo>();
        public static CompanyInfo Current;
        public static string LastId = "";
        public const string Placeholder = "My company";

        static string RegFile() { return System.IO.Path.Combine(Engine.DataDir(), "companies.json"); }
        public static string FolderOf(CompanyInfo c) { return System.IO.Path.IsPathRooted(c.Path) ? c.Path : System.IO.Path.GetFullPath(System.IO.Path.Combine(Engine.DataDir(), c.Path)); }
        public static bool Exists(CompanyInfo c) { return File.Exists(System.IO.Path.Combine(FolderOf(c), "ledjoro.db")); }
        static bool Same(string a, string b) { return string.Equals(System.IO.Path.GetFullPath(a).TrimEnd('\\'), System.IO.Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase); }

        public static void Load()
        {
            List.Clear();
            try
            {
                if (File.Exists(RegFile()))
                {
                    var j = Json.Parse(File.ReadAllText(RegFile())); LastId = j["last"].Str;
                    foreach (var c in j["companies"].Items) List.Add(new CompanyInfo { Id = c["id"].Str, Name = c["name"].Str, Path = c["path"].Str, Opened = c["opened"].Long });
                    List.RemoveAll(c => c.Id.Length == 0 || c.Path.Length == 0);
                }
            }
            catch (Exception) { List.Clear(); }   // an unreadable list is rebuilt from the folders below, never fatal
            string root = Engine.DataDir();
            if (!List.Any(c => Same(FolderOf(c), root)) && (File.Exists(System.IO.Path.Combine(root, "ledjoro.db")) || List.Count == 0))
                List.Insert(0, new CompanyInfo { Id = "main", Name = Placeholder, Path = "." });
            try   // company folders that were copied in
            {
                string dir = System.IO.Path.Combine(root, "Companies");
                if (Directory.Exists(dir))
                    foreach (var d in Directory.GetDirectories(dir).OrderBy(x => x))
                        if (File.Exists(System.IO.Path.Combine(d, "ledjoro.db")) && !List.Any(c => Same(FolderOf(c), d)))
                            List.Add(new CompanyInfo { Id = NewId(), Name = System.IO.Path.GetFileName(d), Path = System.IO.Path.Combine("Companies", System.IO.Path.GetFileName(d)) });
            }
            catch (Exception) { }
            Current = List.FirstOrDefault(c => c.Id == LastId) ?? List.OrderByDescending(c => c.Opened).FirstOrDefault();
            Save();
        }

        public static void Save()
        {
            try
            {
                Directory.CreateDirectory(Engine.DataDir());
                var arr = List.Select(c => (object)Json.O("id", c.Id, "name", c.Name, "path", c.Path, "opened", c.Opened)).ToList();
                string tmp = RegFile() + ".tmp"; File.WriteAllText(tmp, Json.Stringify(Json.O("last", LastId, "companies", arr)));
                if (File.Exists(RegFile())) File.Delete(RegFile()); File.Move(tmp, RegFile());
            }
            catch (Exception) { }   // the list is a convenience; the companies themselves are never at risk
        }

        static string NewId() { return "c" + Guid.NewGuid().ToString("N").Substring(0, 8); }

        /// <summary>A name that is safe to use as a folder name on Windows.</summary>
        static string FolderName(string name)
        {
            var bad = System.IO.Path.GetInvalidFileNameChars(); var sb = new StringBuilder();
            foreach (char ch in name.Trim()) sb.Append(Array.IndexOf(bad, ch) >= 0 ? '-' : ch);
            string s = sb.ToString().Trim().TrimEnd('.', ' ');
            if (s.Length > 60) s = s.Substring(0, 60).TrimEnd();
            if (s.Length == 0 || new[] { "con", "prn", "aux", "nul", "com1", "lpt1" }.Contains(s.ToLowerInvariant())) s = "Company";
            return s;
        }

        /// <summary>Makes a new, empty company folder and adds it to the list. The books are created the first time it is opened.</summary>
        public static CompanyInfo CreateNew(string name)
        {
            name = (name ?? "").Trim();
            if (name.Length == 0) throw new InvalidOperationException("Type a name for the company first.");
            if (List.Any(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException("You already have a company called \"" + name + "\". Choose a different name so you can tell them apart.");
            string baseName = FolderName(name), rel = System.IO.Path.Combine("Companies", baseName); int n = 2;
            while (Directory.Exists(System.IO.Path.Combine(Engine.DataDir(), rel))) rel = System.IO.Path.Combine("Companies", baseName + " " + n++);
            try { Directory.CreateDirectory(System.IO.Path.Combine(Engine.DataDir(), rel)); }
            catch (Exception e) { throw new InvalidOperationException("Windows would not let Ledjoro make the folder for this company (" + e.Message + "). Check that the Ledjoro folder is not read-only."); }
            var c2 = new CompanyInfo { Id = NewId(), Name = name, Path = rel };
            List.Add(c2); Save(); return c2;
        }

        /// <summary>Adds a company folder (one that contains ledjoro.db) that is not in the list, for example from a USB drive.</summary>
        public static CompanyInfo AddExisting(string folder)
        {
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) throw new InvalidOperationException("That folder could not be found.");
            if (!File.Exists(System.IO.Path.Combine(folder, "ledjoro.db")))
                throw new InvalidOperationException("That folder does not contain a company: there is no ledjoro.db file in it. Choose the folder that holds the company's books (it has a file called ledjoro.db).");
            var have = List.FirstOrDefault(c => Same(FolderOf(c), folder)); if (have != null) return have;
            string full = System.IO.Path.GetFullPath(folder), root = System.IO.Path.GetFullPath(Engine.DataDir()).TrimEnd('\\') + "\\";
            string path = full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full.Substring(root.Length) : full;
            var c2 = new CompanyInfo { Id = NewId(), Name = System.IO.Path.GetFileName(full.TrimEnd('\\')), Path = path };
            List.Add(c2); Save(); return c2;
        }

        /// <summary>Takes a company off the list. Its files are NOT deleted.</summary>
        public static void Remove(CompanyInfo c)
        {
            if (List.Count <= 1) throw new InvalidOperationException("This is your only company, so it cannot be taken off the list.");
            List.Remove(c); if (Current == c) Current = null; Save();
        }

        /// <summary>Called after sign-in: remembers the company's real name and that it was just used.</summary>
        public static void Touch(CompanyInfo c, string displayName)
        {
            if (c == null) return;
            if (!string.IsNullOrWhiteSpace(displayName)) c.Name = displayName.Trim();
            c.Opened = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); LastId = c.Id; Save();
        }

        /// <summary>What to pre-fill as the company name on the first-time setup screen.</summary>
        public static string SuggestedName() { return Current != null && Current.Name != Placeholder ? Current.Name : ""; }
    }

    /// <summary>"Choose a company": shown at start-up when there is more than one, and from the sign-in screen and the company menu.</summary>
    sealed class CompanyPicker : AuthCard
    {
        readonly Grid grid = new Grid(); readonly Notice err = new Notice(NK.Error);

        public CompanyPicker() : base("Choose a company", 700)
        {
            Add(new Lbl("Each company has its own books, its own users and passwords, and its own folder. Nothing is shared between them.", LS.Muted, true));
            grid.Cols(Grid.Col("Company", 2.4f), Grid.Col("Last opened", 1.4f), Grid.Col("Folder", 2.6f));
            foreach (var c in Companies.List)
                grid.Add(c, c.Name, c.Opened > 0 ? Fmt.FromUnixMs(c.Opened).ToLocalTime().ToString("g") : "never", Companies.Exists(c) ? Companies.FolderOf(c) : new GC("folder not found: " + Companies.FolderOf(c), Theme.Bad));
            Add(grid, Theme.Px(8), Theme.Px(30 + 32 * Math.Max(3, Math.Min(7, Companies.List.Count)) + 6));
            Add(err, Theme.Px(6));
            var bar = new Flow { Fill = K.Surface };
            bar.Add(Ui.BA("Open", BK.Primary, OpenSelected));
            bar.Add(Ui.BA("New Company…", BK.Normal, async () => { await App.Main.NewCompany(); }));
            bar.Add(Ui.BA("Add Existing…", BK.Normal, async () => { await App.Main.AddExistingCompany(); }));
            bar.Add(Ui.B("Remove from List", BK.Normal, RemoveSelected, true));
            Add(bar, Theme.Px(4));
            grid.Activated += tag => Ui.Run(OpenSelected);
            var cur = Companies.Current; if (cur != null) for (int i = 0; i < grid.Rows.Count; i++) if (ReferenceEquals(grid.Rows[i].Cells[0].Tag, cur)) { grid.Rows[i].Selected = true; grid.CurrentCell = grid.Rows[i].Cells[0]; break; }
        }

        async Task OpenSelected()
        {
            err.Reset(); var c = grid.SelectedTag as CompanyInfo;
            if (c == null) { err.Set("Click a company in the list first, then press Open."); return; }
            if (!Companies.Exists(c) && c.Opened > 0) { err.Set("Ledjoro cannot find this company's folder (" + Companies.FolderOf(c) + "). If it is on a USB drive, plug it in; if it was moved, use Add existing to point to its new place, and Remove from list to forget this entry."); return; }
            await App.Main.OpenCompany(c);
        }

        void RemoveSelected()
        {
            err.Reset(); var c = grid.SelectedTag as CompanyInfo;
            if (c == null) { err.Set("Click a company in the list first."); return; }
            if (!Msg.Confirm("Take \"" + c.Name + "\" off the list?", "Only the list entry is removed. The company's files are NOT deleted: they stay in " + Companies.FolderOf(c) + " and you can bring the company back with Add existing.", "Remove from List", false)) return;
            try { Companies.Remove(c); App.Main.ShowAuth(new CompanyPicker()); }
            catch (Exception e) { err.Set(e.Message); }
        }
    }
}
