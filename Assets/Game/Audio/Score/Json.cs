using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace NightSignal.AudioSynth
{
    public enum JsonKind : byte { Null, Bool, Number, String, Array, Object }

    /// <summary>
    /// Minimal JSON document model. The synth assembly has no references (engine-free, dependency-free), so it
    /// carries its own reader. Used only at load time; never on the audio thread.
    /// </summary>
    public sealed class JsonValue
    {
        public JsonKind Kind;
        public bool Bool;
        public double Number;
        public string String;
        public List<JsonValue> Items;
        public List<KeyValuePair<string, JsonValue>> Members;

        public bool IsObject => Kind == JsonKind.Object;
        public bool IsArray => Kind == JsonKind.Array;
        public bool IsString => Kind == JsonKind.String;
        public bool IsNumber => Kind == JsonKind.Number;
        public int Count => Items != null ? Items.Count : (Members != null ? Members.Count : 0);

        public JsonValue this[int index] => Items[index];

        public JsonValue Get(string key)
        {
            if (Members == null) return null;
            for (int i = 0; i < Members.Count; i++)
                if (Members[i].Key == key) return Members[i].Value;
            return null;
        }

        public bool Has(string key) => Get(key) != null;

        public string Str(string key, string fallback = null)
        {
            var v = Get(key);
            return v != null && v.Kind == JsonKind.String ? v.String : fallback;
        }

        public float Num(string key, float fallback)
        {
            var v = Get(key);
            return v != null && v.Kind == JsonKind.Number ? (float)v.Number : fallback;
        }

        public int Int(string key, int fallback)
        {
            var v = Get(key);
            return v != null && v.Kind == JsonKind.Number ? (int)Math.Round(v.Number) : fallback;
        }

        public bool Flag(string key, bool fallback)
        {
            var v = Get(key);
            return v != null && v.Kind == JsonKind.Bool ? v.Bool : fallback;
        }

        /// <summary>Shallow copy with <paramref name="overlay"/> members replacing ours; nested objects merge one level.</summary>
        public static JsonValue Merge(JsonValue baseObj, JsonValue overlay)
        {
            var result = new JsonValue { Kind = JsonKind.Object, Members = new List<KeyValuePair<string, JsonValue>>() };
            if (baseObj != null && baseObj.Members != null) result.Members.AddRange(baseObj.Members);
            if (overlay == null || overlay.Members == null) return result;
            for (int i = 0; i < overlay.Members.Count; i++)
            {
                var kv = overlay.Members[i];
                int existing = -1;
                for (int j = 0; j < result.Members.Count; j++)
                    if (result.Members[j].Key == kv.Key) { existing = j; break; }
                JsonValue value = kv.Value;
                if (existing >= 0 && value.IsObject && result.Members[existing].Value.IsObject)
                    value = Merge(result.Members[existing].Value, value);
                if (existing >= 0) result.Members[existing] = new KeyValuePair<string, JsonValue>(kv.Key, value);
                else result.Members.Add(new KeyValuePair<string, JsonValue>(kv.Key, value));
            }
            return result;
        }
    }

    public sealed class JsonException : Exception
    {
        public JsonException(string message) : base(message) { }
    }

    public static class Json
    {
        public static JsonValue Parse(string text)
        {
            if (text == null) throw new JsonException("JSON text is null");
            int pos = 0;
            // Tolerate a UTF-8 byte-order mark.
            if (text.Length > 0 && text[0] == '﻿') pos = 1;
            var v = ParseValue(text, ref pos);
            SkipWs(text, ref pos);
            if (pos != text.Length) throw Error(text, pos, "trailing characters");
            return v;
        }

        static JsonException Error(string text, int pos, string what)
        {
            int line = 1, col = 1;
            for (int i = 0; i < pos && i < text.Length; i++)
            {
                if (text[i] == '\n') { line++; col = 1; } else col++;
            }
            return new JsonException("JSON error at line " + line + ", column " + col + ": " + what);
        }

        static void SkipWs(string s, ref int p)
        {
            while (p < s.Length)
            {
                char c = s[p];
                if (c == ' ' || c == '\t' || c == '\n' || c == '\r') p++;
                else break;
            }
        }

        static JsonValue ParseValue(string s, ref int p)
        {
            SkipWs(s, ref p);
            if (p >= s.Length) throw Error(s, p, "unexpected end");
            char c = s[p];
            switch (c)
            {
                case '{': return ParseObject(s, ref p);
                case '[': return ParseArray(s, ref p);
                case '"': return new JsonValue { Kind = JsonKind.String, String = ParseString(s, ref p) };
                case 't': Expect(s, ref p, "true"); return new JsonValue { Kind = JsonKind.Bool, Bool = true };
                case 'f': Expect(s, ref p, "false"); return new JsonValue { Kind = JsonKind.Bool, Bool = false };
                case 'n': Expect(s, ref p, "null"); return new JsonValue { Kind = JsonKind.Null };
                default:
                    if (c == '-' || (c >= '0' && c <= '9')) return ParseNumber(s, ref p);
                    throw Error(s, p, "unexpected character '" + c + "'");
            }
        }

        static void Expect(string s, ref int p, string word)
        {
            if (string.CompareOrdinal(s, p, word, 0, word.Length) != 0) throw Error(s, p, "expected " + word);
            p += word.Length;
        }

        static JsonValue ParseNumber(string s, ref int p)
        {
            int start = p;
            if (s[p] == '-') p++;
            while (p < s.Length)
            {
                char c = s[p];
                if ((c >= '0' && c <= '9') || c == '.' || c == 'e' || c == 'E' || c == '+' || c == '-') p++;
                else break;
            }
            double d;
            if (!double.TryParse(s.Substring(start, p - start), NumberStyles.Float, CultureInfo.InvariantCulture, out d))
                throw Error(s, start, "bad number");
            return new JsonValue { Kind = JsonKind.Number, Number = d };
        }

        static string ParseString(string s, ref int p)
        {
            p++; // opening quote
            var sb = new StringBuilder();
            while (true)
            {
                if (p >= s.Length) throw Error(s, p, "unterminated string");
                char c = s[p++];
                if (c == '"') break;
                if (c != '\\') { sb.Append(c); continue; }
                if (p >= s.Length) throw Error(s, p, "bad escape");
                char e = s[p++];
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
                        if (p + 4 > s.Length) throw Error(s, p, "bad unicode escape");
                        sb.Append((char)int.Parse(s.Substring(p, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                        p += 4;
                        break;
                    default: throw Error(s, p, "bad escape");
                }
            }
            return sb.ToString();
        }

        static JsonValue ParseArray(string s, ref int p)
        {
            p++;
            var v = new JsonValue { Kind = JsonKind.Array, Items = new List<JsonValue>() };
            SkipWs(s, ref p);
            if (p < s.Length && s[p] == ']') { p++; return v; }
            while (true)
            {
                v.Items.Add(ParseValue(s, ref p));
                SkipWs(s, ref p);
                if (p >= s.Length) throw Error(s, p, "unterminated array");
                if (s[p] == ',') { p++; continue; }
                if (s[p] == ']') { p++; return v; }
                throw Error(s, p, "expected , or ]");
            }
        }

        static JsonValue ParseObject(string s, ref int p)
        {
            p++;
            var v = new JsonValue { Kind = JsonKind.Object, Members = new List<KeyValuePair<string, JsonValue>>() };
            SkipWs(s, ref p);
            if (p < s.Length && s[p] == '}') { p++; return v; }
            while (true)
            {
                SkipWs(s, ref p);
                if (p >= s.Length || s[p] != '"') throw Error(s, p, "expected member name");
                string key = ParseString(s, ref p);
                SkipWs(s, ref p);
                if (p >= s.Length || s[p] != ':') throw Error(s, p, "expected :");
                p++;
                var value = ParseValue(s, ref p);
                for (int i = 0; i < v.Members.Count; i++)
                    if (v.Members[i].Key == key) throw Error(s, p, "duplicate member '" + key + "'");
                v.Members.Add(new KeyValuePair<string, JsonValue>(key, value));
                SkipWs(s, ref p);
                if (p >= s.Length) throw Error(s, p, "unterminated object");
                if (s[p] == ',') { p++; continue; }
                if (s[p] == '}') { p++; return v; }
                throw Error(s, p, "expected , or }");
            }
        }
    }
}
