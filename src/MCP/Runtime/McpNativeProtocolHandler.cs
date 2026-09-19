using System;
using System.Collections.Generic;
using UnityExplorer.MCP.Transport;

namespace UnityExplorer.MCP.Runtime
{
    /// <summary>
    /// Implements the MCP protocol surface directly inside the UnityExplorer DLL.
    /// It adapts standard MCP methods to <see cref="McpGameCapabilityExecutor"/>
    /// without requiring the legacy Node.js sidecar.
    /// </summary>
    public sealed class McpNativeProtocolHandler : IMcpRequestHandler
    {
        // The 2026-07-28 revision requires Mcp-Method/Mcp-Name headers on Streamable
        // HTTP requests, which this transport does not implement, so it is not
        // advertised. 2025-11-25 is the newest revision fully satisfied here.
        private const string DefaultProtocol = "2025-11-25";
        private static readonly string[] SupportedProtocols = { DefaultProtocol, "2025-06-18", "2025-03-26", "2024-11-05" };
        private static readonly string[] ProtocolMethods =
        {
            "initialize", "notifications/initialized", "ping", "tools/list", "tools/call"
        };

        private readonly McpGameCapabilityExecutor executor;
        private readonly McpJsonValue tools;
        private readonly Dictionary<string, string[]> requiredArguments;

        public McpNativeProtocolHandler(McpGameCapabilityExecutor executor)
        {
            if (executor == null) throw new ArgumentNullException("executor");
            this.executor = executor;
            tools = BuildTools(executor.Options);
            requiredArguments = BuildRequiredArguments();
        }

        public void Register(McpRequestRouter router)
        {
            if (router == null) throw new ArgumentNullException("router");
            for (int i = 0; i < ProtocolMethods.Length; i++) router.Register(ProtocolMethods[i], this);
        }

        public void Unregister(McpRequestRouter router)
        {
            if (router == null) return;
            for (int i = 0; i < ProtocolMethods.Length; i++) router.Unregister(ProtocolMethods[i]);
        }

        public McpResponse Handle(McpRequest request)
        {
            if (request == null) return McpResponse.Error(-32600, "Request is required.");
            try
            {
                McpJsonValue root = McpJsonValue.Parse(request.RawJson);
                McpJsonValue parameters;
                if (!root.TryGet("params", out parameters) || parameters == null || parameters.IsNull)
                    parameters = McpJsonValue.Object();
                if (parameters.Kind != McpJsonValue.JsonKind.Object)
                    return McpResponse.Error(-32602, "params must be a JSON object.");

                switch (request.Method)
                {
                    case "initialize": return McpResponse.SuccessJson(Initialize(parameters).ToJson());
                    case "notifications/initialized": return McpResponse.Success();
                    case "ping": return McpResponse.SuccessJson("{}");
                    case "tools/list": return McpResponse.SuccessJson(ToolsList(parameters).ToJson());
                    case "tools/call": return CallTool(parameters);
                    default: return McpResponse.Error(-32601, "Method not found: " + request.Method);
                }
            }
            catch (FormatException ex)
            {
                return McpResponse.Error(-32602, ex.Message);
            }
            catch (Exception ex)
            {
                return McpResponse.Error(-32603, McpReflection.Unwrap(ex).Message);
            }
        }

        private McpJsonValue Initialize(McpJsonValue parameters)
        {
            string requested = parameters.GetString("protocolVersion", DefaultProtocol);
            string negotiated = DefaultProtocol;
            for (int i = 0; i < SupportedProtocols.Length; i++)
            {
                if (string.Equals(requested, SupportedProtocols[i], StringComparison.Ordinal))
                {
                    negotiated = requested;
                    break;
                }
            }

            McpJsonValue result = McpJsonValue.Object();
            result.ObjectValue["protocolVersion"] = McpJsonValue.From(negotiated);
            result.ObjectValue["capabilities"] = Capabilities();
            result.ObjectValue["serverInfo"] = ServerInfo();
            result.ObjectValue["instructions"] = McpJsonValue.From(Instructions());
            return result;
        }

        private McpJsonValue ToolsList(McpJsonValue parameters)
        {
            McpJsonValue result = McpJsonValue.Object();
            result.ObjectValue["tools"] = tools;
            return result;
        }

        private McpResponse CallTool(McpJsonValue parameters)
        {
            string name = parameters.GetString("name", null);
            if (string.IsNullOrEmpty(name))
                return McpResponse.Error(-32602, "tools/call requires params.name and optional params.arguments.");

            string[] required;
            if (!requiredArguments.TryGetValue(name, out required))
                return McpResponse.Error(-32602, "Unknown tool: " + name);

            McpJsonValue arguments;
            if (!parameters.TryGet("arguments", out arguments) || arguments == null || arguments.IsNull)
                arguments = McpJsonValue.Object();
            if (arguments.Kind != McpJsonValue.JsonKind.Object)
                return McpResponse.Error(-32602, "tools/call params.arguments must be a JSON object.");

            for (int i = 0; i < required.Length; i++)
            {
                McpJsonValue ignored;
                if (!arguments.TryGet(required[i], out ignored))
                    return McpResponse.Error(-32602, "Missing required argument: " + required[i]);
            }

            McpJsonValue command = CopyObject(arguments);
            command.ObjectValue["command"] = McpJsonValue.From(name);
            McpJsonValue execution = executor.Execute(command);
            return McpResponse.SuccessJson(BuildToolResult(execution).ToJson());
        }

        private static McpJsonValue BuildToolResult(McpJsonValue execution)
        {
            bool failed = false;
            McpJsonValue ok;
            if (execution != null && execution.TryGet("ok", out ok) && ok.Kind == McpJsonValue.JsonKind.Boolean)
                failed = !ok.BooleanValue;

            McpJsonValue structured = McpJsonValue.Object();
            structured.ObjectValue["data"] = execution ?? McpJsonValue.Null();

            McpJsonValue text = McpJsonValue.Object();
            text.ObjectValue["type"] = McpJsonValue.From("text");
            text.ObjectValue["text"] = McpJsonValue.From(structured.ToJson());
            McpJsonValue content = McpJsonValue.Array();
            content.ArrayValue.Add(text);

            McpJsonValue result = McpJsonValue.Object();
            result.ObjectValue["content"] = content;
            result.ObjectValue["structuredContent"] = structured;
            result.ObjectValue["isError"] = McpJsonValue.From(failed);
            return result;
        }

        private static McpJsonValue Capabilities()
        {
            McpJsonValue toolsCapability = McpJsonValue.Object();
            toolsCapability.ObjectValue["listChanged"] = McpJsonValue.From(false);
            McpJsonValue capabilities = McpJsonValue.Object();
            capabilities.ObjectValue["tools"] = toolsCapability;
            return capabilities;
        }

        private static McpJsonValue ServerInfo()
        {
            McpJsonValue info = McpJsonValue.Object();
            info.ObjectValue["name"] = McpJsonValue.From("unity-explorer");
            info.ObjectValue["title"] = McpJsonValue.From("UnityExplorer MCP");
            info.ObjectValue["version"] = McpJsonValue.From(ExplorerCore.VERSION);
            return info;
        }

        private static string Instructions()
        {
            return "Use search_objects to obtain stable object handles, inspect with get_object, then mutate with set_member or invoke_method. Prefer execute_batch for related operations.";
        }

        private static Dictionary<string, string[]> BuildRequiredArguments()
        {
            Dictionary<string, string[]> map = new Dictionary<string, string[]>(StringComparer.Ordinal);
            map["get_status"] = new string[0];
            map["list_scenes"] = new string[0];
            map["search_objects"] = new string[0];
            // These accept either object_id (an instance) or type (a static view, the Inspector's
            // [S] tab). Requiring object_id made the executor's type-only path unreachable.
            map["get_object"] = new string[0];
            map["get_member"] = new[] { "member_path" };
            map["list_methods"] = new string[0];
            map["set_member"] = new[] { "member_path", "value" };
            map["set_transform"] = new[] { "object_id" };
            map["set_enabled"] = new[] { "object_id", "enabled" };
            map["create_object"] = new string[0];
            map["destroy_object"] = new[] { "object_id" };
            map["invoke_method"] = new[] { "method" };
            map["execute_batch"] = new[] { "operations" };
            return map;
        }

        private static McpJsonValue BuildTools(McpGameExecutorOptions options)
        {
            // Advertise the limits the executor actually enforces. Hardcoding wider bounds made
            // the schema promise ranges that were silently clamped at runtime.
            int maxResults = options.MaximumSearchResults;
            int maxDepth = options.MaximumSnapshotDepth;
            int maxItems = options.MaximumSerializedItems;
            McpJsonValue result = McpJsonValue.Array();
            AddTool(result, "get_status", "Get UnityExplorer bridge status", "Check whether the game-side bridge is alive and return game, Unity, scene, and capability metadata.", Schema(), true, false, true);
            AddTool(result, "list_scenes", "List Unity scenes", "List loaded Unity scenes. include_unloaded adds scenes present in the build but not loaded; include_special adds the DontDestroyOnLoad and HideAndDontSave synthetic scenes.", Schema(Prop("include_unloaded", Boolean()), Prop("include_special", Boolean()), Prop("limit", Integer(1, maxResults))), true, false, true);
            AddTool(result, "search_objects", "Search game objects", "Search Unity objects and return stable handles for later calls. kind filters to gameobject, component, scriptableobject or asset; include_inactive=false hides objects inactive in the hierarchy.", Schema(Prop("query", String()), Prop("scene", String()), Prop("type", String()), Prop("kind", String()), Prop("include_inactive", Boolean()), Prop("exact", Boolean()), Prop("limit", Integer(1, maxResults))), true, false, true);
            AddTool(result, "get_object", "Inspect an object", "Read an object summary, hierarchy, components, and members. Pass object_id for an instance, or type alone for a static view of a class (the Inspector's [S] tab). GameObjects include children (with siblingIndex), componentCount/components, parentId and path. memberInfo describes each member (kind, declaredType, declaredBy, static).", Schema(Prop("object_id", Id()), Prop("type", String()), Prop("depth", Integer(0, maxDepth)), Prop("include_members", Boolean()), Prop("include_methods", Boolean()), Prop("include_static", Boolean()), Prop("member_filter", String()), Prop("max_collection_items", Integer(1, maxItems))), true, false, true);
            AddTool(result, "get_member", "Read an object member", "Read one field, property, or nested member path. Pass object_id for an instance, or type for a static member.", SchemaRequired(new[] { "member_path" }, Prop("object_id", Id()), Prop("type", String()), Prop("member_path", String()), Prop("depth", Integer(0, maxDepth)), Prop("max_items", Integer(1, maxItems))), true, false, true);
            AddTool(result, "list_methods", "List callable methods", "List callable instance/static methods and signatures. Pass object_id for an instance, or type for statics only. Set include_constructors to also list constructors, and include_inherited=false to hide inherited members.", Schema(Prop("object_id", Id()), Prop("type", String()), Prop("name_filter", String()), Prop("include_non_public", Boolean()), Prop("include_inherited", Boolean()), Prop("include_static", Boolean()), Prop("include_constructors", Boolean()), Prop("limit", Integer(1, maxItems))), true, false, true);
            AddTool(result, "set_member", "Set an object member", "Set a field/property or nested member path. value_type optionally states the member's expected type and is validated against it.", SchemaRequired(new[] { "member_path", "value" }, Prop("object_id", Id()), Prop("type", String()), Prop("member_path", String()), Prop("value", McpJsonValue.Object()), Prop("value_type", String())), false, true, false);
            AddTool(result, "set_transform", "Set object transform", "Set world/local Transform values or change an object's parent.", SchemaRequired(new[] { "object_id" }, Prop("object_id", Id()), Prop("position", Vector3()), Prop("local_position", Vector3()), Prop("rotation", Quaternion()), Prop("local_rotation", Quaternion()), Prop("euler_angles", Vector3()), Prop("local_euler_angles", Vector3()), Prop("local_scale", Vector3()), Prop("parent_id", Id()), Prop("world_position_stays", Boolean())), false, true, true);
            AddTool(result, "set_enabled", "Set object enabled state", "Enable or disable a GameObject, Behaviour, or writable enabled member.", SchemaRequired(new[] { "object_id", "enabled" }, Prop("object_id", Id()), Prop("enabled", Boolean())), false, true, true);
            AddTool(result, "create_object", "Create a game object", "Create an empty or primitive GameObject and optionally add Components.", Schema(Prop("name", String()), Prop("primitive", String()), Prop("parent_id", Id()), Prop("world_position_stays", Boolean()), Prop("components", ArrayOf(String()))), false, false, false);
            AddTool(result, "destroy_object", "Destroy a game object", "Destroy a UnityEngine.Object by stable handle.", SchemaRequired(new[] { "object_id" }, Prop("object_id", Id()), Prop("immediate", Boolean())), false, true, false);
            AddTool(result, "invoke_method", "Invoke an object method", "Invoke an instance or static method with JSON arguments converted to CLR values. overload selects a specific overload by signature or parameter type list; generic_type_arguments supplies type arguments to close a generic method.", SchemaRequired(new[] { "method" }, Prop("object_id", Id()), Prop("type", String()), Prop("method", String()), Prop("arguments", ArrayOf(McpJsonValue.Object())), Prop("generic_type_arguments", ArrayOf(String())), Prop("overload", String())), false, true, false);
            AddTool(result, "execute_batch", "Execute a bridge batch", "Execute multiple non-batch bridge operations in order.", SchemaRequired(new[] { "operations" }, Prop("operations", ArrayOf(McpJsonValue.Object())), Prop("atomic", Boolean()), Prop("stop_on_error", Boolean())), false, true, false);
            return result;
        }

        private static void AddTool(McpJsonValue array, string name, string title, string description, McpJsonValue schema, bool readOnly, bool destructive, bool idempotent)
        {
            McpJsonValue tool = McpJsonValue.Object();
            tool.ObjectValue["name"] = McpJsonValue.From(name);
            tool.ObjectValue["title"] = McpJsonValue.From(title);
            tool.ObjectValue["description"] = McpJsonValue.From(description);
            tool.ObjectValue["inputSchema"] = schema;
            McpJsonValue annotations = McpJsonValue.Object();
            annotations.ObjectValue["readOnlyHint"] = McpJsonValue.From(readOnly);
            annotations.ObjectValue["destructiveHint"] = McpJsonValue.From(destructive);
            annotations.ObjectValue["idempotentHint"] = McpJsonValue.From(idempotent);
            annotations.ObjectValue["openWorldHint"] = McpJsonValue.From(false);
            tool.ObjectValue["annotations"] = annotations;
            array.ArrayValue.Add(tool);
        }

        private static KeyValuePair<string, McpJsonValue> Prop(string name, McpJsonValue schema) { return new KeyValuePair<string, McpJsonValue>(name, schema); }
        private static McpJsonValue Schema(params KeyValuePair<string, McpJsonValue>[] properties) { return SchemaRequired(null, properties); }
        private static McpJsonValue SchemaRequired(string[] required, params KeyValuePair<string, McpJsonValue>[] properties)
        {
            McpJsonValue schema = McpJsonValue.Object();
            schema.ObjectValue["type"] = McpJsonValue.From("object");
            McpJsonValue props = McpJsonValue.Object();
            for (int i = 0; i < properties.Length; i++) props.ObjectValue[properties[i].Key] = properties[i].Value;
            schema.ObjectValue["properties"] = props;
            if (required != null && required.Length > 0)
            {
                McpJsonValue items = McpJsonValue.Array();
                for (int i = 0; i < required.Length; i++) items.ArrayValue.Add(McpJsonValue.From(required[i]));
                schema.ObjectValue["required"] = items;
            }
            schema.ObjectValue["additionalProperties"] = McpJsonValue.From(false);
            return schema;
        }
        private static McpJsonValue Type(string name) { McpJsonValue v = McpJsonValue.Object(); v.ObjectValue["type"] = McpJsonValue.From(name); return v; }
        private static McpJsonValue String() { return Type("string"); }
        private static McpJsonValue Boolean() { return Type("boolean"); }
        private static McpJsonValue Id() { McpJsonValue v = String(); v.ObjectValue["minLength"] = McpJsonValue.From(1d); return v; }
        private static McpJsonValue Integer(int minimum, int maximum) { McpJsonValue v = Type("integer"); v.ObjectValue["minimum"] = McpJsonValue.From((double)minimum); v.ObjectValue["maximum"] = McpJsonValue.From((double)maximum); return v; }
        private static McpJsonValue ArrayOf(McpJsonValue item) { McpJsonValue v = Type("array"); v.ObjectValue["items"] = item; return v; }
        private static McpJsonValue Vector3() { return CoordinateObject(new[] { "x", "y", "z" }); }
        private static McpJsonValue Quaternion() { return CoordinateObject(new[] { "x", "y", "z", "w" }); }
        private static McpJsonValue CoordinateObject(string[] names)
        {
            KeyValuePair<string, McpJsonValue>[] props = new KeyValuePair<string, McpJsonValue>[names.Length];
            for (int i = 0; i < names.Length; i++) props[i] = Prop(names[i], Type("number"));
            return SchemaRequired(names, props);
        }
        private static McpJsonValue CopyObject(McpJsonValue source)
        {
            McpJsonValue copy = McpJsonValue.Object();
            foreach (KeyValuePair<string, McpJsonValue> pair in source.ObjectValue) copy.ObjectValue[pair.Key] = pair.Value;
            return copy;
        }
    }
}
