using UnityExplorer.MCP.Transport;
using UnityExplorer.UI;

namespace UnityExplorer.Config
{
    public static class ConfigManager
    {
        internal static readonly Dictionary<string, IConfigElement> ConfigElements = new();
        internal static readonly Dictionary<string, IConfigElement> InternalConfigs = new();

        // Each Mod Loader has its own ConfigHandler.
        // See the UnityExplorer.Loader namespace for the implementations.
        public static ConfigHandler Handler { get; private set; }

        // Actual UE Settings
        public static ConfigElement<KeyCode> Master_Toggle;
        public static ConfigElement<bool> Hide_On_Startup;
        public static ConfigElement<float> Startup_Delay_Time;
        public static ConfigElement<bool> Disable_EventSystem_Override;
        public static ConfigElement<bool> Disable_Setup_Force_ReLoad_ManagedAssemblies;
        public static ConfigElement<bool> Bypass_UniverseLib_ICall;
        public static ConfigElement<int> Target_Display;
        public static ConfigElement<bool> Force_Unlock_Mouse;
        public static ConfigElement<KeyCode> Force_Unlock_Toggle;
        public static ConfigElement<string> Default_Output_Path;
        public static ConfigElement<string> DnSpy_Path;
        public static ConfigElement<bool> Log_Unity_Debug;
        public static ConfigElement<bool> Log_To_Disk;
        public static ConfigElement<UIManager.VerticalAnchor> Main_Navbar_Anchor;
        public static ConfigElement<KeyCode> World_MouseInspect_Keybind;
        public static ConfigElement<KeyCode> UI_MouseInspect_Keybind;
        public static ConfigElement<string> CSConsole_Assembly_Blacklist;
        public static ConfigElement<string> Reflection_Signature_Blacklist;

        public static ConfigElement<KeyCode> TIME_SCALE_TOGGLE;
        public static ConfigElement<KeyCode> LOCK_TIME_SCALE_TO_ZERO;
        public static ConfigElement<KeyCode> LOCK_TIME_SCALE_TO_NORMAL;
        public static ConfigElement<KeyCode> LOCK_TIME_SCALE_TO_HALF;
        public static ConfigElement<KeyCode> LOCK_TIME_SCALE_TO_DOUBLE;

        // MCP server settings
        public static ConfigElement<bool> MCP_Enabled;
        public static ConfigElement<McpTransportMode> MCP_Transport_Mode;
        public static ConfigElement<string> MCP_Bind_Address;
        public static ConfigElement<int> MCP_Port;
        public static ConfigElement<string> MCP_Rpc_Path;
        public static ConfigElement<string> MCP_Health_Path;
        public static ConfigElement<string> MCP_Auth_Token;
        public static ConfigElement<bool> MCP_Require_Token_For_Health;
        public static ConfigElement<bool> MCP_Read_Only;
        public static ConfigElement<bool> MCP_Allow_Dangerous_Operations;
        public static ConfigElement<bool> MCP_Request_Logging;
        public static ConfigElement<int> MCP_Request_Timeout_Milliseconds;
        public static ConfigElement<int> MCP_Max_Request_Body_Bytes;
        public static ConfigElement<int> MCP_Max_Pending_Requests;
        public static ConfigElement<int> MCP_Max_Requests_Per_Frame;

        // internal configs
        internal static InternalConfigHandler InternalHandler { get; private set; }
        internal static readonly Dictionary<UIManager.Panels, ConfigElement<string>> PanelSaveData = new();

        internal static ConfigElement<string> GetPanelSaveData(UIManager.Panels panel)
        {
            if (!PanelSaveData.ContainsKey(panel))
                PanelSaveData.Add(panel, new ConfigElement<string>(panel.ToString(), string.Empty, string.Empty, true));
            return PanelSaveData[panel];
        }

        public static void Init(ConfigHandler configHandler)
        {
            Handler = configHandler;
            Handler.Init();

            InternalHandler = new InternalConfigHandler();
            InternalHandler.Init();

            CreateConfigElements();

            Handler.LoadConfig();
            InternalHandler.LoadConfig();

#if STANDALONE
            if (Loader.Standalone.ExplorerEditorBehaviour.Instance)
                Loader.Standalone.ExplorerEditorBehaviour.Instance.LoadConfigs();
#endif
        }

        internal static void RegisterConfigElement<T>(ConfigElement<T> configElement)
        {
            if (!configElement.IsInternal)
            {
                Handler.RegisterConfigElement(configElement);
                ConfigElements.Add(configElement.Name, configElement);
            }
            else
            {
                InternalHandler.RegisterConfigElement(configElement);
                InternalConfigs.Add(configElement.Name, configElement);
            }
        }

        private static void CreateConfigElements()
        {
            Master_Toggle = new("UnityExplorer Toggle",
                "The key to enable or disable UnityExplorer's menu and features.",
                KeyCode.F7);

            Hide_On_Startup = new("Hide On Startup",
                "Should UnityExplorer be hidden on startup?",
                false);

            Startup_Delay_Time = new("Startup Delay Time",
                "The delay on startup before the UI is created.",
                1f);

            Target_Display = new("Target Display",
                "The monitor index for UnityExplorer to use, if you have multiple. 0 is the default display, 1 is secondary, etc. " +
                "Restart recommended when changing this setting. Make sure your extra monitors are the same resolution as your primary monitor.",
                0);

            Force_Unlock_Mouse = new("Force Unlock Mouse",
                "Force the Cursor to be unlocked (visible) when the UnityExplorer menu is open.",
                true);
            Force_Unlock_Mouse.OnValueChanged += (bool value) => UniverseLib.Config.ConfigManager.Force_Unlock_Mouse = value;

            Force_Unlock_Toggle = new("Force Unlock Toggle Key",
                "The keybind to toggle the 'Force Unlock Mouse' setting. Only usable when UnityExplorer is open.",
                KeyCode.None);

            Disable_EventSystem_Override = new("Disable EventSystem override",
                "If enabled, UnityExplorer will not override the EventSystem from the game.\n<b>May require restart to take effect.</b>",
                false);
            Disable_EventSystem_Override.OnValueChanged += (bool value) => UniverseLib.Config.ConfigManager.Disable_EventSystem_Override = value;

            Disable_Setup_Force_ReLoad_ManagedAssemblies = new("Disable Force reload ManagedAssemblies",
                "If enabled, UnityExplorer will not reload ManagedAssemblies on setup(Currently only Mono is supported).\n<b>May require restart to take effect.</b>",
                false);

            Bypass_UniverseLib_ICall = new("Bypass UniverseLib ICall",
                "If enabled, UnityExplorer will bypass UniverseLib's ICall Reflection system. This may help with compatibility in some games.\n<b>May require restart to take effect.</b>",
                false);

            Default_Output_Path = new("Default Output Path",
                "The default output path when exporting things from UnityExplorer.",
                Path.Combine(ExplorerCore.ExplorerFolder, "Output"));

            DnSpy_Path = new("dnSpy Path",
                "The full path to dnSpy.exe (64-bit).",
                @"C:/Program Files/dnspy/dnSpy.exe");

            Main_Navbar_Anchor = new("Main Navbar Anchor",
                "The vertical anchor of the main UnityExplorer Navbar, in case you want to move it.",
                UIManager.VerticalAnchor.Top);

            Log_Unity_Debug = new("Log Unity Debug",
                "Should UnityEngine.Debug.Log messages be printed to UnityExplorer's log?",
                false);

            Log_To_Disk = new("Log To Disk",
                "Should UnityExplorer save log files to the disk?",
                true);

            World_MouseInspect_Keybind = new("World Mouse-Inspect Keybind",
                "Optional keybind to being a World-mode Mouse Inspect.",
                KeyCode.None);

            UI_MouseInspect_Keybind = new("UI Mouse-Inspect Keybind",
                "Optional keybind to begin a UI-mode Mouse Inspect.",
                KeyCode.None);

            CSConsole_Assembly_Blacklist = new("CSharp Console Assembly Blacklist",
                "Use this to blacklist Assembly names from being referenced by the C# Console. Requires a Reset of the C# Console.\n" +
                "Separate each Assembly with a semicolon ';'." +
                "For example, to blacklist Assembly-CSharp, you would add 'Assembly-CSharp;'",
                "");

            Reflection_Signature_Blacklist = new("Member Signature Blacklist",
                "Use this to blacklist certain member signatures if they are known to cause a crash or other issues.\r\n" +
                "Seperate signatures with a semicolon ';'.\r\n" +
                "For example, to blacklist Camera.main, you would add 'UnityEngine.Camera.main;'",
                "");

            TIME_SCALE_TOGGLE = new("TimeScale Toggle",
                "Shortcut key for locking/unlocking TimeScale",
                KeyCode.None);
            LOCK_TIME_SCALE_TO_ZERO = new("Pause Keybind",
                "Shortcut key for setting TimeScale to 0.0",
                KeyCode.None);
            LOCK_TIME_SCALE_TO_NORMAL = new("Playback Keybind",
                "Shortcut key for setting TimeScale to 1.0",
                KeyCode.None);
            LOCK_TIME_SCALE_TO_HALF = new("Speed-Down Keybind",
                "Shortcut key for setting TimeScale to half",
                KeyCode.None);
            LOCK_TIME_SCALE_TO_DOUBLE = new("Speed-Up Keybind",
                "Shortcut key for setting TimeScale to double",
                KeyCode.None);

            MCP_Enabled = new("MCP Enabled",
                "Start the local MCP server after UnityExplorer finishes initializing.",
                false);

            MCP_Transport_Mode = new("MCP Transport Mode",
                "MCP protocol transport hosted directly by the UnityExplorer DLL. Supported values are SSE and StreamableHTTP. Restart MCP after changing this value.",
                McpTransportMode.SSE);

            MCP_Bind_Address = new("MCP Bind Address",
                "Address exposed by the MCP transport. The built-in transport currently supports loopback only.",
                "127.0.0.1");

            MCP_Port = new("MCP Port",
                "TCP port used by the local MCP HTTP bridge. Restart MCP after changing this value.",
                17891);

            MCP_Rpc_Path = new("MCP RPC Path",
                "HTTP path used for JSON-RPC requests. Restart MCP after changing this value.",
                "/mcp");

            MCP_Health_Path = new("MCP Health Path",
                "HTTP path used for MCP health checks. Restart MCP after changing this value.",
                "/health");

            MCP_Auth_Token = new("MCP Auth Token",
                "Bearer token required by the MCP HTTP bridge. A secure token is generated automatically when MCP starts if this value is empty.",
                "");

            MCP_Require_Token_For_Health = new("MCP Require Token For Health",
                "Require the configured MCP bearer token for health-check requests.",
                false);

            MCP_Read_Only = new("MCP Read Only",
                "Block mutation tools and permit inspection-only MCP operations.",
                true);

            MCP_Allow_Dangerous_Operations = new("MCP Allow Dangerous Operations",
                "Allow high-risk tools such as object destruction, arbitrary invocation, and scene changes. Reset to false on every startup.",
                false);

            MCP_Request_Logging = new("MCP Request Logging",
                "Retain a small in-memory log of MCP requests for the MCP UI.",
                false);

            MCP_Request_Timeout_Milliseconds = new("MCP Request Timeout Milliseconds",
                "Maximum time an HTTP request waits for Unity main-thread execution.",
                30000);

            MCP_Max_Request_Body_Bytes = new("MCP Max Request Body Bytes",
                "Maximum accepted JSON-RPC request body size.",
                1024 * 1024);

            MCP_Max_Pending_Requests = new("MCP Max Pending Requests",
                "Maximum number of requests waiting for Unity main-thread execution.",
                128);

            MCP_Max_Requests_Per_Frame = new("MCP Max Requests Per Frame",
                "Maximum number of queued MCP requests executed during one Unity Update.",
                16);
        }
    }
}
