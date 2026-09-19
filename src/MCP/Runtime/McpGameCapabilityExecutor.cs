using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityExplorer.MCP.Transport;
using UnityExplorer.Config;

namespace UnityExplorer.MCP.Runtime
{
    /// <summary>Main-thread Unity game capability executor.</summary>
    public sealed partial class McpGameCapabilityExecutor : IMcpRequestHandler
    {
        private static readonly string[] RoutedMethods = { "game.execute", "game.status", "game.list_scenes", "game.search", "game.snapshot", "game.get_member", "game.list_methods", "game.set_member", "game.set_transform", "game.invoke", "game.set_enabled", "game.create", "game.destroy", "game.batch", "get_status", "list_scenes", "search_objects", "get_object", "get_member", "list_methods", "set_member", "set_transform", "invoke_method", "set_enabled", "create_object", "destroy_object", "execute_batch" };
        private readonly McpObjectRegistry registry;
        private readonly McpGameExecutorOptions options;
        private readonly McpValueCodec codec;

        public McpGameCapabilityExecutor() : this(new McpObjectRegistry(), new McpGameExecutorOptions()) { }
        public McpGameCapabilityExecutor(McpObjectRegistry registry, McpGameExecutorOptions options)
        {
            if (registry == null) throw new ArgumentNullException("registry");
            if (options == null) throw new ArgumentNullException("options");
            options.Validate(); this.registry = registry; this.options = options; codec = new McpValueCodec(registry, options);
        }
        public McpObjectRegistry Registry { get { return registry; } }
        public McpGameExecutorOptions Options { get { return options; } }
        public static IList<string> SupportedMethods { get { return Array.AsReadOnly(RoutedMethods); } }

        public void Register(McpRequestRouter router) { if (router == null) throw new ArgumentNullException("router"); for (int i = 0; i < RoutedMethods.Length; i++) router.Register(RoutedMethods[i], this); }
        public void Unregister(McpRequestRouter router) { if (router != null) for (int i = 0; i < RoutedMethods.Length; i++) router.Unregister(RoutedMethods[i]); }

        public string Execute(string json)
        {
            McpJsonValue command; string error;
            return !McpJsonValue.TryParse(json, out command, out error) ? Failure("invalid_json", error).ToJson() : Execute(command).ToJson();
        }
        public McpJsonValue Execute(McpJsonValue command) { return ExecuteInternal(command, 0); }

        public McpResponse Handle(McpRequest request)
        {
            if (request == null) return McpResponse.Error(-32600, "Request is required.");
            try
            {
                McpJsonValue root = McpJsonValue.Parse(request.RawJson), parameters;
                if (!root.TryGet("params", out parameters) || parameters == null || parameters.IsNull) parameters = McpJsonValue.Object();
                if (parameters.Kind != McpJsonValue.JsonKind.Object) return McpResponse.Error(-32602, "params must be a JSON object.");
                parameters = NormalizeParameters(parameters);
                if (request.Method != "game.execute") { parameters.ObjectValue["command"] = McpJsonValue.From(CommandForMethod(request.Method)); }
                return McpResponse.SuccessJson(ExecuteInternal(parameters, 0).ToJson());
            }
            catch (Exception ex) { return McpResponse.Error(-32603, McpReflection.Unwrap(ex).Message); }
        }

        private McpJsonValue ExecuteInternal(McpJsonValue command, int batchDepth)
        {
            if (command == null || command.Kind != McpJsonValue.JsonKind.Object) return Failure("invalid_command", "Command must be a JSON object.");
            command = NormalizeParameters(command);
            string name = command.GetString("command", null);
            if (string.IsNullOrEmpty(name)) return Failure("invalid_command", "The 'command' string is required.");
            name = CommandForMethod(name.Trim().ToLowerInvariant());
            try
            {
                EnforcePolicy(name);
                McpJsonValue result;
                switch (name)
                {
                    case "status": result = Status(); break; case "list_scenes": result = ListScenes(command); break;
                    case "search": result = Search(command); break; case "snapshot": result = Snapshot(command); break;
                    case "get_member": result = GetMember(command); break; case "list_methods": result = ListMethods(command); break;
                    case "set_member": result = SetMember(command); break; case "set_transform": result = SetTransform(command); break;
                    case "invoke": result = Invoke(command); break;
                    case "set_enabled": result = SetEnabled(command); break; case "create": result = Create(command); break;
                    case "destroy": result = Destroy(command); break; case "batch": result = Batch(command, batchDepth); break;
                    default: return Failure("unknown_command", "Unsupported command: " + name);
                }
                return Success(name, result);
            }
            catch (McpCommandException ex) { return Failure(ex.Code, ex.Message); }
            catch (Exception ex) { Exception actual = McpReflection.Unwrap(ex); return Failure("execution_failed", actual.GetType().Name + ": " + actual.Message); }
        }

        private McpJsonValue Status()
        {
            int removed = registry.Prune(); McpJsonValue r = McpJsonValue.Object();
            r.ObjectValue["ready"] = McpJsonValue.From(true); r.ObjectValue["mainThreadRequired"] = McpJsonValue.From(true);
            r.ObjectValue["unityVersion"] = McpJsonValue.From(Application.unityVersion); r.ObjectValue["platform"] = McpJsonValue.From(Application.platform.ToString());
            r.ObjectValue["application"] = McpJsonValue.From(Application.productName); r.ObjectValue["sceneCount"] = McpJsonValue.From((double)SceneManager.sceneCount);
            r.ObjectValue["registryCount"] = McpJsonValue.From((double)registry.Count); r.ObjectValue["prunedObjectIds"] = McpJsonValue.From((double)removed);
#if CPP
            r.ObjectValue["runtime"] = McpJsonValue.From("IL2CPP");
#else
            r.ObjectValue["runtime"] = McpJsonValue.From("Mono");
#endif
            r.ObjectValue["capabilities"] = McpJsonValue.FromObject(new string[] { "scenes", "search", "snapshot", "fields", "properties", "transform", "methods", "enable", "create", "destroy", "batch" }); return r;
        }
        private McpJsonValue Batch(McpJsonValue command, int depth)
        {
            if (depth >= 4) throw new McpCommandException("batch_depth", "Nested batches are limited to depth 4.");
            McpJsonValue atomic;
            if (command.TryGet("atomic", out atomic))
            {
                if (atomic.Kind != McpJsonValue.JsonKind.Boolean) throw new McpCommandException("invalid_command", "atomic must be a boolean.");
                if (atomic.BooleanValue) throw new McpCommandException("atomic_not_supported", "atomic=true is not supported because the game bridge cannot roll back Unity mutations.");
            }
            McpJsonValue commands;
            if (!command.TryGet("commands", out commands))
            {
                McpJsonValue operations;
                if (!command.TryGet("operations", out operations) || operations.Kind != McpJsonValue.JsonKind.Array) throw new McpCommandException("invalid_command", "commands or operations must be an array.");
                commands = ConvertOperations(operations);
            }
            if (commands.Kind != McpJsonValue.JsonKind.Array) throw new McpCommandException("invalid_command", "commands must be an array.");
            if (commands.ArrayValue.Count > options.MaximumBatchCommands) throw new McpCommandException("batch_limit", "Batch exceeds the configured command limit.");
            bool stop = command.GetBoolean("stopOnError", true); McpJsonValue results = McpJsonValue.Array();
            for (int i = 0; i < commands.ArrayValue.Count; i++)
            {
                McpJsonValue item = ExecuteInternal(commands.ArrayValue[i], depth + 1); results.ArrayValue.Add(item); McpJsonValue ok;
                if (stop && item.TryGet("ok", out ok) && ok.Kind == McpJsonValue.JsonKind.Boolean && !ok.BooleanValue) break;
            }
            McpJsonValue r = McpJsonValue.Object(); r.ObjectValue["results"] = results; r.ObjectValue["executed"] = McpJsonValue.From((double)results.ArrayValue.Count); r.ObjectValue["requested"] = McpJsonValue.From((double)commands.ArrayValue.Count); return r;
        }

        private McpJsonValue ConvertOperations(McpJsonValue operations)
        {
            McpJsonValue commands = McpJsonValue.Array();
            for (int i = 0; i < operations.ArrayValue.Count; i++)
            {
                McpJsonValue operation = operations.ArrayValue[i];
                if (operation == null || operation.Kind != McpJsonValue.JsonKind.Object) throw new McpCommandException("invalid_command", "operations[" + i + "] must be an object.");
                string method = operation.GetString("method", null);
                if (string.IsNullOrEmpty(method)) throw new McpCommandException("invalid_command", "operations[" + i + "].method is required.");
                string commandName = CommandForMethod(method.Trim().ToLowerInvariant());
                if (commandName == "batch" || commandName == "execute") throw new McpCommandException("invalid_command", "Nested execute_batch operations are not supported.");
                McpJsonValue parameters;
                if (!operation.TryGet("params", out parameters) || parameters == null || parameters.IsNull) parameters = McpJsonValue.Object();
                if (parameters.Kind != McpJsonValue.JsonKind.Object) throw new McpCommandException("invalid_command", "operations[" + i + "].params must be an object.");
                McpJsonValue converted = NormalizeParameters(parameters);
                converted.ObjectValue["command"] = McpJsonValue.From(commandName);
                commands.ArrayValue.Add(converted);
            }
            return commands;
        }

        private McpJsonValue ListMethods(McpJsonValue command)
        {
            object target; Type type; ResolveTarget(command, out target, out type);
            string filter = command.GetString("filter", null);
            int limit = command.GetInt32("limit", options.MaximumMembersPerObject, 1, options.MaximumSerializedItems);
            bool includeNonPublic = options.IncludeNonPublicMembers && command.GetBoolean("includeNonPublic", true);
            bool includeInherited = command.GetBoolean("includeInherited", true);
            bool includeStatic = command.GetBoolean("includeStatic", true);
            bool includeConstructors = command.GetBoolean("includeConstructors", false);
            BindingFlags flags = McpReflection.Flags(includeNonPublic);
            // DeclaredOnly must be combined with Instance/Static, otherwise the binding returns
            // nothing. Previously includeInherited=false was silently ignored and callers still
            // received every inherited method, drowning the game's own API in IL2CPP plumbing.
            if (!includeInherited) flags = (flags & ~BindingFlags.FlattenHierarchy) | BindingFlags.DeclaredOnly;
            MethodInfo[] methods = type.GetMethods(flags);
            // The Inspector exposes constructors alongside methods; without this an agent has no
            // way to discover how to build a type. ConstructorInfo is not a MethodInfo, so the
            // combined list is typed as MethodBase.
            List<MethodBase> methodList = new List<MethodBase>(methods.Length);
            for (int i = 0; i < methods.Length; i++) methodList.Add(methods[i]);
            if (includeConstructors)
            {
                ConstructorInfo[] constructors = type.GetConstructors(flags);
                for (int i = 0; i < constructors.Length; i++) methodList.Add(constructors[i]);
            }
            methodList.Sort(delegate(MethodBase a, MethodBase b) { return string.CompareOrdinal(MethodSignature(a), MethodSignature(b)); });
            McpJsonValue entries = McpJsonValue.Array();
            for (int i = 0; i < methodList.Count && entries.ArrayValue.Count < limit; i++)
            {
                MethodBase method = methodList[i];
                if (method.ContainsGenericParameters || McpReflection.IsUnsafeMember(method) || (target == null && !method.IsStatic) || (!includeStatic && method.IsStatic)) continue;
                if (!string.IsNullOrEmpty(filter) && method.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                McpJsonValue entry = McpJsonValue.Object();
                entry.ObjectValue["name"] = McpJsonValue.From(method.Name);
                entry.ObjectValue["kind"] = McpJsonValue.From(method is ConstructorInfo ? "constructor" : "method");
                entry.ObjectValue["signature"] = McpJsonValue.From(MethodSignature(method));
                MethodInfo asMethod = method as MethodInfo;
                entry.ObjectValue["returnType"] = McpJsonValue.From(asMethod == null ? null : asMethod.ReturnType.FullName);
                entry.ObjectValue["declaredBy"] = McpJsonValue.From(method.DeclaringType == null ? null : method.DeclaringType.FullName);
                entry.ObjectValue["inherited"] = McpJsonValue.From(method.DeclaringType != type);
                entry.ObjectValue["static"] = McpJsonValue.From(method.IsStatic);
                entry.ObjectValue["public"] = McpJsonValue.From(method.IsPublic);
                McpJsonValue parameters = McpJsonValue.Array();
                ParameterInfo[] methodParameters = method.GetParameters();
                for (int p = 0; p < methodParameters.Length; p++)
                {
                    ParameterInfo parameter = methodParameters[p];
                    McpJsonValue parameterEntry = McpJsonValue.Object();
                    parameterEntry.ObjectValue["name"] = McpJsonValue.From(parameter.Name);
                    parameterEntry.ObjectValue["type"] = McpJsonValue.From(parameter.ParameterType.FullName);
                    parameterEntry.ObjectValue["optional"] = McpJsonValue.From(parameter.IsOptional);
                    parameterEntry.ObjectValue["out"] = McpJsonValue.From(parameter.IsOut);
                    parameters.ArrayValue.Add(parameterEntry);
                }
                entry.ObjectValue["parameters"] = parameters;
                entries.ArrayValue.Add(entry);
            }
            McpJsonValue result = McpJsonValue.Object();
            result.ObjectValue["type"] = McpJsonValue.From(type.FullName);
            result.ObjectValue["methods"] = entries;
            result.ObjectValue["count"] = McpJsonValue.From((double)entries.ArrayValue.Count);
            result.ObjectValue["truncated"] = McpJsonValue.From(entries.ArrayValue.Count >= limit && methods.Length > entries.ArrayValue.Count);
            return result;
        }
        private void ResolveTarget(McpJsonValue command, out object target, out Type type)
        {
            string id = command.GetString("objectId", null), typeName = command.GetString("type", null);
            // Unity IL2CPP APIs frequently return a Component through a base wrapper such as
            // UnityEngine.Behaviour. GetActualType asks UniverseLib for the native IL2CPP class,
            // otherwise members declared by the generated Board wrapper are invisible here.
            if (!string.IsNullOrEmpty(id)) { target = registry.Resolve(id); type = McpReflection.GetActualType(target); return; }
            if (!string.IsNullOrEmpty(typeName)) { target = null; type = McpReflection.FindType(typeName); return; }
            throw new McpCommandException("invalid_target", "Either objectId or type is required.");
        }

        private static string CommandForMethod(string method)
        {
            switch (method)
            {
                case "get_status": return "status";
                case "list_scenes": return "list_scenes";
                case "search_objects": return "search";
                case "get_object": return "snapshot";
                case "get_member": return "get_member";
                case "list_methods": return "list_methods";
                case "set_member": return "set_member";
                case "set_transform": return "set_transform";
                case "invoke_method": return "invoke";
                case "set_enabled": return "set_enabled";
                case "create_object": return "create";
                case "destroy_object": return "destroy";
                case "execute_batch": return "batch";
                default: return method.StartsWith("game.", StringComparison.Ordinal) ? method.Substring(5) : method;
            }
        }

        private static McpJsonValue NormalizeParameters(McpJsonValue source)
        {
            McpJsonValue copy = CloneObject(source);
            CopyAlias(copy, "object_id", "objectId"); CopyAlias(copy, "id", "objectId");
            CopyAlias(copy, "type_name", "type"); CopyAlias(copy, "object_type", "type");
            CopyAlias(copy, "query", "name"); CopyAlias(copy, "max_results", "limit"); CopyAlias(copy, "exact", "exactName");
            CopyAlias(copy, "name_filter", "filter"); CopyAlias(copy, "include_non_public", "includeNonPublic");
            CopyAlias(copy, "include_inherited", "includeInherited"); CopyAlias(copy, "include_static", "includeStatic");
            CopyAlias(copy, "include_constructors", "includeConstructors");
            CopyAlias(copy, "member_name", "member"); CopyAlias(copy, "member_path", "member");
            CopyAlias(copy, "method_name", "method"); CopyAlias(copy, "arguments", "args");
            CopyAlias(copy, "stop_on_error", "stopOnError"); CopyAlias(copy, "max_depth", "depth");
            CopyAlias(copy, "max_items", "maxItems"); CopyAlias(copy, "exact_name", "exactName");
            CopyAlias(copy, "include_explorer", "includeExplorer"); CopyAlias(copy, "parent_id", "parentId");
            // The get_object schema advertises snake_case names; accept them as aliases of the
            // canonical camelCase keys so callers following the schema are not silently ignored.
            CopyAlias(copy, "include_members", "includeMembers"); CopyAlias(copy, "include_methods", "includeMethods");
            CopyAlias(copy, "member_filter", "memberFilter"); CopyAlias(copy, "max_collection_items", "maxItems");
            CopyAlias(copy, "max_items", "maxItems");
            CopyAlias(copy, "world_position_stays", "worldPositionStays");
            CopyAlias(copy, "local_position", "localPosition"); CopyAlias(copy, "local_rotation", "localRotation");
            CopyAlias(copy, "euler_angles", "eulerAngles"); CopyAlias(copy, "local_euler_angles", "localEulerAngles");
            CopyAlias(copy, "local_scale", "localScale");
            return copy;
        }

        private static void CopyAlias(McpJsonValue value, string alias, string canonical)
        {
            McpJsonValue item, existing;
            if (value.TryGet(alias, out item) && !value.TryGet(canonical, out existing)) value.ObjectValue[canonical] = item;
        }

        private static void EnforcePolicy(string command)
        {
            bool write = command == "set_member" || command == "set_transform" || command == "set_enabled" || command == "invoke" || command == "create" || command == "destroy";
            if (write && ConfigManager.MCP_Read_Only != null && ConfigManager.MCP_Read_Only.Value)
                throw new McpCommandException("read_only", "MCP is in read-only mode; mutation command blocked: " + command);
            bool dangerous = command == "invoke" || command == "create" || command == "destroy";
            if (dangerous && (ConfigManager.MCP_Allow_Dangerous_Operations == null || !ConfigManager.MCP_Allow_Dangerous_Operations.Value))
                throw new McpCommandException("dangerous_operations_disabled", "Dangerous MCP operations are disabled: " + command);
        }
        private static string RequiredString(McpJsonValue command, string name)
        {
            string value = command.GetString(name, null); if (string.IsNullOrEmpty(value)) throw new McpCommandException("invalid_command", "The '" + name + "' string is required."); return value;
        }
        private static bool RequiredBoolean(McpJsonValue command, string name)
        {
            McpJsonValue value; if (!command.TryGet(name, out value) || value.Kind != McpJsonValue.JsonKind.Boolean) throw new McpCommandException("invalid_command", "The '" + name + "' boolean is required."); return value.BooleanValue;
        }
        private static McpJsonValue Success(string command, McpJsonValue result)
        {
            McpJsonValue e = McpJsonValue.Object(); e.ObjectValue["ok"] = McpJsonValue.From(true); e.ObjectValue["command"] = McpJsonValue.From(command); e.ObjectValue["result"] = result ?? McpJsonValue.Null(); return e;
        }
        private static McpJsonValue Failure(string code, string message)
        {
            McpJsonValue error = McpJsonValue.Object(); error.ObjectValue["code"] = McpJsonValue.From(code); error.ObjectValue["message"] = McpJsonValue.From(message ?? string.Empty);
            McpJsonValue e = McpJsonValue.Object(); e.ObjectValue["ok"] = McpJsonValue.From(false); e.ObjectValue["error"] = error; return e;
        }
        private static McpJsonValue CloneObject(McpJsonValue source)
        {
            McpJsonValue copy = McpJsonValue.Object(); foreach (KeyValuePair<string, McpJsonValue> pair in source.ObjectValue) copy.ObjectValue[pair.Key] = pair.Value; return copy;
        }
    }
}
