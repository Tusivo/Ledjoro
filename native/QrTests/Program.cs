using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using Ledjoro;

static class T
{
    static int fails, passes;
    static void Check(bool ok, string what) { if (ok) passes++; else { fails++; Console.WriteLine("FAIL " + what); } }

    static byte[] Lum(Bitmap bmp) { var d = new byte[bmp.Width * bmp.Height]; for (int y = 0; y < bmp.Height; y++) for (int x = 0; x < bmp.Width; x++) { var c = bmp.GetPixel(x, y); d[y * bmp.Width + x] = (byte)((c.R * 299 + c.G * 587 + c.B * 114) / 1000); } return d; }
    static Bitmap Render(bool[,] m, int scale, int quiet = 4)
    {
        int n = m.GetLength(0), size = (n + quiet * 2) * scale; var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp)) { g.Clear(Color.White); for (int y = 0; y < n; y++) for (int x = 0; x < n; x++) if (m[y, x]) g.FillRectangle(Brushes.Black, (x + quiet) * scale, (y + quiet) * scale, scale, scale); }
        return bmp;
    }
    static string Read(Bitmap b) { var l = Lum(b); return QrReader.Decode(l, b.Width, b.Height); }
    static Bitmap OnPaper(Bitmap qr, int pw, int ph, int ox, int oy, double angle, double scale = 1.0, double perspective = 0, bool blur = false, int noise = 0, int seed = 1)
    {
        // the code placed on a bigger grey-ish page, rotated, optionally in perspective, blurred, noisy
        var page = new Bitmap(pw, ph);
        using (var g = Graphics.FromImage(page))
        {
            g.Clear(Color.FromArgb(235, 232, 225)); g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            g.TranslateTransform(ox + qr.Width * (float)scale / 2, oy + qr.Height * (float)scale / 2); g.RotateTransform((float)angle); g.ScaleTransform((float)scale, (float)scale);
            if (perspective > 0)
            {
                int w = qr.Width, h = qr.Height; var dst = new[] { new PointF(-w / 2f, -h / 2f + (float)(perspective * h)), new PointF(w / 2f, -h / 2f), new PointF(-w / 2f, h / 2f - (float)(perspective * h)) };
                g.DrawImage(qr, dst);
            }
            else g.DrawImage(qr, -qr.Width / 2f, -qr.Height / 2f);
        }
        if (blur) page = Blur(page);
        if (noise > 0) { var r = new Random(seed); for (int y = 0; y < ph; y++) for (int x = 0; x < pw; x++) { var c = page.GetPixel(x, y); int d = r.Next(-noise, noise + 1); page.SetPixel(x, y, Color.FromArgb(Math.Max(0, Math.Min(255, c.R + d)), Math.Max(0, Math.Min(255, c.G + d)), Math.Max(0, Math.Min(255, c.B + d)))); } }
        return page;
    }
    static Bitmap Blur(Bitmap b)
    {
        var o = new Bitmap(b.Width, b.Height);
        for (int y = 1; y < b.Height - 1; y++) for (int x = 1; x < b.Width - 1; x++) { int r = 0, g = 0, bl = 0; for (int dy = -1; dy <= 1; dy++) for (int dx = -1; dx <= 1; dx++) { var c = b.GetPixel(x + dx, y + dy); r += c.R; g += c.G; bl += c.B; } o.SetPixel(x, y, Color.FromArgb(r / 9, g / 9, bl / 9)); }
        return o;
    }

    static int Debug()
    {
        string payload = "ledjoro:recover?v=1&c=Birchwood%20Design%20Studio%20LLC&u=selftest&k=7K2QF-9XMWB-C4HTN-8VR3D";
        using (var qr = Render(QrEncoder.Encode(payload), 7)) using (var page = OnPaper(qr, 1280, 960, 300, 150, 45))
        {
            var lum = Lum(page); int w = page.Width, h = page.Height;
            var t = typeof(QrReader); var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
            foreach (int mode in new[] { 0, 1, 2 })
            {
                var bin = (bool[])t.GetMethod("Binarize", flags).Invoke(null, new object[] { lum, w, h, mode });
                var finders = (System.Collections.IList)t.GetMethod("FindFinders", flags).Invoke(null, new object[] { bin, w, h });
                Console.WriteLine("mode " + mode + ": finders found = " + finders.Count);
                foreach (var f in finders) { var ft = f.GetType(); Console.WriteLine("   at " + Math.Round((double)ft.GetField("X").GetValue(f)) + "," + Math.Round((double)ft.GetField("Y").GetValue(f)) + " size " + Math.Round((double)ft.GetField("Size").GetValue(f), 1) + " seen " + ft.GetField("Count").GetValue(f)); }
                var s = (string)t.GetMethod("TryBinary", flags).Invoke(null, new object[] { bin, w, h });
                Console.WriteLine("   decode: " + (s ?? "null"));
            }
        }
        return 0;
    }
    static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "debug") return Debug();
        if (args.Length > 0 && args[0] == "time")
        {
            string tpay = "ledjoro:recover?v=1&c=Birchwood%20Design%20Studio%20LLC&u=selftest&k=7K2QF-9XMWB-C4HTN-8VR3D";
            using (var qr = Render(QrEncoder.Encode(tpay), 5)) using (var page = OnPaper(qr, 1280, 720, 380, 120, 8, 0.9, 0.03, true, 25))
            using (var blank = new Bitmap(1280, 720)) // a frame with nothing useful in it (the common case while waiting)
            {
                using (var g = Graphics.FromImage(blank)) { g.Clear(Color.FromArgb(180, 170, 160)); var r = new Random(2); for (int i = 0; i < 400; i++) g.FillRectangle(Brushes.DimGray, r.Next(1280), r.Next(720), r.Next(3, 40), r.Next(3, 40)); }
                var l1 = Lum(page); var l2 = Lum(blank);
                QrReader.Decode(l1, 1280, 720); var sw = System.Diagnostics.Stopwatch.StartNew();
                for (int i = 0; i < 10; i++) QrReader.Decode(l1, 1280, 720); Console.WriteLine("frame with a code: " + (sw.ElapsedMilliseconds / 10) + " ms each");
                sw.Restart(); for (int i = 0; i < 10; i++) QrReader.Decode(l2, 1280, 720); Console.WriteLine("frame with nothing:  " + (sw.ElapsedMilliseconds / 10) + " ms each");
                Console.WriteLine("reads: " + (QrReader.Decode(l1, 1280, 720) == tpay));
            }
            return 0;
        }
        // 1) Reed-Solomon: repair up to ec/2 damaged codewords, refuse more
        var rnd = new Random(7);
        for (int trial = 0; trial < 200; trial++)
        {
            int dl = rnd.Next(5, 60), ec = new[] { 10, 16, 18, 22, 24, 26 }[rnd.Next(6)];
            var data = Enumerable.Range(0, dl).Select(i => rnd.Next(256)).ToArray(); var all = data.Concat(Gf.Encode(data, ec)).ToArray();
            var damaged = (int[])all.Clone(); int errs = rnd.Next(0, ec / 2 + 1);
            foreach (int p in Enumerable.Range(0, all.Length).OrderBy(_ => rnd.Next()).Take(errs)) damaged[p] ^= rnd.Next(1, 256);
            bool ok = Gf.Decode(damaged, ec) && damaged.SequenceEqual(all);
            Check(ok, $"RS repairs {errs} errors with ec={ec}");
        }
        var d2 = Enumerable.Range(0, 20).ToArray(); var a2 = d2.Concat(Gf.Encode(d2, 10)).ToArray(); var bad = (int[])a2.Clone(); for (int i = 0; i < 8; i++) bad[i] ^= 77;
        Check(!(Gf.Decode(bad, 10) && bad.SequenceEqual(a2)), "RS gives up (does not 'repair' to the wrong thing) when far too damaged");

        // 2) our own codes read back, at every size, several scales
        foreach (int len in new[] { 5, 14, 26, 42, 62, 84, 106, 124, 154, 190, 210 })
        {
            string text = new string(Enumerable.Range(0, len).Select(i => (char)('A' + (i * 7) % 26)).ToArray());
            var m = QrEncoder.Encode(text);
            foreach (int scale in new[] { 3, 5, 8 }) { using (var bmp = Render(m, scale)) Check(Read(bmp) == text, $"own code length {len} (version {(m.GetLength(0) - 17) / 4}) scale {scale}"); }
        }
        var unicode = "Café Ümlaut LLC | marie.dupont | 12345-ABCDE";
        using (var bmp = Render(QrEncoder.Encode(unicode), 6)) Check(Read(bmp) == unicode, "non-English letters survive");

        // 3) the real payload shape, found in a busy picture: rotated, scaled, perspective, blurred, noisy
        string payload = "ledjoro:recover?v=1&c=Birchwood%20Design%20Studio%20LLC&u=selftest&k=7K2QF-9XMWB-C4HTN-8VR3D";
        var code = QrEncoder.Encode(payload); Console.WriteLine("payload " + payload.Length + " bytes -> version " + ((code.GetLength(0) - 17) / 4));
        using (var qr = Render(code, 7))
        {
            foreach (double ang in new[] { 0, 7, -12, 25, 45, 90, 135, 180, 270 })
                using (var page = OnPaper(qr, 1280, 960, 300, 150, ang)) Check(Read(page) == payload, $"rotated {ang} degrees");
            foreach (double sc in new[] { 0.5, 0.7, 1.3 }) using (var page = OnPaper(qr, 1280, 960, 300, 150, 5, sc)) Check(Read(page) == payload, $"scaled x{sc}");
            foreach (double pers in new[] { 0.04, 0.08, 0.12 }) using (var page = OnPaper(qr, 1280, 960, 300, 150, 0, 1.0, pers)) Check(Read(page) == payload, $"perspective {pers}");
            using (var page = OnPaper(qr, 1280, 960, 300, 150, 10, 0.8, 0, true)) Check(Read(page) == payload, "blurred");
            using (var page = OnPaper(qr, 1280, 960, 300, 150, -8, 0.9, 0, false, 40)) Check(Read(page) == payload, "noisy");
            using (var page = OnPaper(qr, 1280, 960, 300, 150, 15, 0.8, 0.05, true, 30)) Check(Read(page) == payload, "rotated + perspective + blur + noise");
            // some dirt on the code (error correction)
            var dirty = (Bitmap)qr.Clone(); using (var g = Graphics.FromImage(dirty)) { g.FillRectangle(Brushes.Black, 200, 230, 30, 14); g.FillRectangle(Brushes.White, 260, 300, 22, 10); }
            using (var page = OnPaper(dirty, 1280, 960, 300, 150, 3)) Check(Read(page) == payload, "smudged");
            // nothing there
            using (var empty = new Bitmap(800, 600)) { using (var g = Graphics.FromImage(empty)) g.Clear(Color.White); Check(Read(empty) == null, "an empty picture reads as nothing"); }
        }

        // 4) pictures from other programs: independent encoder (pip segno / qrcode) written to disk by the test script
        string dir = args.Length > 0 ? args[0] : null;
        if (dir != null && Directory.Exists(dir))
            foreach (var f in Directory.GetFiles(dir, "*.png").OrderBy(x => x))
            {
                string expected = File.ReadAllText(Path.ChangeExtension(f, ".txt"));
                using (var bmp = new Bitmap(f)) Check(Read(bmp) == expected, "other program's code " + Path.GetFileName(f));
            }
        // 5) our codes written to disk for an independent reader to check
        if (dir != null) { Directory.CreateDirectory(Path.Combine(dir, "ours")); int k = 0; foreach (var text in new[] { payload, "short", unicode, new string('x', 100) }) { using (var bmp = Render(QrEncoder.Encode(text), 8, 4)) bmp.Save(Path.Combine(dir, "ours", "ours" + k + ".png")); File.WriteAllText(Path.Combine(dir, "ours", "ours" + k + ".txt"), text); k++; } }

        Console.WriteLine($"{passes} passed, {fails} failed");
        return fails == 0 ? 0 : 1;
    }
}
