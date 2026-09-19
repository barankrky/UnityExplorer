using System;
using UnityEngine;

namespace UnityExplorer.MCP.Runtime
{
    public sealed partial class McpGameCapabilityExecutor
    {
        private McpJsonValue Create(McpJsonValue command)
        {
            if (!options.AllowObjectCreation) throw new McpCommandException("disabled", "Object creation is disabled by policy.");
            string name = command.GetString("name", "MCP GameObject"), primitiveName = command.GetString("primitive", null); GameObject go;
            if (!string.IsNullOrEmpty(primitiveName))
            {
                PrimitiveType primitive; try { primitive = (PrimitiveType)Enum.Parse(typeof(PrimitiveType), primitiveName, true); } catch { throw new McpCommandException("invalid_primitive", "Unknown PrimitiveType: " + primitiveName); }
                go = GameObject.CreatePrimitive(primitive); go.name = name;
            }
            else go = new GameObject(name);
            try
            {
                string parentId = command.GetString("parentId", null);
                if (!string.IsNullOrEmpty(parentId))
                {
                    object p = registry.Resolve(parentId); GameObject pg = p as GameObject; Component pc = p as Component; Transform parent = p as Transform;
                    if (parent == null && pg != null) parent = pg.transform; if (parent == null && pc != null) parent = pc.transform; if (parent == null) throw new McpCommandException("invalid_target", "parentId is not a Transform or GameObject.");
                    go.transform.SetParent(parent, command.GetBoolean("worldPositionStays", false));
                }
                McpJsonValue components, added = McpJsonValue.Array();
                if (command.TryGet("components", out components))
                {
                    if (components.Kind != McpJsonValue.JsonKind.Array) throw new McpCommandException("invalid_command", "components must be an array of type names.");
                    for (int i = 0; i < components.ArrayValue.Count; i++)
                    {
                        if (components.ArrayValue[i].Kind != McpJsonValue.JsonKind.String) throw new McpCommandException("invalid_command", "Each component must be a type name string.");
                        Type componentType = McpReflection.FindType(components.ArrayValue[i].StringValue); if (!typeof(Component).IsAssignableFrom(componentType)) throw new McpCommandException("invalid_type", componentType.FullName + " is not a Component.");
                        #if CPP && INTEROP
                        Component component = go.AddComponent(Il2CppInterop.Runtime.Il2CppType.From(componentType)).TryCast<Component>();
#elif CPP && UNHOLLOWER
                        Component component = go.AddComponent(UnhollowerRuntimeLib.Il2CppType.From(componentType)).Cast<Component>();
#else
                        Component component = go.AddComponent(componentType);
#endif
                        added.ArrayValue.Add(ObjectSummary(component, go));
                    }
                }
                McpJsonValue result = ObjectSummary(go, go); result.ObjectValue["componentsAdded"] = added; return result;
            }
            catch { UnityEngine.Object.Destroy(go); throw; }
        }

        private McpJsonValue Destroy(McpJsonValue command)
        {
            if (!options.AllowObjectDestruction) throw new McpCommandException("disabled", "Object destruction is disabled by policy.");
            string id = RequiredString(command, "objectId"); object target = registry.Resolve(id); UnityEngine.Object unityObject = target as UnityEngine.Object;
            if (unityObject == null) throw new McpCommandException("invalid_target", "Only UnityEngine.Object instances can be destroyed.");
            bool immediate = command.GetBoolean("immediate", false); string name = unityObject.name, type = unityObject.GetType().FullName;
            if (immediate) UnityEngine.Object.DestroyImmediate(unityObject); else UnityEngine.Object.Destroy(unityObject); registry.Forget(id);
            McpJsonValue r = McpJsonValue.Object(); r.ObjectValue["objectId"] = McpJsonValue.From(id); r.ObjectValue["name"] = McpJsonValue.From(name); r.ObjectValue["type"] = McpJsonValue.From(type); r.ObjectValue["immediate"] = McpJsonValue.From(immediate); return r;
        }
    }
}
