using System;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Ledjoro
{
    /// <summary>Money, dates and phone numbers: the same rules as the web interface and the engine.</summary>
    public static class Fmt
    {
        static readonly CultureInfo US = new CultureInfo("en-US");

        public static string Money(long cents)
        {
            long a = Math.Abs(cents);
            return (cents < 0 ? "-" : "") + "$" + (a / 100).ToString("N0", US) + "." + (a % 100).ToString("00", US);
        }

        /// <summary>Plain amount for editing, e.g. 12345 -> "123.45".</summary>
        public static string Plain(long cents) { long a = Math.Abs(cents); return (cents < 0 ? "-" : "") + (a / 100) + "." + (a % 100).ToString("00"); }

        /// <summary>"1,234.56", "$12", "-5.5" -> cents. Never uses floating point.</summary>
        public static bool TryMoney(string s, out long cents)
        {
            cents = 0;
            if (s == null) return false;
            s = s.Trim().Replace("$", "").Replace(",", "").Replace(" ", "");
            if (s.Length == 0) return false;
            bool neg = false;
            if (s[0] == '-') { neg = true; s = s.Substring(1); }
            if (s.Length == 0) return false;
            int dot = s.IndexOf('.');
            string w = dot < 0 ? s : s.Substring(0, dot), f = dot < 0 ? "" : s.Substring(dot + 1);
            if (w.Length == 0 && f.Length == 0) return false;
            if (!w.All(char.IsDigit) || !f.All(char.IsDigit)) return false;
            long whole = w.Length == 0 ? 0 : long.Parse(w, CultureInfo.InvariantCulture);
            if (w.Length > 13) return false;
            long frac = long.Parse((f + "00").Substring(0, 2), CultureInfo.InvariantCulture);
            long c = whole * 100 + frac;
            if (f.Length > 2 && f[2] >= '5') c++;
            cents = neg ? -c : c;
            return true;
        }

        /// <summary>Quantity with up to 3 decimals -> thousandths ("2.5" -> 2500).</summary>
        public static bool TryQty(string s, out long milli)
        {
            milli = 0;
            if (s == null) return false;
            s = s.Trim();
            if (s.Length == 0) return false;
            int dot = s.IndexOf('.');
            string w = dot < 0 ? s : s.Substring(0, dot), f = dot < 0 ? "" : s.Substring(dot + 1);
            if (!w.All(char.IsDigit) || !f.All(char.IsDigit) || f.Length > 3 || (w.Length == 0 && f.Length == 0) || w.Length > 9) return false;
            milli = (w.Length == 0 ? 0 : long.Parse(w, CultureInfo.InvariantCulture)) * 1000 + long.Parse((f + "000").Substring(0, 3), CultureInfo.InvariantCulture);
            return true;
        }
        public static string Qty(long milli) { return (milli / 1000m).ToString("0.###", CultureInfo.InvariantCulture); }

        // ----- phone: xxx-xxx-xxxx -----
        public static string Digits(string s) { return new string((s ?? "").Where(char.IsDigit).ToArray()); }

        /// <summary>Canonical format if it is a 10-digit US number (a leading 1 is dropped), otherwise the text unchanged.</summary>
        public static string Phone(string p)
        {
            string d = Digits(p);
            if (d.Length == 11 && d[0] == '1') d = d.Substring(1);
            return d.Length == 10 ? d.Substring(0, 3) + "-" + d.Substring(3, 3) + "-" + d.Substring(6) : (p ?? "");
        }
        /// <summary>Progressive formatting while typing: "6035" -> "603-5".</summary>
        public static string PhoneTyping(string raw)
        {
            string d = Digits(raw);
            if (d.Length > 10 && d[0] == '1') d = d.Substring(1);
            if (d.Length > 10) d = d.Substring(0, 10);
            if (d.Length > 6) return d.Substring(0, 3) + "-" + d.Substring(3, 3) + "-" + d.Substring(6);
            if (d.Length > 3) return d.Substring(0, 3) + "-" + d.Substring(3);
            return d;
        }

        // ----- dates: stored and sent as yyyy-MM-dd; shown and typed in the format this PC uses -----
        /// <summary>The short date pattern of this PC's regional settings, always with a four-digit year (for example MM/dd/yyyy or dd.MM.yyyy).</summary>
        public static string Pattern
        {
            get
            {
                string p = CultureInfo.CurrentCulture.DateTimeFormat.ShortDatePattern;
                if (p.Contains("yy") && !p.Contains("yyy")) p = p.Replace("yy", "yyyy");
                return p;
            }
        }
        /// <summary>A stored date (yyyy-MM-dd) as this PC writes dates; anything else is returned unchanged.</summary>
        public static string Show(string iso) { DateTime d; return TryDate(iso, out d) ? d.ToString(Pattern, CultureInfo.CurrentCulture) : iso; }
        /// <summary>Reads a date typed the way this PC writes dates (or as yyyy-MM-dd).</summary>
        public static bool TryLocalDate(string s, out DateTime d)
        {
            s = (s ?? "").Trim();
            if (TryDate(s, out d)) return true;
            return s.Length > 0 && DateTime.TryParse(s, CultureInfo.CurrentCulture, DateTimeStyles.None, out d);
        }
        /// <summary>A typed date as yyyy-MM-dd for the books; text that is not a date is returned as typed.</summary>
        public static string ToIso(string s) { DateTime d; return TryLocalDate(s, out d) ? Date(d) : (s ?? "").Trim(); }
        /// <summary>A date written the way this PC writes it, to show as an example.</summary>
        public static string Example { get { return new DateTime(2026, 3, 31).ToString(Pattern, CultureInfo.CurrentCulture); } }
        // ----- dates (always yyyy-MM-dd inside) -----
        public static string Today() { return DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture); }
        public static bool TryDate(string s, out DateTime d)
        {
            return DateTime.TryParseExact((s ?? "").Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out d);
        }
        public static bool IsDate(string s) { DateTime d; return TryDate(s, out d); }
        public static string Date(DateTime d) { return d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture); }
        public static string AddDays(string date, int n) { DateTime d; return TryDate(date, out d) ? Date(d.AddDays(n)) : date; }
        public static string YearStart() { return DateTime.Now.Year + "-01-01"; }
        public static string LastMonthEnd() { var d = DateTime.Now; return Date(new DateTime(d.Year, d.Month, 1).AddDays(-1)); }

        public static string Age(DateTime utc)
        {
            int days = (int)Math.Floor((DateTime.UtcNow - utc).TotalDays);
            return days <= 0 ? "today" : days + " day" + (days == 1 ? "" : "s") + " ago";
        }
        public static DateTime FromUnixMs(long ms) { return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(ms); }
    }
}
