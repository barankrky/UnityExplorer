using System;
using System.Reflection;
using UnityEngine;

namespace UnityExplorer.MCP.Runtime
{
    public sealed partial class McpGameCapabilityExecutor
    {
        private McpJsonValue SetMember(McpJsonValue command)
        {
            object target; Type type; ResolveTarget(command, out target, out type); string path = RequiredString(command, "member"); McpJsonValue input;
            if (!command.TryGet("value", out input)) throw new McpCommandException("invalid_command", "The 'value' property is required.");
            object value = McpMemberPath.Write(target, type, path, input, codec, options.IncludeNonPublicMembers);
            McpJsonValue r = McpJsonValue.Object(); r.ObjectValue["member"] = McpJsonValue.From(path); r.ObjectValue["value"] = codec.Serialize(value, 1, 32); return r;
        }

        private McpJsonValue SetTransform(McpJsonValue command)
        {
            object resolved = registry.Resolve(RequiredString(command, "objectId")); GameObject go = resolved as GameObject; Component component = resolved as Component; Transform t = resolved as Transform;
            if (t == null && go != null) t = go.transform; if (t == null && component != null) t = component.transform; if (t == null) throw new McpCommandException("invalid_target", "Target does not have a Transform.");
            McpJsonValue v;
            if (command.TryGet("parentId", out v))
            {
                Transform parent = null;
                if (!v.IsNull) { object p = registry.Resolve(v.StringValue); GameObject pg = p as GameObject; Component pc = p as Component; parent = p as Transform; if (parent == null && pg != null) parent = pg.transform; if (parent == null && pc != null) parent = pc.transform; if (parent == null) throw new McpCommandException("invalid_target", "parentId is not a Transform or GameObject."); }
                t.SetParent(parent, command.GetBoolean("worldPositionStays", true));
            }
            if (command.TryGet("position", out v)) t.position = (Vector3)codec.ConvertTo(v, typeof(Vector3)); if (command.TryGet("localPosition", out v)) t.localPosition = (Vector3)codec.ConvertTo(v, typeof(Vector3));
            if (command.TryGet("rotation", out v)) t.rotation = (Quaternion)codec.ConvertTo(v, typeof(Quaternion)); if (command.TryGet("localRotation", out v)) t.localRotation = (Quaternion)codec.ConvertTo(v, typeof(Quaternion));
            if (command.TryGet("eulerAngles", out v)) t.eulerAngles = (Vector3)codec.ConvertTo(v, typeof(Vector3)); if (command.TryGet("localEulerAngles", out v)) t.localEulerAngles = (Vector3)codec.ConvertTo(v, typeof(Vector3)); if (command.TryGet("localScale", out v)) t.localScale = (Vector3)codec.ConvertTo(v, typeof(Vector3));
            return TransformSummary(t);
        }

        private McpJsonValue SetEnabled(McpJsonValue command)
        {
            object target = registry.Resolve(RequiredString(command, "objectId")); bool enabled = RequiredBoolean(command, "enabled"); GameObject go = target as GameObject;
            if (go != null) { go.SetActive(enabled); return ObjectSummary(go, go); }
            Behaviour behaviour = target as Behaviour; if (behaviour != null) { behaviour.enabled = enabled; return ObjectSummary(behaviour, behaviour.gameObject); }
            MemberInfo member = McpReflection.FindWritableMember(McpReflection.GetActualType(target), "enabled", options.IncludeNonPublicMembers);
            if (member == null || McpReflection.GetMemberType(member) != typeof(bool)) throw new McpCommandException("invalid_target", "Target has no writable bool enabled property.");
            McpReflection.SetMemberValue(member, target, enabled); return codec.Serialize(target, 1, 32);
        }

        private McpJsonValue TransformSummary(Transform t)
        {
            McpJsonValue r = McpJsonValue.Object(); r.ObjectValue["objectId"] = McpJsonValue.From(registry.Register(t)); r.ObjectValue["gameObjectId"] = McpJsonValue.From(registry.Register(t.gameObject));
            r.ObjectValue["position"] = codec.Serialize(t.position, 0, 16); r.ObjectValue["localPosition"] = codec.Serialize(t.localPosition, 0, 16); r.ObjectValue["rotation"] = codec.Serialize(t.rotation, 0, 16); r.ObjectValue["localRotation"] = codec.Serialize(t.localRotation, 0, 16);
            r.ObjectValue["eulerAngles"] = codec.Serialize(t.eulerAngles, 0, 16); r.ObjectValue["localEulerAngles"] = codec.Serialize(t.localEulerAngles, 0, 16); r.ObjectValue["localScale"] = codec.Serialize(t.localScale, 0, 16); r.ObjectValue["parentId"] = McpJsonValue.From(t.parent == null ? null : registry.Register(t.parent)); return r;
        }
    }
}
