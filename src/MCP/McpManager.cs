using System.Security.Cryptography;
using UnityExplorer.Config;
using UnityExplorer.MCP.Runtime;
using UnityExplorer.MCP.Transport;

namespace UnityExplorer.MCP
{
    /// <summary>
    /// Coordinates MCP configuration, transport lifetime and Unity main-thread dispatch.
    /// Runtime/transport implementations plug in through SetTransportFactory or AttachTransport.
    /// </summary>
    public static class McpManager
    {
        public enum LifecycleState
        {
            Uninitialized,
            Stopped,
            Starting,
            Running,
            Stopping,
            Faulted,
            Shutdown
        }

        private static readonly object SyncRoot = new();
        private static readonly McpRequestRouter RequestRouter = new();
        private static readonly Queue<string> RecentRequestEntries = new();
        private const int MaximumRecentRequestEntries = 100;

        private static Func<McpHttpBridgeOptions, IMcpRequestDispatcher, IMcpTransport> transportFactory =
            (options, requestDispatcher) => new McpHttpBridge(options, requestDispatcher);
        private static IMcpRequestDispatcher dispatcher = RequestRouter;
        private static IMcpTransport transport;
        private static McpGameCapabilityExecutor gameExecutor;
        private static McpNativeProtocolHandler nativeProtocolHandler;
        private static LifecycleState state = LifecycleState.Uninitialized;
        private static Exception lastError;
        private static bool initialized;
        private static bool shuttingDown;
        private static bool configCallbacksRegistered;
        private static bool restartRequired;

        /// <summary>Raised after lifecycle state, error, or pending-restart status changes.</summary>
        public static event Action StateChanged;

        /// <summary>Raised when an MCP configuration value changes.</summary>
        public static event Action ConfigurationChanged;

        /// <summary>
        /// Shared router for runtime modules. Register handlers here before or after Initialize.
        /// Handlers are executed only from PumpMainThread.
        /// </summary>
        public static McpRequestRouter Router => RequestRouter;

        public static McpGameCapabilityExecutor GameExecutor
        {
            get { lock (SyncRoot) return gameExecutor; }
        }

        public static bool IsInitialized
        {
            get { lock (SyncRoot) return initialized; }
        }

        public static bool IsRunning
        {
            get
            {
                lock (SyncRoot)
                    return transport != null && transport.IsRunning;
            }
        }

        public static LifecycleState State
        {
            get { lock (SyncRoot) return state; }
        }

        public static Exception LastError
        {
            get { lock (SyncRoot) return lastError; }
        }

        public static string LastErrorMessage
        {
            get
            {
                lock (SyncRoot)
                    return lastError?.Message ?? string.Empty;
            }
        }

        public static int PendingRequestCount
        {
            get
            {
                lock (SyncRoot)
                    return transport?.PendingRequestCount ?? 0;
            }
        }

        public static bool RestartRequired
        {
            get { lock (SyncRoot) return restartRequired; }
        }

        public static string RpcEndpoint
            => $"http://127.0.0.1:{ConfigManager.MCP_Port.Value}{NormalizeDisplayPath(ConfigManager.MCP_Rpc_Path.Value)}";

        public static string HealthEndpoint
            => $"http://127.0.0.1:{ConfigManager.MCP_Port.Value}{NormalizeDisplayPath(ConfigManager.MCP_Health_Path.Value)}";

        /// <summary>The configured transport mode in UI/config-friendly form.</summary>
        public static string TransportMode
        {
            get => ToConfiguredTransportMode(ConfigManager.MCP_Transport_Mode.Value);
            set => ConfigManager.MCP_Transport_Mode.Value = ParseTransportMode(value);
        }

        /// <summary>
        /// The mode used by the currently-created transport. This can differ from
        /// <see cref="TransportMode"/> while a configuration change is awaiting restart.
        /// </summary>
        public static string CurrentTransportMode
        {
            get
            {
                lock (SyncRoot)
                {
                    if (transport?.Options != null)
                        return ToConfiguredTransportMode(transport.Options.TransportMode);
                }

                return TransportMode;
            }
        }

        /// <summary>The MCP endpoint clients should use for the selected transport.</summary>
        public static string TransportEndpoint => RpcEndpoint;

        /// <summary>Compact mode/endpoint status intended for the MCP settings UI.</summary>
        public static string TransportStatus
            => $"{CurrentTransportMode}: {TransportEndpoint}";

        // Stable settings/control API consumed by the MCP UI and external integrations.
        public static bool Enabled
        {
            get => ConfigManager.MCP_Enabled.Value;
            set => ConfigManager.MCP_Enabled.Value = value;
        }

        public static string BindAddress
        {
            get => ConfigManager.MCP_Bind_Address.Value;
            set
            {
                string address = string.IsNullOrEmpty(value) ? "127.0.0.1" : value.Trim();
                if (address.Equals("localhost", StringComparison.OrdinalIgnoreCase))
                    address = "127.0.0.1";
                if (address != "127.0.0.1")
                    throw new ArgumentException("The built-in MCP transport currently supports loopback (127.0.0.1) only.", nameof(value));
                ConfigManager.MCP_Bind_Address.Value = address;
            }
        }

        public static int Port
        {
            get => ConfigManager.MCP_Port.Value;
            set
            {
                if (value < 1 || value > 65535)
                    throw new ArgumentOutOfRangeException(nameof(value), "MCP port must be between 1 and 65535.");
                ConfigManager.MCP_Port.Value = value;
            }
        }

        public static string AuthenticationToken
        {
            get => ConfigManager.MCP_Auth_Token.Value;
            set => ConfigManager.MCP_Auth_Token.Value = value ?? string.Empty;
        }

        public static bool ReadOnly
        {
            get => ConfigManager.MCP_Read_Only.Value;
            set => ConfigManager.MCP_Read_Only.Value = value;
        }

        public static bool AllowDangerousOperations
        {
            get => ConfigManager.MCP_Allow_Dangerous_Operations.Value;
            set => ConfigManager.MCP_Allow_Dangerous_Operations.Value = value;
        }

        public static bool RequestLoggingEnabled
        {
            get => ConfigManager.MCP_Request_Logging.Value;
            set => ConfigManager.MCP_Request_Logging.Value = value;
        }

        public static string StatusText
        {
            get
            {
                string result = State.ToString();
                if (RestartRequired)
                    result += " (restart required)";
                if (State == LifecycleState.Faulted && !string.IsNullOrEmpty(LastErrorMessage))
                    result += ": " + LastErrorMessage;
                return result;
            }
        }

        public static string RecentRequests
        {
            get
            {
                lock (SyncRoot)
                    return string.Join(Environment.NewLine, RecentRequestEntries.ToArray());
            }
        }

        public static string GenerateAuthenticationToken()
        {
            byte[] bytes = new byte[32];
            RandomNumberGenerator generator = RandomNumberGenerator.Create();
            generator.GetBytes(bytes);

            string token = Convert.ToBase64String(bytes)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
            AuthenticationToken = token;
            return token;
        }

        public static void SaveSettings()
            => ConfigManager.Handler.SaveConfig();

        /// <summary>Allows runtime handlers to add useful entries to the UI request log.</summary>
        public static void RecordRequest(string entry)
        {
            if (!ConfigManager.MCP_Request_Logging.Value || string.IsNullOrEmpty(entry))
                return;

            lock (SyncRoot)
            {
                RecentRequestEntries.Enqueue(entry);
                while (RecentRequestEntries.Count > MaximumRecentRequestEntries)
                    RecentRequestEntries.Dequeue();
            }
        }
        /// <summary>
        /// Installs the transport constructor. This is the preferred integration API for the
        /// Transport submodule because it lets the manager rebuild the transport after settings change.
        /// </summary>
        public static void SetTransportFactory(
            Func<McpHttpBridgeOptions, IMcpRequestDispatcher, IMcpTransport> factory)
        {
            if (factory == null)
                throw new ArgumentNullException(nameof(factory));

            lock (SyncRoot)
            {
                ThrowIfShutdown();
                transportFactory = factory;
            }

            if (IsInitialized)
                RecreateTransport(startIfEnabled: ConfigManager.MCP_Enabled.Value);
        }

        /// <summary>
        /// Overrides the request dispatcher passed to the transport factory. By default the shared
        /// Router is used. Changing it recreates an initialized transport.
        /// </summary>
        public static void SetDispatcher(IMcpRequestDispatcher requestDispatcher)
        {
            if (requestDispatcher == null)
                throw new ArgumentNullException(nameof(requestDispatcher));

            lock (SyncRoot)
            {
                ThrowIfShutdown();
                dispatcher = requestDispatcher;
            }

            if (IsInitialized && transportFactory != null)
                RecreateTransport(startIfEnabled: ConfigManager.MCP_Enabled.Value);
        }

        /// <summary>
        /// Attaches an already-created transport. Prefer SetTransportFactory when settings should
        /// be editable at runtime. Ownership transfers to McpManager and the transport is disposed
        /// during replacement or Shutdown.
        /// </summary>
        public static void AttachTransport(IMcpTransport newTransport, bool startIfEnabled = true)
        {
            if (newTransport == null)
                throw new ArgumentNullException(nameof(newTransport));

            IMcpTransport oldTransport;
            lock (SyncRoot)
            {
                ThrowIfShutdown();
                oldTransport = transport;
                transport = newTransport;
                lastError = null;
                restartRequired = false;
                state = newTransport.IsRunning ? LifecycleState.Running : LifecycleState.Stopped;
            }

            if (!ReferenceEquals(oldTransport, newTransport))
                StopAndDispose(oldTransport);

            NotifyStateChanged();

            if (initialized && startIfEnabled && ConfigManager.MCP_Enabled.Value && !newTransport.IsRunning)
                Start();
        }

        /// <summary>Builds a fresh options object from the persisted MCP configuration.</summary>
        public static McpHttpBridgeOptions CreateOptions()
        {
            return new McpHttpBridgeOptions
            {
                Port = ConfigManager.MCP_Port.Value,
                TransportMode = ToHttpTransportMode(ConfigManager.MCP_Transport_Mode.Value),
                RpcPath = ConfigManager.MCP_Rpc_Path.Value,
                HealthPath = ConfigManager.MCP_Health_Path.Value,
                Token = ConfigManager.MCP_Auth_Token.Value,
                RequireTokenForHealth = ConfigManager.MCP_Require_Token_For_Health.Value,
                RequestTimeoutMilliseconds = ConfigManager.MCP_Request_Timeout_Milliseconds.Value,
                MaxRequestBodyBytes = ConfigManager.MCP_Max_Request_Body_Bytes.Value,
                MaxPendingRequests = ConfigManager.MCP_Max_Pending_Requests.Value,
                MaxRequestsPerPump = ConfigManager.MCP_Max_Requests_Per_Frame.Value
            };
        }

        /// <summary>Called once from ExplorerCore.LateInit.</summary>
        public static void Initialize()
        {
            lock (SyncRoot)
            {
                if (initialized || shuttingDown)
                    return;

                initialized = true;
                state = LifecycleState.Stopped;
                RegisterConfigCallbacks();
            }

            // Dangerous authorization is intentionally session-scoped and never survives a restart.
            if (ConfigManager.MCP_Allow_Dangerous_Operations.Value)
                ConfigManager.MCP_Allow_Dangerous_Operations.Value = false;

            EnsureRuntimeRegistered();

            if (transportFactory != null)
                RecreateTransport(startIfEnabled: ConfigManager.MCP_Enabled.Value);
            else if (ConfigManager.MCP_Enabled.Value)
                SetFault(new InvalidOperationException(
                    "MCP is enabled, but no transport factory or transport has been registered."));
            else
                NotifyStateChanged();
        }

        public static bool Start()
        {
            IMcpTransport current;
            lock (SyncRoot)
            {
                if (!initialized || shuttingDown)
                    return false;
                if (transport != null && transport.IsRunning)
                    return true;

                // Never expose the control bridge without authentication, even on loopback.
                // Generate a token lazily so existing installations start safely.
                if (string.IsNullOrEmpty(ConfigManager.MCP_Auth_Token.Value))
                    ConfigManager.MCP_Auth_Token.Value = GenerateAuthenticationToken();

                current = transport;

                // A stopped transport may have been created before the user edited its settings
                // (or before a token was generated). Synchronize every startup option before
                // Start() captures its immutable listener snapshot.
                SynchronizeStoppedTransportOptions(current);

                state = LifecycleState.Starting;
                lastError = null;
            }

            NotifyStateChanged();

            if (current == null)
            {
                if (transportFactory != null)
                {
                    RecreateTransport(startIfEnabled: false);
                    lock (SyncRoot)
                        current = transport;
                }

                if (current == null)
                {
                    SetFault(new InvalidOperationException(
                        "No MCP transport is registered. Install a transport factory before starting MCP."));
                    return false;
                }
            }

            try
            {
                current.Start();
                lock (SyncRoot)
                {
                    state = current.IsRunning ? LifecycleState.Running : LifecycleState.Stopped;
                    restartRequired = false;
                }
                NotifyStateChanged();
                return current.IsRunning;
            }
            catch (Exception ex)
            {
                SetFault(ex);
                ExplorerCore.LogError($"Failed to start MCP: {ex}");
                return false;
            }
        }

        public static void Stop()
        {
            IMcpTransport current;
            lock (SyncRoot)
            {
                if (shuttingDown)
                    return;
                current = transport;
                state = LifecycleState.Stopping;
            }

            NotifyStateChanged();

            try
            {
                current?.Stop();
                lock (SyncRoot)
                    state = LifecycleState.Stopped;
            }
            catch (Exception ex)
            {
                SetFault(ex);
                ExplorerCore.LogError($"Failed to stop MCP: {ex}");
                return;
            }

            NotifyStateChanged();
        }

        /// <summary>Applies all current configuration by rebuilding and optionally starting MCP.</summary>
        public static bool Restart()
        {
            if (!IsInitialized)
                return false;

            RecreateTransport(startIfEnabled: ConfigManager.MCP_Enabled.Value);
            return !ConfigManager.MCP_Enabled.Value || IsRunning;
        }

        /// <summary>Runs queued MCP work on Unity's main thread. Called from Update.</summary>
        public static int PumpMainThread()
        {
            IMcpTransport current;
            lock (SyncRoot)
            {
                if (!initialized || shuttingDown || transport == null || !transport.IsRunning)
                    return 0;
                current = transport;
            }

            try
            {
                return current.PumpMainThread(ConfigManager.MCP_Max_Requests_Per_Frame.Value);
            }
            catch (Exception ex)
            {
                SetFault(ex);
                ExplorerCore.LogError($"MCP main-thread pump failed: {ex}");
                return 0;
            }
        }

        /// <summary>Stops and disposes all MCP resources. Safe to call more than once.</summary>
        public static void Shutdown()
        {
            IMcpTransport current;
            lock (SyncRoot)
            {
                if (shuttingDown)
                    return;

                shuttingDown = true;
                current = transport;
                transport = null;
                state = LifecycleState.Shutdown;
            }

            StopAndDispose(current);

            lock (SyncRoot)
            {
                gameExecutor?.Unregister(RequestRouter);
                gameExecutor = null;
            }

            NotifyStateChanged();
        }

        private static void SynchronizeStoppedTransportOptions(IMcpTransport value)
        {
            if (value == null || value.IsRunning || value.Options == null)
                return;

            McpHttpBridgeOptions configured = CreateOptions();
            McpHttpBridgeOptions target = value.Options;
            target.Port = configured.Port;
            target.TransportMode = configured.TransportMode;
            target.RpcPath = configured.RpcPath;
            target.HealthPath = configured.HealthPath;
            target.Token = configured.Token;
            target.RequireTokenForHealth = configured.RequireTokenForHealth;
            target.RequestTimeoutMilliseconds = configured.RequestTimeoutMilliseconds;
            target.MaxRequestBodyBytes = configured.MaxRequestBodyBytes;
            target.MaxPendingRequests = configured.MaxPendingRequests;
            target.MaxRequestsPerPump = configured.MaxRequestsPerPump;
        }
        private static void RecreateTransport(bool startIfEnabled)
        {
            Func<McpHttpBridgeOptions, IMcpRequestDispatcher, IMcpTransport> factory;
            IMcpRequestDispatcher currentDispatcher;
            IMcpTransport oldTransport;

            lock (SyncRoot)
            {
                if (shuttingDown)
                    return;
                factory = transportFactory;
                currentDispatcher = new LoggingDispatcher(dispatcher);
                oldTransport = transport;
                transport = null;
                state = LifecycleState.Stopped;
            }

            StopAndDispose(oldTransport);

            if (factory == null)
            {
                NotifyStateChanged();
                return;
            }

            try
            {
                IMcpTransport newTransport = factory(CreateOptions(), currentDispatcher);
                if (newTransport == null)
                    throw new InvalidOperationException("The MCP transport factory returned null.");

                lock (SyncRoot)
                {
                    transport = newTransport;
                    lastError = null;
                    restartRequired = false;
                    state = newTransport.IsRunning ? LifecycleState.Running : LifecycleState.Stopped;
                }
                NotifyStateChanged();

                if (initialized && startIfEnabled && !newTransport.IsRunning)
                    Start();
            }
            catch (Exception ex)
            {
                SetFault(ex);
                ExplorerCore.LogError($"Failed to create MCP transport: {ex}");
            }
        }

        private static void RegisterConfigCallbacks()
        {
            if (configCallbacksRegistered)
                return;

            configCallbacksRegistered = true;
            ConfigManager.MCP_Enabled.OnValueChanged += OnEnabledChanged;
            ConfigManager.MCP_Bind_Address.OnValueChanged += _ => OnRestartSettingChanged();
            ConfigManager.MCP_Port.OnValueChanged += _ => OnRestartSettingChanged();
            ConfigManager.MCP_Transport_Mode.OnValueChanged += _ => OnRestartSettingChanged();
            ConfigManager.MCP_Rpc_Path.OnValueChanged += _ => OnRestartSettingChanged();
            ConfigManager.MCP_Health_Path.OnValueChanged += _ => OnRestartSettingChanged();
            ConfigManager.MCP_Auth_Token.OnValueChanged += _ => OnRestartSettingChanged();
            ConfigManager.MCP_Require_Token_For_Health.OnValueChanged += _ => OnRestartSettingChanged();
            ConfigManager.MCP_Read_Only.OnValueChanged += _ => OnRuntimeSecurityChanged();
            ConfigManager.MCP_Allow_Dangerous_Operations.OnValueChanged += _ => OnRuntimeSecurityChanged();
            ConfigManager.MCP_Request_Logging.OnValueChanged += _ => OnConfigurationChanged(requiresRestart: false);
            ConfigManager.MCP_Request_Timeout_Milliseconds.OnValueChanged += _ => OnRestartSettingChanged();
            ConfigManager.MCP_Max_Request_Body_Bytes.OnValueChanged += _ => OnRestartSettingChanged();
            ConfigManager.MCP_Max_Pending_Requests.OnValueChanged += _ => OnRestartSettingChanged();
            ConfigManager.MCP_Max_Requests_Per_Frame.OnValueChanged += _ => OnConfigurationChanged(requiresRestart: false);
        }

        private static void EnsureRuntimeRegistered()
        {
            lock (SyncRoot)
            {
                if (gameExecutor == null)
                {
                    gameExecutor = new McpGameCapabilityExecutor();
                    gameExecutor.Register(RequestRouter);
                }
                if (nativeProtocolHandler == null)
                {
                    nativeProtocolHandler = new McpNativeProtocolHandler(gameExecutor);
                    nativeProtocolHandler.Register(RequestRouter);
                }
            }

            ApplyRuntimeSecurityOptions();
        }

        private static void ApplyRuntimeSecurityOptions()
        {
            McpGameCapabilityExecutor executor;
            lock (SyncRoot)
                executor = gameExecutor;
            if (executor == null)
                return;

            bool allowDangerous = !ConfigManager.MCP_Read_Only.Value &&
                ConfigManager.MCP_Allow_Dangerous_Operations.Value;
            executor.Options.AllowMethodInvocation = allowDangerous;
            executor.Options.AllowObjectCreation = allowDangerous;
            executor.Options.AllowObjectDestruction = allowDangerous;
        }

        private static void OnRuntimeSecurityChanged()
        {
            ApplyRuntimeSecurityOptions();
            OnConfigurationChanged(requiresRestart: false);
        }
        private static void OnEnabledChanged(bool enabled)
        {
            OnConfigurationChanged(requiresRestart: false);
            if (!initialized || shuttingDown)
                return;

            if (enabled)
                Start();
            else
                Stop();
        }

        private static void OnRestartSettingChanged()
            => OnConfigurationChanged(requiresRestart: true);

        private static void OnConfigurationChanged(bool requiresRestart)
        {
            lock (SyncRoot)
            {
                if (requiresRestart && transport != null && transport.IsRunning)
                    restartRequired = true;
            }

            try
            {
                ConfigurationChanged?.Invoke();
            }
            catch (Exception ex)
            {
                ExplorerCore.LogWarning($"MCP ConfigurationChanged subscriber failed: {ex}");
            }

            NotifyStateChanged();
        }

        private static void SetFault(Exception error)
        {
            lock (SyncRoot)
            {
                lastError = error;
                state = LifecycleState.Faulted;
            }
            NotifyStateChanged();
        }

        private static void StopAndDispose(IMcpTransport value)
        {
            if (value == null)
                return;

            try
            {
                if (value.IsRunning)
                    value.Stop();
            }
            catch (Exception ex)
            {
                ExplorerCore.LogWarning($"Exception while stopping MCP transport: {ex}");
            }

            try
            {
                value.Dispose();
            }
            catch (Exception ex)
            {
                ExplorerCore.LogWarning($"Exception while disposing MCP transport: {ex}");
            }
        }

        private static void NotifyStateChanged()
        {
            try
            {
                StateChanged?.Invoke();
            }
            catch (Exception ex)
            {
                ExplorerCore.LogWarning($"MCP StateChanged subscriber failed: {ex}");
            }
        }

        private sealed class LoggingDispatcher : IMcpRequestDispatcher
        {
            private readonly IMcpRequestDispatcher inner;

            public LoggingDispatcher(IMcpRequestDispatcher inner)
            {
                this.inner = inner;
            }

            public McpResponse Dispatch(McpRequest request)
            {
                RecordRequest($"{DateTime.UtcNow:O} {request.RemoteAddress} {request.Method}");
                return inner.Dispatch(request);
            }
        }
        private static void ThrowIfShutdown()
        {
            if (shuttingDown)
                throw new InvalidOperationException("MCP manager has already been shut down.");
        }

        private static McpTransportMode ParseTransportMode(string value)
        {
            string normalized = (value ?? string.Empty)
                .Trim()
                .Replace(" ", string.Empty)
                .Replace("-", string.Empty)
                .Replace("_", string.Empty);

            if (normalized.Equals("SSE", StringComparison.OrdinalIgnoreCase) ||
                normalized.Equals("LegacySSE", StringComparison.OrdinalIgnoreCase))
                return McpTransportMode.SSE;
            if (normalized.Equals("StreamableHTTP", StringComparison.OrdinalIgnoreCase))
                return McpTransportMode.StreamableHTTP;

            throw new ArgumentException(
                "MCP transport mode must be 'SSE' or 'Streamable HTTP'.",
                nameof(value));
        }

        private static McpHttpTransportMode ToHttpTransportMode(McpTransportMode value)
        {
            switch (value)
            {
                case McpTransportMode.SSE:
                    return McpHttpTransportMode.LegacySse;
                case McpTransportMode.StreamableHTTP:
                    return McpHttpTransportMode.StreamableHttp;
                default:
                    throw new ArgumentOutOfRangeException(nameof(value), value, "Unsupported MCP transport mode.");
            }
        }

        private static string ToConfiguredTransportMode(McpTransportMode value)
        {
            switch (value)
            {
                case McpTransportMode.SSE:
                    return "SSE";
                case McpTransportMode.StreamableHTTP:
                    return "Streamable HTTP";
                default:
                    return value.ToString();
            }
        }

        private static string ToConfiguredTransportMode(McpHttpTransportMode value)
        {
            switch (value)
            {
                case McpHttpTransportMode.LegacySse:
                    return "SSE";
                case McpHttpTransportMode.StreamableHttp:
                    return "Streamable HTTP";
                default:
                    return value.ToString();
            }
        }
        private static string NormalizeDisplayPath(string value)
        {
            if (string.IsNullOrEmpty(value))
                return "/";
            return value[0] == '/' ? value : "/" + value;
        }
    }
}
