using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace UnityExplorer.MCP.Transport
{
    /// <summary>
    /// Loopback-only HTTP/JSON bridge implemented on TcpListener for compatibility
    /// with old Unity Mono profiles and newer CoreCLR/IL2CPP configurations.
    /// Network workers only validate and enqueue; executor code is exclusively
    /// invoked by PumpMainThread.
    /// </summary>
    public sealed class McpHttpBridge : IMcpTransport
    {
        private const int MaximumHeaderBytes = 32768;
        private readonly object lifecycleSync = new object();
        private readonly object queueSync = new object();
        private readonly object sessionSync = new object();
        private readonly Queue<PendingRequest> pendingRequests = new Queue<PendingRequest>();
        private readonly Dictionary<string, McpLegacySseSession> legacySessions =
            new Dictionary<string, McpLegacySseSession>(StringComparer.Ordinal);
        private readonly IMcpRequestDispatcher dispatcher;
        private readonly int mainThreadId;

        private TcpListener listener;
        private Thread listenerThread;
        private volatile bool running;
        private bool disposed;
        private DateTime startedUtc;

        // Validated startup snapshot. Options remain editable for the next Start().
        private string rpcPath;
        private string healthPath;
        private string token;
        private bool requireTokenForHealth;
        private int requestTimeoutMilliseconds;
        private int maxRequestBodyBytes;
        private int maxPendingRequests;
        private int maxRequestsPerPump;
        private McpHttpTransportMode configuredTransportMode = McpHttpTransportMode.LegacySse;
        private McpHttpTransportMode activeTransportMode;

        public McpHttpBridge(IMcpRequestDispatcher dispatcher)
            : this(new McpHttpBridgeOptions(), dispatcher)
        {
        }

        /// <summary>
        /// Construct this object on Unity's main thread. PumpMainThread rejects calls
        /// from any other thread, preventing accidental Unity API access by workers.
        /// </summary>
        public McpHttpBridge(McpHttpBridgeOptions options, IMcpRequestDispatcher dispatcher)
        {
            if (options == null)
                throw new ArgumentNullException("options");
            if (dispatcher == null)
                throw new ArgumentNullException("dispatcher");

            Options = options;
            configuredTransportMode = options.TransportMode;
            this.dispatcher = dispatcher;
            mainThreadId = Thread.CurrentThread.ManagedThreadId;
        }
        public McpHttpBridge(McpHttpBridgeOptions options, IMcpRequestDispatcher dispatcher,
            McpHttpTransportMode transportMode)
            : this(options, dispatcher)
        {
            TransportMode = transportMode;
        }

        public McpHttpBridgeOptions Options { get; private set; }
        public bool IsRunning { get { return running; } }
        public int MainThreadId { get { return mainThreadId; } }

        /// <summary>
        /// Selects legacy HTTP+SSE or the current Streamable HTTP transport.
        /// The value is captured by Start() and may only be changed while stopped.
        /// LegacySse is the default to preserve compatibility with SSE-only MCP clients.
        /// </summary>
        public McpHttpTransportMode TransportMode
        {
            get
            {
                lock (lifecycleSync)
                    return configuredTransportMode;
            }
            set
            {
                if (!Enum.IsDefined(typeof(McpHttpTransportMode), value))
                    throw new ArgumentOutOfRangeException("value");
                lock (lifecycleSync)
                {
                    ThrowIfDisposed();
                    if (running)
                        throw new InvalidOperationException("TransportMode may only be changed while the bridge is stopped.");
                    configuredTransportMode = value;
                    Options.TransportMode = value;
                }
            }
        }

        public McpHttpTransportMode ActiveTransportMode
        {
            get
            {
                lock (lifecycleSync)
                    return running ? activeTransportMode : configuredTransportMode;
            }
        }

        public int PendingRequestCount
        {
            get
            {
                lock (queueSync)
                    return pendingRequests.Count;
            }
        }

        public void Start()
        {
            lock (lifecycleSync)
            {
                ThrowIfDisposed();
                if (running)
                    return;

                Options.Validate();
                CaptureOptions();

                TcpListener newListener = new TcpListener(IPAddress.Loopback, Options.Port);
                try
                {
                    newListener.Start();
                }
                catch
                {
                    try { newListener.Stop(); } catch { }
                    throw;
                }

                listener = newListener;
                startedUtc = DateTime.UtcNow;
                running = true;
                listenerThread = new Thread(ListenLoop);
                listenerThread.IsBackground = true;
                listenerThread.Name = "UnityExplorer MCP HTTP Listener";
                listenerThread.Start();
            }
        }

        public void Stop()
        {
            Thread threadToJoin;
            lock (lifecycleSync)
            {
                if (!running && listener == null)
                    return;

                running = false;
                TcpListener oldListener = listener;
                listener = null;
                threadToJoin = listenerThread;
                listenerThread = null;
                if (oldListener != null)
                {
                    try { oldListener.Stop(); } catch { }
                }
            }

            CloseAllLegacySessions();
            CancelQueuedRequests(McpResponse.Error(-32000, "MCP bridge stopped."));
            if (threadToJoin != null && threadToJoin != Thread.CurrentThread)
            {
                try { threadToJoin.Join(1000); } catch { }
            }
        }

        public int PumpMainThread()
        {
            return PumpMainThread(maxRequestsPerPump > 0 ? maxRequestsPerPump : Options.MaxRequestsPerPump);
        }

        public int PumpMainThread(int maximumRequests)
        {
            ThrowIfDisposed();
            if (Thread.CurrentThread.ManagedThreadId != mainThreadId)
                throw new InvalidOperationException("MCP requests may only be pumped on the thread that created the bridge.");
            if (maximumRequests < 1)
                throw new ArgumentOutOfRangeException("maximumRequests");

            int executed = 0;
            while (executed < maximumRequests)
            {
                PendingRequest pending;
                lock (queueSync)
                {
                    if (pendingRequests.Count == 0)
                        break;
                    pending = pendingRequests.Dequeue();
                }

                if (!pending.TryBeginExecution())
                    continue;

                McpResponse response;
                try
                {
                    response = dispatcher.Dispatch(pending.Request);
                    if (response == null)
                        response = McpResponse.Error(-32603, "Dispatcher returned no response.");
                }
                catch (Exception ex)
                {
                    response = McpResponse.Error(-32603, "Executor error.",
                        "{\"type\":" + JsonWire.Quote(ex.GetType().FullName) +
                        ",\"message\":" + JsonWire.Quote(ex.Message) + "}");
                }

                pending.Complete(response);
                executed++;
            }
            return executed;
        }

        public void Dispose()
        {
            lock (lifecycleSync)
            {
                if (disposed)
                    return;
            }
            Stop();
            lock (lifecycleSync)
                disposed = true;
        }

        private void ListenLoop()
        {
            while (running)
            {
                try
                {
                    TcpListener current = listener;
                    if (current == null)
                        break;
                    TcpClient client = current.AcceptTcpClient();
                    ThreadPool.QueueUserWorkItem(HandleClient, client);
                }
                catch (SocketException)
                {
                    if (running)
                        Thread.Sleep(25);
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch
                {
                    if (!running)
                        break;
                    Thread.Sleep(25);
                }
            }
        }

        private void HandleClient(object state)
        {
            TcpClient client = state as TcpClient;
            if (client == null)
                return;

            try
            {
                client.NoDelay = true;
                client.ReceiveTimeout = requestTimeoutMilliseconds;
                client.SendTimeout = requestTimeoutMilliseconds;
                IPEndPoint remote = client.Client.RemoteEndPoint as IPEndPoint;
                using (client)
                using (NetworkStream stream = client.GetStream())
                {
                    if (remote == null || !IPAddress.IsLoopback(remote.Address))
                    {
                        WriteSimpleError(stream, 403, "Loopback clients only.");
                        return;
                    }

                    HttpRequestData request;
                    try
                    {
                        request = ReadHttpRequest(stream, maxRequestBodyBytes);
                    }
                    catch (RequestTooLargeException)
                    {
                        WriteSimpleError(stream, 413, "Request is too large.");
                        return;
                    }
                    catch (HttpParseException ex)
                    {
                        WriteSimpleError(stream, ex.StatusCode, ex.Message);
                        return;
                    }

                    if (string.Equals(request.Path, healthPath, StringComparison.OrdinalIgnoreCase))
                    {
                        HandleHealth(stream, request);
                        return;
                    }
                    if (string.Equals(request.Path, rpcPath, StringComparison.OrdinalIgnoreCase))
                    {
                        HandleRpc(stream, request, remote.ToString());
                        return;
                    }
                    WriteSimpleError(stream, 404, "Endpoint not found.");
                }
            }
            catch
            {
                try { client.Close(); } catch { }
            }
        }

        private void HandleHealth(Stream stream, HttpRequestData request)
        {
            if (!string.Equals(request.Method, "GET", StringComparison.OrdinalIgnoreCase))
            {
                WriteSimpleError(stream, 405, "GET required.", "Allow: GET\r\n");
                return;
            }
            if (requireTokenForHealth && !IsAuthorized(request))
            {
                WriteSimpleError(stream, 401, "Unauthorized.", "WWW-Authenticate: Bearer\r\n");
                return;
            }

            double uptime = Math.Max(0, (DateTime.UtcNow - startedUtc).TotalSeconds);
            string json = "{\"status\":\"ok\",\"running\":" + (running ? "true" : "false") +
                ",\"pendingRequests\":" + PendingRequestCount.ToString(CultureInfo.InvariantCulture) +
                ",\"uptimeSeconds\":" + ((long)uptime).ToString(CultureInfo.InvariantCulture) + "}";
            WriteJson(stream, 200, json, null);
        }

        private void HandleRpc(Stream stream, HttpRequestData httpRequest, string remoteAddress)
        {
            if (!IsAuthorized(httpRequest))
            {
                WriteSimpleError(stream, 401, "Unauthorized.", "WWW-Authenticate: Bearer\r\n");
                return;
            }
            if (!IsAllowedOrigin(httpRequest))
            {
                WriteSimpleError(stream, 403, "Origin is not allowed.");
                return;
            }
            if (!running)
            {
                WriteSimpleError(stream, 503, "MCP bridge is stopping.");
                return;
            }

            if (activeTransportMode == McpHttpTransportMode.LegacySse)
                HandleLegacySse(stream, httpRequest, remoteAddress);
            else
                HandleStreamableHttp(stream, httpRequest, remoteAddress);
        }

        private void HandleLegacySse(Stream stream, HttpRequestData httpRequest, string remoteAddress)
        {
            if (string.Equals(httpRequest.Method, "GET", StringComparison.OrdinalIgnoreCase))
            {
                if (!Accepts(httpRequest, "text/event-stream"))
                {
                    WriteSimpleError(stream, 406, "Accept must allow text/event-stream.");
                    return;
                }

                string sessionId = Guid.NewGuid().ToString("N");
                string postEndpoint = rpcPath + "?sessionId=" + sessionId;
                McpLegacySseSession session = new McpLegacySseSession(sessionId, postEndpoint);
                RegisterLegacySession(session);
                try
                {
                    session.Run(stream, delegate { return running; });
                }
                finally
                {
                    UnregisterLegacySession(session);
                    session.Dispose();
                }
                return;
            }

            if (!string.Equals(httpRequest.Method, "POST", StringComparison.OrdinalIgnoreCase))
            {
                WriteSimpleError(stream, 405, "GET or POST required.", "Allow: GET, POST\r\n");
                return;
            }

            if (!HasJsonContentType(httpRequest))
            {
                WriteSimpleError(stream, 415, "Content-Type must be application/json.");
                return;
            }

            string sessionIdValue;
            if (!httpRequest.Query.TryGetValue("sessionId", out sessionIdValue) || string.IsNullOrEmpty(sessionIdValue))
            {
                WriteSimpleError(stream, 400, "Legacy SSE POST requires a sessionId query parameter.");
                return;
            }

            McpLegacySseSession sessionForPost;
            if (!TryGetLegacySession(sessionIdValue, out sessionForPost))
            {
                WriteSimpleError(stream, 404, "SSE session was not found or has closed.");
                return;
            }

            McpRequest request;
            if (!TryReadMcpRequest(stream, httpRequest, remoteAddress, out request))
                return;

            PendingRequest pending = new PendingRequest(request, delegate(McpResponse response)
            {
                if (!request.IsNotification)
                    sessionForPost.TryEnqueueMessage(response.ToJson(request.IdJson));
            });

            if (!TryEnqueue(pending))
            {
                WriteJson(stream, 503,
                    McpResponse.Error(-32001, "MCP request queue is full or stopped.").ToJson(request.IdJson), null);
                return;
            }

            // Legacy SSE acknowledges the POST immediately. JSON-RPC responses are
            // delivered as `message` events on the associated GET stream.
            WriteResponse(stream, 202, null, null, null);
        }

        private void HandleStreamableHttp(Stream stream, HttpRequestData httpRequest, string remoteAddress)
        {
            if (string.Equals(httpRequest.Method, "GET", StringComparison.OrdinalIgnoreCase))
            {
                // This bridge has no unsolicited server messages. Streamable HTTP
                // explicitly permits a server to reject a standalone SSE GET.
                WriteSimpleError(stream, 405, "Standalone SSE streams are not supported in Streamable HTTP mode.",
                    "Allow: POST\r\n");
                return;
            }
            if (string.Equals(httpRequest.Method, "DELETE", StringComparison.OrdinalIgnoreCase))
            {
                // The implementation is stateless and therefore has no session to end.
                WriteSimpleError(stream, 405, "This Streamable HTTP endpoint is stateless.", "Allow: POST\r\n");
                return;
            }
            if (!string.Equals(httpRequest.Method, "POST", StringComparison.OrdinalIgnoreCase))
            {
                WriteSimpleError(stream, 405, "POST required.", "Allow: POST\r\n");
                return;
            }
            if (!HasJsonContentType(httpRequest))
            {
                WriteSimpleError(stream, 415, "Content-Type must be application/json.");
                return;
            }
            if (!AcceptsJsonResponse(httpRequest))
            {
                WriteSimpleError(stream, 406, "Accept must allow application/json.");
                return;
            }

            McpRequest request;
            if (!TryReadMcpRequest(stream, httpRequest, remoteAddress, out request))
                return;

            PendingRequest pending = new PendingRequest(request, null);
            if (!TryEnqueue(pending))
            {
                WriteJson(stream, 503,
                    McpResponse.Error(-32001, "MCP request queue is full or stopped.").ToJson(request.IdJson), null);
                return;
            }

            if (request.IsNotification)
            {
                // Notifications have no JSON-RPC response. The queued work still runs
                // on Unity's main thread after this HTTP acknowledgement is sent.
                WriteResponse(stream, 202, null, null, null);
                return;
            }

            if (!pending.Wait(requestTimeoutMilliseconds))
            {
                pending.TryCancelQueued();
                WriteJson(stream, 504,
                    McpResponse.Error(-32002, "Timed out waiting for Unity's main thread.").ToJson(request.IdJson), null);
                return;
            }

            McpResponse response = pending.Response ?? McpResponse.Error(-32603, "Missing executor response.");
            WriteJson(stream, 200, response.ToJson(request.IdJson), null);
        }

        private bool TryReadMcpRequest(Stream stream, HttpRequestData httpRequest, string remoteAddress,
            out McpRequest request)
        {
            request = null;
            string body;
            try
            {
                body = new UTF8Encoding(false, true).GetString(httpRequest.Body);
            }
            catch (DecoderFallbackException)
            {
                WriteSimpleError(stream, 400, "Request body is not valid UTF-8.");
                return false;
            }

            string parseError;
            if (!JsonWire.TryParseRequest(body, remoteAddress, out request, out parseError))
            {
                WriteJson(stream, 400, McpResponse.Error(-32700, parseError).ToJson(null), null);
                return false;
            }
            return true;
        }

        private void RegisterLegacySession(McpLegacySseSession session)
        {
            lock (sessionSync)
                legacySessions.Add(session.Id, session);
        }

        private bool TryGetLegacySession(string id, out McpLegacySseSession session)
        {
            lock (sessionSync)
                return legacySessions.TryGetValue(id, out session);
        }

        private void UnregisterLegacySession(McpLegacySseSession session)
        {
            lock (sessionSync)
            {
                McpLegacySseSession current;
                if (legacySessions.TryGetValue(session.Id, out current) && object.ReferenceEquals(current, session))
                    legacySessions.Remove(session.Id);
            }
        }

        private void CloseAllLegacySessions()
        {
            McpLegacySseSession[] sessions;
            lock (sessionSync)
            {
                sessions = new McpLegacySseSession[legacySessions.Count];
                legacySessions.Values.CopyTo(sessions, 0);
                legacySessions.Clear();
            }
            for (int i = 0; i < sessions.Length; i++)
                sessions[i].Close();
        }

        private static bool HasJsonContentType(HttpRequestData request)
        {
            string value;
            if (!request.Headers.TryGetValue("Content-Type", out value))
                return false;
            int semicolon = value.IndexOf(';');
            if (semicolon >= 0)
                value = value.Substring(0, semicolon);
            return string.Equals(value.Trim(), "application/json", StringComparison.OrdinalIgnoreCase);
        }

        private static bool AcceptsJsonResponse(HttpRequestData request)
        {
            string accept;
            if (!request.Headers.TryGetValue("Accept", out accept) || string.IsNullOrEmpty(accept))
                return true;
            return HeaderContainsMediaType(accept, "application/json") || HeaderContainsMediaType(accept, "*/*");
        }

        private static bool Accepts(HttpRequestData request, string mediaType)
        {
            string accept;
            if (!request.Headers.TryGetValue("Accept", out accept) || string.IsNullOrEmpty(accept))
                return false;
            return HeaderContainsMediaType(accept, mediaType) || HeaderContainsMediaType(accept, "*/*");
        }

        private static bool HeaderContainsMediaType(string header, string mediaType)
        {
            string[] values = header.Split(new char[] { ',' });
            for (int i = 0; i < values.Length; i++)
            {
                string value = values[i].Trim();
                int semicolon = value.IndexOf(';');
                if (semicolon >= 0)
                    value = value.Substring(0, semicolon).Trim();
                if (string.Equals(value, mediaType, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private static bool IsAllowedOrigin(HttpRequestData request)
        {
            string origin;
            if (!request.Headers.TryGetValue("Origin", out origin) || string.IsNullOrEmpty(origin))
                return true;

            Uri uri;
            if (!Uri.TryCreate(origin, UriKind.Absolute, out uri))
                return false;
            if (string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase))
                return true;

            IPAddress address;
            return IPAddress.TryParse(uri.Host, out address) && IPAddress.IsLoopback(address);
        }
        private bool TryEnqueue(PendingRequest pending)
        {
            lock (queueSync)
            {
                if (!running || pendingRequests.Count >= maxPendingRequests)
                    return false;
                pendingRequests.Enqueue(pending);
                return true;
            }
        }

        private void CancelQueuedRequests(McpResponse response)
        {
            lock (queueSync)
            {
                while (pendingRequests.Count > 0)
                {
                    PendingRequest pending = pendingRequests.Dequeue();
                    if (pending.TryBeginExecution())
                        pending.Complete(response);
                }
            }
        }

        private bool IsAuthorized(HttpRequestData request)
        {
            if (token.Length == 0)
                return true;

            string supplied;
            request.Headers.TryGetValue("X-MCP-Token", out supplied);
            if (string.IsNullOrEmpty(supplied))
            {
                string authorization;
                request.Headers.TryGetValue("Authorization", out authorization);
                const string prefix = "Bearer ";
                if (!string.IsNullOrEmpty(authorization) && authorization.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    supplied = authorization.Substring(prefix.Length).Trim();
            }
            return FixedTimeEquals(token, supplied ?? string.Empty);
        }

        private static bool FixedTimeEquals(string expected, string supplied)
        {
            int maximum = Math.Max(expected.Length, supplied.Length);
            int difference = expected.Length ^ supplied.Length;
            for (int i = 0; i < maximum; i++)
            {
                char left = i < expected.Length ? expected[i] : (char)0;
                char right = i < supplied.Length ? supplied[i] : (char)0;
                difference |= left ^ right;
            }
            return difference == 0;
        }

        private static HttpRequestData ReadHttpRequest(Stream stream, int maximumBodyBytes)
        {
            MemoryStream received = new MemoryStream();
            byte[] one = new byte[1];
            int headerEnd = -1;
            while (received.Length < MaximumHeaderBytes)
            {
                int read = stream.Read(one, 0, 1);
                if (read == 0)
                    throw new HttpParseException(400, "Connection closed before HTTP headers completed.");
                received.WriteByte(one[0]);
                byte[] bytes = received.GetBuffer();
                int length = (int)received.Length;
                if (length >= 4 && bytes[length - 4] == 13 && bytes[length - 3] == 10 &&
                    bytes[length - 2] == 13 && bytes[length - 1] == 10)
                {
                    headerEnd = length;
                    break;
                }
            }
            if (headerEnd < 0)
                throw new RequestTooLargeException();

            string headerText = Encoding.ASCII.GetString(received.GetBuffer(), 0, headerEnd - 4);
            string[] lines = headerText.Split(new string[] { "\r\n" }, StringSplitOptions.None);
            if (lines.Length == 0)
                throw new HttpParseException(400, "Missing HTTP request line.");
            string[] requestLine = lines[0].Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (requestLine.Length != 3 || !requestLine[2].StartsWith("HTTP/", StringComparison.OrdinalIgnoreCase))
                throw new HttpParseException(400, "Invalid HTTP request line.");

            Dictionary<string, string> headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 1; i < lines.Length; i++)
            {
                int colon = lines[i].IndexOf(':');
                if (colon <= 0)
                    throw new HttpParseException(400, "Invalid HTTP header.");
                string name = lines[i].Substring(0, colon).Trim();
                string value = lines[i].Substring(colon + 1).Trim();
                if (name.Length == 0)
                    throw new HttpParseException(400, "Invalid HTTP header name.");
                headers[name] = value;
            }

            string transferEncoding;
            if (headers.TryGetValue("Transfer-Encoding", out transferEncoding) &&
                !string.IsNullOrEmpty(transferEncoding) &&
                !string.Equals(transferEncoding, "identity", StringComparison.OrdinalIgnoreCase))
                throw new HttpParseException(501, "Chunked request bodies are not supported.");

            int contentLength = 0;
            string lengthText;
            if (headers.TryGetValue("Content-Length", out lengthText))
            {
                if (!int.TryParse(lengthText, NumberStyles.None, CultureInfo.InvariantCulture, out contentLength) || contentLength < 0)
                    throw new HttpParseException(400, "Invalid Content-Length.");
            }
            else if (string.Equals(requestLine[0], "POST", StringComparison.OrdinalIgnoreCase))
                throw new HttpParseException(411, "Content-Length is required.");

            if (contentLength > maximumBodyBytes)
                throw new RequestTooLargeException();

            byte[] body = new byte[contentLength];
            int offset = 0;
            while (offset < contentLength)
            {
                int read = stream.Read(body, offset, contentLength - offset);
                if (read == 0)
                    throw new HttpParseException(400, "Connection closed before request body completed.");
                offset += read;
            }

            string target = requestLine[1];
            string path = target;
            string queryText = string.Empty;
            int query = path.IndexOf('?');
            if (query >= 0)
            {
                queryText = path.Substring(query + 1);
                path = path.Substring(0, query);
            }
            while (path.Length > 1 && path[path.Length - 1] == '/')
                path = path.Substring(0, path.Length - 1);

            return new HttpRequestData(requestLine[0], target, path, ParseQueryString(queryText), headers, body);
        }

        private static Dictionary<string, string> ParseQueryString(string query)
        {
            Dictionary<string, string> result = new Dictionary<string, string>(StringComparer.Ordinal);
            if (string.IsNullOrEmpty(query))
                return result;

            string[] pairs = query.Split(new char[] { '&' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < pairs.Length; i++)
            {
                int equals = pairs[i].IndexOf('=');
                string name = equals < 0 ? pairs[i] : pairs[i].Substring(0, equals);
                string value = equals < 0 ? string.Empty : pairs[i].Substring(equals + 1);
                try
                {
                    name = Uri.UnescapeDataString(name.Replace('+', ' '));
                    value = Uri.UnescapeDataString(value.Replace('+', ' '));
                }
                catch (UriFormatException)
                {
                    continue;
                }
                if (name.Length > 0)
                    result[name] = value;
            }
            return result;
        }
        private static void WriteSimpleError(Stream stream, int statusCode, string message)
        {
            WriteSimpleError(stream, statusCode, message, null);
        }

        private static void WriteSimpleError(Stream stream, int statusCode, string message, string extraHeaders)
        {
            WriteJson(stream, statusCode, "{\"error\":" + JsonWire.Quote(message) + "}", extraHeaders);
        }

        private static void WriteJson(Stream stream, int statusCode, string json, string extraHeaders)
        {
            WriteResponse(stream, statusCode, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(json), extraHeaders);
        }

        private static void WriteResponse(Stream stream, int statusCode, string contentType, byte[] body, string extraHeaders)
        {
            if (body == null)
                body = new byte[0];
            StringBuilder header = new StringBuilder();
            header.Append("HTTP/1.1 ").Append(statusCode.ToString(CultureInfo.InvariantCulture)).Append(' ')
                .Append(GetReasonPhrase(statusCode)).Append("\r\n");
            header.Append("Connection: close\r\nCache-Control: no-store\r\n");
            if (!string.IsNullOrEmpty(contentType))
                header.Append("Content-Type: ").Append(contentType).Append("\r\n");
            if (!string.IsNullOrEmpty(extraHeaders))
                header.Append(extraHeaders);
            header.Append("Content-Length: ").Append(body.Length.ToString(CultureInfo.InvariantCulture)).Append("\r\n\r\n");

            byte[] headerBytes = Encoding.ASCII.GetBytes(header.ToString());
            stream.Write(headerBytes, 0, headerBytes.Length);
            if (body.Length > 0)
                stream.Write(body, 0, body.Length);
            stream.Flush();
        }

        private static string GetReasonPhrase(int statusCode)
        {
            switch (statusCode)
            {
                case 200: return "OK";
                case 202: return "Accepted";
                case 204: return "No Content";
                case 400: return "Bad Request";
                case 401: return "Unauthorized";
                case 403: return "Forbidden";
                case 404: return "Not Found";
                case 405: return "Method Not Allowed";
                case 406: return "Not Acceptable";
                case 411: return "Length Required";
                case 413: return "Payload Too Large";
                case 415: return "Unsupported Media Type";
                case 500: return "Internal Server Error";
                case 501: return "Not Implemented";
                case 503: return "Service Unavailable";
                case 504: return "Gateway Timeout";
                default: return "Error";
            }
        }

        private void CaptureOptions()
        {
            rpcPath = Options.RpcPath;
            healthPath = Options.HealthPath;
            token = Options.Token;
            requireTokenForHealth = Options.RequireTokenForHealth;
            requestTimeoutMilliseconds = Options.RequestTimeoutMilliseconds;
            maxRequestBodyBytes = Options.MaxRequestBodyBytes;
            maxPendingRequests = Options.MaxPendingRequests;
            maxRequestsPerPump = Options.MaxRequestsPerPump;
            configuredTransportMode = Options.TransportMode;
            activeTransportMode = configuredTransportMode;
        }

        private void ThrowIfDisposed()
        {
            if (disposed)
                throw new ObjectDisposedException("McpHttpBridge");
        }

        private sealed class HttpRequestData
        {
            internal HttpRequestData(string method, string target, string path,
                Dictionary<string, string> query, Dictionary<string, string> headers, byte[] body)
            {
                Method = method;
                Target = target;
                Path = path;
                Query = query;
                Headers = headers;
                Body = body;
            }
            internal string Method { get; private set; }
            internal string Target { get; private set; }
            internal string Path { get; private set; }
            internal Dictionary<string, string> Query { get; private set; }
            internal Dictionary<string, string> Headers { get; private set; }
            internal byte[] Body { get; private set; }
        }

        private sealed class PendingRequest
        {
            private readonly ManualResetEvent completed = new ManualResetEvent(false);
            private readonly Action<McpResponse> completionCallback;
            // 0 queued, 1 executing, 2 completed, 3 canceled while queued.
            private int state;

            internal PendingRequest(McpRequest request, Action<McpResponse> completionCallback)
            {
                Request = request;
                this.completionCallback = completionCallback;
            }
            internal McpRequest Request { get; private set; }
            internal McpResponse Response { get; private set; }
            internal bool TryBeginExecution() { return Interlocked.CompareExchange(ref state, 1, 0) == 0; }
            internal bool TryCancelQueued() { return Interlocked.CompareExchange(ref state, 3, 0) == 0; }
            internal void Complete(McpResponse response)
            {
                Response = response;
                Interlocked.Exchange(ref state, 2);
                completed.Set();
                if (completionCallback != null)
                {
                    try { completionCallback(response); }
                    catch { }
                }
            }
            internal bool Wait(int milliseconds) { return completed.WaitOne(milliseconds, false); }
        }

        private sealed class RequestTooLargeException : Exception { }

        private sealed class HttpParseException : Exception
        {
            internal HttpParseException(int statusCode, string message) : base(message) { StatusCode = statusCode; }
            internal int StatusCode { get; private set; }
        }
    }
}
