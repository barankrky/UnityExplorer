using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace UnityExplorer.MCP.Runtime
{
    /// <summary>Dependency-free JSON value used by the game capability executor.</summary>
    public sealed class McpJsonValue
    {
        public enum JsonKind { Null, Boolean, Number, String, Array, Object }

        private readonly object value;

        private McpJsonValue(JsonKind kind, object value)
        {
            Kind = kind;
            this.value = value;
        }

        public JsonKind Kind { get; private set; }
        public bool IsNull { get { return Kind == JsonKind.Null; } }
        public bool BooleanValue { get { return (bool)value; } }
        public double NumberValue { get { return (double)value; } }
        public string StringValue { get { return (string)value; } }
        public IList<McpJsonValue> ArrayValue { get { return (IList<McpJsonValue>)value; } }
        public IDictionary<string, McpJsonValue> ObjectValue { get { return (IDictionary<string, McpJsonValue>)value; } }

        public McpJsonValue this[string key]
        {
            get
            {
                McpJsonValue result;
                return Kind == JsonKind.Object && ObjectValue.TryGetValue(key, out result) ? result : Null();
            }
        }

        public static McpJsonValue Null() { return new McpJsonValue(JsonKind.Null, null); }
        public static McpJsonValue From(bool value) { return new McpJsonValue(JsonKind.Boolean, value); }
        public static McpJsonValue From(double value) { return new McpJsonValue(JsonKind.Number, value); }
        public static McpJsonValue From(string value) { return value == null ? Null() : new McpJsonValue(JsonKind.String, value); }
        public static McpJsonValue Array() { return new McpJsonValue(JsonKind.Array, new List<McpJsonValue>()); }
        public static McpJsonValue Object() { return new McpJsonValue(JsonKind.Object, new Dictionary<string, McpJsonValue>(StringComparer.Ordinal)); }

        public static McpJsonValue FromObject(object input)
        {
            if (input == null) return Null();
            McpJsonValue json = input as McpJsonValue;
            if (json != null) return json;
            if (input is string) return From((string)input);
            if (input is char) return From(input.ToString());
            if (input is bool) return From((bool)input);
            if (input is Enum) return From(input.ToString());
            if (IsNumber(input)) return From(Convert.ToDouble(input, CultureInfo.InvariantCulture));

            IDictionary dictionary = input as IDictionary;
            if (dictionary != null)
            {
                McpJsonValue obj = Object();
                foreach (DictionaryEntry entry in dictionary)
                    obj.ObjectValue[Convert.ToString(entry.Key, CultureInfo.InvariantCulture)] = FromObject(entry.Value);
                return obj;
            }

            IEnumerable sequence = input as IEnumerable;
            if (sequence != null)
            {
                McpJsonValue array = Array();
                foreach (object item in sequence)
                    array.ArrayValue.Add(FromObject(item));
                return array;
            }

            return From(Convert.ToString(input, CultureInfo.InvariantCulture));
        }

        public bool TryGet(string key, out McpJsonValue result)
        {
            if (Kind == JsonKind.Object)
                return ObjectValue.TryGetValue(key, out result);
            result = null;
            return false;
        }

        public string GetString(string key, string defaultValue)
        {
            McpJsonValue item;
            return TryGet(key, out item) && item.Kind == JsonKind.String ? item.StringValue : defaultValue;
        }

        public bool GetBoolean(string key, bool defaultValue)
        {
            McpJsonValue item;
            return TryGet(key, out item) && item.Kind == JsonKind.Boolean ? item.BooleanValue : defaultValue;
        }

        public int GetInt32(string key, int defaultValue, int minimum, int maximum)
        {
            McpJsonValue item;
            if (!TryGet(key, out item) || item.Kind != JsonKind.Number)
                return defaultValue;
            double number = item.NumberValue;
            if (double.IsNaN(number) || double.IsInfinity(number))
                return defaultValue;
            // Clamp a supplied value that is outside the accepted range instead of falling
            // back to the default. Returning the default made larger requests silently return
            // fewer results than smaller ones: a limit above the cap dropped to the default,
            // so asking for more than MaximumSearchResults returned fewer than asking for the
            // maximum. Out-of-range input still never exceeds the configured ceiling.
            if (number < minimum) return minimum;
            if (number > maximum) return maximum;
            return (int)number;
        }

        public string ToJson()
        {
            StringBuilder builder = new StringBuilder();
            Write(builder, this, 0);
            return builder.ToString();
        }

        public override string ToString() { return ToJson(); }

        public static McpJsonValue Parse(string json)
        {
            if (json == null) throw new ArgumentNullException("json");
            Parser parser = new Parser(json);
            McpJsonValue result = parser.ReadValue(0);
            parser.SkipWhiteSpace();
            if (!parser.AtEnd) throw new FormatException("Unexpected content after JSON value.");
            return result;
        }

        public static bool TryParse(string json, out McpJsonValue value, out string error)
        {
            try
            {
                value = Parse(json);
                error = null;
                return true;
            }
            catch (Exception ex)
            {
                value = null;
                error = ex.Message;
                return false;
            }
        }

        private static bool IsNumber(object input)
        {
            TypeCode code = Type.GetTypeCode(input.GetType());
            return code >= TypeCode.SByte && code <= TypeCode.Decimal;
        }

        private static void Write(StringBuilder builder, McpJsonValue node, int depth)
        {
            if (depth > 128) throw new InvalidOperationException("JSON nesting is too deep.");
            switch (node.Kind)
            {
                case JsonKind.Null: builder.Append("null"); break;
                case JsonKind.Boolean: builder.Append(node.BooleanValue ? "true" : "false"); break;
                case JsonKind.Number:
                    if (double.IsNaN(node.NumberValue) || double.IsInfinity(node.NumberValue)) builder.Append("null");
                    else builder.Append(node.NumberValue.ToString("R", CultureInfo.InvariantCulture));
                    break;
                case JsonKind.String: WriteString(builder, node.StringValue); break;
                case JsonKind.Array:
                    builder.Append('[');
                    for (int i = 0; i < node.ArrayValue.Count; i++)
                    {
                        if (i != 0) builder.Append(',');
                        Write(builder, node.ArrayValue[i] ?? Null(), depth + 1);
                    }
                    builder.Append(']');
                    break;
                case JsonKind.Object:
                    builder.Append('{');
                    bool first = true;
                    foreach (KeyValuePair<string, McpJsonValue> pair in node.ObjectValue)
                    {
                        if (!first) builder.Append(',');
                        first = false;
                        WriteString(builder, pair.Key);
                        builder.Append(':');
                        Write(builder, pair.Value ?? Null(), depth + 1);
                    }
                    builder.Append('}');
                    break;
            }
        }

        private static void WriteString(StringBuilder builder, string text)
        {
            builder.Append('"');
            if (text != null)
            {
                for (int i = 0; i < text.Length; i++)
                {
                    char c = text[i];
                    switch (c)
                    {
                        case '"': builder.Append("\\\""); break;
                        case '\\': builder.Append("\\\\"); break;
                        case '\b': builder.Append("\\b"); break;
                        case '\f': builder.Append("\\f"); break;
                        case '\n': builder.Append("\\n"); break;
                        case '\r': builder.Append("\\r"); break;
                        case '\t': builder.Append("\\t"); break;
                        default:
                            if (c < 32) builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                            else builder.Append(c);
                            break;
                    }
                }
            }
            builder.Append('"');
        }

        private sealed class Parser
        {
            private const int MaximumDepth = 128;
            private readonly string json;
            private int index;

            internal Parser(string json) { this.json = json; }
            internal bool AtEnd { get { return index >= json.Length; } }

            internal void SkipWhiteSpace()
            {
                while (!AtEnd && (json[index] == ' ' || json[index] == '\t' || json[index] == '\r' || json[index] == '\n')) index++;
            }

            internal McpJsonValue ReadValue(int depth)
            {
                if (depth > MaximumDepth) throw Error("JSON nesting is too deep.");
                SkipWhiteSpace();
                if (AtEnd) throw Error("Expected a JSON value.");
                char c = json[index];
                if (c == '"') return From(ReadString());
                if (c == '{') return ReadObject(depth + 1);
                if (c == '[') return ReadArray(depth + 1);
                if (c == 't') { ReadLiteral("true"); return From(true); }
                if (c == 'f') { ReadLiteral("false"); return From(false); }
                if (c == 'n') { ReadLiteral("null"); return Null(); }
                if (c == '-' || (c >= '0' && c <= '9')) return From(ReadNumber());
                throw Error("Invalid JSON value.");
            }

            private McpJsonValue ReadObject(int depth)
            {
                index++;
                McpJsonValue result = Object();
                SkipWhiteSpace();
                if (!AtEnd && json[index] == '}') { index++; return result; }
                while (true)
                {
                    SkipWhiteSpace();
                    if (AtEnd || json[index] != '"') throw Error("Expected object property name.");
                    string name = ReadString();
                    SkipWhiteSpace();
                    Require(':');
                    result.ObjectValue[name] = ReadValue(depth);
                    SkipWhiteSpace();
                    if (!AtEnd && json[index] == '}') { index++; return result; }
                    Require(',');
                }
            }

            private McpJsonValue ReadArray(int depth)
            {
                index++;
                McpJsonValue result = Array();
                SkipWhiteSpace();
                if (!AtEnd && json[index] == ']') { index++; return result; }
                while (true)
                {
                    result.ArrayValue.Add(ReadValue(depth));
                    SkipWhiteSpace();
                    if (!AtEnd && json[index] == ']') { index++; return result; }
                    Require(',');
                }
            }

            private string ReadString()
            {
                Require('"');
                StringBuilder builder = new StringBuilder();
                while (!AtEnd)
                {
                    char c = json[index++];
                    if (c == '"') return builder.ToString();
                    if (c < 32) throw Error("Control character in JSON string.");
                    if (c != '\\') { builder.Append(c); continue; }
                    if (AtEnd) throw Error("Unterminated JSON escape.");
                    char escape = json[index++];
                    switch (escape)
                    {
                        case '"': builder.Append('"'); break;
                        case '\\': builder.Append('\\'); break;
                        case '/': builder.Append('/'); break;
                        case 'b': builder.Append('\b'); break;
                        case 'f': builder.Append('\f'); break;
                        case 'n': builder.Append('\n'); break;
                        case 'r': builder.Append('\r'); break;
                        case 't': builder.Append('\t'); break;
                        case 'u': builder.Append((char)ReadHex4()); break;
                        default: throw Error("Invalid JSON escape.");
                    }
                }
                throw Error("Unterminated JSON string.");
            }

            private int ReadHex4()
            {
                if (index + 4 > json.Length) throw Error("Invalid Unicode escape.");
                int code = 0;
                for (int i = 0; i < 4; i++)
                {
                    char c = json[index++];
                    int digit = c >= '0' && c <= '9' ? c - '0' : c >= 'a' && c <= 'f' ? c - 'a' + 10 : c >= 'A' && c <= 'F' ? c - 'A' + 10 : -1;
                    if (digit < 0) throw Error("Invalid Unicode escape.");
                    code = (code << 4) | digit;
                }
                return code;
            }

            private double ReadNumber()
            {
                int start = index;
                if (json[index] == '-') index++;
                if (AtEnd) throw Error("Invalid JSON number.");
                if (json[index] == '0') index++;
                else
                {
                    if (json[index] < '1' || json[index] > '9') throw Error("Invalid JSON number.");
                    while (!AtEnd && json[index] >= '0' && json[index] <= '9') index++;
                }
                if (!AtEnd && json[index] == '.')
                {
                    index++;
                    int fraction = index;
                    while (!AtEnd && json[index] >= '0' && json[index] <= '9') index++;
                    if (fraction == index) throw Error("Invalid JSON number.");
                }
                if (!AtEnd && (json[index] == 'e' || json[index] == 'E'))
                {
                    index++;
                    if (!AtEnd && (json[index] == '+' || json[index] == '-')) index++;
                    int exponent = index;
                    while (!AtEnd && json[index] >= '0' && json[index] <= '9') index++;
                    if (exponent == index) throw Error("Invalid JSON number.");
                }
                double result;
                if (!double.TryParse(json.Substring(start, index - start), NumberStyles.Float, CultureInfo.InvariantCulture, out result) || double.IsInfinity(result))
                    throw Error("JSON number is out of range.");
                return result;
            }

            private void ReadLiteral(string literal)
            {
                if (index + literal.Length > json.Length || string.CompareOrdinal(json, index, literal, 0, literal.Length) != 0)
                    throw Error("Invalid JSON literal.");
                index += literal.Length;
            }

            private void Require(char expected)
            {
                SkipWhiteSpace();
                if (AtEnd || json[index] != expected) throw Error("Expected '" + expected + "'.");
                index++;
            }

            private FormatException Error(string message) { return new FormatException(message + " At character " + index + "."); }
        }
    }
}
