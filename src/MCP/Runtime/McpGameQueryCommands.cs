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
            for (int i = 0; i < SceneManager.sceneCount && scenes.ArrayValue.Count < maximum; i++)
            {
                Scene scene = SceneManager.GetSceneAt(i); McpJsonValue item = McpJsonValue.Object();
                // Scene.handle is an int on Unity <= 6.2 and a SceneHandle struct on 6.3+.
                // GetSceneIntHandle() normalizes both to an int.
                item.ObjectValue["handle"] = McpJsonValue.From((double)scene.GetSceneIntHandle()); item.ObjectValue["name"] = McpJsonValue.From(scene.name);
                item.ObjectValue["path"] = McpJsonValue.From(scene.path); item.ObjectValue["buildIndex"] = McpJsonValue.From((double)scene.buildIndex);
                item.ObjectValue["loaded"] = McpJsonValue.From(scene.isLoaded); item.ObjectValue["valid"] = McpJsonValue.From(scene.IsValid()); item.ObjectValue["active"] = McpJsonValue.From(scene == active);
                try { item.ObjectValue["rootCount"] = McpJsonValue.From((double)RuntimeHelper.GetRootGameObjects(scene).Count()); } catch { item.ObjectValue["rootCount"] = McpJsonValue.Null(); }
                scenes.ArrayValue.Add(item);
            }
            McpJsonValue r = McpJsonValue.Object(); r.ObjectValue["scenes"] = scenes; r.ObjectValue["count"] = McpJsonValue.From((double)scenes.ArrayValue.Count); return r;
        }

        private McpJsonValue Search(McpJsonValue command)
        {
            Type type = McpReflection.FindType(command.GetString("type", "UnityEngine.GameObject"));
            if (!typeof(UnityEngine.Object).IsAssignableFrom(type)) throw new McpCommandException("invalid_type", "Search type must derive from UnityEngine.Object.");
            int limit = command.GetInt32("limit", 50, 1, options.MaximumSearchResults); string name = command.GetString("name", null), sceneName = command.GetString("scene", null), path = command.GetString("path", null);
            bool exact = command.GetBoolean("exactName", false), includeExplorer = command.GetBoolean("includeExplorer", false);
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
                if (go != null)
                {
                    if (!includeExplorer && McpReflection.IsExplorerObject(go)) continue;
                    if (!string.IsNullOrEmpty(sceneName) && !string.Equals(go.scene.name, sceneName, StringComparison.OrdinalIgnoreCase) && go.scene.GetSceneIntHandle().ToString() != sceneName) continue;
                    string objectPath = McpReflection.GetGameObjectPath(go); if (!string.IsNullOrEmpty(path) && objectPath.IndexOf(path, StringComparison.OrdinalIgnoreCase) < 0) continue;
                }
                matches.ArrayValue.Add(ObjectSummary(obj, go));
            }
            McpJsonValue r = McpJsonValue.Object(); r.ObjectValue["objects"] = matches; r.ObjectValue["count"] = McpJsonValue.From((double)matches.ArrayValue.Count); r.ObjectValue["scanned"] = McpJsonValue.From((double)objects.Length); r.ObjectValue["limited"] = McpJsonValue.From(matches.ArrayValue.Count >= limit); return r;
        }

        private McpJsonValue Snapshot(McpJsonValue command)
        {
            string objectId = RequiredString(command, "objectId");
            object target = registry.Resolve(objectId);
            int depth = command.GetInt32("depth", 2, 0, options.MaximumSnapshotDepth), maxItems = command.GetInt32("maxItems", options.MaximumSerializedItems, 1, options.MaximumSerializedItems);
            McpJsonValue result = codec.Serialize(target, depth, maxItems);
            if (result != null && result.Kind == McpJsonValue.JsonKind.Object)
            {
                // These options are part of the get_object contract; honour them rather than
                // returning the same payload regardless of what the caller asked for.
                bool includeMembers = command.GetBoolean("includeMembers", true);
                if (!includeMembers) result.ObjectValue.Remove("members");
                else FilterMembers(result, command.GetString("memberFilter", null));
                if (command.GetBoolean("includeMethods", false))
                {
                    // Reuse the list_methods path so the two surfaces cannot drift apart.
                    McpJsonValue listing = McpJsonValue.Object();
                    listing.ObjectValue["objectId"] = McpJsonValue.From(objectId);
                    listing.ObjectValue["limit"] = McpJsonValue.From((double)options.MaximumMembersPerObject);
                    McpJsonValue methods;
                    if (ListMethods(listing).TryGet("methods", out methods)) result.ObjectValue["methods"] = methods;
                }
            }
            return result;
        }

        private static void FilterMembers(McpJsonValue snapshot, string filter)
        {
            if (string.IsNullOrEmpty(filter)) return;
            McpJsonValue members;
            if (!snapshot.TryGet("members", out members) || members == null || members.Kind != McpJsonValue.JsonKind.Object) return;
            List<string> discarded = new List<string>();
            foreach (KeyValuePair<string, McpJsonValue> pair in members.ObjectValue)
                if (pair.Key.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) discarded.Add(pair.Key);
            for (int i = 0; i < discarded.Count; i++) members.ObjectValue.Remove(discarded[i]);
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
