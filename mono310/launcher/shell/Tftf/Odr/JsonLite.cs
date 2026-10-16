// Newly authored. Minimal order-preserving JSON reader/writer for the ODR builder (objects keep key order, integers
// stay integers).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Tftf.Odr
{
    public sealed class JObj
    {
        public readonly List<KeyValuePair<string, object>> Items = new List<KeyValuePair<string, object>>();

        public object this[string key]
        {
            get { foreach (var kv in Items) if (kv.Key == key) return kv.Value; return null; }
            set
            {
                for (int i = 0; i < Items.Count; i++)
                    if (Items[i].Key == key) { Items[i] = new KeyValuePair<string, object>(key, value); return; }
                Items.Add(new KeyValuePair<string, object>(key, value));
            }
        }

        public bool Has(string key) { foreach (var kv in Items) if (kv.Key == key) return true; return false; }
        public void Remove(string key) { Items.RemoveAll(kv => kv.Key == key); }
    }

    public static class JsonLite
    {
        public static object Parse(string s)
        {
            int i = 0;
            var v = Value(s, ref i);
            return v;
        }

        static void Ws(string s, ref int i) { while (i < s.Length && char.IsWhiteSpace(s[i])) i++; }

        static object Value(string s, ref int i)
        {
            Ws(s, ref i);
            char c = s[i];
            if (c == '{')
            {
                i++; var o = new JObj();
                Ws(s, ref i);
                if (s[i] == '}') { i++; return o; }
                while (true)
                {
                    Ws(s, ref i);
                    string k = Str(s, ref i);
                    Ws(s, ref i); i++; // :
                    o.Items.Add(new KeyValuePair<string, object>(k, Value(s, ref i)));
                    Ws(s, ref i);
                    if (s[i++] == '}') return o;
                }
            }
            if (c == '[')
            {
                i++; var a = new List<object>();
                Ws(s, ref i);
                if (s[i] == ']') { i++; return a; }
                while (true)
                {
                    a.Add(Value(s, ref i));
                    Ws(s, ref i);
                    if (s[i++] == ']') return a;
                }
            }
            if (c == '"') return Str(s, ref i);
            if (string.CompareOrdinal(s, i, "true", 0, 4) == 0) { i += 4; return true; }
            if (string.CompareOrdinal(s, i, "false", 0, 5) == 0) { i += 5; return false; }
            if (string.CompareOrdinal(s, i, "null", 0, 4) == 0) { i += 4; return null; }
            int st = i;
            while (i < s.Length && "+-0123456789.eE".IndexOf(s[i]) >= 0) i++;
            string n = s.Substring(st, i - st);
            long l;
            if (long.TryParse(n, NumberStyles.Integer, CultureInfo.InvariantCulture, out l)) return l;
            return double.Parse(n, CultureInfo.InvariantCulture);
        }

        static string Str(string s, ref int i)
        {
            i++; var sb = new StringBuilder();
            while (s[i] != '"')
            {
                char c = s[i++];
                if (c != '\\') { sb.Append(c); continue; }
                c = s[i++];
                switch (c)
                {
                    case 'n': sb.Append('\n'); break;
                    case 't': sb.Append('\t'); break;
                    case 'r': sb.Append('\r'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'u': sb.Append((char)System.Convert.ToInt32(s.Substring(i, 4), 16)); i += 4; break;
                    default: sb.Append(c); break;
                }
            }
            i++;
            return sb.ToString();
        }

        public static string Write(object v, bool indent)
        {
            var sb = new StringBuilder();
            Emit(sb, v, indent, 0);
            return sb.ToString();
        }

        static void Nl(StringBuilder sb, bool indent, int d)
        {
            if (!indent) return;
            sb.Append('\n').Append(' ', d * 2);
        }

        static void Emit(StringBuilder sb, object v, bool indent, int d)
        {
            if (v == null) sb.Append("null");
            else if (v is bool) sb.Append((bool)v ? "true" : "false");
            else if (v is string) Quote(sb, (string)v);
            else if (v is long || v is int) sb.Append(System.Convert.ToInt64(v).ToString(CultureInfo.InvariantCulture));
            else if (v is double) sb.Append(((double)v).ToString("R", CultureInfo.InvariantCulture));
            else if (v is JObj)
            {
                var o = (JObj)v;
                if (o.Items.Count == 0) { sb.Append("{}"); return; }
                sb.Append('{');
                for (int i = 0; i < o.Items.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    Nl(sb, indent, d + 1);
                    Quote(sb, o.Items[i].Key); sb.Append(indent ? ": " : ":");
                    Emit(sb, o.Items[i].Value, indent, d + 1);
                }
                Nl(sb, indent, d); sb.Append('}');
            }
            else
            {
                var a = (List<object>)v;
                if (a.Count == 0) { sb.Append("[]"); return; }
                sb.Append('[');
                for (int i = 0; i < a.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    Nl(sb, indent, d + 1);
                    Emit(sb, a[i], indent, d + 1);
                }
                Nl(sb, indent, d); sb.Append(']');
            }
        }

        static void Quote(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s)
            {
                if (c == '"') sb.Append("\\\"");
                else if (c == '\\') sb.Append("\\\\");
                else if (c == '\n') sb.Append("\\n");
                else if (c == '\r') sb.Append("\\r");
                else if (c == '\t') sb.Append("\\t");
                else if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                else sb.Append(c);
            }
            sb.Append('"');
        }
    }
}
