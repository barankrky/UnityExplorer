using System;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnityExplorer.MCP.Runtime
{
    public sealed partial class McpGameCapabilityExecutor
    {
        private McpJsonValue ListScenes(McpJsonValue command)
        {
            McpJsonValue scenes = McpJsonValue.Array(); Scene active = SceneManager.GetActiveScene();
            int maximum = command.GetInt32("limit", Math.Max(1, SceneManager.sceneCount), 1, options.MaximumSearchResults);
            // Declared in the schema but never read. include_unloaded reports scenes present in the
            // build but not loaded, and include_special adds the two synthetic scenes the Object
            // Explorer shows alongside the real ones: DontDestroyOnLoad (handle -12) and the
            // HideAndDontSave asset scene (handle -1).
            bool includeUnloaded = command.GetBoolean("includeUnloaded", false);
            bool includeSpecial = command.GetBoolean("includeSpecial", false);
            for (int i = 0; i < SceneManager.sceneCount && scenes.ArrayValue.Count < maximum; i++)
            {
                Scene scene = SceneManager.GetSceneAt(i);
                scenes.ArrayValue.Add(DescribeScene(scene, active));
            }
            if (includeUnloaded)
            {
                try
                {
                    int total = SceneManager.sceneCountInBuildSettings;
                    for (int i = 0; i < total && scenes.ArrayValue.Count < maximum; i++)
                    {
                        string path = UnityEngine.SceneManagement.SceneUtility.GetScenePathByBuildIndex(i);
                        if (string.IsNullOrEmpty(path)) continue;
                        string name = System.IO.Path.GetFileNameWithoutExtension(path);
                        if (ContainsSceneNamed(scenes, name)) continue;
                        McpJsonValue item = McpJsonValue.Object();
                        item.ObjectValue["handle"] = McpJsonValue.Null(); item.ObjectValue["name"] = McpJsonValue.From(name);
                        item.ObjectValue["path"] = McpJsonValue.From(path); item.ObjectValue["buildIndex"] = McpJsonValue.From((double)i);
                        item.ObjectValue["loaded"] = McpJsonValue.From(false); item.ObjectValue["valid"] = McpJsonValue.From(true);
                        item.ObjectValue["active"] = McpJsonValue.From(false); item.ObjectValue["special"] = McpJsonValue.From(false);
                        item.ObjectValue["rootCount"] = McpJsonValue.Null();
                        scenes.ArrayValue.Add(item);
                    }
                }
                catch (Exception ex) { throw new McpCommandException("scene_enumeration_failed", "Unable to enumerate scenes in build settings: " + McpReflection.Unwrap(ex).Message); }
            }
            if (includeSpecial)
            {
                AddSpecialScene(scenes, -12, "DontDestroyOnLoad", active, maximum);
                AddSpecialScene(scenes, -1, "HideAndDontSave", active, maximum);
            }
            McpJsonValue r = McpJsonValue.Object(); r.ObjectValue["scenes"] = scenes; r.ObjectValue["count"] = McpJsonValue.From((double)scenes.ArrayValue.Count); return r;
        }

        private static bool ContainsSceneNamed(McpJsonValue scenes, string name)
        {
            for (int i = 0; i < scenes.ArrayValue.Count; i++)
            {
                McpJsonValue existing;
                if (scenes.ArrayValue[i].TryGet("name", out existing) && existing.Kind == McpJsonValue.JsonKind.String
                    && string.Equals(existing.StringValue, name, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private static void AddSpecialScene(McpJsonValue scenes, int handle, string name, Scene active, int maximum)
        {
            if (scenes.ArrayValue.Count >= maximum || ContainsSceneNamed(scenes, name)) return;
            try
            {
                // The synthetic scenes are addressed by their fixed handle rather than enumerated.
                Scene scene = UnityHelpers.CreateSceneFromIntHandle(handle);
                if (!scene.IsValid()) return;
                McpJsonValue item = DescribeScene(scene, active);
                item.ObjectValue["special"] = McpJsonValue.From(true);
                scenes.ArrayValue.Add(item);
            }
            catch { }
        }

        private static McpJsonValue DescribeScene(Scene scene, Scene active)
        {
            McpJsonValue item = McpJsonValue.Object();
            // Scene.handle is an int on Unity <= 6.2 and a SceneHandle struct on 6.3+.
            // GetSceneIntHandle() normalizes both to an int.
            item.ObjectValue["handle"] = McpJsonValue.From((double)scene.GetSceneIntHandle()); item.ObjectValue["name"] = McpJsonValue.From(scene.name);
            item.ObjectValue["path"] = McpJsonValue.From(scene.path); item.ObjectValue["buildIndex"] = McpJsonValue.From((double)scene.buildIndex);
            item.ObjectValue["loaded"] = McpJsonValue.From(scene.isLoaded); item.ObjectValue["valid"] = McpJsonValue.From(scene.IsValid()); item.ObjectValue["active"] = McpJsonValue.From(scene == active);
            try { item.ObjectValue["rootCount"] = McpJsonValue.From((double)RuntimeHelper.GetRootGameObjects(scene).Count()); } catch { item.ObjectValue["rootCount"] = McpJsonValue.Null(); }
            return item;
        }

        private McpJsonValue Search(McpJsonValue command)
        {
            Type type = McpReflection.FindType(command.GetString("type", "UnityEngine.GameObject"));
            if (!typeof(UnityEngine.Object).IsAssignableFrom(type)) throw new McpCommandException("invalid_type", "Search type must derive from UnityEngine.Object.");
            int limit = command.GetInt32("limit", 50, 1, options.MaximumSearchResults); string name = command.GetString("name", null), sceneName = command.GetString("scene", null), path = command.GetString("path", null);
            bool exact = command.GetBoolean("exactName", false), includeExplorer = command.GetBoolean("includeExplorer", false);
            // These were declared in the tool schema but never read, so callers filtering by kind or
            // asking for inactive objects got the unfiltered result set instead.
            bool includeInactive = command.GetBoolean("includeInactive", true);
            string kind = NormalizeKind(command.GetString("kind", null));
            UnityEngine.Object[] objects = RuntimeHelper.FindObjectsOfTypeAll(type); McpJsonValue matches = McpJsonValue.Array();
            for (int i = 0; i < objects.Length && matches.ArrayValue.Count < limit; i++)
            {
                UnityEngine.Object obj = objects[i]; if (obj == null || !Matches(obj.name, name, exact)) continue;
                // `as GameObject` is a plain CLR cast and fails for IL2CPP objects whose managed
                // wrapper is not exactly GameObject, which left `go` null and skipped every
                // filter below (letting UnityExplorer's own UI objects through). TryCast performs
                // the native interop cast, matching SearchProvider's behaviour.
                GameObject go = null; Component component = null;
                Type actualType = McpReflection.GetActualType(obj);
                if (actualType == typeof(GameObject)) go = obj.TryCast<GameObject>();
                else if (typeof(Component).IsAssignableFrom(actualType)) { component = obj.TryCast<Component>(); if (component != null) go = component.gameObject; }
                if (kind != null && !MatchesKind(kind, obj, actualType, go)) continue;
                if (go != null)
                {
                    if (!includeExplorer && McpReflection.IsExplorerObject(go)) continue;
                    if (!includeInactive && !go.activeInHierarchy) continue;
                    if (!string.IsNullOrEmpty(sceneName) && !string.Equals(go.scene.name, sceneName, StringComparison.OrdinalIgnoreCase) && go.scene.GetSceneIntHandle().ToString() != sceneName) continue;
                    string objectPath = McpReflection.GetGameObjectPath(go); if (!string.IsNullOrEmpty(path) && objectPath.IndexOf(path, StringComparison.OrdinalIgnoreCase) < 0) continue;
                }
                matches.ArrayValue.Add(ObjectSummary(obj, go));
            }
            McpJsonValue r = McpJsonValue.Object(); r.ObjectValue["objects"] = matches; r.ObjectValue["count"] = McpJsonValue.From((double)matches.ArrayValue.Count); r.ObjectValue["scanned"] = McpJsonValue.From((double)objects.Length); r.ObjectValue["limited"] = McpJsonValue.From(matches.ArrayValue.Count >= limit); return r;
        }

        /// <summary>
        /// Normalise the coarse kind filter, rejecting values that are not understood rather than
        /// silently returning everything.
        /// </summary>
        private static string NormalizeKind(string kind)
        {
            if (string.IsNullOrEmpty(kind)) return null;
            string value = kind.Trim().ToLowerInvariant();
            if (value == "gameobject" || value == "gameobjects") return "gameobject";
            if (value == "component" || value == "components" || value == "behaviour") return "component";
            if (value == "scriptableobject" || value == "scriptableobjects") return "scriptableobject";
            if (value == "asset" || value == "assets") return "asset";
            throw new McpCommandException("invalid_kind", "Unknown kind: " + kind + ". Expected gameobject, component, scriptableobject or asset.");
        }

        private static bool MatchesKind(string kind, UnityEngine.Object obj, Type actualType, GameObject go)
        {
            switch (kind)
            {
                case "gameobject": return actualType == typeof(GameObject);
                case "component": return typeof(Component).IsAssignableFrom(actualType);
                case "scriptableobject": return obj is ScriptableObject;
                case "asset":
                    // Objects that belong to no loaded scene: assets, resources, prefabs.
                    return go == null || string.IsNullOrEmpty(go.scene.name);
                default: return true;
            }
        }

        private McpJsonValue Snapshot(McpJsonValue command)
        {
            // Accept either an instance or a type. A type-only call inspects statics, matching the
            // Inspector's [S] tab, and is the only way to read a class without an instance.
            object target; Type type; ResolveTarget(command, out target, out type);
            string objectId = command.GetString("objectId", null);
            // include_static mirrors the Inspector's All/Instance/Static scope control. A type-only
            // request has no instance to read from, so statics must be included for it to return
            // anything at all.
            bool includeStatic = command.GetBoolean("includeStatic", options.IncludeStaticMembers || target == null);
            McpGameExecutorOptions effective = options;
            if (includeStatic != options.IncludeStaticMembers)
            {
                effective = options.Clone();
                effective.IncludeStaticMembers = includeStatic;
            }
            int depth = command.GetInt32("depth", 2, 0, effective.MaximumSnapshotDepth), maxItems = command.GetInt32("maxItems", effective.MaximumSerializedItems, 1, effective.MaximumSerializedItems);
            // The filter is applied during serialization, before the per-object member cap, so it
            // can select members that alphabetical truncation would otherwise discard.
            McpJsonValue result = new McpValueCodec(registry, effective).Serialize(target, type, depth, maxItems, command.GetString("memberFilter", null));
            if (result != null && result.Kind == McpJsonValue.JsonKind.Object)
            {
                // These options are part of the get_object contract; honour them rather than
                // returning the same payload regardless of what the caller asked for.
                bool includeMembers = command.GetBoolean("includeMembers", true);
                if (!includeMembers) { result.ObjectValue.Remove("members"); result.ObjectValue.Remove("memberInfo"); }
                if (command.GetBoolean("includeMethods", false))
                {
                    // Reuse the list_methods path so the two surfaces cannot drift apart.
                    McpJsonValue listing = McpJsonValue.Object();
                    if (!string.IsNullOrEmpty(objectId)) listing.ObjectValue["objectId"] = McpJsonValue.From(objectId);
                    else listing.ObjectValue["type"] = McpJsonValue.From(type.FullName);
                    listing.ObjectValue["limit"] = McpJsonValue.From((double)effective.MaximumMembersPerObject);
                    McpJsonValue methods;
                    if (ListMethods(listing).TryGet("methods", out methods)) result.ObjectValue["methods"] = methods;
                }
            }
            return result;
        }

        private McpJsonValue GetMember(McpJsonValue command)
        {
            object target; Type type; ResolveTarget(command, out target, out type);
            // Pass the path through unresolved so an empty or missing path is reported as
            // invalid_member_path, consistent with a malformed one, instead of the generic
            // invalid_command that RequiredString produced.
            object value = McpMemberPath.Read(target, type, command.GetString("member", null), options.IncludeNonPublicMembers);
            return codec.Serialize(value, command.GetInt32("depth", 2, 0, options.MaximumSnapshotDepth), command.GetInt32("maxItems", options.MaximumSerializedItems, 1, options.MaximumSerializedItems));
        }

        private McpJsonValue ObjectSummary(UnityEngine.Object obj, GameObject go)
        {
            McpJsonValue r = McpJsonValue.Object(); r.ObjectValue["objectId"] = McpJsonValue.From(registry.Register(obj)); r.ObjectValue["instanceId"] = McpJsonValue.From((double)obj.GetInstanceID()); r.ObjectValue["name"] = McpJsonValue.From(obj.name); r.ObjectValue["type"] = McpJsonValue.From(McpReflection.GetActualType(obj).FullName);
            if (go != null) { r.ObjectValue["gameObjectId"] = McpJsonValue.From(registry.Register(go)); r.ObjectValue["path"] = McpJsonValue.From(McpReflection.GetGameObjectPath(go)); r.ObjectValue["scene"] = McpJsonValue.From(go.scene.name); r.ObjectValue["activeSelf"] = McpJsonValue.From(go.activeSelf); r.ObjectValue["activeInHierarchy"] = McpJsonValue.From(go.activeInHierarchy); }
            return r;
        }

        private static bool Matches(string candidate, string filter, bool exact)
        {
            if (string.IsNullOrEmpty(filter)) return true; candidate = candidate ?? string.Empty;
            return exact ? string.Equals(candidate, filter, StringComparison.OrdinalIgnoreCase) : candidate.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
