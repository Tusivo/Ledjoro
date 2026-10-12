using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace Ledjoro
{
    /// <summary>
    /// The small coloured icons beside the sidebar entries. They are drawn here as vector shapes (a white glyph on a rounded coloured tile),
    /// so they stay sharp at any screen scaling and need no image files.
    /// </summary>
    public static class NavIcons
    {
        // One colour per sidebar section: every entry in a section has exactly the same colour. Each colour has a deeper version for light themes and a lighter one
        // for dark themes, so the symbols stay clear on any background.
        //   Overview = graphite   Sales = deep red   Purchases = bright orange   Banking = green   Accounting = purple   Data = sea blue   Admin = slate
        static readonly Color[][] Palette =
        {   //                        on light themes               on dark themes
            new[] { Color.FromArgb(55, 65, 81),    Color.FromArgb(209, 213, 219) },   // 0 graphite
            new[] { Color.FromArgb(185, 28, 28),   Color.FromArgb(248, 113, 113) },   // 1 deep red
            new[] { Color.FromArgb(249, 115, 22),  Color.FromArgb(251, 146, 60) },    // 2 bright orange
            new[] { Color.FromArgb(22, 163, 74),   Color.FromArgb(74, 222, 128) },    // 3 green
            new[] { Color.FromArgb(124, 58, 237),  Color.FromArgb(167, 139, 250) },   // 4 purple
            new[] { Color.FromArgb(6, 119, 163),   Color.FromArgb(77, 196, 232) },    // 5 sea blue
            new[] { Color.FromArgb(100, 116, 139), Color.FromArgb(148, 163, 184) },   // 6 slate
            new[] { Color.FromArgb(219, 39, 119),  Color.FromArgb(244, 114, 182) },   // 7 pink
        };
        static readonly Dictionary<string, int> Tiles = new Dictionary<string, int>
        {
            { "dashboard", 0 },
            { "invoices", 1 }, { "customers", 1 }, { "salestax", 1 }, { "items", 1 }, { "credits", 1 },
            { "bills", 2 }, { "vendors", 2 },
            { "banking", 3 }, { "reconcile", 3 },
            { "accounts", 4 }, { "journal", 4 }, { "assets", 4 }, { "reports", 4 }, { "health", 4 },
            { "import", 5 }, { "quickbooks", 5 },
            { "settings", 6 }, { "about", 6 },
            { "support", 7 },
        };
        public static bool Has(string key) { return key != null && Tiles.ContainsKey(key); }
        public static Color TileColor(string key) { int i; return Tiles.TryGetValue(key ?? "", out i) ? Palette[i][Theme.Dark ? 1 : 0] : Color.Gray; }

        /// <summary>Draw the icon for a sidebar entry inside <paramref name="box"/> (a square): the symbol alone, bold, in its section colour, with no background.</summary>
        public static void Draw(Graphics g, string key, Rectangle box) { Draw(g, key, box, TileColor(key), true, true); }

        /// <summary>Draw an icon in a given colour: a solid tile with a white symbol, or (tinted) a soft tile of that colour with a symbol in the colour itself.</summary>
        public static void Draw(Graphics g, string key, Rectangle box, Color color, bool tinted) { Draw(g, key, box, color, tinted, false); }

        /// <summary>The symbol alone, in the section colour, on no background at all (so it blends into whatever is behind it).</summary>
        public static void DrawBare(Graphics g, string key, Rectangle box, Color color) { Draw(g, key, box, color, true, true); }

        static void Draw(Graphics g, string key, Rectangle box, Color color, bool tinted, bool bare)
        {
            if (!Has(key)) return;
            Color ink = tinted ? color : Color.White;
            var old = g.SmoothingMode; g.SmoothingMode = SmoothingMode.AntiAlias;
            if (!bare) using (var tile = Theme.Round(box, Math.Max(3, box.Width / 4))) using (var b = new SolidBrush(tinted ? Color.FromArgb(40, color) : color)) g.FillPath(b, tile);
            float pad = box.Width * (bare ? 0.12f : 0.24f), s = box.Width - 2 * pad, x0 = box.X + pad, y0 = box.Y + pad;
            Func<float, float, PointF> P = (u, v) => new PointF(x0 + u * s, y0 + v * s);
            Func<float, float, float, float, RectangleF> R = (u, v, w, h) => new RectangleF(x0 + u * s, y0 + v * s, w * s, h * s);
            float lw = bare ? Math.Max(2.0f, box.Width * 0.10f) : Math.Max(1.4f, box.Width * 0.085f);
            using (var pen = new Pen(ink, lw) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            using (var fill = new SolidBrush(ink))
            {
                switch (key)
                {
                    case "dashboard":
                        g.FillRectangle(fill, R(0f, 0f, 0.44f, 0.5f)); g.FillRectangle(fill, R(0.56f, 0f, 0.44f, 0.3f)); g.FillRectangle(fill, R(0f, 0.62f, 0.44f, 0.38f)); g.FillRectangle(fill, R(0.56f, 0.42f, 0.44f, 0.58f)); break;
                    case "invoices":
                        g.DrawLines(pen, new[] { P(0.12f, 0.02f), P(0.64f, 0.02f), P(0.88f, 0.26f), P(0.88f, 0.98f), P(0.12f, 0.98f), P(0.12f, 0.02f) });
                        g.DrawLine(pen, P(0.64f, 0.02f), P(0.64f, 0.26f)); g.DrawLine(pen, P(0.64f, 0.26f), P(0.88f, 0.26f));
                        g.DrawLine(pen, P(0.3f, 0.52f), P(0.7f, 0.52f)); g.DrawLine(pen, P(0.3f, 0.74f), P(0.7f, 0.74f)); break;
                    case "customers":
                        g.DrawEllipse(pen, R(0.3f, 0.02f, 0.4f, 0.4f)); g.DrawArc(pen, R(0.08f, 0.56f, 0.84f, 0.84f), 200, 140); break;
                    case "bills":
                        g.DrawLines(pen, new[] { P(0.14f, 0.02f), P(0.86f, 0.02f), P(0.86f, 0.98f), P(0.7f, 0.86f), P(0.5f, 0.98f), P(0.3f, 0.86f), P(0.14f, 0.98f), P(0.14f, 0.02f) });
                        g.DrawLine(pen, P(0.32f, 0.3f), P(0.68f, 0.3f)); g.DrawLine(pen, P(0.32f, 0.52f), P(0.68f, 0.52f)); break;
                    case "vendors":
                        g.DrawLines(pen, new[] { P(0.04f, 0.4f), P(0.14f, 0.06f), P(0.86f, 0.06f), P(0.96f, 0.4f) });
                        g.DrawLines(pen, new[] { P(0.04f, 0.4f), P(0.96f, 0.4f) }); g.DrawLines(pen, new[] { P(0.14f, 0.4f), P(0.14f, 0.96f), P(0.86f, 0.96f), P(0.86f, 0.4f) });
                        g.DrawRectangle(pen, x0 + 0.4f * s, y0 + 0.62f * s, 0.2f * s, 0.34f * s); break;
                    case "banking":
                        g.DrawLines(pen, new[] { P(0.0f, 0.34f), P(0.5f, 0.0f), P(1.0f, 0.34f), P(0.0f, 0.34f) });
                        foreach (float u in new[] { 0.14f, 0.5f, 0.86f }) g.DrawLine(pen, P(u, 0.5f), P(u, 0.8f));
                        g.DrawLine(pen, P(0.0f, 0.98f), P(1.0f, 0.98f)); break;
                    case "reconcile":
                        g.DrawEllipse(pen, R(0.0f, 0.0f, 1.0f, 1.0f)); g.DrawLines(pen, new[] { P(0.28f, 0.52f), P(0.45f, 0.7f), P(0.74f, 0.34f) }); break;
                    case "accounts":
                        g.FillEllipse(fill, R(0.0f, 0.0f, 0.16f, 0.16f)); g.FillEllipse(fill, R(0.0f, 0.42f, 0.16f, 0.16f)); g.FillEllipse(fill, R(0.0f, 0.84f, 0.16f, 0.16f));
                        g.DrawLine(pen, P(0.32f, 0.08f), P(1.0f, 0.08f)); g.DrawLine(pen, P(0.32f, 0.5f), P(1.0f, 0.5f)); g.DrawLine(pen, P(0.32f, 0.92f), P(1.0f, 0.92f)); break;
                    case "journal":
                        g.DrawLines(pen, new[] { P(0.5f, 0.16f), P(0.5f, 0.96f) });
                        g.DrawLines(pen, new[] { P(0.5f, 0.16f), P(0.2f, 0.04f), P(0.0f, 0.1f), P(0.0f, 0.84f), P(0.2f, 0.78f), P(0.5f, 0.96f) });
                        g.DrawLines(pen, new[] { P(0.5f, 0.16f), P(0.8f, 0.04f), P(1.0f, 0.1f), P(1.0f, 0.84f), P(0.8f, 0.78f), P(0.5f, 0.96f) }); break;
                    case "assets":
                        g.DrawLines(pen, new[] { P(0.5f, 0.0f), P(0.96f, 0.24f), P(0.96f, 0.76f), P(0.5f, 1.0f), P(0.04f, 0.76f), P(0.04f, 0.24f), P(0.5f, 0.0f) });
                        g.DrawLines(pen, new[] { P(0.04f, 0.24f), P(0.5f, 0.5f), P(0.96f, 0.24f) }); g.DrawLine(pen, P(0.5f, 0.5f), P(0.5f, 1.0f)); break;
                    case "reports":
                        g.FillRectangle(fill, R(0.0f, 0.5f, 0.24f, 0.5f)); g.FillRectangle(fill, R(0.38f, 0.0f, 0.24f, 1.0f)); g.FillRectangle(fill, R(0.76f, 0.3f, 0.24f, 0.7f)); break;
                    case "health":
                        g.DrawLines(pen, new[] { P(0.0f, 0.55f), P(0.28f, 0.55f), P(0.4f, 0.2f), P(0.6f, 0.9f), P(0.72f, 0.55f), P(1.0f, 0.55f) }); break;
                    case "import":
                        g.DrawLine(pen, P(0.5f, 0.0f), P(0.5f, 0.66f)); g.DrawLines(pen, new[] { P(0.26f, 0.44f), P(0.5f, 0.68f), P(0.74f, 0.44f) });
                        g.DrawLines(pen, new[] { P(0.0f, 0.7f), P(0.0f, 1.0f), P(1.0f, 1.0f), P(1.0f, 0.7f) }); break;
                    case "quickbooks":
                        g.DrawLine(pen, P(0.0f, 0.28f), P(1.0f, 0.28f)); g.DrawLines(pen, new[] { P(0.72f, 0.0f), P(1.0f, 0.28f), P(0.72f, 0.56f) });
                        g.DrawLine(pen, P(1.0f, 0.74f), P(0.0f, 0.74f)); g.DrawLines(pen, new[] { P(0.28f, 0.46f), P(0.0f, 0.74f), P(0.28f, 1.0f) }); break;
                    case "items":
                        g.DrawPolygon(pen, new[] { P(0.04f, 0.56f), P(0.56f, 0.04f), P(0.96f, 0.04f), P(0.96f, 0.44f), P(0.44f, 0.96f) });
                        g.FillEllipse(fill, R(0.66f, 0.16f, 0.14f, 0.14f)); break;
                    case "credits":
                        g.DrawLines(pen, new[] { P(0.36f, 0.04f), P(0.04f, 0.36f), P(0.36f, 0.68f) });
                        g.DrawLines(pen, new[] { P(0.04f, 0.36f), P(0.68f, 0.36f), P(0.96f, 0.56f), P(0.96f, 0.78f), P(0.68f, 0.98f), P(0.36f, 0.98f) }); break;
                    case "salestax":
                        g.DrawLine(pen, P(0.86f, 0.04f), P(0.14f, 0.96f)); g.DrawEllipse(pen, R(0.0f, 0.0f, 0.4f, 0.4f)); g.DrawEllipse(pen, R(0.6f, 0.6f, 0.4f, 0.4f)); break;
                    case "support":
                        using (var heart = new GraphicsPath())
                        {
                            heart.AddBezier(P(0.5f, 0.95f), P(0.1f, 0.65f), P(0.0f, 0.35f), P(0.2f, 0.12f)); heart.AddBezier(P(0.2f, 0.12f), P(0.35f, 0.0f), P(0.5f, 0.12f), P(0.5f, 0.28f));
                            heart.AddBezier(P(0.5f, 0.28f), P(0.5f, 0.12f), P(0.65f, 0.0f), P(0.8f, 0.12f)); heart.AddBezier(P(0.8f, 0.12f), P(1.0f, 0.35f), P(0.9f, 0.65f), P(0.5f, 0.95f));
                            g.DrawPath(pen, heart);
                        }
                        break;
                    case "about":
                        g.DrawEllipse(pen, R(0.0f, 0.0f, 1.0f, 1.0f)); g.DrawLine(pen, P(0.5f, 0.44f), P(0.5f, 0.74f)); g.FillEllipse(fill, R(0.44f, 0.22f, 0.12f, 0.12f)); break;
                    case "settings":
                        g.DrawEllipse(pen, R(0.3f, 0.3f, 0.4f, 0.4f));
                        for (int k = 0; k < 8; k++) { double a = k * Math.PI / 4; g.DrawLine(pen, P(0.5f + (float)Math.Cos(a) * 0.38f, 0.5f + (float)Math.Sin(a) * 0.38f), P(0.5f + (float)Math.Cos(a) * 0.52f, 0.5f + (float)Math.Sin(a) * 0.52f)); }
                        break;
                }
            }
            g.SmoothingMode = old;
        }
    }
}
