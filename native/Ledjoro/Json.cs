using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Ledjoro
{
    /// <summary>Read-only, null-safe view of a parsed JSON value: j["a"][0]["b"].Str never throws on a missing path.</summary>
    public sealed class Jv
    {
        readonly object v;
        public static readonly Jv Null = new Jv(null);
        public Jv(object value) { v = value; }

        public object Raw { get { return v; } }
        public bool IsNull { get { return v == null; } }
        public bool IsArray { get { return v is List<object>; } }
        public bool IsObject { get { return v is Dictionary<string, object>; } }

        public Jv this[string key]
        {
            get { var d = v as Dictionary<string, object>; object x; return d != null && d.TryGetValue(key, out x) ? new Jv(x) : Null; }
        }
        public Jv this[int i]
        {
            get { var l = v as List<object>; return l != null && i >= 0 && i < l.Count ? new Jv(l[i]) : Null; }
        }
        public int Count
        {
            get { var l = v as List<object>; if (l != null) return l.Count; var d = v as Dictionary<string, object>; return d != null ? d.Count : 0; }
        }
        public IEnumerable<Jv> Items
        {
            get { var l = v as List<object>; if (l == null) yield break; foreach (var x in l) yield return new Jv(x); }
        }
        public IEnumerable<string> Keys
        {
            get { var d = v as Dictionary<string, object>; return d != null ? (IEnumerable<string>)d.Keys : new string[0]; }
        }
        public bool Has(string key) { var d = v as Dictionary<string, object>; return d != null && d.ContainsKey(key); }

        public string Str
        {
            get
            {
                if (v == null) return "";
                var s = v as string; if (s != null) return s;
                if (v is bool) return (bool)v ? "true" : "false";
                if (v is double) return ((double)v).ToString(CultureInfo.InvariantCulture);
                return Convert.ToString(v, CultureInfo.InvariantCulture);
            }
        }
        public long Long
        {
            get
            {
                if (v is long) return (long)v;
                if (v is double) return (long)Math.Round((double)v);
                if (v is bool) return (bool)v ? 1 : 0;
                long r; var s = v as string; return s != null && long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out r) ? r : 0;
            }
        }
        public int Int { get { return (int)Long; } }
        public double Double { get { if (v is double) return (double)v; if (v is long) return (long)v; double r; var s = v as string; return s != null && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out r) ? r : 0; } }
        public bool Bool { get { if (v is bool) return (bool)v; if (v is long) return (long)v != 0; var s = v as string; return s == "true" || s == "1"; } }
        public override string ToString() { return Str; }
    }

    public static class Json
    {
        public static Jv Parse(string text)
        {
            var p = new Parser { s = text, i = 0 };
            p.Ws();
            var val = p.Value();
            p.Ws();
            if (p.i != text.Length) throw new FormatException("Unexpected data after JSON value");
            return new Jv(val);
        }

        /// <summary>Build an object from alternating key, value arguments: Json.O("a", 1, "b", "x").</summary>
        public static Dictionary<string, object> O(params object[] kv)
        {
            var d = new Dictionary<string, object>();
            for (int i = 0; i + 1 < kv.Length; i += 2) d[(string)kv[i]] = kv[i + 1];
            return d;
        }

        public static string Stringify(object o) { var sb = new StringBuilder(); Write(sb, o); return sb.ToString(); }

        static void Write(StringBuilder sb, object o)
        {
            if (o == null) { sb.Append("null"); return; }
            var jv = o as Jv; if (jv != null) { Write(sb, jv.Raw); return; }
            var s = o as string; if (s != null) { Str(sb, s); return; }
            if (o is bool) { sb.Append((bool)o ? "true" : "false"); return; }
            if (o is int || o is long || o is short || o is byte) { sb.Append(Convert.ToInt64(o).ToString(CultureInfo.InvariantCulture)); return; }
            if (o is double || o is float || o is decimal) { sb.Append(Convert.ToDouble(o).ToString("R", CultureInfo.InvariantCulture)); return; }
            var dict = o as IDictionary;
            if (dict != null)
            {
                sb.Append('{'); bool first = true;
                foreach (DictionaryEntry e in dict) { if (!first) sb.Append(','); first = false; Str(sb, (string)e.Key); sb.Append(':'); Write(sb, e.Value); }
                sb.Append('}'); return;
            }
            var en = o as IEnumerable;
            if (en != null)
            {
                sb.Append('['); bool first = true;
                foreach (var x in en) { if (!first) sb.Append(','); first = false; Write(sb, x); }
                sb.Append(']'); return;
            }
            Str(sb, Convert.ToString(o, CultureInfo.InvariantCulture));
        }

        static void Str(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4")); else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        sealed class Parser
        {
            public string s; public int i;
            public void Ws() { while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '\n' || s[i] == '\r')) i++; }
            Exception Err(string m) { return new FormatException(m + " at position " + i); }

            public object Value()
            {
                if (i >= s.Length) throw Err("Unexpected end of JSON");
                char c = s[i];
                if (c == '{') return Obj();
                if (c == '[') return Arr();
                if (c == '"') return String();
                if (c == 't') { Lit("true"); return true; }
                if (c == 'f') { Lit("false"); return false; }
                if (c == 'n') { Lit("null"); return null; }
                return Number();
            }
            void Lit(string w) { if (string.CompareOrdinal(s, i, w, 0, w.Length) != 0) throw Err("Invalid literal"); i += w.Length; }

            object Obj()
            {
                var d = new Dictionary<string, object>(); i++; Ws();
                if (i < s.Length && s[i] == '}') { i++; return d; }
                while (true)
                {
                    Ws(); if (i >= s.Length || s[i] != '"') throw Err("Expected property name");
                    string k = String(); Ws();
                    if (i >= s.Length || s[i] != ':') throw Err("Expected ':'"); i++; Ws();
                    d[k] = Value(); Ws();
                    if (i < s.Length && s[i] == ',') { i++; continue; }
                    if (i < s.Length && s[i] == '}') { i++; return d; }
                    throw Err("Expected ',' or '}'");
                }
            }
            object Arr()
            {
                var l = new List<object>(); i++; Ws();
                if (i < s.Length && s[i] == ']') { i++; return l; }
                while (true)
                {
                    Ws(); l.Add(Value()); Ws();
                    if (i < s.Length && s[i] == ',') { i++; continue; }
                    if (i < s.Length && s[i] == ']') { i++; return l; }
                    throw Err("Expected ',' or ']'");
                }
            }
            string String()
            {
                i++; var sb = new StringBuilder();
                while (true)
                {
                    if (i >= s.Length) throw Err("Unterminated string");
                    char c = s[i++];
                    if (c == '"') return sb.ToString();
                    if (c != '\\') { sb.Append(c); continue; }
                    if (i >= s.Length) throw Err("Bad escape");
                    char e = s[i++];
                    switch (e)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            if (i + 4 > s.Length) throw Err("Bad unicode escape");
                            sb.Append((char)Convert.ToInt32(s.Substring(i, 4), 16)); i += 4; break;
                        default: throw Err("Bad escape");
                    }
                }
            }
            object Number()
            {
                int st = i; bool real = false;
                if (i < s.Length && s[i] == '-') i++;
                while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.' || s[i] == 'e' || s[i] == 'E' || s[i] == '+' || s[i] == '-')) { if (!char.IsDigit(s[i])) real = true; i++; }
                if (i == st) throw Err("Unexpected character");
                string t = s.Substring(st, i - st); long l;
                if (!real && long.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out l)) return l;
                return double.Parse(t, NumberStyles.Float, CultureInfo.InvariantCulture);
            }
        }
    }
}
