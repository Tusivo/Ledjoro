using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;

namespace Ledjoro
{
    /// <summary>Lays out an invoice, or a credit memo, as a one-or-more page PDF with the company's logo and address.</summary>
    static class InvoicePdf
    {
        const double Left = 48, Right = PdfDocument.PageWidth - 48, Bottom = PdfDocument.PageHeight - 60;

        /// <summary>
        /// <paramref name="d"/> is an invoice (api/documents/ID) or a credit memo (api/credits/ID); <paramref name="profile"/> is api/profile;
        /// <paramref name="contact"/> is the customer's row from api/contacts (may be null).
        /// </summary>
        public static byte[] Build(Jv d, bool credit, Jv profile, Jv contact)
        {
            var doc = new PdfDocument { Title = (credit ? "Credit memo " : "Invoice ") + d["number"].Str };
            int logo = -1; double logoW = 0, logoH = 0;
            string b64 = profile == null ? "" : profile["logo"].Str ?? "";
            if (b64.Length > 0)
            {
                try
                {
                    var bytes = Convert.FromBase64String(b64);
                    using (var ms = new MemoryStream(bytes)) using (var img = Image.FromStream(ms))
                    {
                        double k = Math.Min(170.0 / img.Width, 70.0 / img.Height); if (k > 1) k = 1;   // never enlarge a small picture
                        logoW = img.Width * k; logoH = img.Height * k; logo = doc.AddJpeg(bytes, img.Width, img.Height);
                    }
                }
                catch (Exception) { logo = -1; }
            }

            string status = d["status"].Str; bool inv = !credit;
            long total = d["total_cents"].Long, paid = credit ? d["applied_cents"].Long + d["refunded_cents"].Long : d["paid_cents"].Long;
            string dateIso = credit ? d["memo_date"].Str : d["doc_date"].Str;
            var rows = d["lines"].Items.ToList();

            Func<PdfDocument.PdfPage, bool, double> header = (p, first) =>
            {
                double y = 56;
                if (first)
                {
                    double x = Left;
                    if (logo >= 0) { p.Image(logo, Left, 44, logoW, logoH); y = 44 + logoH + 16; }
                    string name = profile != null && (profile["name"].Str ?? "").Length > 0 ? profile["name"].Str : App.Api.Company;
                    p.Text(x, y, name, 15, true); y += 15;
                    var addr = new List<string>();
                    if (profile != null)
                    {
                        if ((profile["street"].Str ?? "").Length > 0) addr.Add(profile["street"].Str);
                        if ((profile["street2"].Str ?? "").Length > 0) addr.Add(profile["street2"].Str);
                        string cs = string.Join(", ", new[] { profile["city"].Str, ((profile["state"].Str ?? "") + " " + (profile["zip"].Str ?? "")).Trim() }.Where(v => !string.IsNullOrEmpty(v)));
                        if (cs.Length > 0) addr.Add(cs);
                        if ((profile["phone"].Str ?? "").Length > 0) addr.Add(profile["phone"].Str);
                        if ((profile["email"].Str ?? "").Length > 0) addr.Add(profile["email"].Str);
                        if ((profile["website"].Str ?? "").Length > 0) addr.Add(profile["website"].Str);
                    }
                    foreach (var a in addr) { p.Text(x, y, a, 9.5, false, 0, 0.3); y += 12.5; }
                    // title and numbers on the right
                    p.Text(Right, 70, credit ? "CREDIT MEMO" : "INVOICE", 22, true, 1, 0.15);
                    double my = 94;
                    Action<string, string> meta = (k, v) => { p.Text(Right - 120, my, k, 9.5, false, 1, 0.45); p.Text(Right, my, v, 10, true, 1); my += 15; };
                    meta(credit ? "Credit memo no." : "Invoice no.", d["number"].Str);
                    meta("Date", Fmt.Show(dateIso));
                    if (inv) meta("Due", Fmt.Show(d["due_date"].Str));
                    if (status == "void") meta("Status", "VOID"); else if (status == "paid") meta("Status", "PAID");
                    y = Math.Max(y, my) + 22;
                    // customer
                    p.Text(Left, y, credit ? "CREDIT TO" : "BILL TO", 8.5, true, 0, 0.45); y += 14;
                    p.Text(Left, y, d["contact_name"].Str, 11, true); y += 14;
                    string address = contact == null || contact.IsNull ? "" : (contact["address"].Str ?? "");
                    foreach (var line in address.Replace("\r", "").Split('\n').Where(l => l.Trim().Length > 0)) { p.Text(Left, y, line, 10, false, 0, 0.25); y += 12.5; }
                    y += 18;
                }
                else
                {
                    p.Text(Left, 56, (credit ? "Credit memo " : "Invoice ") + d["number"].Str + " (continued)", 10, true, 0, 0.35);
                    y = 80;
                }
                // column headings
                p.Box(Left, y, Right - Left, 20, 0.93);
                p.Text(Left + 8, y + 14, "Description", 9, true, 0, 0.25);
                p.Text(Right - 190, y + 14, "Qty", 9, true, 1, 0.25);
                p.Text(Right - 110, y + 14, "Price", 9, true, 1, 0.25);
                p.Text(Right - 8, y + 14, "Amount", 9, true, 1, 0.25);
                return y + 28;
            };

            var page = doc.NewPage(); double yy = header(page, true);
            foreach (var l in rows)
            {
                var lines = PdfDocument.Wrap(l["description"].Str, 300, 10, false); double h = Math.Max(1, lines.Count) * 13 + 8;
                if (yy + h > Bottom - 20) { page = doc.NewPage(); yy = header(page, false); }
                double ty = yy + 11;
                foreach (var line in lines) { page.Text(Left + 8, ty, line, 10); ty += 13; }
                page.Text(Right - 190, yy + 11, Fmt.Qty(l["qty_milli"].Long), 10, false, 1);
                page.Text(Right - 110, yy + 11, Fmt.Money(l["unit_price_cents"].Long), 10, false, 1);
                page.Text(Right - 8, yy + 11, Fmt.Money(l["amount_cents"].Long), 10, false, 1);
                yy += h; page.Line(Left, yy - 2, Right, yy - 2, 0.5, 0.85);
            }

            // totals: kept together, so move to a new page if they would not fit
            var totals = new List<string[]>();   // label, value, bold
            totals.Add(new[] { "Subtotal", Fmt.Money(d["subtotal_cents"].Long), "0" });
            foreach (var t in d["tax"].Items) totals.Add(new[] { "Sales tax: " + t["name"].Str + " (" + SalesTaxScreen.Pct(t["rate_milli"].Long) + ")", Fmt.Money(t["tax_cents"].Long), "0" });
            totals.Add(new[] { credit ? "Credit total" : "Total", Fmt.Money(total), "1" });
            if (inv)
            {
                long credits = d["credits"].Items.Sum(c => c["amount_cents"].Long), pay = paid - credits;
                if (credits > 0) totals.Add(new[] { "Credits applied", Fmt.Money(-credits), "0" });
                if (pay > 0) totals.Add(new[] { "Payments received", Fmt.Money(-pay), "0" });
                totals.Add(new[] { "Balance due", Fmt.Money(status == "void" ? 0 : total - paid), "2" });
            }
            else
            {
                if (d["applied_cents"].Long > 0) totals.Add(new[] { "Applied to invoices", Fmt.Money(-d["applied_cents"].Long), "0" });
                if (d["refunded_cents"].Long > 0) totals.Add(new[] { "Refunded", Fmt.Money(-d["refunded_cents"].Long), "0" });
                totals.Add(new[] { "Credit remaining", Fmt.Money(status == "void" ? 0 : d["remaining_cents"].Long), "2" });
            }
            string note = (d["memo"].Str ?? "").Trim();
            string footNote = profile == null ? "" : (profile["note"].Str ?? "").Trim();
            double needed = totals.Count * 17 + 30 + (note.Length > 0 ? 40 : 0) + (footNote.Length > 0 ? 50 : 0);
            if (yy + needed > Bottom) { page = doc.NewPage(); yy = header(page, false); }
            yy += 18;
            foreach (var t in totals)
            {
                bool bold = t[2] != "0"; double size = t[2] == "2" ? 12 : 10;
                if (t[2] == "1") page.Line(Right - 230, yy - 11, Right, yy - 11, 0.75, 0.5);
                page.Text(Right - 120, yy, t[0], size, bold, 1, 0.2); page.Text(Right - 8, yy, t[1], size, bold, 1);
                yy += t[2] == "2" ? 20 : 17;
            }
            yy += 14;
            if (note.Length > 0)
            {
                page.Text(Left, yy, "Memo", 9, true, 0, 0.45); yy += 13;
                foreach (var line in PdfDocument.Wrap(note, Right - Left, 10, false)) { page.Text(Left, yy, line, 10); yy += 13; }
                yy += 10;
            }
            if (footNote.Length > 0)
            {
                foreach (var line in PdfDocument.Wrap(footNote, Right - Left, 10, false)) { page.Text(Left, yy, line, 10, false, 0, 0.25); yy += 13; }
            }
            // footer on every page: tax id and page numbers
            string taxId = profile == null ? "" : (profile["taxid"].Str ?? "");
            // (page numbers are written after layout, once the page count is known; PdfDocument keeps pages in order)
            return Finish(doc, taxId);
        }

        static byte[] Finish(PdfDocument doc, string taxId)
        {
            // add the small footer to each page by creating the pages' footers through a second pass
            return doc.ToBytesWithFooter(taxId);
        }

        /// <summary>Shrinks a picture to at most 400 x 160 pixels on a white background and returns it as a JPEG (for the company logo setting).</summary>
        public static byte[] LogoJpeg(string path, out int width, out int height)
        {
            using (var src = Image.FromFile(path))
            {
                double k = Math.Min(1.0, Math.Min(400.0 / src.Width, 160.0 / src.Height));
                width = Math.Max(1, (int)Math.Round(src.Width * k)); height = Math.Max(1, (int)Math.Round(src.Height * k));
                using (var bmp = new Bitmap(width, height, PixelFormat.Format24bppRgb))
                {
                    using (var g = Graphics.FromImage(bmp)) { g.Clear(Color.White); g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.SmoothingMode = SmoothingMode.HighQuality; g.DrawImage(src, 0, 0, width, height); }
                    var enc = ImageCodecInfo.GetImageEncoders().First(e => e.MimeType == "image/jpeg");
                    using (var ps = new EncoderParameters(1)) using (var ms = new MemoryStream())
                    {
                        ps.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 90L);
                        bmp.Save(ms, enc, ps); return ms.ToArray();
                    }
                }
            }
        }
    }
}
