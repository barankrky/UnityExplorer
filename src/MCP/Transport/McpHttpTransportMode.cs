using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace UnityExplorer.MCP.Transport
{
    /// <summary>
    /// Selects the HTTP transport semantics exposed by <see cref="McpHttpBridge"/>.
    /// Change the bridge mode only while it is stopped.
    /// </summary>
    public enum McpHttpTransportMode
    {
        LegacySse = 0,
        StreamableHttp = 1
    }

    /// <summary>
    /// Owns one legacy HTTP+SSE connection. Producers only enqueue complete JSON
    /// messages; the connection worker is the sole writer to the network stream.
    /// </summary>
    internal sealed class McpLegacySseSession : IDisposable
    {
        private const int KeepAliveMilliseconds = 15000;
        private readonly object sync = new object();
        private readonly Queue<string> messages = new Queue<string>();
        private readonly AutoResetEvent wake = new AutoResetEvent(false);
        private bool closed;

        internal McpLegacySseSession(string id, string postEndpoint)
        {
            Id = id;
            PostEndpoint = postEndpoint;
        }

        internal string Id { get; private set; }
        internal string PostEndpoint { get; private set; }

        internal bool TryEnqueueMessage(string json)
        {
            if (string.IsNullOrEmpty(json))
                return false;

            lock (sync)
            {
                if (closed)
                    return false;
                messages.Enqueue(json);
            }
            try { wake.Set(); }
            catch (ObjectDisposedException) { return false; }
            return true;
        }

        internal void Run(Stream stream, Func<bool> isServerRunning)
        {
            WriteHeaders(stream);
            WriteEvent(stream, "endpoint", PostEndpoint);

            while (isServerRunning() && !IsClosed)
            {
                string message;
                bool wroteMessage = false;
                while (TryDequeue(out message))
                {
                    WriteEvent(stream, "message", message);
                    wroteMessage = true;
                }

                if (!isServerRunning() || IsClosed)
                    break;

                if (!wroteMessage && !wake.WaitOne(KeepAliveMilliseconds, false))
                    WriteComment(stream, "keepalive");
            }
        }

        internal void Close()
        {
            bool signal = false;
            lock (sync)
            {
                if (!closed)
                {
                    closed = true;
                    signal = true;
                }
            }
            if (signal)
            {
                try { wake.Set(); }
                catch (ObjectDisposedException) { }
            }
        }

        public void Dispose()
        {
            Close();
            wake.Close();
        }

        private bool IsClosed
        {
            get
            {
                lock (sync)
                    return closed;
            }
        }

        private bool TryDequeue(out string message)
        {
            lock (sync)
            {
                if (messages.Count == 0)
                {
                    message = null;
                    return false;
                }
                message = messages.Dequeue();
                return true;
            }
        }

        private static void WriteHeaders(Stream stream)
        {
            string headers = "HTTP/1.1 200 OK\r\n" +
                "Content-Type: text/event-stream\r\n" +
                "Cache-Control: no-cache, no-transform\r\n" +
                "Connection: keep-alive\r\n" +
                "X-Accel-Buffering: no\r\n\r\n";
            byte[] bytes = Encoding.ASCII.GetBytes(headers);
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush();
        }

        private static void WriteEvent(Stream stream, string eventName, string data)
        {
            // JSON produced by this server is single-line. Splitting nevertheless keeps
            // the SSE framing valid if an endpoint or future payload contains newlines.
            StringBuilder frame = new StringBuilder();
            frame.Append("event: ").Append(eventName).Append('\n');
            string normalized = (data ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');
            string[] lines = normalized.Split(new char[] { '\n' });
            for (int i = 0; i < lines.Length; i++)
                frame.Append("data: ").Append(lines[i]).Append('\n');
            frame.Append('\n');

            byte[] bytes = Encoding.UTF8.GetBytes(frame.ToString());
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush();
        }

        private static void WriteComment(Stream stream, string comment)
        {
            byte[] bytes = Encoding.ASCII.GetBytes(": " + comment + "\n\n");
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush();
        }
    }
}
