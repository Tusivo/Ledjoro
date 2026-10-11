using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Ledjoro
{
    /// <summary>Arithmetic in GF(256) (primitive polynomial 0x11D) and Reed-Solomon coding, as QR codes use them.</summary>
    static class Gf
    {
        public static readonly int[] Exp = new int[512], Log = new int[256];
        static Gf() { int x = 1; for (int i = 0; i < 255; i++) { Exp[i] = x; Log[x] = i; x <<= 1; if (x >= 256) x ^= 0x11D; } for (int i = 255; i < 512; i++) Exp[i] = Exp[i - 255]; }
        public static int Mul(int a, int b) { return a == 0 || b == 0 ? 0 : Exp[Log[a] + Log[b]]; }
        public static int Inv(int a) { return Exp[255 - Log[a]]; }

        // polynomials are int[] with the highest degree first
        static int[] Trim(int[] p) { int i = 0; while (i < p.Length - 1 && p[i] == 0) i++; return i == 0 ? p : p.Skip(i).ToArray(); }
        static int[] Add(int[] a, int[] b) { int n = Math.Max(a.Length, b.Length); var r = new int[n]; for (int i = 0; i < a.Length; i++) r[n - a.Length + i] ^= a[i]; for (int i = 0; i < b.Length; i++) r[n - b.Length + i] ^= b[i]; return Trim(r); }
        static int[] MulPoly(int[] a, int[] b) { var r = new int[a.Length + b.Length - 1]; for (int i = 0; i < a.Length; i++) for (int j = 0; j < b.Length; j++) r[i + j] ^= Mul(a[i], b[j]); return Trim(r); }
        static int[] Scale(int[] a, int s) { return Trim(a.Select(c => Mul(c, s)).ToArray()); }
        static int[] Shift(int[] a, int deg) { return Trim(a.Concat(new int[deg]).ToArray()); }
        static int Degree(int[] p) { return p.Length - 1; }
        static bool IsZero(int[] p) { return p.Length == 1 && p[0] == 0; }
        static int Eval(int[] p, int x) { int r = 0; foreach (int c in p) r = Mul(r, x) ^ c; return r; }
        static int Coef(int[] p, int deg) { return deg < p.Length ? p[p.Length - 1 - deg] : 0; }

        /// <summary>The error-correction codewords for a block of data codewords.</summary>
        public static int[] Encode(int[] data, int ec)
        {
            var gen = new[] { 1 };
            for (int i = 0; i < ec; i++) gen = MulPoly(gen, new[] { 1, Exp[i] });
            var rem = new int[ec];
            foreach (int d in data)
            {
                int f = d ^ rem[0];
                Array.Copy(rem, 1, rem, 0, ec - 1); rem[ec - 1] = 0;
                if (f != 0) for (int i = 0; i < ec; i++) rem[i] ^= Mul(gen[i + 1], f);
            }
            return rem;
        }

        /// <summary>Repairs a received block in place (data followed by ec codewords). False when it is too damaged to repair.</summary>
        public static bool Decode(int[] received, int ec)
        {
            var poly = Trim((int[])received.Clone());
            var synd = new int[ec]; bool clean = true;
            for (int i = 0; i < ec; i++) { int e = Eval(poly, Exp[i]); synd[ec - 1 - i] = e; if (e != 0) clean = false; }
            if (clean) return true;
            var syndrome = Trim(synd);
            // Euclid: find the error locator (sigma) and evaluator (omega)
            var a = Shift(new[] { 1 }, ec); var b = syndrome;
            if (Degree(a) < Degree(b)) { var tmp = a; a = b; b = tmp; }
            var rLast = a; var r = b; var tLast = new[] { 0 }; var t = new[] { 1 };
            while (Degree(r) >= ec / 2)
            {
                var rLastLast = rLast; var tLastLast = tLast; rLast = r; tLast = t;
                if (IsZero(rLast)) return false;
                r = rLastLast; var q = new[] { 0 };
                int dlt = Inv(rLast[0]);
                while (Degree(r) >= Degree(rLast) && !IsZero(r))
                {
                    int diff = Degree(r) - Degree(rLast), scale = Mul(r[0], dlt);
                    q = Add(q, Shift(new[] { scale }, diff)); r = Add(r, Shift(Scale(rLast, scale), diff));
                }
                t = Add(MulPoly(q, tLast), tLastLast);
                if (Degree(r) >= Degree(rLast)) return false;
            }
            int sigmaAtZero = Coef(t, 0); if (sigmaAtZero == 0) return false;
            int inv = Inv(sigmaAtZero); var sigma = Scale(t, inv); var omega = Scale(r, inv);
            int n = Degree(sigma);
            var locs = new List<int>();
            if (n == 1) locs.Add(Coef(sigma, 1));
            else for (int i = 1; i < 256 && locs.Count < n; i++) if (Eval(sigma, i) == 0) locs.Add(Inv(i));
            if (locs.Count != n) return false;
            for (int i = 0; i < locs.Count; i++)
            {
                int xiInv = Inv(locs[i]), den = 1;
                for (int j = 0; j < locs.Count; j++) if (i != j) { int term = Mul(locs[j], xiInv); den = Mul(den, (term & 1) == 0 ? term | 1 : term & ~1); }
                int mag = Mul(Eval(omega, xiInv), Inv(den));
                int pos = received.Length - 1 - Log[locs[i]];
                if (pos < 0) return false;
                received[pos] ^= mag;
            }
            for (int i = 0; i < ec; i++) if (Eval(Trim((int[])received.Clone()), Exp[i]) != 0) return false;   // make sure the repair really worked
            return true;
        }
    }

    /// <summary>Facts about QR versions 1 to 10 at error-correction level M (about 15% of the code can be damaged and still read).</summary>
    static class QrSpec
    {
        // groups: {blocks in group 1, data codewords per block, blocks in group 2, data codewords per block, error-correction codewords per block}
        static readonly int[][] Blocks = { null, new[] { 1, 16, 0, 0, 10 }, new[] { 1, 28, 0, 0, 16 }, new[] { 1, 44, 0, 0, 26 }, new[] { 2, 32, 0, 0, 18 }, new[] { 2, 43, 0, 0, 24 },
            new[] { 4, 27, 0, 0, 16 }, new[] { 4, 31, 0, 0, 18 }, new[] { 2, 38, 2, 39, 22 }, new[] { 3, 36, 2, 37, 22 }, new[] { 4, 43, 1, 44, 26 } };
        static readonly int[][] Align = { null, new int[0], new[] { 6, 18 }, new[] { 6, 22 }, new[] { 6, 26 }, new[] { 6, 30 }, new[] { 6, 34 }, new[] { 6, 22, 38 }, new[] { 6, 24, 42 }, new[] { 6, 26, 46 }, new[] { 6, 28, 50 } };
        public const int MaxVersion = 10;
        public static int Size(int v) { return 17 + 4 * v; }
        public static int DataCodewords(int v) { var t = Blocks[v]; return t[0] * t[1] + t[2] * t[3]; }
        public static int TotalCodewords(int v) { var t = Blocks[v]; return DataCodewords(v) + (t[0] + t[2]) * t[4]; }
        public static int ByteCapacity(int v) { return (DataCodewords(v) * 8 - 4 - (v < 10 ? 8 : 16)) / 8; }
        public static int[] AlignCenters(int v) { return Align[v]; }
        /// <summary>Data-block sizes in order, and the error-correction codewords per block.</summary>
        public static int[] BlockSizes(int v) { var t = Blocks[v]; return Enumerable.Repeat(t[1], t[0]).Concat(Enumerable.Repeat(t[3], t[2])).ToArray(); }
        public static int EcPerBlock(int v) { return Blocks[v][4]; }

        /// <summary>true for the modules that are part of the fixed patterns (finders, timing, alignment, format and version areas).</summary>
        public static bool[,] FunctionModules(int v)
        {
            int n = Size(v); var f = new bool[n, n];
            Action<int, int, int, int> fill = (x0, y0, w, h) => { for (int y = y0; y < y0 + h; y++) for (int x = x0; x < x0 + w; x++) if (x >= 0 && y >= 0 && x < n && y < n) f[y, x] = true; };
            fill(0, 0, 9, 9); fill(n - 8, 0, 8, 9); fill(0, n - 8, 9, 8);    // three finders with their separators and the format bits beside them
            fill(6, 0, 1, n); fill(0, 6, n, 1);                                // timing patterns
            var c = Align[v];
            for (int i = 0; i < c.Length; i++) for (int j = 0; j < c.Length; j++)
            {
                if ((i == 0 && j == 0) || (i == 0 && j == c.Length - 1) || (i == c.Length - 1 && j == 0)) continue;   // those spots are finders
                fill(c[i] - 2, c[j] - 2, 5, 5);
            }
            if (v >= 7) { fill(n - 11, 0, 3, 6); fill(0, n - 11, 6, 3); }      // version information
            return f;
        }
    }

    /// <summary>Makes QR codes (byte mode, error correction level M, versions 1 to 10).</summary>
    public static class QrEncoder
    {
        /// <summary>The modules of the code (true = dark), without the quiet zone.</summary>
        public static bool[,] Encode(string text)
        {
            byte[] data = Encoding.UTF8.GetBytes(text);
            int v = 1; while (v <= QrSpec.MaxVersion && data.Length > QrSpec.ByteCapacity(v)) v++;
            if (v > QrSpec.MaxVersion) throw new ArgumentException("That is too much text for one QR code (" + data.Length + " bytes; the most is " + QrSpec.ByteCapacity(QrSpec.MaxVersion) + ").");
            // bit stream: mode (byte) + length + data, then padding
            var bits = new List<bool>(); Action<int, int> put = (val, count) => { for (int i = count - 1; i >= 0; i--) bits.Add(((val >> i) & 1) != 0); };
            put(4, 4); put(data.Length, v < 10 ? 8 : 16); foreach (byte b in data) put(b, 8);
            int cap = QrSpec.DataCodewords(v) * 8;
            for (int i = 0; i < 4 && bits.Count < cap; i++) bits.Add(false);
            while (bits.Count % 8 != 0) bits.Add(false);
            var words = new List<int>(); for (int i = 0; i < bits.Count; i += 8) { int w = 0; for (int k = 0; k < 8; k++) w = (w << 1) | (bits[i + k] ? 1 : 0); words.Add(w); }
            for (int pad = 0xEC; words.Count < QrSpec.DataCodewords(v); pad ^= 0xEC ^ 0x11) words.Add(pad);
            // split into blocks, add error correction, interleave
            var sizes = QrSpec.BlockSizes(v); int ec = QrSpec.EcPerBlock(v);
            var dataBlocks = new List<int[]>(); var ecBlocks = new List<int[]>(); int pos = 0;
            foreach (int sz in sizes) { var blk = words.Skip(pos).Take(sz).ToArray(); pos += sz; dataBlocks.Add(blk); ecBlocks.Add(Gf.Encode(blk, ec)); }
            var all = new List<int>();
            for (int i = 0; i < sizes.Max(); i++) foreach (var blk in dataBlocks) if (i < blk.Length) all.Add(blk[i]);
            for (int i = 0; i < ec; i++) foreach (var blk in ecBlocks) all.Add(blk[i]);
            var stream = new List<bool>(); foreach (int w in all) for (int k = 7; k >= 0; k--) stream.Add(((w >> k) & 1) != 0);
            int n = QrSpec.Size(v); while (stream.Count < n * n) { if (stream.Count >= QrSpec.TotalCodewords(v) * 8 + 7) break; stream.Add(false); }

            var fn = QrSpec.FunctionModules(v);
            bool[,] best = null; int bestScore = int.MaxValue;
            for (int mask = 0; mask < 8; mask++)
            {
                var m = new bool[n, n];
                DrawFunctionPatterns(m, v, mask);
                int bi = 0;
                for (int right = n - 1; right >= 1; right -= 2)
                {
                    if (right == 6) right = 5;
                    for (int vert = 0; vert < n; vert++)
                        for (int j = 0; j < 2; j++)
                        {
                            int x = right - j; bool up = ((right + 1) & 2) == 0; int y = up ? n - 1 - vert : vert;
                            if (fn[y, x]) continue;
                            bool dark = bi < stream.Count && stream[bi]; bi++;
                            m[y, x] = dark ^ MaskBit(mask, x, y);
                        }
                }
                int score = Penalty(m, n);
                if (score < bestScore) { bestScore = score; best = m; }
            }
            return best;
        }

        public static bool MaskBit(int mask, int x, int y)
        {
            switch (mask)
            {
                case 0: return (x + y) % 2 == 0;
                case 1: return y % 2 == 0;
                case 2: return x % 3 == 0;
                case 3: return (x + y) % 3 == 0;
                case 4: return (x / 3 + y / 2) % 2 == 0;
                case 5: return x * y % 2 + x * y % 3 == 0;
                case 6: return (x * y % 2 + x * y % 3) % 2 == 0;
                default: return ((x + y) % 2 + x * y % 3) % 2 == 0;
            }
        }

        static void DrawFunctionPatterns(bool[,] m, int v, int mask)
        {
            int n = m.GetLength(0);
            Action<int, int, bool> set = (x, y, d) => { if (x >= 0 && y >= 0 && x < n && y < n) m[y, x] = d; };
            for (int i = 0; i < n; i++) { set(6, i, i % 2 == 0); set(i, 6, i % 2 == 0); }
            Action<int, int> finder = (cx, cy) =>
            {
                for (int dy = -4; dy <= 4; dy++) for (int dx = -4; dx <= 4; dx++)
                { int d = Math.Max(Math.Abs(dx), Math.Abs(dy)); set(cx + dx, cy + dy, d != 2 && d != 4); }
            };
            finder(3, 3); finder(n - 4, 3); finder(3, n - 4);
            var c = QrSpec.AlignCenters(v);
            for (int i = 0; i < c.Length; i++) for (int j = 0; j < c.Length; j++)
            {
                if ((i == 0 && j == 0) || (i == 0 && j == c.Length - 1) || (i == c.Length - 1 && j == 0)) continue;
                for (int dy = -2; dy <= 2; dy++) for (int dx = -2; dx <= 2; dx++) set(c[i] + dx, c[j] + dy, Math.Max(Math.Abs(dx), Math.Abs(dy)) != 1);
            }
            // format information (level M = 00, then the mask), written twice
            int data = (0 << 3) | mask, rem = data;
            for (int i = 0; i < 10; i++) rem = (rem << 1) ^ ((rem >> 9) * 0x537);
            int fbits = ((data << 10) | rem) ^ 0x5412;
            Func<int, bool> bit = i => ((fbits >> i) & 1) != 0;
            for (int i = 0; i <= 5; i++) set(8, i, bit(i));
            set(8, 7, bit(6)); set(8, 8, bit(7)); set(7, 8, bit(8));
            for (int i = 9; i < 15; i++) set(14 - i, 8, bit(i));
            for (int i = 0; i < 8; i++) set(n - 1 - i, 8, bit(i));
            for (int i = 8; i < 15; i++) set(8, n - 15 + i, bit(i));
            set(8, n - 8, true);
            if (v >= 7)
            {
                int r = v; for (int i = 0; i < 12; i++) r = (r << 1) ^ ((r >> 11) * 0x1F25);
                int vbits = (v << 12) | r;
                for (int i = 0; i < 18; i++) { bool d = ((vbits >> i) & 1) != 0; int a = n - 11 + i % 3, b = i / 3; set(a, b, d); set(b, a, d); }
            }
        }

        // the standard four penalty rules: runs, 2x2 blocks, finder look-alikes, dark/light balance
        static int Penalty(bool[,] m, int n)
        {
            int score = 0;
            for (int pass = 0; pass < 2; pass++)
                for (int a = 0; a < n; a++)
                {
                    int run = 1; bool prev = pass == 0 ? m[a, 0] : m[0, a];
                    for (int b = 1; b < n; b++)
                    {
                        bool cur = pass == 0 ? m[a, b] : m[b, a];
                        if (cur == prev) { run++; if (run == 5) score += 3; else if (run > 5) score++; } else { run = 1; prev = cur; }
                    }
                    // 1:1:3:1:1 look-alike with light space
                    for (int b = 0; b + 10 < n; b++)
                    {
                        Func<int, bool> g = k => pass == 0 ? m[a, b + k] : m[b + k, a];
                        bool p1 = g(0) && !g(1) && g(2) && g(3) && g(4) && !g(5) && g(6) && !g(7) && !g(8) && !g(9) && !g(10);
                        bool p2 = !g(0) && !g(1) && !g(2) && !g(3) && g(4) && !g(5) && g(6) && g(7) && g(8) && !g(9) && g(10);
                        if (p1 || p2) score += 40;
                    }
                }
            for (int y = 0; y < n - 1; y++) for (int x = 0; x < n - 1; x++) if (m[y, x] == m[y, x + 1] && m[y, x] == m[y + 1, x] && m[y, x] == m[y + 1, x + 1]) score += 3;
            int dark = 0; for (int y = 0; y < n; y++) for (int x = 0; x < n; x++) if (m[y, x]) dark++;
            int k = (int)Math.Ceiling(Math.Abs(dark * 20 - n * n * 10) / (double)(n * n)) - 1;
            return score + Math.Max(0, k) * 10;
        }
    }

    /// <summary>Reads QR codes from a picture (as a grid of brightness values), tolerating rotation, mild perspective, blur and noise.</summary>
    public static class QrReader
    {
        sealed class Finder { public double X, Y, Size; public int Count = 1; }

        /// <summary>The text in the first readable QR code found, or null. `lum` = brightness 0..255, row by row.</summary>
        public static string Decode(byte[] lum, int w, int h)
        {
            // look at the picture as it is (a small code needs every pixel), then at half size (helps with a very slanted or blurred code); huge pictures start at half size
            int shrink = 0; while (w > 1500) { lum = Halve(lum, ref w, ref h); shrink++; }
            string t = DecodeOne(lum, w, h); if (t != null) return t;
            if (w >= 800) { int w2 = w, h2 = h; var half = Halve(lum, ref w2, ref h2); t = DecodeOne(half, w2, h2); }
            return t;
        }

        static byte[] Halve(byte[] lum, ref int w, ref int h)
        {
            int nw = w / 2, nh = h / 2; var s = new byte[nw * nh];
            for (int y = 0; y < nh; y++) for (int x = 0; x < nw; x++) s[y * nw + x] = (byte)((lum[2 * y * w + 2 * x] + lum[2 * y * w + 2 * x + 1] + lum[(2 * y + 1) * w + 2 * x] + lum[(2 * y + 1) * w + 2 * x + 1]) / 4);
            w = nw; h = nh; return s;
        }

        static string DecodeOne(byte[] lum, int w, int h)
        {
            foreach (int mode in new[] { 0, 1, 2 })
            {
                var bin = Binarize(lum, w, h, mode);
                string t = TryBinary(bin, w, h);
                if (t != null) return t;
            }
            return null;
        }

        // mode 0: local average (copes with uneven light), 1: local average over a larger area, 2: one global threshold
        static bool[] Binarize(byte[] lum, int w, int h, int mode)
        {
            var bin = new bool[w * h];
            if (mode == 2)
            {
                var hist = new int[256]; foreach (byte b in lum) hist[b]++;
                long total = lum.Length, sumAll = 0; for (int i = 0; i < 256; i++) sumAll += (long)i * hist[i];
                long sumB = 0, wB = 0; double max = -1; int thr = 128;
                for (int i = 0; i < 256; i++) { wB += hist[i]; if (wB == 0) continue; long wF = total - wB; if (wF == 0) break; sumB += (long)i * hist[i]; double mB = (double)sumB / wB, mF = (double)(sumAll - sumB) / wF, between = (double)wB * wF * (mB - mF) * (mB - mF); if (between > max) { max = between; thr = i; } }
                for (int i = 0; i < bin.Length; i++) bin[i] = lum[i] <= thr;
                return bin;
            }
            int win = Math.Max(15, (mode == 0 ? Math.Min(w, h) / 8 : Math.Min(w, h) / 4) | 1);
            var integral = new long[(w + 1) * (h + 1)];
            for (int y = 0; y < h; y++) { long row = 0; for (int x = 0; x < w; x++) { row += lum[y * w + x]; integral[(y + 1) * (w + 1) + x + 1] = integral[y * (w + 1) + x + 1] + row; } }
            int r = win / 2;
            for (int y = 0; y < h; y++)
            {
                int y0 = Math.Max(0, y - r), y1 = Math.Min(h, y + r + 1);
                for (int x = 0; x < w; x++)
                {
                    int x0 = Math.Max(0, x - r), x1 = Math.Min(w, x + r + 1);
                    long sum = integral[y1 * (w + 1) + x1] - integral[y0 * (w + 1) + x1] - integral[y1 * (w + 1) + x0] + integral[y0 * (w + 1) + x0];
                    double mean = (double)sum / ((x1 - x0) * (y1 - y0));
                    bin[y * w + x] = lum[y * w + x] < mean * 0.92;
                }
            }
            return bin;
        }

        static string TryBinary(bool[] bin, int w, int h)
        {
            var finders = FindFinders(bin, w, h);
            if (finders.Count < 3) return null;
            // try the best-looking triples first
            var triples = new List<Tuple<double, Finder[]>>();
            for (int i = 0; i < finders.Count; i++) for (int j = i + 1; j < finders.Count; j++) for (int k = j + 1; k < finders.Count; k++)
            {
                var f = new[] { finders[i], finders[j], finders[k] };
                double smin = f.Min(q => q.Size), smax = f.Max(q => q.Size);
                if (smax > smin * 1.6) continue;
                var ordered = Orient(f); if (ordered == null) continue;
                double a = Dist(ordered[0], ordered[1]), b = Dist(ordered[0], ordered[2]), c = Dist(ordered[1], ordered[2]);
                double sidesDiff = Math.Abs(a - b) / Math.Max(a, b), hypErr = Math.Abs(c - Math.Sqrt(a * a + b * b)) / c;
                if (sidesDiff > 0.35 || hypErr > 0.25) continue;
                triples.Add(Tuple.Create(sidesDiff + hypErr, ordered));
            }
            foreach (var t in triples.OrderBy(x => x.Item1).Take(6))
            {
                string s = SampleAndDecode(bin, w, h, t.Item2);
                if (s != null) return s;
            }
            return null;
        }

        static double Dist(Finder a, Finder b) { double dx = a.X - b.X, dy = a.Y - b.Y; return Math.Sqrt(dx * dx + dy * dy); }

        /// <summary>Which finder is top-left (the corner of the right angle), top-right and bottom-left.</summary>
        static Finder[] Orient(Finder[] f)
        {
            double d01 = Dist(f[0], f[1]), d02 = Dist(f[0], f[2]), d12 = Dist(f[1], f[2]);
            Finder tl, a, b;
            if (d12 >= d01 && d12 >= d02) { tl = f[0]; a = f[1]; b = f[2]; }
            else if (d02 >= d01 && d02 >= d12) { tl = f[1]; a = f[0]; b = f[2]; }
            else { tl = f[2]; a = f[0]; b = f[1]; }
            double cross = (a.X - tl.X) * (b.Y - tl.Y) - (a.Y - tl.Y) * (b.X - tl.X);
            return cross > 0 ? new[] { tl, a, b } : new[] { tl, b, a };   // image y points down, so a positive cross product means b is clockwise from a
        }

        // finder patterns: look along rows for dark-light-dark-light-dark in the ratio 1:1:3:1:1, then confirm down the column and across the row
        static List<Finder> FindFinders(bool[] bin, int w, int h)
        {
            var found = new List<Finder>();
            var c = new int[5];
            for (int y = 0; y < h; y++)
            {
                Array.Clear(c, 0, 5); int s = 0;
                for (int x = 0; x < w; x++)
                {
                    if (bin[y * w + x])
                    {
                        if ((s & 1) == 1) s++;             // was counting light: start the next dark run
                        c[s]++;
                    }
                    else if ((s & 1) == 0)                 // was counting dark: a light run starts
                    {
                        if (s == 4)
                        {
                            if (LooksLikeFinder(c)) Consider(found, bin, w, h, x - c[4] - c[3] - c[2] / 2.0 - 0.5, y, c);
                            c[0] = c[2]; c[1] = c[3]; c[2] = c[4]; c[3] = 1; c[4] = 0; s = 3;   // slide the window along
                        }
                        else { s++; c[s]++; }
                    }
                    else c[s]++;
                }
                if (s == 4 && LooksLikeFinder(c)) Consider(found, bin, w, h, w - c[4] - c[3] - c[2] / 2.0 - 0.5, y, c);
            }
            return found.Where(f => f.Count >= 2).OrderByDescending(f => f.Count).Take(14).ToList();   // a real finder shows up on several rows
        }

        static bool LooksLikeFinder(int[] r)
        {
            int total = r[0] + r[1] + r[2] + r[3] + r[4]; if (total < 7) return false;
            double m = total / 7.0, tol = m * 0.6;
            return Math.Abs(r[0] - m) < tol && Math.Abs(r[1] - m) < tol && Math.Abs(r[2] - 3 * m) < 3 * tol && Math.Abs(r[3] - m) < tol && Math.Abs(r[4] - m) < tol;
        }

        static void Consider(List<Finder> found, bool[] bin, int w, int h, double cx, int cy, int[] runs)
        {
            double size = (runs[0] + runs[1] + runs[2] + runs[3] + runs[4]) / 7.0;
            int x = (int)Math.Round(cx);
            double vy, vsize, hx, hsize;
            if (!Cross(bin, w, h, x, cy, 0, 1, size, out vy, out vsize)) return;                         // down the column through the candidate
            if (!Cross(bin, w, h, x, (int)Math.Round(vy), 1, 0, size, out hx, out hsize)) return;        // and across the row through the corrected centre
            double fx = hx, fy = vy, sz = (size + vsize + hsize) / 3;
            foreach (var f in found)
                if (Math.Abs(f.X - fx) < f.Size * 2 && Math.Abs(f.Y - fy) < f.Size * 2 && Math.Abs(f.Size - sz) < f.Size * 0.5)
                { f.X = (f.X * f.Count + fx) / (f.Count + 1); f.Y = (f.Y * f.Count + fy) / (f.Count + 1); f.Size = (f.Size * f.Count + sz) / (f.Count + 1); f.Count++; return; }
            found.Add(new Finder { X = fx, Y = fy, Size = sz });
        }

        // measure the five runs through (x,y) in direction (dx,dy) (one of them is 0); gives the centre of the middle run along that direction
        static bool Cross(bool[] bin, int w, int h, int x, int y, int dx, int dy, double expect, out double center, out double size)
        {
            center = 0; size = 0;
            if (x < 0 || y < 0 || x >= w || y >= h || !bin[y * w + x]) return false;
            Func<int, int, bool> inside = (px, py) => px >= 0 && py >= 0 && px < w && py < h;
            var r = new int[5]; int cx, cy, n;
            cx = x; cy = y; n = 0; while (inside(cx, cy) && bin[cy * w + cx]) { n++; cx -= dx; cy -= dy; } int back = n;
            n = 0; while (inside(cx, cy) && !bin[cy * w + cx]) { n++; cx -= dx; cy -= dy; } r[1] = n;
            n = 0; while (inside(cx, cy) && bin[cy * w + cx]) { n++; cx -= dx; cy -= dy; } r[0] = n;
            cx = x + dx; cy = y + dy; n = 0; while (inside(cx, cy) && bin[cy * w + cx]) { n++; cx += dx; cy += dy; } int fwd = n;
            n = 0; while (inside(cx, cy) && !bin[cy * w + cx]) { n++; cx += dx; cy += dy; } r[3] = n;
            n = 0; while (inside(cx, cy) && bin[cy * w + cx]) { n++; cx += dx; cy += dy; } r[4] = n;
            r[2] = back + fwd;
            if (!LooksLikeFinder(r)) return false;
            size = (r[0] + r[1] + r[2] + r[3] + r[4]) / 7.0;
            if (Math.Abs(size - expect) > expect * 0.8 + 1) return false;
            int along = dx != 0 ? x : y;
            center = (along - (back - 1)) + (r[2] - 1) / 2.0;
            return true;
        }

        static string SampleAndDecode(bool[] bin, int w, int h, Finder[] f)
        {
            var tl = f[0]; var tr = f[1]; var bl = f[2];
            // runs measured along a row look longer when the code is tilted (by up to 1.41x at 45 degrees): undo that to get the true module size
            double phi = Math.Atan2(tr.Y - tl.Y, tr.X - tl.X), tilt = Math.Max(Math.Abs(Math.Cos(phi)), Math.Abs(Math.Sin(phi)));
            double ms = (tl.Size + tr.Size + bl.Size) / 3 * tilt;
            double dimGuess = (Dist(tl, tr) / ms + Dist(tl, bl) / ms) / 2 + 7;
            int ver = (int)Math.Round((dimGuess - 17) / 4);
            foreach (int v in new[] { ver, ver - 1, ver + 1 }.Where(q => q >= 1 && q <= QrSpec.MaxVersion).Distinct())
            {
                int n = QrSpec.Size(v);
                // module (c, r) -> picture, first from the three finder centres alone (affine), then improved with the alignment pattern (perspective)
                Func<double, double, double[]> affine = (c, r) =>
                {
                    double u = (c - 3.5) / (n - 7), t = (r - 3.5) / (n - 7);
                    return new[] { tl.X + u * (tr.X - tl.X) + t * (bl.X - tl.X), tl.Y + u * (tr.Y - tl.Y) + t * (bl.Y - tl.Y) };
                };
                var grids = new List<Func<double, double, double[]>> { affine };
                if (v >= 2)
                {
                    var est = affine(n - 6.5, n - 6.5); var al = FindAlignment(bin, w, h, est[0], est[1], ms);
                    if (al != null) { var H = Homography(new[] { new[] { 3.5, 3.5 }, new[] { n - 3.5, 3.5 }, new[] { 3.5, n - 3.5 }, new[] { n - 6.5, n - 6.5 } }, new[] { new[] { tl.X, tl.Y }, new[] { tr.X, tr.Y }, new[] { bl.X, bl.Y }, new[] { al[0], al[1] } }); if (H != null) grids.Insert(0, (c, r) => Apply(H, c, r)); }
                }
                foreach (var map in grids)
                {
                    var m = new bool[n, n]; bool ok = true;
                    for (int r = 0; r < n && ok; r++) for (int c = 0; c < n; c++)
                    {
                        var p = map(c + 0.5, r + 0.5);
                        int dark = 0, tot = 0;
                        for (int oy = -1; oy <= 1; oy++) for (int ox = -1; ox <= 1; ox++) { int px = (int)Math.Round(p[0] + ox * ms * 0.2), py = (int)Math.Round(p[1] + oy * ms * 0.2); if (px < 0 || py < 0 || px >= w || py >= h) continue; tot++; if (bin[py * w + px]) dark++; }
                        if (tot == 0) { ok = false; break; }
                        m[r, c] = dark * 2 > tot;
                    }
                    if (!ok) continue;
                    string s = DecodeMatrix(m, v);
                    if (s != null) return s;
                }
            }
            return null;
        }

        static double[] FindAlignment(bool[] bin, int w, int h, double ex, double ey, double ms)
        {
            // the alignment pattern is a dark 3x3 core inside a light ring inside a dark ring: look for its centre near where it should be
            double best = double.MaxValue; double bx = 0, by = 0; int rad = (int)Math.Ceiling(ms * 4);
            for (int dy = -rad; dy <= rad; dy++) for (int dx = -rad; dx <= rad; dx++)
            {
                double cx = ex + dx, cy = ey + dy; double score = 0;
                for (int ry = -2; ry <= 2; ry++) for (int rx = -2; rx <= 2; rx++)
                {
                    int px = (int)Math.Round(cx + rx * ms), py = (int)Math.Round(cy + ry * ms);
                    if (px < 0 || py < 0 || px >= w || py >= h) { score += 1; continue; }
                    bool want = Math.Max(Math.Abs(rx), Math.Abs(ry)) != 1;
                    if (bin[py * w + px] != want) score += 1;
                }
                score += Math.Sqrt(dx * dx + dy * dy) * 0.02;
                if (score < best) { best = score; bx = cx; by = cy; }
            }
            return best <= 4 ? new[] { bx, by } : null;
        }

        // 3x3 homography from four point pairs (module space -> picture space), solved with Gaussian elimination
        static double[] Homography(double[][] src, double[][] dst)
        {
            var A = new double[8, 9];
            for (int i = 0; i < 4; i++)
            {
                double x = src[i][0], y = src[i][1], u = dst[i][0], v = dst[i][1];
                int r = 2 * i;
                A[r, 0] = x; A[r, 1] = y; A[r, 2] = 1; A[r, 6] = -u * x; A[r, 7] = -u * y; A[r, 8] = u;
                A[r + 1, 3] = x; A[r + 1, 4] = y; A[r + 1, 5] = 1; A[r + 1, 6] = -v * x; A[r + 1, 7] = -v * y; A[r + 1, 8] = v;
            }
            for (int c = 0; c < 8; c++)
            {
                int p = c; for (int r = c + 1; r < 8; r++) if (Math.Abs(A[r, c]) > Math.Abs(A[p, c])) p = r;
                if (Math.Abs(A[p, c]) < 1e-9) return null;
                if (p != c) for (int k = 0; k < 9; k++) { double t = A[c, k]; A[c, k] = A[p, k]; A[p, k] = t; }
                for (int r = 0; r < 8; r++) if (r != c) { double f = A[r, c] / A[c, c]; for (int k = c; k < 9; k++) A[r, k] -= f * A[c, k]; }
            }
            var H = new double[8]; for (int i = 0; i < 8; i++) H[i] = A[i, 8] / A[i, i];
            return H;
        }
        static double[] Apply(double[] H, double x, double y) { double d = H[6] * x + H[7] * y + 1; return new[] { (H[0] * x + H[1] * y + H[2]) / d, (H[3] * x + H[4] * y + H[5]) / d }; }

        /// <summary>Turns a sampled grid of modules into text (level M only). Null when it does not read.</summary>
        public static string DecodeMatrix(bool[,] m, int v)
        {
            int n = QrSpec.Size(v);
            // format information: two copies, each 15 bits; pick the closest valid pattern
            Func<int[][], int> readBits = pos => { int val = 0; for (int i = 0; i < 15; i++) val |= (m[pos[i][1], pos[i][0]] ? 1 : 0) << i; return val; };
            var copy1 = new List<int[]>(); for (int i = 0; i <= 5; i++) copy1.Add(new[] { 8, i }); copy1.Add(new[] { 8, 7 }); copy1.Add(new[] { 8, 8 }); copy1.Add(new[] { 7, 8 }); for (int i = 9; i < 15; i++) copy1.Add(new[] { 14 - i, 8 });
            var copy2 = new List<int[]>(); for (int i = 0; i < 8; i++) copy2.Add(new[] { n - 1 - i, 8 }); for (int i = 8; i < 15; i++) copy2.Add(new[] { 8, n - 15 + i });
            int bestMask = -1, bestDist = 99;
            for (int data = 0; data < 32; data++)
            {
                int rem = data; for (int i = 0; i < 10; i++) rem = (rem << 1) ^ ((rem >> 9) * 0x537);
                int code = ((data << 10) | rem) ^ 0x5412;
                if (((data >> 3) & 3) != 0) continue;      // level M only
                int d = Math.Min(Pop(code ^ readBits(copy1.ToArray())), Pop(code ^ readBits(copy2.ToArray())));
                if (d < bestDist) { bestDist = d; bestMask = data & 7; }
            }
            if (bestMask < 0 || bestDist > 3) return null;
            var fn = QrSpec.FunctionModules(v);
            var bits = new List<bool>();
            for (int right = n - 1; right >= 1; right -= 2)
            {
                if (right == 6) right = 5;
                for (int vert = 0; vert < n; vert++)
                    for (int j = 0; j < 2; j++)
                    {
                        int x = right - j; bool up = ((right + 1) & 2) == 0; int y = up ? n - 1 - vert : vert;
                        if (fn[y, x]) continue;
                        bits.Add(m[y, x] ^ QrEncoder.MaskBit(bestMask, x, y));
                    }
            }
            int total = QrSpec.TotalCodewords(v); if (bits.Count < total * 8) return null;
            var words = new int[total]; for (int i = 0; i < total; i++) { int wv = 0; for (int k = 0; k < 8; k++) wv = (wv << 1) | (bits[i * 8 + k] ? 1 : 0); words[i] = wv; }
            // undo the interleaving, repair each block, join the data parts
            var sizes = QrSpec.BlockSizes(v); int ec = QrSpec.EcPerBlock(v); int blocks = sizes.Length;
            var data2 = sizes.Select(s => new int[s + ec]).ToArray(); int pos = 0;
            for (int i = 0; i < sizes.Max(); i++) for (int b = 0; b < blocks; b++) if (i < sizes[b]) data2[b][i] = words[pos++];
            for (int i = 0; i < ec; i++) for (int b = 0; b < blocks; b++) data2[b][sizes[b] + i] = words[pos++];
            var stream = new List<int>();
            for (int b = 0; b < blocks; b++) { if (!Gf.Decode(data2[b], ec)) return null; stream.AddRange(data2[b].Take(sizes[b])); }
            // parse: only byte mode (what the encoder writes)
            var bs = new List<bool>(); foreach (int wv in stream) for (int k = 7; k >= 0; k--) bs.Add(((wv >> k) & 1) != 0);
            int p = 0; Func<int, int> take = cnt => { int val = 0; for (int i = 0; i < cnt; i++) val = (val << 1) | (bs[p++] ? 1 : 0); return val; };
            if (take(4) != 4) return null;
            int len = take(v < 10 ? 8 : 16); if (len < 0 || p + len * 8 > bs.Count) return null;
            var bytes = new byte[len]; for (int i = 0; i < len; i++) bytes[i] = (byte)take(8);
            return Encoding.UTF8.GetString(bytes);
        }

        static int Pop(int x) { int c = 0; while (x != 0) { c += x & 1; x >>= 1; } return c; }
    }
}
