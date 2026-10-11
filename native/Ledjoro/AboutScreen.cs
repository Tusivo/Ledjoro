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
                using (var r = new StreamReader(s, Encoding.UTF8)) return Plain(r.ReadToEnd());
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
            card.Add(new Lbl("Small-business accounting for Windows. One program, your books in a file on your own computer, nothing sent anywhere.", LS.Normal, true), Theme.Px(10));
            string vv = Version.Contains("beta") ? "This is a beta version: it works and has been tested a great deal, but it has not yet been used on many real companies and has not had an independent security review. Keep your own backups, and have an accountant look over your books before relying on them." : "";
            if (vv.Length > 0) card.Add(new Notice(NK.Warn, vv), Theme.Px(6));
            card.Add(new Lbl("Licence: GNU Affero General Public License 3.0 or later. The program is free software; its source code can be read and checked by anyone.", LS.Muted, true), Theme.Px(6));
            card.Add(new Lbl("Your books are kept in: " + Engine.CompanyFolder(), LS.Muted, true), Theme.Px(2));
            card.Add(new Lbl("Ledjoro is not affiliated with, endorsed by, or sponsored by Intuit. QuickBooks is a trademark of Intuit Inc., used only to describe which file formats Ledjoro can read.", LS.Muted, true), Theme.Px(2));
            s.Add(card);

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
            SetBody(page); return Task.FromResult(0);
        }
    }
}
