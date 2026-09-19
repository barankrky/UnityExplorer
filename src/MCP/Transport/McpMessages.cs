using System;

namespace UnityExplorer.MCP.Transport
{
    /// <summary>
    /// A validated JSON-RPC 2.0 request. RawJson preserves params and extension
    /// fields without imposing a JSON library dependency on old Unity runtimes.
    /// </summary>
    public sealed class McpRequest
    {
        internal McpRequest(string rawJson, string idJson, string method, string remoteAddress)
        {
            RawJson = rawJson;
            IdJson = idJson;
            Method = method;
            RemoteAddress = remoteAddress;
        }

        public string RawJson { get; private set; }
        public string IdJson { get; private set; }
        public string Method { get; private set; }
        public string RemoteAddress { get; private set; }
        public bool IsNotification { get { return string.IsNullOrEmpty(IdJson); } }
    }

    /// <summary>
    /// JSON-RPC response returned by an executor. ResultJson/DataJson must each
    /// be one complete JSON value, not an entire JSON-RPC envelope.
    /// </summary>
    public sealed class McpResponse
    {
        private McpResponse(bool success, string resultJson, int errorCode, string errorMessage, string dataJson)
        {
            IsSuccess = success;
            ResultJson = resultJson;
            ErrorCode = errorCode;
            ErrorMessage = errorMessage;
            DataJson = dataJson;
        }

        public bool IsSuccess { get; private set; }
        public string ResultJson { get; private set; }
        public int ErrorCode { get; private set; }
        public string ErrorMessage { get; private set; }
        public string DataJson { get; private set; }

        public static McpResponse Success()
        {
            return SuccessJson("null");
        }

        public static McpResponse SuccessJson(string resultJson)
        {
            JsonWire.ValidateSingleValue(resultJson, "resultJson");
            return new McpResponse(true, resultJson, 0, null, null);
        }

        public static McpResponse SuccessString(string value)
        {
            return SuccessJson(JsonWire.Quote(value));
        }

        public static McpResponse Error(int code, string message)
        {
            return Error(code, message, null);
        }

        public static McpResponse Error(int code, string message, string dataJson)
        {
            if (message == null)
                message = string.Empty;
            if (dataJson != null)
                JsonWire.ValidateSingleValue(dataJson, "dataJson");
            return new McpResponse(false, null, code, message, dataJson);
        }

        internal string ToJson(string idJson)
        {
            string id = string.IsNullOrEmpty(idJson) ? "null" : idJson;
            if (IsSuccess)
                return "{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"result\":" + ResultJson + "}";

            string data = DataJson == null ? string.Empty : ",\"data\":" + DataJson;
            return "{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"error\":{\"code\":" +
                ErrorCode.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                ",\"message\":" + JsonWire.Quote(ErrorMessage) + data + "}}";
        }
    }
}
