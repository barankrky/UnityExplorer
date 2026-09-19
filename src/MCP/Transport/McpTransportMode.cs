namespace UnityExplorer.MCP.Transport
{
    /// <summary>
    /// MCP network transports hosted directly by the UnityExplorer DLL.
    /// Enum names are intentionally stable because configuration backends persist
    /// them as strings.
    /// </summary>
    public enum McpTransportMode
    {
        /// <summary>Legacy HTTP + Server-Sent Events MCP transport.</summary>
        SSE = 0,

        /// <summary>MCP Streamable HTTP transport.</summary>
        StreamableHTTP = 1
    }
}
