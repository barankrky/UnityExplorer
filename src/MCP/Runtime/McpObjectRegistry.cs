using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace UnityExplorer.MCP.Runtime
{
    /// <summary>Stable session-local IDs. Unity objects are keyed by native instance ID for IL2CPP wrapper stability.</summary>
    public sealed class McpObjectRegistry
    {
        private readonly object sync = new object();
        private readonly Dictionary<string, object> byId = new Dictionary<string, object>(StringComparer.Ordinal);
        private readonly Dictionary<object, string> managedIds = new Dictionary<object, string>(ReferenceComparer.Instance);
        private readonly Dictionary<int, string> unityIds = new Dictionary<int, string>();
        private long nextId = 1;

        public int Count { get { lock (sync) return byId.Count; } }

        public string Register(object value)
        {
            if (value == null || IsDestroyed(value)) return null;
            UnityEngine.Object unityObject = value as UnityEngine.Object;
            lock (sync)
            {
                if (!ReferenceEquals(unityObject, null))
                {
                    int instanceId = unityObject.GetInstanceID(); string existing; object previous;
                    if (unityIds.TryGetValue(instanceId, out existing) && byId.TryGetValue(existing, out previous))
                    {
                        if (!IsDestroyed(previous)) { byId[existing] = value; return existing; }
                        RemoveInternal(existing, previous);
                    }
                    string id = "u" + instanceId.ToString(System.Globalization.CultureInfo.InvariantCulture) + "-" + NextSuffix();
                    unityIds[instanceId] = id; byId[id] = value; return id;
                }

                string managed;
                if (managedIds.TryGetValue(value, out managed) && byId.ContainsKey(managed)) return managed;
                managed = "m" + NextSuffix(); managedIds[value] = managed; byId[managed] = value; return managed;
            }
        }

        public bool TryResolve(string id, out object value)
        {
            value = null; if (string.IsNullOrEmpty(id)) return false;
            lock (sync)
            {
                if (!byId.TryGetValue(id, out value)) return false;
                if (value == null || IsDestroyed(value)) { RemoveInternal(id, value); value = null; return false; }
                return true;
            }
        }

        public object Resolve(string id)
        {
            object value; if (!TryResolve(id, out value)) throw new McpCommandException("object_not_found", "Object ID is unknown or the Unity object was destroyed: " + id); return value;
        }

        public bool Forget(string id)
        {
            if (string.IsNullOrEmpty(id)) return false;
            lock (sync) { object value; return byId.TryGetValue(id, out value) && RemoveInternal(id, value); }
        }

        public int Prune()
        {
            lock (sync)
            {
                List<string> stale = new List<string>();
                foreach (KeyValuePair<string, object> pair in byId) if (pair.Value == null || IsDestroyed(pair.Value)) stale.Add(pair.Key);
                for (int i = 0; i < stale.Count; i++) RemoveInternal(stale[i], null); return stale.Count;
            }
        }

        private string NextSuffix() { return (nextId++).ToString("x", System.Globalization.CultureInfo.InvariantCulture); }

        private bool RemoveInternal(string id, object knownTarget)
        {
            object target;
            if (!byId.TryGetValue(id, out target)) return false;
            if (knownTarget != null) target = knownTarget; byId.Remove(id);
            UnityEngine.Object unityObject = target as UnityEngine.Object;
            if (!ReferenceEquals(unityObject, null))
            {
                int instanceId = unityObject.GetInstanceID(); string mapped;
                if (unityIds.TryGetValue(instanceId, out mapped) && mapped == id) unityIds.Remove(instanceId);
            }
            else if (target != null)
            {
                string mapped; if (managedIds.TryGetValue(target, out mapped) && mapped == id) managedIds.Remove(target);
            }
            else
            {
                int key = 0; bool found = false;
                foreach (KeyValuePair<int, string> pair in unityIds) if (pair.Value == id) { key = pair.Key; found = true; break; }
                if (found) unityIds.Remove(key);
            }
            return true;
        }

        internal static bool IsDestroyed(object value)
        {
            UnityEngine.Object unityObject = value as UnityEngine.Object;
            return value != null && !ReferenceEquals(unityObject, null) && !unityObject;
        }

        private sealed class ReferenceComparer : IEqualityComparer<object>
        {
            internal static readonly ReferenceComparer Instance = new ReferenceComparer();
            public new bool Equals(object x, object y) { return ReferenceEquals(x, y); }
            public int GetHashCode(object obj) { return RuntimeHelpers.GetHashCode(obj); }
        }
    }

    public sealed class McpGameExecutorOptions
    {
        public McpGameExecutorOptions()
        {
            MaximumSearchResults = 200; MaximumSnapshotDepth = 3; MaximumSerializedItems = 256; MaximumMembersPerObject = 128; MaximumBatchCommands = 64;
            IncludeNonPublicMembers = true; AllowMethodInvocation = true; AllowObjectCreation = true; AllowObjectDestruction = true;
        }
        public int MaximumSearchResults { get; set; }
        public int MaximumSnapshotDepth { get; set; }
        public int MaximumSerializedItems { get; set; }
        public int MaximumMembersPerObject { get; set; }
        public int MaximumBatchCommands { get; set; }
        public bool IncludeNonPublicMembers { get; set; }
        public bool AllowMethodInvocation { get; set; }
        public bool AllowObjectCreation { get; set; }
        public bool AllowObjectDestruction { get; set; }
        internal void Validate()
        {
            if (MaximumSearchResults < 1) throw new ArgumentOutOfRangeException("MaximumSearchResults");
            if (MaximumSnapshotDepth < 0 || MaximumSnapshotDepth > 16) throw new ArgumentOutOfRangeException("MaximumSnapshotDepth");
            if (MaximumSerializedItems < 1) throw new ArgumentOutOfRangeException("MaximumSerializedItems");
            if (MaximumMembersPerObject < 1) throw new ArgumentOutOfRangeException("MaximumMembersPerObject");
            if (MaximumBatchCommands < 1) throw new ArgumentOutOfRangeException("MaximumBatchCommands");
        }
    }

    public sealed class McpCommandException : Exception
    {
        public McpCommandException(string code, string message) : base(message) { Code = code; }
        public McpCommandException(string code, string message, Exception inner) : base(message, inner) { Code = code; }
        public string Code { get; private set; }
    }
}
