using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Windows.Forms;

namespace Ledjoro
{
    public interface IThemed { void OnTheme(); }

    /// <summary>Black theme (default) and light theme. Everything custom-drawn reads these colours at paint time.</summary>
    public static class Theme
    {
        public static bool Dark = false; // true for every theme with a dark background (title bar and scroll bars follow it)
        public static string Name = "light"; // most people prefer a light screen, so that is the default
        public static readonly string[,] Choices = { { "dark", "Dark" }, { "light", "Light" }, { "night", "Night" } };
        public static string Title { get { for (int i = 0; i < Choices.GetLength(0); i++) if (Choices[i, 0] == Name) return Choices[i, 1]; return "Dark"; } }
        public static Color Promo, PromoInk, Spark, SparkSoft, Bg, Surface, Soft, Line, Ink, Muted, Accent, AccentInk, AccentSoft, Good, GoodSoft, Bad, BadSoft, Warn, WarnSoft;
        public static readonly Color AlertRed = Color.FromArgb(224, 0, 39), AlertBorder = Color.FromArgb(255, 107, 129);
        public static float Scale = 1f;
        public static event Action Changed;

        static Color H(string s) { return ColorTranslator.FromHtml(s); }

        static Theme()
        {
            using (var g = Graphics.FromHwnd(IntPtr.Zero)) Scale = g.DpiX / 96f;
            Palette(); Sparks();
        }

        static void Palette()
        {
            PaletteColors(); Sparks();
        }

        static void PaletteColors()
        {
            if (Name == "night")
            {   // inspired by the Night Owl editor theme: deep navy, soft blue accent, mint and sand highlights
                Dark = true;
                Bg = H("#011627"); Surface = H("#0b253a"); Soft = H("#10304a"); Line = H("#1d3b53"); Ink = H("#d6deeb"); Muted = H("#8aa1b4");
                Accent = H("#82aaff"); AccentInk = H("#011627"); AccentSoft = H("#1d3b53");
                Spark = H("#7fdbca"); Promo = H("#ffcb6b"); PromoInk = H("#011627"); Good = H("#addb67"); GoodSoft = H("#0f3a2e"); Bad = H("#ef5350"); BadSoft = H("#3a1f2b"); Warn = H("#ecc48d"); WarnSoft = H("#3a3524");
            }
            else if (Name != "light")
            {
                Dark = true;
                Bg = H("#0a0a0b"); Surface = H("#121214"); Soft = H("#1a1a1d"); Line = H("#2a2a2f"); Ink = H("#f4f4f5"); Muted = H("#a1a1a8");
                Accent = H("#f4f4f5"); AccentInk = H("#0a0a0b"); AccentSoft = H("#26262a");
                Spark = H("#22d3ee"); Promo = H("#22d3ee"); PromoInk = H("#02161b"); Good = H("#5fcf9a"); GoodSoft = H("#112219"); Bad = H("#ff7b72"); BadSoft = H("#2a1412"); Warn = H("#e5b567"); WarnSoft = H("#2a2110");
            }
            else
            {
                Dark = false;
                Bg = H("#f4f4f5"); Surface = H("#ffffff"); Soft = H("#efeff1"); Line = H("#d9d9de"); Ink = H("#0f0f12"); Muted = H("#62626b");
                Accent = H("#0f0f12"); AccentInk = H("#ffffff"); AccentSoft = H("#e6e6ea");
                Spark = H("#7c3aed"); Promo = H("#5b21b6"); PromoInk = H("#ffffff"); Good = H("#166b49"); GoodSoft = H("#e3f3ea"); Bad = H("#b3261e"); BadSoft = H("#fcebe9"); Warn = H("#8a5a00"); WarnSoft = H("#fff1d6");
            }
        }

        /// <summary>A colour for things that should stand out a little (cyan on dark themes, purple on light ones), and a soft tint of it for backgrounds.</summary>
        static void Sparks()
        {
            SparkSoft = Color.FromArgb((Surface.R * 86 + Spark.R * 14) / 100, (Surface.G * 86 + Spark.G * 14) / 100, (Surface.B * 86 + Spark.B * 14) / 100);
        }

        public static void Set(bool dark) { Use(dark ? "dark" : "light"); }
        public static void Use(string name)
        {
            Name = name; Palette(); Sparks(); Fonts.Clear();
            var h = Changed; if (h != null) h();
        }

        /// <summary>Text size chosen by the person: normal (as designed), medium or large. Everything (fonts and spacing) scales together.</summary>
        public static string Text = "normal";
        public static readonly string[,] TextChoices = { { "normal", "Normal" }, { "medium", "Medium" }, { "large", "Large" } };
        public static float Zoom { get { return Text == "large" ? 1.3f : Text == "medium" ? 1.15f : 1f; } }
        public static string TextTitle { get { for (int i = 0; i < TextChoices.GetLength(0); i++) if (TextChoices[i, 0] == Text) return TextChoices[i, 1]; return "Normal"; } }
        public static int Px(int n) { return (int)Math.Round(n * Scale * Zoom); }

        // ----- fonts (cached) -----
        static readonly Dictionary<string, Font> Fonts = new Dictionary<string, Font>();
        public static Font F(float pt = 10f, FontStyle st = FontStyle.Regular, string family = "Segoe UI")
        {
            string k = family + pt + st + Zoom; Font f;
            if (!Fonts.TryGetValue(k, out f)) Fonts[k] = f = new Font(family, pt * Zoom, st, GraphicsUnit.Point);
            return f;
        }
        public static Font Mono(float pt = 10f, FontStyle st = FontStyle.Regular) { return F(pt, st, "Consolas"); }

        /// <summary>Re-applies the theme to a whole control tree (custom controls repaint; IThemed ones restyle themselves).</summary>
        public static void Refresh(Control root)
        {
            var t = root as IThemed; if (t != null) t.OnTheme();
            foreach (Control c in root.Controls) Refresh(c);
            root.Invalidate();
        }

        // ----- persistence of the user's choice -----
        static string PrefFile() { return Path.Combine(Engine.DataDir(), "shell.json"); }
        public static void Load()
        {
            Text = "normal";
            try
            {
                if (File.Exists(PrefFile()))
                {
                    string txt = File.ReadAllText(PrefFile()), n = null;
                    try { n = Json.Parse(txt)["theme"].Str; } catch (Exception) { }   // the file may have had text added by hand: then read the setting out of whatever is there
                    if (string.IsNullOrEmpty(n)) { var m = System.Text.RegularExpressions.Regex.Match(txt, "\"theme\"\\s*:\\s*\"([a-z-]+)\""); if (m.Success) n = m.Groups[1].Value; }
                    var tm = System.Text.RegularExpressions.Regex.Match(txt, "\"text\"\\s*:\\s*\"(normal|medium|large)\""); if (tm.Success) Text = tm.Groups[1].Value;
                    for (int i = 0; i < Choices.GetLength(0); i++) if (Choices[i, 0] == n) Name = n;
                    if (n == "solarized-dark") Name = "night"; else if (n == "solarized-light") Name = "light";   // the Solarized themes were replaced
                }
            }
            catch (Exception) { }
            string forced = Environment.GetEnvironmentVariable("LEDJORO_TEXT");   // for testing only
            if (forced == "normal" || forced == "medium" || forced == "large") Text = forced;
            Palette();
        }
        public static void Save()
        {
            try { Directory.CreateDirectory(Engine.DataDir()); File.WriteAllText(PrefFile(), Json.Stringify(Json.O("theme", Name, "text", Text))); } catch (Exception) { }
        }

        public static GraphicsPath Round(Rectangle r, int radius)
        {
            var p = new GraphicsPath(); int d = Math.Max(1, radius * 2);
            if (radius <= 0) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90); p.CloseFigure();
            return p;
        }
        public static void Smooth(Graphics g) { g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit; }
    }
}
