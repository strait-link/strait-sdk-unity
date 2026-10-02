using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Strait
{
    /// <summary>
    /// Minimal, dependency-free JSON for the SDK's small request/response bodies, so a Unity
    /// project needs no extra DLLs (System.Text.Json is not part of Unity's netstandard2.1 profile).
    /// Writer escapes every string (B11); reader is a strict RFC 8259 recursive-descent parser.
    /// Parsed values: Dictionary&lt;string, object?&gt;, List&lt;object?&gt;, string, double, bool, null.
    /// </summary>
    public static class StraitJson
    {
        private const int MaxDepth = 64;

        // ---------------------------------------------------------------- writer

        /// <summary>
        /// Serialize an object from ordered key/value pairs. Values may be string, bool, int, long,
        /// double, float, decimal, IDictionary&lt;string, object?&gt;, IEnumerable&lt;object?&gt; or null.
        /// Keys whose value is null are omitted (like JSON.stringify of undefined).
        /// </summary>
        public static string Serialize(IEnumerable<KeyValuePair<string, object?>> fields)
        {
            var sb = new StringBuilder();
            WriteObject(sb, fields, 0);
            return sb.ToString();
        }

        internal static void WriteValue(StringBuilder sb, object? v, int depth)
        {
            if (depth > MaxDepth) throw new InvalidOperationException("JSON nesting too deep");
            switch (v)
            {
                case null: sb.Append("null"); break;
                case string s: WriteString(sb, s); break;
                case bool b: sb.Append(b ? "true" : "false"); break;
                case int i: sb.Append(i.ToString(CultureInfo.InvariantCulture)); break;
                case long l: sb.Append(l.ToString(CultureInfo.InvariantCulture)); break;
                case double d: WriteNumber(sb, d); break;
                case float f: WriteNumber(sb, double.Parse(f.ToString("R", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture)); break;
                case decimal m: sb.Append(m.ToString(CultureInfo.InvariantCulture)); break;
                case IEnumerable<KeyValuePair<string, object?>> obj: WriteObject(sb, obj, depth + 1); break;
                case System.Collections.IDictionary dict:
                    var pairs = new List<KeyValuePair<string, object?>>();
                    foreach (System.Collections.DictionaryEntry de in dict)
                        pairs.Add(new KeyValuePair<string, object?>(Convert.ToString(de.Key, CultureInfo.InvariantCulture) ?? "", de.Value));
                    WriteObject(sb, pairs, depth + 1);
                    break;
                case System.Collections.IEnumerable arr:
                    sb.Append('[');
                    bool first = true;
                    foreach (var item in arr)
                    {
                        if (!first) sb.Append(',');
                        first = false;
                        WriteValue(sb, item, depth + 1);
                    }
                    sb.Append(']');
                    break;
                default:
                    WriteString(sb, Convert.ToString(v, CultureInfo.InvariantCulture) ?? "");
                    break;
            }
        }

        private static void WriteObject(StringBuilder sb, IEnumerable<KeyValuePair<string, object?>> fields, int depth)
        {
            sb.Append('{');
            bool first = true;
            foreach (var kv in fields)
            {
                if (kv.Value == null) continue;
                if (!first) sb.Append(',');
                first = false;
                WriteString(sb, kv.Key);
                sb.Append(':');
                WriteValue(sb, kv.Value, depth);
            }
            sb.Append('}');
        }

        /// <summary>JSON.stringify number rules: non-finite → null; otherwise shortest round-trip.</summary>
        private static void WriteNumber(StringBuilder sb, double d)
        {
            if (double.IsNaN(d) || double.IsInfinity(d)) { sb.Append("null"); return; }
            if (d == Math.Floor(d) && Math.Abs(d) < 1e15)
                sb.Append(((long)d).ToString(CultureInfo.InvariantCulture));
            else
                sb.Append(d.ToString("R", CultureInfo.InvariantCulture)); // e.g. 49.99, 1E+21 (valid JSON)
        }

        /// <summary>Quote + escape: ", \, control chars, U+2028/2029 and lone surrogates.</summary>
        public static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20 || c == '\u2028' || c == '\u2029') { AppendU(sb, c); }
                        else if (char.IsHighSurrogate(c))
                        {
                            if (i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) { sb.Append(c).Append(s[i + 1]); i++; }
                            else AppendU(sb, c);
                        }
                        else if (char.IsLowSurrogate(c)) AppendU(sb, c);
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        private static void AppendU(StringBuilder sb, char c)
        {
            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
        }

        // ---------------------------------------------------------------- reader

        /// <summary>Parse JSON text. Throws <see cref="FormatException"/> on invalid input.</summary>
        public static object? Parse(string text)
        {
            if (text == null) throw new FormatException("null input");
            int pos = 0;
            SkipWs(text, ref pos);
            var v = ReadValue(text, ref pos, 0);
            SkipWs(text, ref pos);
            if (pos != text.Length) throw new FormatException("trailing characters at " + pos);
            return v;
        }

        /// <summary>Parse a JSON object; anything unparseable or not an object → empty dictionary.</summary>
        public static Dictionary<string, object?> ParseObjectOrEmpty(string? text)
        {
            if (string.IsNullOrEmpty(text)) return new Dictionary<string, object?>();
            try
            {
                return Parse(text!) as Dictionary<string, object?> ?? new Dictionary<string, object?>();
            }
            catch (Exception) // FormatException, or OverflowException on older runtimes for 1e400
            {
                return new Dictionary<string, object?>();
            }
        }

        private static object? ReadValue(string t, ref int p, int depth)
        {
            if (depth > MaxDepth) throw new FormatException("nesting too deep");
            if (p >= t.Length) throw new FormatException("unexpected end");
            char c = t[p];
            switch (c)
            {
                case '{': return ReadObject(t, ref p, depth + 1);
                case '[': return ReadArray(t, ref p, depth + 1);
                case '"': return ReadString(t, ref p);
                case 't': Expect(t, ref p, "true"); return true;
                case 'f': Expect(t, ref p, "false"); return false;
                case 'n': Expect(t, ref p, "null"); return null;
                default:
                    if (c == '-' || (c >= '0' && c <= '9')) return ReadNumber(t, ref p);
                    throw new FormatException("unexpected '" + c + "' at " + p);
            }
        }

        private static Dictionary<string, object?> ReadObject(string t, ref int p, int depth)
        {
            var d = new Dictionary<string, object?>();
            p++; // {
            SkipWs(t, ref p);
            if (p < t.Length && t[p] == '}') { p++; return d; }
            while (true)
            {
                SkipWs(t, ref p);
                if (p >= t.Length || t[p] != '"') throw new FormatException("expected key at " + p);
                var key = ReadString(t, ref p);
                SkipWs(t, ref p);
                if (p >= t.Length || t[p] != ':') throw new FormatException("expected ':' at " + p);
                p++;
                SkipWs(t, ref p);
                d[key] = ReadValue(t, ref p, depth); // duplicate keys: last wins, like JSON.parse
                SkipWs(t, ref p);
                if (p >= t.Length) throw new FormatException("unterminated object");
                if (t[p] == ',') { p++; continue; }
                if (t[p] == '}') { p++; return d; }
                throw new FormatException("expected ',' or '}' at " + p);
            }
        }

        private static List<object?> ReadArray(string t, ref int p, int depth)
        {
            var list = new List<object?>();
            p++; // [
            SkipWs(t, ref p);
            if (p < t.Length && t[p] == ']') { p++; return list; }
            while (true)
            {
                SkipWs(t, ref p);
                list.Add(ReadValue(t, ref p, depth));
                SkipWs(t, ref p);
                if (p >= t.Length) throw new FormatException("unterminated array");
                if (t[p] == ',') { p++; continue; }
                if (t[p] == ']') { p++; return list; }
                throw new FormatException("expected ',' or ']' at " + p);
            }
        }

        private static string ReadString(string t, ref int p)
        {
            p++; // opening quote
            var sb = new StringBuilder();
            while (true)
            {
                if (p >= t.Length) throw new FormatException("unterminated string");
                char c = t[p++];
                if (c == '"') return sb.ToString();
                if (c < 0x20) throw new FormatException("control character in string");
                if (c != '\\') { sb.Append(c); continue; }
                if (p >= t.Length) throw new FormatException("unterminated escape");
                char e = t[p++];
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
                        if (p + 4 > t.Length) throw new FormatException("short \\u escape");
                        int code = 0;
                        for (int i = 0; i < 4; i++)
                        {
                            int h = Hex(t[p + i]);
                            if (h < 0) throw new FormatException("bad \\u escape");
                            code = (code << 4) | h;
                        }
                        p += 4;
                        sb.Append((char)code); // surrogate pairs arrive as two escapes, JS-style
                        break;
                    default: throw new FormatException("bad escape \\" + e);
                }
            }
        }

        private static double ReadNumber(string t, ref int p)
        {
            int start = p;
            if (t[p] == '-') p++;
            if (p >= t.Length) throw new FormatException("bad number");
            if (t[p] == '0') p++;
            else if (t[p] >= '1' && t[p] <= '9') { while (p < t.Length && t[p] >= '0' && t[p] <= '9') p++; }
            else throw new FormatException("bad number");
            if (p < t.Length && t[p] == '.')
            {
                p++;
                int fs = p;
                while (p < t.Length && t[p] >= '0' && t[p] <= '9') p++;
                if (p == fs) throw new FormatException("bad fraction");
            }
            if (p < t.Length && (t[p] == 'e' || t[p] == 'E'))
            {
                p++;
                if (p < t.Length && (t[p] == '+' || t[p] == '-')) p++;
                int es = p;
                while (p < t.Length && t[p] >= '0' && t[p] <= '9') p++;
                if (p == es) throw new FormatException("bad exponent");
            }
            return double.Parse(t.Substring(start, p - start), NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        private static void Expect(string t, ref int p, string word)
        {
            if (p + word.Length > t.Length || string.CompareOrdinal(t, p, word, 0, word.Length) != 0)
                throw new FormatException("expected " + word + " at " + p);
            p += word.Length;
        }

        private static void SkipWs(string t, ref int p)
        {
            while (p < t.Length && (t[p] == ' ' || t[p] == '\t' || t[p] == '\n' || t[p] == '\r')) p++;
        }

        private static int Hex(char c)
        {
            if (c >= '0' && c <= '9') return c - '0';
            if (c >= 'a' && c <= 'f') return c - 'a' + 10;
            if (c >= 'A' && c <= 'F') return c - 'A' + 10;
            return -1;
        }
    }
}
