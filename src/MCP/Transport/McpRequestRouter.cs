using System;
using System.Collections.Generic;

namespace UnityExplorer.MCP.Transport
{
    /// <summary>
    /// Thread-safe method router intended to be populated by the MCP executor.
    /// Handlers are invoked only when the transport is pumped on Unity's main thread.
    /// </summary>
    public sealed class McpRequestRouter : IMcpRequestDispatcher
    {
        private readonly object sync = new object();
        private readonly Dictionary<string, IMcpRequestHandler> handlers =
            new Dictionary<string, IMcpRequestHandler>(StringComparer.Ordinal);

        public void Register(string method, IMcpRequestHandler handler)
        {
            if (string.IsNullOrEmpty(method))
                throw new ArgumentException("Method must not be empty.", "method");
            if (handler == null)
                throw new ArgumentNullException("handler");

            lock (sync)
                handlers[method] = handler;
        }

        public bool Unregister(string method)
        {
            if (method == null)
                return false;
            lock (sync)
                return handlers.Remove(method);
        }

        public bool Contains(string method)
        {
            if (method == null)
                return false;
            lock (sync)
                return handlers.ContainsKey(method);
        }

        public McpResponse Dispatch(McpRequest request)
        {
            if (request == null)
                throw new ArgumentNullException("request");

            IMcpRequestHandler handler;
            lock (sync)
                handlers.TryGetValue(request.Method, out handler);

            if (handler == null)
                return McpResponse.Error(-32601, "Method not found: " + request.Method);

            return handler.Handle(request) ?? McpResponse.Error(-32603, "Handler returned no response.");
        }
    }

    /// <summary>Convenience adapter for registering delegates as handlers.</summary>
    public sealed class DelegateMcpRequestHandler : IMcpRequestHandler
    {
        private readonly Func<McpRequest, McpResponse> callback;

        public DelegateMcpRequestHandler(Func<McpRequest, McpResponse> callback)
        {
            if (callback == null)
                throw new ArgumentNullException("callback");
            this.callback = callback;
        }

        public McpResponse Handle(McpRequest request)
        {
            return callback(request);
        }
    }
}
