using System.Collections;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using UnityExplorer.MCP;
using UniverseLib.UI;
using UniverseLib.UI.Models;

namespace UnityExplorer.UI.Panels
{
    /// <summary>
    /// Configures and controls the in-game MCP server. Talks to <see cref="McpManager"/>
    /// directly; the MCP implementation is compiled into this same assembly.
    /// </summary>
    public class McpPanel : UEPanel
    {
        public override string Name => "MCP";

        public override UIManager.Panels PanelType => UIManager.Panels.Mcp;

        public override int MinWidth => 620;
        public override int MinHeight => 360;
        public override Vector2 DefaultAnchorMin => new(0.5f, 0.5f);
        public override Vector2 DefaultAnchorMax => new(0.5f, 0.5f);
        public override bool ShowByDefault => false;
        public override bool ShouldSaveActiveState => true;

        private Toggle enabledToggle;
        private Toggle readOnlyToggle;
        private Toggle dangerousOperationsToggle;
        private Toggle requestLoggingToggle;
        private Dropdown transportModeDropdown;
        private InputFieldRef bindAddressInput;
        private InputFieldRef portInput;
        private InputFieldRef tokenInput;
        private InputFieldRef agentConfigInput;
        private InputFieldRef requestLogInput;
        private Text statusText;
        private Text endpointText;
        private ButtonRef startButton;
        private ButtonRef stopButton;
        private GameObject scrollContent;

        // Unity overrides == for destroyed objects, so use an explicit comparison
        // instead of ?? (which would return a destroyed object reference).
        private GameObject LayoutRoot => scrollContent != null ? scrollContent : ContentRoot;

        private bool updatingControls;
        private float nextRefreshTime;
        private const float RefreshInterval = 0.5f;

        public McpPanel(UIBase owner) : base(owner) { }

        public override void SetDefaultSizeAndPosition()
        {
            base.SetDefaultSizeAndPosition();
            Rect.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, 700f);
            Rect.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, 820f);
        }

        public override void Update()
        {
            base.Update();

            if (!Enabled || Time.unscaledTime < nextRefreshTime)
                return;

            nextRefreshTime = Time.unscaledTime + RefreshInterval;
            RefreshRuntimeState();
        }

        protected override void ConstructPanelContent()
        {
            UIFactory.SetLayoutGroup<VerticalLayoutGroup>(ContentRoot, false, false, true, true, 0, 0, 0, 0, 0);
            UIFactory.SetLayoutElement(ContentRoot, flexibleWidth: 9999, flexibleHeight: 9999);

            GameObject scrollView = UIFactory.CreateScrollView(
                ContentRoot,
                "McpSettingsScrollView",
                out scrollContent,
                out _,
                new Color(0.065f, 0.065f, 0.065f));
            UIFactory.SetLayoutElement(scrollView, minHeight: 200, flexibleWidth: 9999, flexibleHeight: 9999);
            UIFactory.SetLayoutGroup<VerticalLayoutGroup>(scrollContent, false, false, true, true, 3, 2, 2, 2, 2);

            Text warning = UIFactory.CreateLabel(
                LayoutRoot,
                "McpSecurityWarning",
                "MCP gives an external agent access to the running game. Keep 127.0.0.1 unless remote access is explicitly required, and always use a strong token.",
                TextAnchor.MiddleLeft,
                new Color(1f, 0.78f, 0.3f),
                true,
                12);
            UIFactory.SetLayoutElement(warning.gameObject, minHeight: 36, flexibleWidth: 9999);

            CreateToggleRow("Enabled", "Enable MCP server", out enabledToggle, OnEnabledChanged);

            bindAddressInput = CreateInputRow(
                "BindAddress",
                "Bind address",
                "127.0.0.1",
                "127.0.0.1 (local machine only; recommended)",
                OnBindAddressEndEdit);

            portInput = CreateInputRow("Port", "Port", "17891", "TCP port (1-65535)", OnPortEndEdit, 120f);
            portInput.Component.contentType = InputField.ContentType.IntegerNumber;

            CreateTransportModeRow();
            CreateTokenRow();

            CreateToggleRow("ReadOnly", "Read-only mode (blocks all mutation tools)", out readOnlyToggle, OnReadOnlyChanged);
            CreateToggleRow(
                "DangerousOperations",
                "Allow dangerous operations (destroy objects, invoke arbitrary methods, load scenes, etc.)",
                out dangerousOperationsToggle,
                OnDangerousOperationsChanged,
                new Color(1f, 0.55f, 0.4f));
            CreateToggleRow("RequestLogging", "Log MCP requests", out requestLoggingToggle, OnRequestLoggingChanged);

            CreateStatusSection();
            CreateActionRow();
            CreateAgentToolsSection();
            CreateRequestLogSection();

            LoadSettingsFromManager();
            RefreshRuntimeState();
        }

        private void CreateToggleRow(
            string objectName,
            string label,
            out Toggle toggle,
            Action<bool> onChanged,
            Color? labelColor = null)
        {
            GameObject row = UIFactory.CreateToggle(LayoutRoot, objectName, out toggle, out Text text);
            UIFactory.SetLayoutElement(row, minHeight: 25, flexibleWidth: 9999);
            text.text = label;
            if (labelColor.HasValue)
                text.color = labelColor.Value;
            toggle.onValueChanged.AddListener(value =>
            {
                if (!updatingControls)
                    onChanged(value);
            });
        }

        private InputFieldRef CreateInputRow(
            string objectName,
            string label,
            string initialValue,
            string hint,
            Action<string> onEndEdit,
            float inputWidth = 300f)
        {
            GameObject row = UIFactory.CreateHorizontalGroup(
                LayoutRoot,
                objectName + "Row",
                false,
                false,
                true,
                true,
                3,
                default,
                new Color(1f, 1f, 1f, 0f),
                TextAnchor.MiddleLeft);
            UIFactory.SetLayoutElement(row, minHeight: 25, flexibleWidth: 9999);

            Text labelText = UIFactory.CreateLabel(row, objectName + "Label", label, TextAnchor.MiddleLeft);
            UIFactory.SetLayoutElement(labelText.gameObject, minWidth: 135, minHeight: 25);

            InputFieldRef input = UIFactory.CreateInputField(row, objectName + "Input", hint);
            UIFactory.SetLayoutElement(input.GameObject, minWidth: (int)inputWidth, minHeight: 25, flexibleWidth: 9999);
            input.Text = initialValue;
            input.Component.GetOnEndEdit().AddListener(onEndEdit);
            return input;
        }

        private void CreateTransportModeRow()
        {
            GameObject row = UIFactory.CreateHorizontalGroup(
                LayoutRoot,
                "McpTransportModeRow",
                false,
                false,
                true,
                true,
                3,
                default,
                new Color(1f, 1f, 1f, 0f),
                TextAnchor.MiddleLeft);
            UIFactory.SetLayoutElement(row, minHeight: 25, flexibleWidth: 9999);

            Text label = UIFactory.CreateLabel(row, "McpTransportModeLabel", "Transport", TextAnchor.MiddleLeft);
            UIFactory.SetLayoutElement(label.gameObject, minWidth: 135, minHeight: 25);

            GameObject dropdownObject = UIFactory.CreateDropdown(
                row,
                "McpTransportModeDropdown",
                out transportModeDropdown,
                "SSE",
                13,
                OnTransportModeChanged);
            UIFactory.SetLayoutElement(dropdownObject, minWidth: 220, minHeight: 25, flexibleWidth: 9999);
            transportModeDropdown.ClearOptions();
            transportModeDropdown.options.Add(new Dropdown.OptionData("SSE"));
            transportModeDropdown.options.Add(new Dropdown.OptionData("Streamable HTTP"));
            transportModeDropdown.RefreshShownValue();
        }
        private void CreateTokenRow()
        {
            GameObject row = UIFactory.CreateHorizontalGroup(
                LayoutRoot,
                "AuthenticationTokenRow",
                false,
                false,
                true,
                true,
                3,
                default,
                new Color(1f, 1f, 1f, 0f),
                TextAnchor.MiddleLeft);
            UIFactory.SetLayoutElement(row, minHeight: 25, flexibleWidth: 9999);

            Text label = UIFactory.CreateLabel(row, "AuthenticationTokenLabel", "Authentication token", TextAnchor.MiddleLeft);
            UIFactory.SetLayoutElement(label.gameObject, minWidth: 135, minHeight: 25);

            tokenInput = UIFactory.CreateInputField(row, "AuthenticationTokenInput", "Generate a token before starting");
            UIFactory.SetLayoutElement(tokenInput.GameObject, minWidth: 260, minHeight: 25, flexibleWidth: 9999);
            tokenInput.Component.GetOnEndEdit().AddListener(OnTokenEndEdit);

            ButtonRef generateButton = UIFactory.CreateButton(row, "GenerateTokenButton", "Generate");
            UIFactory.SetLayoutElement(generateButton.GameObject, minWidth: 82, minHeight: 25);
            generateButton.OnClick += GenerateToken;

            ButtonRef copyButton = UIFactory.CreateButton(row, "CopyTokenButton", "Copy");
            UIFactory.SetLayoutElement(copyButton.GameObject, minWidth: 58, minHeight: 25);
            copyButton.OnClick += CopyToken;
        }

        private void CreateStatusSection()
        {
            GameObject row = UIFactory.CreateHorizontalGroup(
                LayoutRoot,
                "McpStatusRow",
                false,
                false,
                true,
                true,
                3,
                default,
                new Color(1f, 1f, 1f, 0f),
                TextAnchor.MiddleLeft);
            UIFactory.SetLayoutElement(row, minHeight: 42, flexibleWidth: 9999);

            statusText = UIFactory.CreateLabel(row, "McpStatus", "Status: unavailable", TextAnchor.MiddleLeft, Color.grey, true, 12);
            UIFactory.SetLayoutElement(statusText.gameObject, minWidth: 180, minHeight: 38, flexibleWidth: 1);

            endpointText = UIFactory.CreateLabel(row, "McpEndpoint", "Endpoint: -", TextAnchor.MiddleLeft, Color.grey, true, 12);
            UIFactory.SetLayoutElement(endpointText.gameObject, minWidth: 260, minHeight: 38, flexibleWidth: 2);
        }

        private void CreateActionRow()
        {
            GameObject row = UIFactory.CreateHorizontalGroup(
                LayoutRoot,
                "McpActions",
                false,
                false,
                true,
                true,
                3,
                default,
                new Color(1f, 1f, 1f, 0f),
                TextAnchor.MiddleCenter);
            UIFactory.SetLayoutElement(row, minHeight: 27, flexibleWidth: 9999);

            startButton = UIFactory.CreateButton(row, "StartMcpButton", "Start MCP", new Color(0.2f, 0.3f, 0.2f));
            UIFactory.SetLayoutElement(startButton.GameObject, minWidth: 140, minHeight: 25, flexibleWidth: 1);
            startButton.OnClick += StartServer;

            stopButton = UIFactory.CreateButton(row, "StopMcpButton", "Stop MCP", new Color(0.3f, 0.2f, 0.2f));
            UIFactory.SetLayoutElement(stopButton.GameObject, minWidth: 140, minHeight: 25, flexibleWidth: 1);
            stopButton.OnClick += StopServer;
        }

        private void CreateAgentToolsSection()
        {
            Text title = UIFactory.CreateLabel(
                LayoutRoot,
                "McpAgentToolsTitle",
                "Agent tools / Agent 可用工具",
                TextAnchor.MiddleLeft,
                Color.white,
                false,
                14);
            UIFactory.SetLayoutElement(title.gameObject, minHeight: 24, flexibleWidth: 9999);

            Text tools = UIFactory.CreateLabel(
                LayoutRoot,
                "McpAgentToolsHelp",
                "READ-ONLY: get_status, list_scenes, search_objects, get_object, get_member, list_methods\n" +
                "WRITE (Read-only OFF): set_member, set_transform, set_enabled\n" +
                "DANGEROUS (Read-only OFF + Allow dangerous operations ON): invoke_method, create_object, destroy_object\n" +
                "BATCH: execute_batch (each operation is checked against the same permissions)\n" +
                "Trae connection: Trae Agent --SSE / Streamable HTTP--> UnityExplorer DLL /mcp --main thread--> Unity game (no Node adapter)",
                TextAnchor.UpperLeft,
                Color.grey,
                true,
                12);
            UIFactory.SetLayoutElement(tools.gameObject, minHeight: 90, flexibleWidth: 9999);

            Text usage = UIFactory.CreateLabel(
                LayoutRoot,
                "McpAgentUsageHelp",
                "Recommended workflow / 推荐流程: get_status -> list_scenes -> search_objects -> get_object -> modify or invoke. " +
                "Object IDs are session-scoped; search again after a scene change or game restart. " +
                "Add the generated URL configuration directly in Trae. The MCP server runs inside the UnityExplorer DLL; no external Node process is required.",
                TextAnchor.UpperLeft,
                Color.grey,
                true,
                12);
            UIFactory.SetLayoutElement(usage.gameObject, minHeight: 58, flexibleWidth: 9999);

            GameObject configHeader = UIFactory.CreateHorizontalGroup(
                LayoutRoot,
                "McpAgentConfigHeader",
                false,
                false,
                true,
                true,
                3,
                default,
                new Color(1f, 1f, 1f, 0f),
                TextAnchor.MiddleLeft);
            UIFactory.SetLayoutElement(configHeader, minHeight: 25, flexibleWidth: 9999);

            Text configLabel = UIFactory.CreateLabel(
                configHeader,
                "McpAgentConfigLabel",
                "Trae direct DLL config (contains the authentication token / 包含敏感 Token)",
                TextAnchor.MiddleLeft,
                Color.grey,
                true,
                12);
            UIFactory.SetLayoutElement(configLabel.gameObject, minHeight: 25, flexibleWidth: 9999);

            ButtonRef copyConfigButton = UIFactory.CreateButton(
                configHeader,
                "CopyMcpAgentConfigButton",
                "Copy config (sensitive)");
            UIFactory.SetLayoutElement(copyConfigButton.GameObject, minWidth: 170, minHeight: 25);
            copyConfigButton.OnClick += CopyAgentConfig;

            agentConfigInput = UIFactory.CreateInputField(
                LayoutRoot,
                "McpAgentConfig",
                "Generate or enter a token to create the Trae direct MCP configuration template.");
            UIFactory.SetLayoutElement(agentConfigInput.GameObject, minHeight: 128, flexibleWidth: 9999);
            agentConfigInput.Component.readOnly = true;
            agentConfigInput.Component.lineType = InputField.LineType.MultiLineNewline;
            agentConfigInput.Component.textComponent.supportRichText = false;
            agentConfigInput.Component.textComponent.font = UniversalUI.ConsoleFont;
            agentConfigInput.PlaceholderText.font = UniversalUI.ConsoleFont;
            RefreshAgentConfigText();
        }
        private void CreateRequestLogSection()
        {
            Text label = UIFactory.CreateLabel(LayoutRoot, "RequestLogLabel", "Recent MCP requests", TextAnchor.MiddleLeft, Color.white, false, 13);
            UIFactory.SetLayoutElement(label.gameObject, minHeight: 25, flexibleWidth: 9999);

            requestLogInput = UIFactory.CreateInputField(LayoutRoot, "McpRequestLog", "Request logging is empty or disabled.");
            UIFactory.SetLayoutElement(requestLogInput.GameObject, minHeight: 120, preferredHeight: 160, flexibleWidth: 9999);
            requestLogInput.Component.readOnly = true;
            requestLogInput.Component.lineType = InputField.LineType.MultiLineNewline;
            requestLogInput.Component.textComponent.supportRichText = true;
            requestLogInput.Component.textComponent.font = UniversalUI.ConsoleFont;
            requestLogInput.PlaceholderText.font = UniversalUI.ConsoleFont;
        }

        private void LoadSettingsFromManager()
        {
            updatingControls = true;
            try
            {
                enabledToggle.isOn = McpManager.Enabled;
                bindAddressInput.Text = McpManager.BindAddress;
                portInput.Text = McpManager.Port.ToString();
                transportModeDropdown.value = IsStreamableHttpMode(McpManager.TransportMode) ? 1 : 0;
                transportModeDropdown.RefreshShownValue();
                tokenInput.Text = McpManager.AuthenticationToken;
                readOnlyToggle.isOn = McpManager.ReadOnly;
                dangerousOperationsToggle.isOn = McpManager.AllowDangerousOperations;
                requestLoggingToggle.isOn = McpManager.RequestLoggingEnabled;
            }
            finally
            {
                updatingControls = false;
            }
            RefreshAgentConfigText();
        }

        private void OnEnabledChanged(bool value)
        {
            McpManager.Enabled = value;
            McpManager.SaveSettings();
            RefreshRuntimeState();
        }

        private void OnBindAddressEndEdit(string value)
        {
            string address = IsBlank(value) ? "127.0.0.1" : value.Trim();
            bindAddressInput.Text = address;
            try
            {
                McpManager.BindAddress = address;
            }
            catch (ArgumentException ex)
            {
                // BindAddress rejects anything other than loopback.
                bindAddressInput.Text = McpManager.BindAddress;
                SetStatus(ex.Message, new Color(1f, 0.5f, 0.35f));
                return;
            }
            McpManager.SaveSettings();
            RefreshAgentConfigText();
            RefreshRuntimeState();
        }

        private void OnPortEndEdit(string value)
        {
            if (!int.TryParse(value, out int port) || port < 1 || port > 65535)
            {
                port = McpManager.Port;
                portInput.Text = port.ToString();
                SetStatus("Invalid port. Enter a value from 1 to 65535.", new Color(1f, 0.5f, 0.35f));
                return;
            }

            McpManager.Port = port;
            McpManager.SaveSettings();
            RefreshAgentConfigText();
            RefreshRuntimeState();
        }

        private void OnTransportModeChanged(int value)
        {
            if (updatingControls)
                return;

            McpManager.TransportMode = value == 1 ? "StreamableHTTP" : "SSE";
            McpManager.SaveSettings();
            RefreshAgentConfigText();
            RefreshRuntimeState();

            if (McpManager.IsRunning)
                SetStatus("Transport changed to " + GetTransportDisplayName() + ". Restart MCP to apply it.", new Color(1f, 0.75f, 0.3f));
        }
        private void OnTokenEndEdit(string value)
        {
            McpManager.AuthenticationToken = value ?? string.Empty;
            McpManager.SaveSettings();
            RefreshAgentConfigText();
        }

        private void OnReadOnlyChanged(bool value)
        {
            McpManager.ReadOnly = value;
            McpManager.SaveSettings();
        }

        private void OnDangerousOperationsChanged(bool value)
        {
            McpManager.AllowDangerousOperations = value;
            McpManager.SaveSettings();
        }

        private void OnRequestLoggingChanged(bool value)
        {
            McpManager.RequestLoggingEnabled = value;
            McpManager.SaveSettings();
            RefreshRuntimeState();
        }

        private void GenerateToken()
        {
            string token = McpManager.GenerateAuthenticationToken();
            if (string.IsNullOrEmpty(token))
                token = GenerateLocalToken();

            tokenInput.Text = token;
            McpManager.AuthenticationToken = token;
            McpManager.SaveSettings();
            RefreshAgentConfigText();
            SetStatus("A new authentication token was generated.", new Color(0.45f, 0.9f, 0.5f));
        }

        private void CopyToken()
        {
            GUIUtility.systemCopyBuffer = tokenInput.Text ?? string.Empty;
            SetStatus("Authentication token copied. Treat clipboard contents as sensitive.", new Color(1f, 0.68f, 0.32f));
        }

        private void CopyAgentConfig()
        {
            if (IsBlank(tokenInput?.Text))
            {
                SetStatus("Generate or enter a token before copying the Agent configuration.", new Color(1f, 0.6f, 0.3f));
                return;
            }

            string config = BuildAgentConfig();
            GUIUtility.systemCopyBuffer = config;
            SetStatus(
                "Sensitive direct MCP config copied. It contains the MCP token; do not paste it into logs, chat, or source control.",
                new Color(1f, 0.68f, 0.32f));
        }

        private void RefreshAgentConfigText()
        {
            if (agentConfigInput != null)
                agentConfigInput.Text = BuildAgentConfig();
        }

        private string BuildAgentConfig()
        {
            string address = IsBlank(bindAddressInput?.Text) ? "127.0.0.1" : bindAddressInput.Text.Trim();
            string port = IsBlank(portInput?.Text) ? "17891" : portInput.Text.Trim();
            string token = IsBlank(tokenInput?.Text) ? "<GENERATE_TOKEN_IN_UNITYEXPLORER>" : tokenInput.Text;
            string endpoint = "http://" + address + ":" + port + "/mcp";

            string traeType = IsStreamableHttpSelected() ? "streamableHttp" : "sse";

            StringBuilder json = new StringBuilder(480);
            json.AppendLine("{");
            json.AppendLine("  \"mcpServers\": {");
            json.AppendLine("    \"unity-explorer\": {");
            json.Append("      \"type\": \"").Append(traeType).AppendLine("\",");
            json.Append("      \"url\": \"").Append(EscapeJson(endpoint)).AppendLine("\",");
            json.AppendLine("      \"headers\": {");
            json.Append("        \"Authorization\": \"Bearer ").Append(EscapeJson(token)).AppendLine("\"");
            json.AppendLine("      }");
            json.AppendLine("    }");
            json.AppendLine("  }");
            json.Append("}");
            return json.ToString();
        }

        private bool IsStreamableHttpSelected() => transportModeDropdown != null && transportModeDropdown.value == 1;

        private string GetTransportDisplayName() => IsStreamableHttpSelected() ? "Streamable HTTP" : "SSE";

        private static bool IsStreamableHttpMode(string value)
        {
            if (string.IsNullOrEmpty(value))
                return false;

            string normalized = value.Replace("-", string.Empty).Replace("_", string.Empty).Replace(" ", string.Empty);
            return normalized.Equals("StreamableHTTP", StringComparison.OrdinalIgnoreCase)
                || normalized.Equals("StreamableHttp", StringComparison.OrdinalIgnoreCase)
                || normalized.Equals("HTTP", StringComparison.OrdinalIgnoreCase);
        }
        private static string EscapeJson(string value)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;

            StringBuilder escaped = new StringBuilder(value.Length + 8);
            for (int i = 0; i < value.Length; i++)
            {
                char character = value[i];
                switch (character)
                {
                    case '\\': escaped.Append("\\\\"); break;
                    case '"': escaped.Append("\\\""); break;
                    case '\b': escaped.Append("\\b"); break;
                    case '\f': escaped.Append("\\f"); break;
                    case '\n': escaped.Append("\\n"); break;
                    case '\r': escaped.Append("\\r"); break;
                    case '\t': escaped.Append("\\t"); break;
                    default:
                        if (character < 0x20)
                            escaped.Append("\\u").Append(((int)character).ToString("x4"));
                        else
                            escaped.Append(character);
                        break;
                }
            }
            return escaped.ToString();
        }

        private void StartServer()
        {
            OnBindAddressEndEdit(bindAddressInput.Text);
            OnPortEndEdit(portInput.Text);
            OnTokenEndEdit(tokenInput.Text);

            if (!enabledToggle.isOn)
            {
                SetStatus("Enable MCP before starting the server.", new Color(1f, 0.65f, 0.3f));
                return;
            }

            if (IsBlank(tokenInput.Text))
            {
                SetStatus("Generate or enter an authentication token before starting MCP.", new Color(1f, 0.5f, 0.35f));
                return;
            }

            if (!McpManager.IsInitialized)
            {
                SetStatus("MCP manager is not initialized yet.", new Color(1f, 0.5f, 0.35f));
                return;
            }

            try
            {
                McpManager.SaveSettings();
                McpManager.Start();
                RefreshRuntimeState();
            }
            catch (Exception ex)
            {
                SetStatus("Failed to start MCP: " + ex.Message, new Color(1f, 0.4f, 0.35f));
                ExplorerCore.LogWarning("Failed to start MCP server: " + ex);
            }
        }

        private void StopServer()
        {
            try
            {
                McpManager.Stop();
                RefreshRuntimeState();
            }
            catch (Exception ex)
            {
                SetStatus("Failed to stop MCP: " + ex.Message, new Color(1f, 0.4f, 0.35f));
                ExplorerCore.LogWarning("Failed to stop MCP server: " + ex);
            }
        }

        private void RefreshRuntimeState()
        {
            if (statusText == null)
                return;

            bool available = McpManager.IsInitialized;
            bool running = available && McpManager.IsRunning;
            bool restartRequired = available && McpManager.RestartRequired;
            string managerStatus = McpManager.StatusText;
            string lastError = McpManager.LastErrorMessage;
            string bindAddress = IsBlank(bindAddressInput?.Text) ? "127.0.0.1" : bindAddressInput.Text.Trim();
            string port = IsBlank(portInput?.Text) ? "17891" : portInput.Text.Trim();

            if (!available)
                SetStatus("MCP manager unavailable", new Color(1f, 0.5f, 0.35f));
            else if (!string.IsNullOrEmpty(lastError))
                SetStatus("Faulted: " + lastError, new Color(1f, 0.4f, 0.35f));
            else if (running)
            {
                string runningStatus = string.IsNullOrEmpty(managerStatus) ? "Running" : managerStatus;
                if (restartRequired)
                    runningStatus += " (restart required to apply changed settings)";
                SetStatus(runningStatus, restartRequired ? new Color(1f, 0.75f, 0.3f) : new Color(0.45f, 0.95f, 0.5f));
            }
            else
                SetStatus(string.IsNullOrEmpty(managerStatus) ? "Stopped" : managerStatus, Color.grey);

            string reportedEndpoint = McpManager.RpcEndpoint;
            endpointText.text = "Endpoint: " + (string.IsNullOrEmpty(reportedEndpoint)
                ? "http://" + bindAddress + ":" + port + "/mcp"
                : reportedEndpoint);
            endpointText.color = bindAddress == "127.0.0.1" || bindAddress.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                ? new Color(0.65f, 0.85f, 1f)
                : new Color(1f, 0.55f, 0.35f);

            startButton.Component.interactable = available && enabledToggle.isOn && !running;
            stopButton.Component.interactable = available && running;

            if (requestLoggingToggle.isOn)
                requestLogInput.Text = McpManager.RecentRequests;
            else
                requestLogInput.Text = "Request logging is disabled.";
        }

        private void SetStatus(string value, Color color)
        {
            if (statusText == null)
                return;

            statusText.text = "Status: " + value;
            statusText.color = color;
        }

        private static bool IsBlank(string value) => string.IsNullOrEmpty(value) || value.Trim().Length == 0;

        private static string GenerateLocalToken()
        {
            byte[] bytes = new byte[32];
            RandomNumberGenerator generator = RandomNumberGenerator.Create();
            generator.GetBytes(bytes);

            StringBuilder result = new(bytes.Length * 2);
            for (int i = 0; i < bytes.Length; i++)
                result.Append(bytes[i].ToString("x2"));
            return result.ToString();
        }
    }
}
