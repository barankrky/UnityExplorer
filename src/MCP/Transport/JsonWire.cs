using System;
using System.Text;

namespace UnityExplorer.MCP.Transport
{
    /// <summary>
    /// Small dependency-free JSON scanner. It deliberately exposes raw JSON values
    /// so the transport works on net35 without selecting a serializer for executors.
    /// </summary>
    internal static class JsonWire
    {
        private const int MaximumDepth = 128;

        internal static bool TryParseRequest(string json, string remoteAddress, out McpRequest request, out string error)
        {
            request = null;
            error = null;
            if (json == null)
            {
                error = "Request body is missing.";
                return false;
            }

            try
            {
                int index = 0;
                SkipWhiteSpace(json, ref index);
                if (index >= json.Length || json[index] != '{')
                    throw new FormatException("The JSON-RPC request must be an object.");
                index++;

                string version = null;
                string method = null;
                string idJson = null;
                bool hasMethod = false;
                bool hasId = false;

                SkipWhiteSpace(json, ref index);
                if (index < json.Length && json[index] == '}')
                    index++;
                else
                {
                    while (true)
                    {
                        SkipWhiteSpace(json, ref index);
                        string name = ReadString(json, ref index);
                        SkipWhiteSpace(json, ref index);
                        Require(json, ref index, ':');
                        SkipWhiteSpace(json, ref index);

                        int valueStart = index;
                        string stringValue = null;
                        if (index < json.Length && json[index] == '"')
                        {
                            stringValue = ReadString(json, ref index);
                        }
                        else
                        {
                            SkipValue(json, ref index, 0);
                        }
                        int valueEnd = index;

                        if (name == "jsonrpc")
                            version = stringValue;
                        else if (name == "method")
                        {
                            method = stringValue;
                            hasMethod = true;
                        }
                        else if (name == "id")
                        {
                            idJson = json.Substring(valueStart, valueEnd - valueStart);
                            hasId = true;
                        }

                        SkipWhiteSpace(json, ref index);
                        if (index >= json.Length)
                            throw new FormatException("Unterminated JSON object.");
                        if (json[index] == '}')
                        {
                            index++;
                            break;
                        }
                        Require(json, ref index, ',');
                    }
                }

                SkipWhiteSpace(json, ref index);
                if (index != json.Length)
                    throw new FormatException("Unexpected content after the JSON object.");
                if (version != "2.0")
                    throw new FormatException("jsonrpc must be the string \"2.0\".");
                if (!hasMethod || string.IsNullOrEmpty(method))
                    throw new FormatException("method must be a non-empty string.");
                if (hasId && !IsValidId(idJson))
                    throw new FormatException("id must be a string, number, or null.");

                request = new McpRequest(json, hasId ? idJson : null, method, remoteAddress);
                return true;
            }
            catch (FormatException ex)
            {
                error = ex.Message;
                return false;
            }
        }

        internal static void ValidateSingleValue(string json, string parameterName)
        {
            if (json == null)
                throw new ArgumentNullException(parameterName);
            try
            {
                int index = 0;
                SkipWhiteSpace(json, ref index);
                SkipValue(json, ref index, 0);
                SkipWhiteSpace(json, ref index);
                if (index != json.Length)
                    throw new FormatException("Unexpected trailing JSON content.");
            }
            catch (FormatException ex)
            {
                throw new ArgumentException("Value is not valid JSON: " + ex.Message, parameterName);
            }
        }

        internal static string Quote(string value)
        {
            if (value == null)
                return "null";

            StringBuilder builder = new StringBuilder(value.Length + 2);
            builder.Append('"');
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
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
                        if (c < 32)
                        {
                            builder.Append("\\u");
                            builder.Append(((int)c).ToString("x4", System.Globalization.CultureInfo.InvariantCulture));
                        }
                        else
                            builder.Append(c);
                        break;
                }
            }
            builder.Append('"');
            return builder.ToString();
        }

        private static bool IsValidId(string idJson)
        {
            if (idJson == "null")
                return true;
            if (idJson.Length > 0 && idJson[0] == '"')
                return true;
            int index = 0;
            try
            {
                SkipNumber(idJson, ref index);
                return index == idJson.Length;
            }
            catch (FormatException)
            {
                return false;
            }
        }

        private static void SkipValue(string json, ref int index, int depth)
        {
            if (depth > MaximumDepth)
                throw new FormatException("JSON nesting is too deep.");
            if (index >= json.Length)
                throw new FormatException("Expected a JSON value.");

            char c = json[index];
            if (c == '"')
            {
                ReadString(json, ref index);
                return;
            }
            if (c == '{')
            {
                index++;
                SkipWhiteSpace(json, ref index);
                if (index < json.Length && json[index] == '}')
                {
                    index++;
                    return;
                }
                while (true)
                {
                    SkipWhiteSpace(json, ref index);
                    ReadString(json, ref index);
                    SkipWhiteSpace(json, ref index);
                    Require(json, ref index, ':');
                    SkipWhiteSpace(json, ref index);
                    SkipValue(json, ref index, depth + 1);
                    SkipWhiteSpace(json, ref index);
                    if (index >= json.Length)
                        throw new FormatException("Unterminated JSON object.");
                    if (json[index] == '}')
                    {
                        index++;
                        return;
                    }
                    Require(json, ref index, ',');
                }
            }
            if (c == '[')
            {
                index++;
                SkipWhiteSpace(json, ref index);
                if (index < json.Length && json[index] == ']')
                {
                    index++;
                    return;
                }
                while (true)
                {
                    SkipWhiteSpace(json, ref index);
                    SkipValue(json, ref index, depth + 1);
                    SkipWhiteSpace(json, ref index);
                    if (index >= json.Length)
                        throw new FormatException("Unterminated JSON array.");
                    if (json[index] == ']')
                    {
                        index++;
                        return;
                    }
                    Require(json, ref index, ',');
                }
            }
            if (c == '-' || (c >= '0' && c <= '9'))
            {
                SkipNumber(json, ref index);
                return;
            }
            if (StartsWith(json, index, "true")) { index += 4; return; }
            if (StartsWith(json, index, "false")) { index += 5; return; }
            if (StartsWith(json, index, "null")) { index += 4; return; }
            throw new FormatException("Invalid JSON value.");
        }

        private static string ReadString(string json, ref int index)
        {
            Require(json, ref index, '"');
            StringBuilder builder = null;
            int segmentStart = index;
            while (index < json.Length)
            {
                char c = json[index++];
                if (c == '"')
                {
                    if (builder == null)
                        return json.Substring(segmentStart, index - segmentStart - 1);
                    builder.Append(json, segmentStart, index - segmentStart - 1);
                    return builder.ToString();
                }
                if (c < 32)
                    throw new FormatException("Control character in JSON string.");
                if (c != '\\')
                    continue;

                if (builder == null)
                    builder = new StringBuilder();
                builder.Append(json, segmentStart, index - segmentStart - 1);
                if (index >= json.Length)
                    throw new FormatException("Unterminated JSON escape.");
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
                    case 'u':
                        if (index + 4 > json.Length)
                            throw new FormatException("Invalid Unicode escape.");
                        int code = 0;
                        for (int i = 0; i < 4; i++)
                        {
                            int hex = HexValue(json[index++]);
                            if (hex < 0)
                                throw new FormatException("Invalid Unicode escape.");
                            code = (code << 4) | hex;
                        }
                        builder.Append((char)code);
                        break;
                    default:
                        throw new FormatException("Invalid JSON escape.");
                }
                segmentStart = index;
            }
            throw new FormatException("Unterminated JSON string.");
        }

        private static void SkipNumber(string json, ref int index)
        {
            int start = index;
            if (index < json.Length && json[index] == '-') index++;
            if (index >= json.Length) throw new FormatException("Invalid JSON number.");
            if (json[index] == '0')
                index++;
            else
            {
                if (json[index] < '1' || json[index] > '9') throw new FormatException("Invalid JSON number.");
                while (index < json.Length && json[index] >= '0' && json[index] <= '9') index++;
            }
            if (index < json.Length && json[index] == '.')
            {
                index++;
                int fractionStart = index;
                while (index < json.Length && json[index] >= '0' && json[index] <= '9') index++;
                if (index == fractionStart) throw new FormatException("Invalid JSON number.");
            }
            if (index < json.Length && (json[index] == 'e' || json[index] == 'E'))
            {
                index++;
                if (index < json.Length && (json[index] == '+' || json[index] == '-')) index++;
                int exponentStart = index;
                while (index < json.Length && json[index] >= '0' && json[index] <= '9') index++;
                if (index == exponentStart) throw new FormatException("Invalid JSON number.");
            }
            if (index == start) throw new FormatException("Invalid JSON number.");
        }

        private static void SkipWhiteSpace(string json, ref int index)
        {
            while (index < json.Length)
            {
                char c = json[index];
                if (c != ' ' && c != '\t' && c != '\r' && c != '\n')
                    return;
                index++;
            }
        }

        private static void Require(string json, ref int index, char expected)
        {
            if (index >= json.Length || json[index] != expected)
                throw new FormatException("Expected '" + expected + "'.");
            index++;
        }

        private static bool StartsWith(string value, int index, string expected)
        {
            if (index + expected.Length > value.Length)
                return false;
            for (int i = 0; i < expected.Length; i++)
                if (value[index + i] != expected[i]) return false;
            return true;
        }

        private static int HexValue(char c)
        {
            if (c >= '0' && c <= '9') return c - '0';
            if (c >= 'a' && c <= 'f') return c - 'a' + 10;
            if (c >= 'A' && c <= 'F') return c - 'A' + 10;
            return -1;
        }
    }
}
