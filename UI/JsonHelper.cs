using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;

namespace WIS.UI
{
    public enum JsonNodeType
    {
        Null,
        Boolean,
        Number,
        String,
        Array,
        Object
    }

    public class JsonNode
    {
        public JsonNodeType NodeType { get; set; }
        public object Value { get; set; }
        public Dictionary<string, JsonNode> Properties { get; private set; }
        public List<JsonNode> Elements { get; private set; }

        public JsonNode()
        {
            NodeType = JsonNodeType.Null;
            Properties = new Dictionary<string, JsonNode>(StringComparer.OrdinalIgnoreCase);
            Elements = new List<JsonNode>();
        }

        public bool IsNull { get { return NodeType == JsonNodeType.Null; } }
        public bool IsObject { get { return NodeType == JsonNodeType.Object; } }
        public bool IsArray { get { return NodeType == JsonNodeType.Array; } }
        public bool IsString { get { return NodeType == JsonNodeType.String; } }
        public bool IsNumber { get { return NodeType == JsonNodeType.Number; } }
        public bool IsBoolean { get { return NodeType == JsonNodeType.Boolean; } }

        public int Count
        {
            get
            {
                if (IsArray) return Elements.Count;
                if (IsObject) return Properties.Count;
                return 0;
            }
        }

        public IEnumerable<string> Keys
        {
            get { return Properties.Keys; }
        }

        public JsonNode this[string key]
        {
            get
            {
                if (IsObject)
                {
                    JsonNode node;
                    if (Properties.TryGetValue(key, out node))
                    {
                        return node;
                    }
                }
                return NullNode;
            }
            set
            {
                NodeType = JsonNodeType.Object;
                Properties[key] = value ?? NullNode;
            }
        }

        public JsonNode this[int index]
        {
            get
            {
                if (IsArray && index >= 0 && index < Elements.Count)
                {
                    return Elements[index];
                }
                return NullNode;
            }
        }

        public bool ContainsKey(string key)
        {
            return IsObject && Properties.ContainsKey(key);
        }

        public string AsString(string fallback = "")
        {
            if (IsNull) return fallback;
            if (Value != null) return Value.ToString();
            return fallback;
        }

        public int AsInt(int fallback = 0)
        {
            if (Value == null) return fallback;
            if (Value is int) return (int)Value;
            if (Value is long) return (int)(long)Value;
            if (Value is double) return (int)(double)Value;
            int res;
            if (int.TryParse(Value.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out res)) return res;
            return fallback;
        }

        public long AsLong(long fallback = 0)
        {
            if (Value == null) return fallback;
            if (Value is long) return (long)Value;
            if (Value is int) return (long)(int)Value;
            if (Value is double) return (long)(double)Value;
            long res;
            if (long.TryParse(Value.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out res)) return res;
            return fallback;
        }

        public double AsDouble(double fallback = 0.0)
        {
            if (Value == null) return fallback;
            if (Value is double) return (double)Value;
            if (Value is int) return (double)(int)Value;
            if (Value is long) return (double)(long)Value;
            double res;
            if (double.TryParse(Value.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out res)) return res;
            return fallback;
        }

        public bool AsBool(bool fallback = false)
        {
            if (Value == null) return fallback;
            if (Value is bool) return (bool)Value;
            string s = Value.ToString().Trim().ToLowerInvariant();
            if (s == "true" || s == "1" || s == "yes") return true;
            if (s == "false" || s == "0" || s == "no") return false;
            return fallback;
        }

        public static readonly JsonNode NullNode = new JsonNode { NodeType = JsonNodeType.Null };
    }

    public static class JsonHelper
    {
        public static JsonNode Parse(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return JsonNode.NullNode;

            int index = 0;
            try
            {
                SkipWhitespace(json, ref index);
                return ParseValue(json, ref index);
            }
            catch
            {
                return JsonNode.NullNode;
            }
        }

        private static void SkipWhitespace(string s, ref int index)
        {
            while (index < s.Length && char.IsWhiteSpace(s[index]))
            {
                index++;
            }
        }

        private static JsonNode ParseValue(string s, ref int index)
        {
            SkipWhitespace(s, ref index);
            if (index >= s.Length) return JsonNode.NullNode;

            char c = s[index];
            if (c == '{') return ParseObject(s, ref index);
            if (c == '[') return ParseArray(s, ref index);
            if (c == '"' || c == '\'') return ParseString(s, ref index);
            if (c == 't' || c == 'T' || c == 'f' || c == 'F') return ParseBoolean(s, ref index);
            if (c == 'n' || c == 'N') return ParseNull(s, ref index);
            if (c == '-' || char.IsDigit(c)) return ParseNumber(s, ref index);

            return JsonNode.NullNode;
        }

        private static JsonNode ParseObject(string s, ref int index)
        {
            var node = new JsonNode { NodeType = JsonNodeType.Object };
            index++; // skip '{'

            while (index < s.Length)
            {
                SkipWhitespace(s, ref index);
                if (index >= s.Length) break;
                if (s[index] == '}')
                {
                    index++;
                    break;
                }

                // Parse key
                string key = "";
                if (s[index] == '"' || s[index] == '\'')
                {
                    var keyNode = ParseString(s, ref index);
                    key = keyNode.AsString();
                }
                else
                {
                    int start = index;
                    while (index < s.Length && s[index] != ':' && !char.IsWhiteSpace(s[index]))
                        index++;
                    key = s.Substring(start, index - start);
                }

                SkipWhitespace(s, ref index);
                if (index < s.Length && s[index] == ':') index++;
                SkipWhitespace(s, ref index);

                var valNode = ParseValue(s, ref index);
                node[key] = valNode;

                SkipWhitespace(s, ref index);
                if (index < s.Length && s[index] == ',')
                {
                    index++;
                }
            }

            return node;
        }

        private static JsonNode ParseArray(string s, ref int index)
        {
            var node = new JsonNode { NodeType = JsonNodeType.Array };
            index++; // skip '['

            while (index < s.Length)
            {
                SkipWhitespace(s, ref index);
                if (index >= s.Length) break;
                if (s[index] == ']')
                {
                    index++;
                    break;
                }

                var item = ParseValue(s, ref index);
                node.Elements.Add(item);

                SkipWhitespace(s, ref index);
                if (index < s.Length && s[index] == ',')
                {
                    index++;
                }
            }

            return node;
        }

        private static JsonNode ParseString(string s, ref int index)
        {
            char quote = s[index];
            index++; // skip opening quote
            var sb = new StringBuilder();

            while (index < s.Length)
            {
                char c = s[index++];
                if (c == quote)
                {
                    break;
                }
                if (c == '\\' && index < s.Length)
                {
                    char esc = s[index++];
                    switch (esc)
                    {
                        case '"': sb.Append('"'); break;
                        case '\'': sb.Append('\''); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            if (index + 4 <= s.Length)
                            {
                                string hex = s.Substring(index, 4);
                                int code;
                                if (int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code))
                                {
                                    sb.Append((char)code);
                                }
                                index += 4;
                            }
                            break;
                        default: sb.Append(esc); break;
                    }
                }
                else
                {
                    sb.Append(c);
                }
            }

            return new JsonNode { NodeType = JsonNodeType.String, Value = sb.ToString() };
        }

        private static JsonNode ParseNumber(string s, ref int index)
        {
            int start = index;
            if (s[index] == '-') index++;
            while (index < s.Length && (char.IsDigit(s[index]) || s[index] == '.' || s[index] == 'e' || s[index] == 'E' || s[index] == '+' || s[index] == '-'))
            {
                index++;
            }

            string numStr = s.Substring(start, index - start);
            if (numStr.Contains(".") || numStr.Contains("e") || numStr.Contains("E"))
            {
                double d;
                if (double.TryParse(numStr, NumberStyles.Any, CultureInfo.InvariantCulture, out d))
                {
                    return new JsonNode { NodeType = JsonNodeType.Number, Value = d };
                }
            }
            else
            {
                long l;
                if (long.TryParse(numStr, NumberStyles.Any, CultureInfo.InvariantCulture, out l))
                {
                    if (l >= int.MinValue && l <= int.MaxValue)
                        return new JsonNode { NodeType = JsonNodeType.Number, Value = (int)l };
                    return new JsonNode { NodeType = JsonNodeType.Number, Value = l };
                }
            }

            return new JsonNode { NodeType = JsonNodeType.Number, Value = 0 };
        }

        private static JsonNode ParseBoolean(string s, ref int index)
        {
            if (s.Length >= index + 4 && s.Substring(index, 4).Equals("true", StringComparison.OrdinalIgnoreCase))
            {
                index += 4;
                return new JsonNode { NodeType = JsonNodeType.Boolean, Value = true };
            }
            if (s.Length >= index + 5 && s.Substring(index, 5).Equals("false", StringComparison.OrdinalIgnoreCase))
            {
                index += 5;
                return new JsonNode { NodeType = JsonNodeType.Boolean, Value = false };
            }
            index++;
            return new JsonNode { NodeType = JsonNodeType.Boolean, Value = false };
        }

        private static JsonNode ParseNull(string s, ref int index)
        {
            if (s.Length >= index + 4 && s.Substring(index, 4).Equals("null", StringComparison.OrdinalIgnoreCase))
            {
                index += 4;
            }
            else
            {
                index++;
            }
            return JsonNode.NullNode;
        }

        public static string Serialize(object obj)
        {
            var sb = new StringBuilder();
            WriteValue(sb, obj);
            return sb.ToString();
        }

        private static void WriteValue(StringBuilder sb, object obj)
        {
            if (obj == null)
            {
                sb.Append("null");
                return;
            }

            if (obj is bool)
            {
                sb.Append((bool)obj ? "true" : "false");
                return;
            }

            string s = obj as string;
            if (s != null)
            {
                WriteString(sb, s);
                return;
            }

            if (obj is int || obj is long || obj is short || obj is byte)
            {
                sb.Append(obj.ToString());
                return;
            }

            if (obj is double)
            {
                sb.Append(((double)obj).ToString("R", CultureInfo.InvariantCulture));
                return;
            }

            if (obj is float)
            {
                sb.Append(((float)obj).ToString("R", CultureInfo.InvariantCulture));
                return;
            }

            if (obj is decimal)
            {
                sb.Append(((decimal)obj).ToString(CultureInfo.InvariantCulture));
                return;
            }

            if (obj is DateTime)
            {
                WriteString(sb, ((DateTime)obj).ToString("o"));
                return;
            }

            IDictionary dict = obj as IDictionary;
            if (dict != null)
            {
                sb.Append("{");
                bool first = true;
                foreach (DictionaryEntry de in dict)
                {
                    if (!first) sb.Append(",");
                    first = false;
                    WriteString(sb, de.Key.ToString());
                    sb.Append(":");
                    WriteValue(sb, de.Value);
                }
                sb.Append("}");
                return;
            }

            IEnumerable enumerable = obj as IEnumerable;
            if (enumerable != null)
            {
                sb.Append("[");
                bool first = true;
                foreach (var item in enumerable)
                {
                    if (!first) sb.Append(",");
                    first = false;
                    WriteValue(sb, item);
                }
                sb.Append("]");
                return;
            }

            // Object via reflection
            sb.Append("{");
            bool firstProp = true;
            PropertyInfo[] props = obj.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance);
            foreach (var prop in props)
            {
                if (!prop.CanRead) continue;
                if (!firstProp) sb.Append(",");
                firstProp = false;
                WriteString(sb, prop.Name);
                sb.Append(":");
                WriteValue(sb, prop.GetValue(obj, null));
            }
            sb.Append("}");
        }

        private static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s)
            {
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
                        if (c < 32)
                        {
                            sb.AppendFormat("\\u{0:x4}", (int)c);
                        }
                        else
                        {
                            sb.Append(c);
                        }
                        break;
                }
            }
            sb.Append('"');
        }
    }
}
