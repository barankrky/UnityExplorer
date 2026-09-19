using System;

namespace UnityExplorer.MCP.Transport
{
    /// <summary>
    /// Receives validated JSON-RPC requests. Dispatch is always performed by
    /// <see cref="IMcpTransport.PumpMainThread"/>, never by an HTTP worker thread.
    /// </summary>
    public interface IMcpRequestDispatcher
    {
        McpResponse Dispatch(McpRequest request);
    }

    public interface IMcpRequestHandler
    {
        McpResponse Handle(McpRequest request);
    }

    public interface IMcpTransport : IDisposable
    {
        bool IsRunning { get; }
        int PendingRequestCount { get; }
        McpHttpBridgeOptions Options { get; }

        void Start();
        void Stop();

        /// <summary>
        /// Executes queued requests on the calling thread. The owner must call
        /// this method from Unity's Update loop (or another known main-thread hook).
        /// </summary>
        int PumpMainThread();

        int PumpMainThread(int maximumRequests);
    }

    /// <summary>
    /// Mutable startup settings. Change these only while the bridge is stopped.
    /// </summary>
    public sealed class McpHttpBridgeOptions
    {
        public McpHttpBridgeOptions()
        {
            Port = 17891;
            TransportMode = McpHttpTransportMode.LegacySse;
            RpcPath = "/mcp";
            HealthPath = "/health";
            Token = string.Empty;
            RequireTokenForHealth = false;
            RequestTimeoutMilliseconds = 30000;
            MaxRequestBodyBytes = 1024 * 1024;
            MaxPendingRequests = 128;
            MaxRequestsPerPump = 16;
        }

        public int Port { get; set; }
        public McpHttpTransportMode TransportMode { get; set; }
        public string RpcPath { get; set; }
        public string HealthPath { get; set; }
        public string Token { get; set; }
        public bool RequireTokenForHealth { get; set; }
        public int RequestTimeoutMilliseconds { get; set; }
        public int MaxRequestBodyBytes { get; set; }
        public int MaxPendingRequests { get; set; }
        public int MaxRequestsPerPump { get; set; }

        internal void Validate()
        {
            if (Port < 1 || Port > 65535)
                throw new ArgumentOutOfRangeException("Port", "Port must be between 1 and 65535.");
            if (!Enum.IsDefined(typeof(McpHttpTransportMode), TransportMode))
                throw new ArgumentOutOfRangeException("TransportMode");
            if (RequestTimeoutMilliseconds < 1)
                throw new ArgumentOutOfRangeException("RequestTimeoutMilliseconds");
            if (MaxRequestBodyBytes < 1)
                throw new ArgumentOutOfRangeException("MaxRequestBodyBytes");
            if (MaxPendingRequests < 1)
                throw new ArgumentOutOfRangeException("MaxPendingRequests");
            if (MaxRequestsPerPump < 1)
                throw new ArgumentOutOfRangeException("MaxRequestsPerPump");

            RpcPath = NormalizePath(RpcPath, "RpcPath");
            HealthPath = NormalizePath(HealthPath, "HealthPath");
            Token = Token ?? string.Empty;

            if (string.Equals(RpcPath, HealthPath, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("RpcPath and HealthPath must be different.");
        }

        private static string NormalizePath(string value, string parameterName)
        {
            if (string.IsNullOrEmpty(value))
                throw new ArgumentException("Path must not be empty.", parameterName);

            string path = value.Trim();
            if (path.Length == 0)
                throw new ArgumentException("Path must not be empty.", parameterName);
            if (path[0] != '/')
                path = "/" + path;
            while (path.Length > 1 && path[path.Length - 1] == '/')
                path = path.Substring(0, path.Length - 1);
            return path;
        }
    }
}
