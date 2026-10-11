using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Ledjoro
{
    /// <summary>The few web addresses Ledjoro can open (only when a button is clicked), and the text people can paste into a problem report.</summary>
    static class Links
    {
        public const string Issues = "https://github.com/Tusivo/Ledjoro/issues", NewIssue = "https://github.com/Tusivo/Ledjoro/issues/new/choose", Donate = "https://buymeacoffee.com/tusivo";

        public static void Open(string url)
        {
            if (App.SelfTesting) return;
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception) { Msg.Info("The page could not be opened", "Windows did not open your web browser. The address is " + url, true); }
        }

        /// <summary>Nothing about the books: only what the program and the computer are, so a problem report can say what was running.</summary>
        public static string VersionDetails()
        {
            return "Ledjoro " + AboutScreen.Version + "\nWindows " + Environment.OSVersion.Version + (Environment.Is64BitOperatingSystem ? " (64-bit)" : " (32-bit)")
                + "\nTheme: " + Theme.Name + ", text size: " + Theme.Text + "\n.NET Framework " + Environment.Version;
        }
    }

    /// <summary>About: the version, what the program is, where its data lives, and a short excerpt of the README (the whole README one click away).</summary>
    sealed class AboutScreen : Screen
    {
        public static string Version
        {
            get
            {
                var a = (System.Reflection.AssemblyInformationalVersionAttribute)Attribute.GetCustomAttribute(typeof(AboutScreen).Assembly, typeof(System.Reflection.AssemblyInformationalVersionAttribute));
                string v = a != null ? a.InformationalVersion : typeof(AboutScreen).Assembly.GetName().Version.ToString();
                int plus = v.IndexOf('+'); return plus > 0 ? v.Substring(0, plus) : v;
            }
        }

        /// <summary>The README that was built into the program (plain text, markdown marks removed).</summary>
        public static string ReadmeText()
        {
            using (var s = typeof(AboutScreen).Assembly.GetManifestResourceStream("readme.md"))
            {
                if (s == null) return "";
                using (var r = new StreamReader(s, Encoding.UTF8)) { string md = r.ReadToEnd(); int k = md.IndexOf("## More about how it works"); return Plain(k > 0 ? md.Substring(k) : md); }   // the download steps are for people who do not have the program yet
            }
        }

        /// <summary>Markdown to readable text: no heading marks, bold or code marks, link addresses, quote arrows or tables.</summary>
        public static string Plain(string md)
        {
            var sb = new StringBuilder();
            foreach (var raw in md.Replace("\r\n", "\n").Split('\n'))
            {
                string line = raw.TrimEnd();
                if (line.TrimStart().StartsWith("|")) continue;                       // tables are not readable as plain text
                line = Regex.Replace(line, @"^\s{0,3}#{1,6}\s*", "");                 // headings
                line = Regex.Replace(line, @"^\s*>\s?", "");                          // quotes
                line = Regex.Replace(line, @"\[([^\]]+)\]\([^)]+\)", "$1");           // links keep their words, lose their addresses
                line = line.Replace("**", "").Replace("`", "");
                line = Regex.Replace(line, @"^(\s*)[-*]\s+", "$1•  ");           // bullets
                sb.AppendLine(line);
            }
            return Regex.Replace(sb.ToString(), "\n{3,}", "\n\n").Trim();
        }

        /// <summary>The start of the text, cut at the end of a paragraph near <paramref name="max"/> characters, so it never stops mid-sentence.</summary>
        public static string Excerpt(string text, int max)
        {
            if (text.Length <= max) return text;
            var paras = text.Split(new[] { "\n\n" }, StringSplitOptions.None); var sb = new StringBuilder();
            foreach (var p in paras) { if (sb.Length > 0 && sb.Length + p.Length > max) break; if (sb.Length > 0) sb.Append("\n\n"); sb.Append(p); if (sb.Length >= max) break; }
            string cut = sb.ToString();
            if (cut.Length > max + 400) { cut = cut.Substring(0, max); int sp = cut.LastIndexOf(' '); if (sp > 0) cut = cut.Substring(0, sp); }
            return cut.TrimEnd() + " …";
        }

        /// <summary>Check for Updates: looks at the project's GitHub releases only when clicked, then downloads, checks and installs the newer program.</summary>
        static Card UpdateCard(ScrollPage page)
        {
            var upd = new Card(); upd.Padding = new Padding(Theme.Px(24)); upd.Gap = Theme.Px(10);
            upd.Add(new Lbl("Updates", LS.H3));
            upd.Add(new Lbl("Ledjoro looks for a newer version only when you click the button below. It contacts GitHub (github.com/" + Updater.Repo + ") and tells it nothing about you or your books. A new version is checked against the fingerprint GitHub published for it before it is installed, and your current Ledjoro.exe is kept as a backup in the same folder.", LS.Muted, true), Theme.Px(2));
            var result = new Stack { Fill = Pnl.K.Surface, Gap = Theme.Px(8) };
            Action<Control, Control> show = (a, b) => { result.Controls.Clear(); result.Add(a); if (b != null) result.Add(b); Vx.Relayout(upd); page.PerformLayout(); };
            Btn check = null;
            Func<Updater.Release, Task> install = async rel =>
            {
                string exe = Application.ExecutablePath, backup;
                try
                {
                    var lbl = new Lbl("Downloading Ledjoro " + rel.Version + " … 0%", LS.Normal, true); show(lbl, null);
                    string file = await Updater.Download(rel, exe, new Progress<int>(p => { lbl.Text = "Downloading Ledjoro " + rel.Version + " … " + p + "%"; }));
                    backup = Updater.Install(file, exe, Version);
                }
                catch (Exception ex) { show(new Notice(NK.Error, Updater.Friendly(ex)), null); return; }
                show(new Notice(NK.Ok, "Ledjoro " + rel.Version + " is installed. Your previous version was kept as " + Path.GetFileName(backup) + " in the same folder. Close and reopen Ledjoro to start using the new version."), null);
                if (Msg.Confirm("Ledjoro was updated", "Version " + rel.Version + " is ready. Ledjoro will close and open again, and you will sign in again. Your books are not affected. Your previous version is kept as " + Path.GetFileName(backup) + " in the same folder.", "Close and Reopen"))
                {
                    try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, "--after " + System.Diagnostics.Process.GetCurrentProcess().Id) { UseShellExecute = false }); }
                    catch (Exception) { Msg.Info("Ledjoro could not open itself again", "The update is installed. Close Ledjoro and open it yourself.", true); return; }
                    App.Main.Close();
                }
            };
            check = Ui.BA("Check for Updates", BK.Normal, async () =>
            {
                check.Enabled = false; show(new Lbl("Looking at GitHub …", LS.Muted), null);
                try
                {
                    var rel = await Updater.Check(Version);
                    if (rel == null) { show(new Notice(NK.Ok, "You have the newest version (" + Version + ")."), null); return; }
                    string notes = rel.Notes.Trim(); if (notes.Length > 600) notes = notes.Substring(0, 600).TrimEnd() + " …";
                    var go = Ui.BA("Download and Install " + rel.Version, BK.Primary, () => install(rel));
                    show(new Notice(NK.Warn, "Version " + rel.Version + " is available (you have " + Version + ")." + (notes.Length > 0 ? "\n\n" + AboutScreen.Plain(notes) : "")), go);
                }
                catch (Exception ex) { show(new Notice(NK.Error, Updater.Friendly(ex)), null); }
                finally { check.Enabled = true; }
            });
            var row = new Flow { Fill = Pnl.K.Surface, HGap = Theme.Px(12) }; row.Add(check); upd.Add(row, Theme.Px(4));
            upd.Add(result, Theme.Px(4));
            return upd;
        }

        /// <summary>Help and donations: quiet, always here, never a popup. Nothing is sent from Ledjoro; the buttons open pages in the browser.</summary>
        static Card SupportCard()
        {
            var c = new Card(); c.Padding = new Padding(Theme.Px(24)); c.Gap = Theme.Px(10);
            c.Add(new Lbl("Support", LS.H3));
            c.Add(new Lbl("Found a problem, or stuck on something? Reports are made on GitHub, where the answer helps the next person too.", LS.Normal, true), Theme.Px(2));
            c.Add(new Notice(NK.Warn, "Reports on GitHub are public. Never paste your books, account numbers, passwords, recovery keys or backup files. Describe what happened instead."), Theme.Px(4));
            var row = new Flow { Fill = Pnl.K.Surface, HGap = Theme.Px(12) };
            row.Add(Ui.B("Report a Problem", BK.Primary, () => Links.Open(Links.NewIssue)));
            row.Add(Ui.B("See Known Issues", BK.Normal, () => Links.Open(Links.Issues)));
            row.Add(Ui.B("Copy Version Details", BK.Normal, () => { try { Clipboard.SetText(Links.VersionDetails()); App.Toast("Version details copied. Paste them into your report."); } catch (Exception) { App.Toast("Could not copy. Select and copy the version above by hand.", true); } }));
            c.Add(row, Theme.Px(4));
            c.Add(new Lbl("Ledjoro is free and made by one person. If it saves you time or money, you can support its development. It changes nothing about the program: every feature is there for everyone.", LS.Normal, true), Theme.Px(14));
            var give = new Flow { Fill = Pnl.K.Surface, HGap = Theme.Px(12) };
            give.Add(Ui.B("Buy Me a Coffee", BK.Normal, () => Links.Open(Links.Donate)));
            c.Add(give, Theme.Px(4));
            return c;
        }

        public override Task Load()
        {
            var page = new ScrollPage(); var s = page.Body;
            s.Add(new Header("About Ledjoro"));

            var card = new Card(); card.Padding = new Padding(Theme.Px(24)); card.Gap = Theme.Px(8);
            var top = new Flow { Fill = Pnl.K.Surface, HGap = Theme.Px(16) };
            top.Add(new PictureBox { Image = App.Logo(Theme.Px(64)), SizeMode = PictureBoxSizeMode.AutoSize, BackColor = Color.Transparent });
            var names = new Stack { Fill = Pnl.K.Surface, Gap = Theme.Px(2) }; names.Tag = Theme.Px(420);
            names.Add(new Lbl("Ledjoro", LS.H1)); names.Add(new Lbl("Version " + Version, LS.Bold));
            top.Add(names); card.Add(top, Theme.Px(2));
            card.Add(new Lbl("Small-business accounting for Windows. One program, your books in a file on your own computer, nothing sent anywhere (apart from the update check below, which only happens when you click it).", LS.Normal, true), Theme.Px(10));
            string vv = Version.Contains("beta") ? "This is a beta version: it works and has been tested a great deal, but it has not yet been used on many real companies and has not had an independent security review. Keep your own backups, and have an accountant look over your books before relying on them." : "";
            if (vv.Length > 0) card.Add(new Notice(NK.Warn, vv), Theme.Px(6));
            card.Add(new Lbl("Licence: GNU Affero General Public License 3.0 or later. The program is free software; its source code can be read and checked by anyone.", LS.Muted, true), Theme.Px(6));
            card.Add(new Lbl("Your books are kept in: " + Engine.CompanyFolder(), LS.Muted, true), Theme.Px(2));
            card.Add(new Lbl("Ledjoro is not affiliated with, endorsed by, or sponsored by Intuit. QuickBooks is a trademark of Intuit Inc., used only to describe which file formats Ledjoro can read.", LS.Muted, true), Theme.Px(2));
            s.Add(card);
            s.Add(UpdateCard(page));

            string full = ReadmeText();
            var read = new Card(); read.Padding = new Padding(Theme.Px(24)); read.Gap = Theme.Px(10);
            read.Add(new Lbl("From the README", LS.H3));
            var shown = new Lbl(full.Length == 0 ? "The README is not included in this build." : Excerpt(full, 1500), LS.Normal, true); read.Add(shown, Theme.Px(6));
            if (full.Length > 0 && Excerpt(full, 1500).Length < full.Length)
            {
                bool open = false; var toggle = new Lbl("Show the whole README", LS.Link) { Underline = true };
                toggle.Click += (a, b) => { open = !open; shown.Text = open ? full : Excerpt(full, 1500); toggle.Text = open ? "Show less" : "Show the whole README"; Vx.Relayout(read); page.PerformLayout(); };
                read.Add(toggle, Theme.Px(4));
            }
            s.Add(read);
            s.Add(SupportCard());
            SetBody(page); return Task.FromResult(0);
        }
    }
}
