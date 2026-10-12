using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace Ledjoro
{
    /// <summary>
    /// A small PDF writer for invoices and credit memos: text in Helvetica, lines, filled boxes and one JPEG picture, on US Letter pages.
    /// No library and no printer driver: the file is built directly, so it looks the same on every computer.
    /// Coordinates are in points (1/72 inch) measured from the top-left corner of the page.
    /// </summary>
    public sealed class PdfDocument
    {
        public const double PageWidth = 612, PageHeight = 792;
        readonly List<PdfPage> pages = new List<PdfPage>();
        readonly List<byte[]> jpegs = new List<byte[]>(); readonly List<int[]> jpegSizes = new List<int[]>();
        public string Title = "";

        public PdfPage NewPage() { var p = new PdfPage(this); pages.Add(p); return p; }
        public int PageCount { get { return pages.Count; } }

        /// <summary>Adds a JPEG picture; the returned number is used with PdfPage.Image.</summary>
        public int AddJpeg(byte[] data, int width, int height) { jpegs.Add(data); jpegSizes.Add(new[] { width, height }); return jpegs.Count - 1; }

        // Helvetica and Helvetica-Bold character widths for the characters from space (32) to tilde (126), in 1/1000 of the font size.
        static readonly int[] Regular = { 278, 278, 355, 556, 556, 889, 667, 191, 333, 333, 389, 584, 278, 333, 278, 278, 556, 556, 556, 556, 556, 556, 556, 556, 556, 556, 278, 278, 584, 584, 584, 556, 1015,
            667, 667, 722, 722, 667, 611, 778, 722, 278, 500, 667, 556, 833, 722, 778, 667, 778, 722, 667, 611, 722, 667, 944, 667, 667, 611, 278, 278, 278, 469, 556, 333,
            556, 556, 500, 556, 556, 278, 556, 556, 222, 222, 500, 222, 833, 556, 556, 556, 556, 333, 500, 278, 556, 500, 722, 500, 500, 500, 334, 260, 334, 584 };
        static readonly int[] Bold = { 278, 333, 474, 556, 556, 889, 722, 238, 333, 333, 389, 584, 278, 333, 278, 278, 556, 556, 556, 556, 556, 556, 556, 556, 556, 556, 333, 333, 584, 584, 584, 611, 975,
            722, 722, 722, 722, 667, 611, 778, 722, 278, 556, 722, 611, 833, 722, 778, 667, 778, 722, 667, 611, 722, 667, 944, 667, 667, 611, 333, 278, 333, 584, 556, 333,
            556, 611, 556, 611, 556, 333, 611, 611, 278, 278, 556, 278, 889, 611, 611, 611, 611, 389, 556, 333, 611, 556, 778, 556, 556, 500, 389, 280, 389, 584 };

        public static double Width(string s, double size, bool bold)
        {
            var w = bold ? Bold : Regular; double total = 0;
            foreach (char c in s ?? "") total += (c >= 32 && c <= 126) ? w[c - 32] : 556;
            return total * size / 1000.0;
        }

        /// <summary>Breaks text into lines no wider than <paramref name="width"/>, on spaces (and inside a very long word if it must).</summary>
        public static List<string> Wrap(string text, double width, double size, bool bold)
        {
            var lines = new List<string>();
            foreach (var para in (text ?? "").Replace("\r", "").Split('\n'))
            {
                var cur = new StringBuilder();
                foreach (var word0 in para.Split(' '))
                {
                    string word = word0;
                    while (Width(word, size, bold) > width && word.Length > 1)
                    {
                        int n = word.Length; while (n > 1 && Width(word.Substring(0, n), size, bold) > width) n--;
                        if (cur.Length > 0) { lines.Add(cur.ToString()); cur.Clear(); }
                        lines.Add(word.Substring(0, n)); word = word.Substring(n);
                    }
                    string tryLine = cur.Length == 0 ? word : cur + " " + word;
                    if (cur.Length > 0 && Width(tryLine, size, bold) > width) { lines.Add(cur.ToString()); cur.Clear(); cur.Append(word); }
                    else { cur.Clear(); cur.Append(tryLine); }
                }
                lines.Add(cur.ToString());
            }
            return lines;
        }

        static string F(double v) { return v.ToString("0.##", CultureInfo.InvariantCulture); }

        static string Escape(string s)
        {
            var enc = Encoding.GetEncoding(1252, new EncoderReplacementFallback("?"), DecoderFallback.ReplacementFallback);
            var bytes = enc.GetBytes(s ?? ""); var sb = new StringBuilder();
            foreach (byte b in bytes)
            {
                if (b == '(' || b == ')' || b == '\\') sb.Append('\\').Append((char)b);
                else if (b < 32 || b > 126) sb.Append('\\').Append(Convert.ToString(b, 8).PadLeft(3, '0'));
                else sb.Append((char)b);
            }
            return sb.ToString();
        }

        public sealed class PdfPage
        {
            readonly PdfDocument doc; internal readonly StringBuilder Content = new StringBuilder(); internal readonly HashSet<int> Images = new HashSet<int>();
            internal PdfPage(PdfDocument d) { doc = d; }

            /// <summary>Text with its baseline at y. align: 0 left, 1 right (x is the right edge), 2 centred on x. gray 0 = black, 1 = white.</summary>
            public void Text(double x, double y, string s, double size = 10, bool bold = false, int align = 0, double gray = 0)
            {
                if (string.IsNullOrEmpty(s)) return;
                double w = Width(s, size, bold); double left = align == 1 ? x - w : align == 2 ? x - w / 2 : x;
                Content.Append(F(gray)).Append(" g BT /").Append(bold ? "F2 " : "F1 ").Append(F(size)).Append(" Tf ").Append(F(left)).Append(' ').Append(F(PageHeight - y)).Append(" Td (").Append(Escape(s)).Append(") Tj ET\n");
            }

            public void Line(double x1, double y1, double x2, double y2, double width = 0.75, double gray = 0.6)
            {
                Content.Append(F(gray)).Append(" G ").Append(F(width)).Append(" w ").Append(F(x1)).Append(' ').Append(F(PageHeight - y1)).Append(" m ").Append(F(x2)).Append(' ').Append(F(PageHeight - y2)).Append(" l S\n");
            }

            public void Box(double x, double y, double w, double h, double gray)
            {
                Content.Append(F(gray)).Append(" g ").Append(F(x)).Append(' ').Append(F(PageHeight - y - h)).Append(' ').Append(F(w)).Append(' ').Append(F(h)).Append(" re f\n");
            }

            public void Image(int index, double x, double y, double w, double h)
            {
                Images.Add(index);
                Content.Append("q ").Append(F(w)).Append(" 0 0 ").Append(F(h)).Append(' ').Append(F(x)).Append(' ').Append(F(PageHeight - y - h)).Append(" cm /Im").Append(index).Append(" Do Q\n");
            }
        }

        /// <summary>Writes the file with a small footer on every page: <paramref name="leftText"/> on the left and "Page n of N" on the right.</summary>
        public byte[] ToBytesWithFooter(string leftText)
        {
            if (pages.Count == 0) NewPage();
            for (int i = 0; i < pages.Count; i++)
            {
                if (!string.IsNullOrEmpty(leftText)) pages[i].Text(48, PageHeight - 34, leftText.Length > 0 && !leftText.StartsWith("Tax ID") ? "Tax ID: " + leftText : leftText, 8.5, false, 0, 0.5);
                pages[i].Text(PageWidth - 48, PageHeight - 34, "Page " + (i + 1) + " of " + pages.Count, 8.5, false, 1, 0.5);
            }
            return ToBytes();
        }

        public byte[] ToBytes()
        {
            if (pages.Count == 0) NewPage();
            var objs = new List<byte[]>();   // object number n is objs[n - 1]
            Func<string, byte[]> A = s => Encoding.GetEncoding(28591).GetBytes(s);
            int fontRegular = 3, fontBold = 4, first = 5, imgBase = first + pages.Count * 2;   // objects: 1 catalog, 2 pages, 3-4 fonts, then page/content pairs, then images
            var kids = string.Join(" ", Enumerable.Range(0, pages.Count).Select(i => (first + i * 2) + " 0 R"));
            objs.Add(A("<< /Type /Catalog /Pages 2 0 R >>"));
            objs.Add(A("<< /Type /Pages /Kids [" + kids + "] /Count " + pages.Count + " >>"));
            objs.Add(A("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>"));
            objs.Add(A("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>"));
            for (int i = 0; i < pages.Count; i++)
            {
                var p = pages[i]; int content = first + i * 2 + 1;
                string xo = p.Images.Count == 0 ? "" : " /XObject << " + string.Join(" ", p.Images.Select(k => "/Im" + k + " " + (imgBase + k) + " 0 R")) + " >>";
                objs.Add(A("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 " + F(PageWidth) + " " + F(PageHeight) + "] /Contents " + content + " 0 R /Resources << /Font << /F1 " + fontRegular + " 0 R /F2 " + fontBold + " 0 R >>" + xo + " >> >>"));
                var body = A(p.Content.ToString());
                using (var ms = new MemoryStream())
                {
                    var head = A("<< /Length " + body.Length + " >>\nstream\n"); ms.Write(head, 0, head.Length); ms.Write(body, 0, body.Length);
                    var tail = A("\nendstream"); ms.Write(tail, 0, tail.Length); objs.Add(ms.ToArray());
                }
            }
            for (int k = 0; k < jpegs.Count; k++)
            {
                using (var ms = new MemoryStream())
                {
                    var head = A("<< /Type /XObject /Subtype /Image /Width " + jpegSizes[k][0] + " /Height " + jpegSizes[k][1] + " /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /Length " + jpegs[k].Length + " >>\nstream\n");
                    ms.Write(head, 0, head.Length); ms.Write(jpegs[k], 0, jpegs[k].Length);
                    var tail = A("\nendstream"); ms.Write(tail, 0, tail.Length); objs.Add(ms.ToArray());
                }
            }
            int info = objs.Count + 1;
            objs.Add(A("<< /Title (" + Escape(Title) + ") /Producer (Ledjoro) >>"));
            using (var o = new MemoryStream())
            {
                var hdr = A("%PDF-1.4\n"); o.Write(hdr, 0, hdr.Length);
                var offsets = new List<long>();
                for (int n = 0; n < objs.Count; n++)
                {
                    offsets.Add(o.Position);
                    var b = A((n + 1) + " 0 obj\n"); o.Write(b, 0, b.Length); o.Write(objs[n], 0, objs[n].Length);
                    var e = A("\nendobj\n"); o.Write(e, 0, e.Length);
                }
                long xref = o.Position; var sb = new StringBuilder();
                sb.Append("xref\n0 ").Append(objs.Count + 1).Append("\n0000000000 65535 f \n");
                foreach (var off in offsets) sb.Append(off.ToString("0000000000", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
                sb.Append("trailer\n<< /Size ").Append(objs.Count + 1).Append(" /Root 1 0 R /Info ").Append(info).Append(" 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
                var tr = A(sb.ToString()); o.Write(tr, 0, tr.Length);
                return o.ToArray();
            }
        }
    }
}
