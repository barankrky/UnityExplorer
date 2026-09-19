using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using UnityEngine;

namespace UnityExplorer.MCP.Runtime
{
    internal sealed class McpValueCodec
    {
        private readonly McpObjectRegistry registry;
        private readonly McpGameExecutorOptions options;

        internal McpValueCodec(McpObjectRegistry registry, McpGameExecutorOptions options)
        {
            this.registry = registry;
            this.options = options;
        }

        internal McpJsonValue Serialize(object value, int requestedDepth, int requestedItems)
        {
            int depth = Clamp(requestedDepth, 0, options.MaximumSnapshotDepth);
            int items = Clamp(requestedItems, 1, options.MaximumSerializedItems);
            SerializationState state = new SerializationState(items);
            return SerializeValue(value, depth, state);
        }

        internal object ConvertTo(McpJsonValue json, Type targetType)
        {
            if (targetType == null) throw new ArgumentNullException("targetType");
            Type nullable = Nullable.GetUnderlyingType(targetType);
            if (json == null || json.IsNull)
            {
                if (!targetType.IsValueType || nullable != null) return null;
                throw new McpCommandException("conversion_failed", "null cannot be assigned to " + targetType.FullName + ".");
            }
            if (nullable != null) return ConvertTo(json, nullable);

            if (typeof(UnityEngine.Object).IsAssignableFrom(targetType))
            {
                string id = json.Kind == McpJsonValue.JsonKind.String ? json.StringValue : json.GetString("objectId", null);
                object resolved = registry.Resolve(id);
                if (!targetType.IsInstanceOfType(resolved))
                    throw new McpCommandException("conversion_failed", "Object " + id + " is not a " + targetType.FullName + ".");
                return resolved;
            }

            if (targetType == typeof(string)) return json.Kind == McpJsonValue.JsonKind.String ? json.StringValue : json.ToJson();
            if (targetType == typeof(bool)) return RequireBoolean(json, targetType);
            if (targetType == typeof(char))
            {
                string text = RequireString(json, targetType);
                if (text.Length != 1) throw Conversion(targetType);
                return text[0];
            }
            if (targetType.IsEnum)
            {
                if (json.Kind == McpJsonValue.JsonKind.String) return Enum.Parse(targetType, json.StringValue, true);
                return Enum.ToObject(targetType, Convert.ChangeType(RequireNumber(json, targetType), Enum.GetUnderlyingType(targetType), CultureInfo.InvariantCulture));
            }
            if (IsNumeric(targetType)) return Convert.ChangeType(RequireNumber(json, targetType), targetType, CultureInfo.InvariantCulture);
            if (targetType == typeof(Vector2)) return new Vector2(Float(json, "x"), Float(json, "y"));
            if (targetType == typeof(Vector3)) return new Vector3(Float(json, "x"), Float(json, "y"), Float(json, "z"));
            if (targetType == typeof(Vector4)) return new Vector4(Float(json, "x"), Float(json, "y"), Float(json, "z"), Float(json, "w"));
            if (targetType == typeof(Quaternion)) return new Quaternion(Float(json, "x"), Float(json, "y"), Float(json, "z"), Float(json, "w"));
            if (targetType == typeof(Color)) return new Color(Float(json, "r"), Float(json, "g"), Float(json, "b"), Float(json, "a", 1f));
            if (targetType == typeof(Rect)) return new Rect(Float(json, "x"), Float(json, "y"), Float(json, "width"), Float(json, "height"));
            if (targetType == typeof(LayerMask)) return (LayerMask)(int)RequireNumber(json, targetType);
            if (targetType == typeof(Type)) return McpReflection.FindType(RequireString(json, targetType));

            if (targetType.IsArray)
            {
                RequireArray(json, targetType);
                Type elementType = targetType.GetElementType();
                Array array = Array.CreateInstance(elementType, json.ArrayValue.Count);
                for (int i = 0; i < json.ArrayValue.Count; i++) array.SetValue(ConvertTo(json.ArrayValue[i], elementType), i);
                return array;
            }

            if (targetType.IsGenericType && typeof(IList).IsAssignableFrom(targetType))
            {
                RequireArray(json, targetType);
                Type elementType = targetType.GetGenericArguments()[0];
                IList list = (IList)Activator.CreateInstance(targetType);
                for (int i = 0; i < json.ArrayValue.Count; i++) list.Add(ConvertTo(json.ArrayValue[i], elementType));
                return list;
            }

            if (targetType == typeof(object)) return ConvertUntyped(json);
            if (json.Kind != McpJsonValue.JsonKind.Object) throw Conversion(targetType);

            object instance;
            try { instance = Activator.CreateInstance(targetType); }
            catch (Exception ex) { throw new McpCommandException("conversion_failed", "Cannot construct " + targetType.FullName + ".", ex); }
            foreach (KeyValuePair<string, McpJsonValue> pair in json.ObjectValue)
            {
                MemberInfo member = McpReflection.FindWritableMember(targetType, pair.Key, options.IncludeNonPublicMembers);
                if (member == null) continue;
                McpReflection.SetMemberValue(member, instance, ConvertTo(pair.Value, McpReflection.GetMemberType(member)));
            }
            return instance;
        }

        private McpJsonValue SerializeValue(object value, int depth, SerializationState state)
        {
            if (value == null || McpObjectRegistry.IsDestroyed(value)) return McpJsonValue.Null();
            Type type = McpReflection.GetActualType(value);
            if (value is string || value is char) return McpJsonValue.From(value.ToString());
            if (value is bool) return McpJsonValue.From((bool)value);
            if (value is Enum) return McpJsonValue.From(value.ToString());
            if (IsNumeric(type)) return McpJsonValue.From(Convert.ToDouble(value, CultureInfo.InvariantCulture));
            if (value is Type) return McpJsonValue.From(((Type)value).AssemblyQualifiedName);

            if (value is Vector2) { Vector2 v = (Vector2)value; return ObjectOf("x", v.x, "y", v.y); }
            if (value is Vector3) { Vector3 v = (Vector3)value; return ObjectOf("x", v.x, "y", v.y, "z", v.z); }
            if (value is Vector4) { Vector4 v = (Vector4)value; return ObjectOf("x", v.x, "y", v.y, "z", v.z, "w", v.w); }
            if (value is Quaternion) { Quaternion v = (Quaternion)value; return ObjectOf("x", v.x, "y", v.y, "z", v.z, "w", v.w); }
            if (value is Color) { Color v = (Color)value; return ObjectOf("r", v.r, "g", v.g, "b", v.b, "a", v.a); }
            if (value is Rect) { Rect v = (Rect)value; return ObjectOf("x", v.x, "y", v.y, "width", v.width, "height", v.height); }
            if (value is LayerMask) return McpJsonValue.From((double)((LayerMask)value).value);

            UnityEngine.Object unityObject = value as UnityEngine.Object;
            if (!ReferenceEquals(unityObject, null))
            {
                McpJsonValue reference = ObjectReference(unityObject);
                if (depth <= 0) return reference;
                GameObject go = unityObject as GameObject;
                Component component = unityObject as Component;
                if (go != null) AddGameObjectSummary(reference, go);
                else if (component != null) reference.ObjectValue["gameObjectId"] = McpJsonValue.From(registry.Register(component.gameObject));
                AddMembers(reference, value, type, depth - 1, state);
                return reference;
            }

            if (depth <= 0 || state.Remaining <= 0)
            {
                McpJsonValue reference = McpJsonValue.Object();
                reference.ObjectValue["objectId"] = McpJsonValue.From(registry.Register(value));
                reference.ObjectValue["type"] = McpJsonValue.From(type.FullName);
                return reference;
            }

            IEnumerable enumerable = value as IEnumerable;
            if (enumerable != null)
            {
                McpJsonValue array = McpJsonValue.Array();
                try
                {
                    foreach (object item in enumerable)
                    {
                        if (state.Remaining-- <= 0) { array.ArrayValue.Add(Truncated()); break; }
                        array.ArrayValue.Add(SerializeValue(item, depth - 1, state));
                    }
                }
                catch (Exception ex) { array.ArrayValue.Add(ErrorValue(ex)); }
                return array;
            }

            if (!type.IsValueType && !state.Visited.Add(value))
            {
                McpJsonValue cycle = McpJsonValue.Object();
                cycle.ObjectValue["objectId"] = McpJsonValue.From(registry.Register(value));
                cycle.ObjectValue["cycle"] = McpJsonValue.From(true);
                return cycle;
            }

            McpJsonValue result = McpJsonValue.Object();
            result.ObjectValue["objectId"] = McpJsonValue.From(registry.Register(value));
            result.ObjectValue["type"] = McpJsonValue.From(type.FullName);
            AddMembers(result, value, type, depth - 1, state);
            return result;
        }

        private void AddMembers(McpJsonValue target, object value, Type type, int depth, SerializationState state)
        {
            McpJsonValue members = McpJsonValue.Object();
            int count = 0;
            // Two members can differ only by case, such as a property and its backing field,
            // or a field named Method beside a property named method. Emitting both produces a
            // JSON object with keys that differ only in casing, which strict parsers reject
            // (PowerShell ConvertFrom-Json throws). Keep the first name and skip the rest.
            HashSet<string> emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (MemberInfo member in McpReflection.GetReadableMembers(type, options.IncludeNonPublicMembers))
            {
                if (!emitted.Add(member.Name)) continue;
                if (count++ >= options.MaximumMembersPerObject || state.Remaining-- <= 0) { members.ObjectValue["$truncated"] = McpJsonValue.From(true); break; }
                try { members.ObjectValue[member.Name] = SerializeValue(McpReflection.GetMemberValue(member, value), depth, state); }
                catch (Exception ex) { members.ObjectValue[member.Name] = ErrorValue(ex); }
            }
            target.ObjectValue["members"] = members;
        }

        private McpJsonValue ObjectReference(UnityEngine.Object value)
        {
            McpJsonValue result = McpJsonValue.Object();
            result.ObjectValue["objectId"] = McpJsonValue.From(registry.Register(value));
            result.ObjectValue["instanceId"] = McpJsonValue.From((double)value.GetInstanceID());
            result.ObjectValue["name"] = McpJsonValue.From(value.name);
            result.ObjectValue["type"] = McpJsonValue.From(McpReflection.GetActualType(value).FullName);
            return result;
        }

        private void AddGameObjectSummary(McpJsonValue result, GameObject go)
        {
            result.ObjectValue["activeSelf"] = McpJsonValue.From(go.activeSelf);
            result.ObjectValue["activeInHierarchy"] = McpJsonValue.From(go.activeInHierarchy);
            result.ObjectValue["scene"] = McpJsonValue.From(go.scene.name);
            result.ObjectValue["path"] = McpJsonValue.From(McpReflection.GetGameObjectPath(go));
            result.ObjectValue["transformId"] = McpJsonValue.From(registry.Register(go.transform));
        }

        private static object ConvertUntyped(McpJsonValue json)
        {
            switch (json.Kind)
            {
                case McpJsonValue.JsonKind.Null: return null;
                case McpJsonValue.JsonKind.Boolean: return json.BooleanValue;
                case McpJsonValue.JsonKind.Number: return json.NumberValue;
                case McpJsonValue.JsonKind.String: return json.StringValue;
                case McpJsonValue.JsonKind.Array:
                    List<object> list = new List<object>();
                    for (int i = 0; i < json.ArrayValue.Count; i++) list.Add(ConvertUntyped(json.ArrayValue[i]));
                    return list;
                default:
                    Dictionary<string, object> dictionary = new Dictionary<string, object>(StringComparer.Ordinal);
                    foreach (KeyValuePair<string, McpJsonValue> pair in json.ObjectValue) dictionary[pair.Key] = ConvertUntyped(pair.Value);
                    return dictionary;
            }
        }

        private static McpJsonValue ObjectOf(params object[] pairs)
        {
            McpJsonValue result = McpJsonValue.Object();
            for (int i = 0; i + 1 < pairs.Length; i += 2) result.ObjectValue[(string)pairs[i]] = McpJsonValue.FromObject(pairs[i + 1]);
            return result;
        }

        private static McpJsonValue ErrorValue(Exception ex)
        {
            McpJsonValue result = McpJsonValue.Object();
            result.ObjectValue["error"] = McpJsonValue.From(McpReflection.Unwrap(ex).Message);
            return result;
        }

        private static McpJsonValue Truncated()
        {
            McpJsonValue result = McpJsonValue.Object();
            result.ObjectValue["truncated"] = McpJsonValue.From(true);
            return result;
        }

        private static bool IsNumeric(Type type)
        {
            TypeCode code = Type.GetTypeCode(type);
            return code >= TypeCode.SByte && code <= TypeCode.Decimal;
        }

        private static double RequireNumber(McpJsonValue value, Type target)
        {
            if (value.Kind != McpJsonValue.JsonKind.Number) throw Conversion(target);
            return value.NumberValue;
        }

        private static bool RequireBoolean(McpJsonValue value, Type target)
        {
            if (value.Kind != McpJsonValue.JsonKind.Boolean) throw Conversion(target);
            return value.BooleanValue;
        }

        private static string RequireString(McpJsonValue value, Type target)
        {
            if (value.Kind != McpJsonValue.JsonKind.String) throw Conversion(target);
            return value.StringValue;
        }

        private static void RequireArray(McpJsonValue value, Type target)
        {
            if (value.Kind != McpJsonValue.JsonKind.Array) throw Conversion(target);
        }

        private static float Float(McpJsonValue value, string key) { return Float(value, key, 0f); }
        private static float Float(McpJsonValue value, string key, float defaultValue)
        {
            if (value.Kind != McpJsonValue.JsonKind.Object) throw new McpCommandException("conversion_failed", "Expected an object containing '" + key + "'.");
            McpJsonValue item;
            return value.TryGet(key, out item) && item.Kind == McpJsonValue.JsonKind.Number ? (float)item.NumberValue : defaultValue;
        }

        private static McpCommandException Conversion(Type target) { return new McpCommandException("conversion_failed", "JSON value cannot be converted to " + target.FullName + "."); }
        private static int Clamp(int value, int minimum, int maximum) { return value < minimum ? minimum : value > maximum ? maximum : value; }

        private sealed class SerializationState
        {
            internal SerializationState(int remaining) { Remaining = remaining; Visited = new HashSet<object>(ReferenceEqualityComparer.Instance); }
            internal int Remaining;
            internal readonly HashSet<object> Visited;
        }

        private sealed class ReferenceEqualityComparer : IEqualityComparer<object>
        {
            internal static readonly ReferenceEqualityComparer Instance = new ReferenceEqualityComparer();
            public new bool Equals(object x, object y) { return ReferenceEquals(x, y); }
            public int GetHashCode(object obj) { return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj); }
        }
    }

    internal static class McpReflection
    {
        // UniverseLib names its root canvas this, and UnityExplorer's panels are parented under it.
        private const string ExplorerCanvasName = "UniverseLibCanvas";

        internal static Type GetActualType(object value)
        {
            if (value == null) return null;
            try { return value.GetActualType() ?? value.GetType(); }
            catch { return value.GetType(); }
        }

        internal static BindingFlags Flags(bool nonPublic)
        {
            BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.FlattenHierarchy;
            if (nonPublic) flags |= BindingFlags.NonPublic;
            return flags;
        }

        internal static Type FindType(string name)
        {
            if (string.IsNullOrEmpty(name)) throw new McpCommandException("type_not_found", "Type name is required.");
            try
            {
                Type reflected = ReflectionUtility.GetTypeByName(name);
                if (reflected != null) return reflected;
            }
            catch { }
            Type type = Type.GetType(name, false, true);
            if (type != null) return type;
            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < assemblies.Length; i++)
            {
                try
                {
                    type = assemblies[i].GetType(name, false, true);
                    if (type != null) return type;
                    Type[] types = assemblies[i].GetTypes();
                    for (int j = 0; j < types.Length; j++)
                        if (string.Equals(types[j].Name, name, StringComparison.OrdinalIgnoreCase)) return types[j];
                }
                catch (ReflectionTypeLoadException ex)
                {
                    Type[] types = ex.Types;
                    for (int j = 0; types != null && j < types.Length; j++)
                        if (types[j] != null && (string.Equals(types[j].FullName, name, StringComparison.OrdinalIgnoreCase) || string.Equals(types[j].Name, name, StringComparison.OrdinalIgnoreCase))) return types[j];
                }
                catch { }
            }
            throw new McpCommandException("type_not_found", "Type was not found: " + name);
        }

        internal static IEnumerable<MemberInfo> GetReadableMembers(Type type, bool nonPublic)
        {
            List<MemberInfo> result = new List<MemberInfo>();
            FieldInfo[] fields = type.GetFields(Flags(nonPublic));
            for (int i = 0; i < fields.Length; i++)
                if (!fields[i].IsLiteral && !fields[i].IsStatic && !IsUnsafeMember(fields[i])) result.Add(fields[i]);
            PropertyInfo[] properties = type.GetProperties(Flags(nonPublic));
            for (int i = 0; i < properties.Length; i++)
            {
                PropertyInfo property = properties[i];
                if (property.GetIndexParameters().Length == 0 && property.GetGetMethod(nonPublic) != null && !property.GetGetMethod(nonPublic).IsStatic && !IsUnsafeMember(property)) result.Add(property);
            }
            result.Sort(delegate(MemberInfo a, MemberInfo b) { return string.CompareOrdinal(a.Name, b.Name); });
            return result;
        }

        internal static MemberInfo FindReadableMember(Type type, string name, bool nonPublic)
        {
            FieldInfo field = FindField(type, name, nonPublic);
            if (field != null) return field;
            PropertyInfo property = FindProperty(type, name, nonPublic);
            return property != null && property.GetIndexParameters().Length == 0 && property.GetGetMethod(nonPublic) != null ? property : null;
        }

        internal static MemberInfo FindWritableMember(Type type, string name, bool nonPublic)
        {
            FieldInfo field = FindField(type, name, nonPublic);
            if (field != null && !field.IsInitOnly && !field.IsLiteral) return field;
            PropertyInfo property = FindProperty(type, name, nonPublic);
            return property != null && property.GetIndexParameters().Length == 0 && property.GetSetMethod(nonPublic) != null ? property : null;
        }

        private static FieldInfo FindField(Type type, string name, bool nonPublic)
        {
            BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            if (nonPublic) flags |= BindingFlags.NonPublic;
            for (Type current = type; current != null; current = current.BaseType)
            {
                FieldInfo field = current.GetField(name, flags);
                if (field != null) return field;
            }
            return null;
        }

        private static PropertyInfo FindProperty(Type type, string name, bool nonPublic)
        {
            BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            if (nonPublic) flags |= BindingFlags.NonPublic;
            for (Type current = type; current != null; current = current.BaseType)
            {
                PropertyInfo property = current.GetProperty(name, flags);
                if (property != null) return property;
            }
            return null;
        }

        internal static Type GetMemberType(MemberInfo member)
        {
            FieldInfo field = member as FieldInfo;
            if (field != null) return field.FieldType;
            return ((PropertyInfo)member).PropertyType;
        }

        internal static object GetMemberValue(MemberInfo member, object target)
        {
            FieldInfo field = member as FieldInfo;
            object instance = GetDeclaringInstance(member, target);
            return field != null ? field.GetValue(instance) : ((PropertyInfo)member).GetValue(instance, null);
        }

        internal static void SetMemberValue(MemberInfo member, object target, object value)
        {
            FieldInfo field = member as FieldInfo;
            object instance = GetDeclaringInstance(member, target);
            if (field != null) field.SetValue(instance, value);
            else ((PropertyInfo)member).SetValue(instance, value, null);
        }

        internal static object GetDeclaringInstance(MemberInfo member, object target)
        {
            if (target == null || member == null) return target;
            try { return target.TryCast(member.DeclaringType) ?? target; }
            catch { return target; }
        }

        // IL2CPP collections (Il2CppSystem.Collections.Generic.List<T>, Il2CppArrayBase<T> and its
        // Il2CppReferenceArray/Il2CppStringArray/Il2CppStructArray subclasses) implement the shadow
        // Il2CppSystem.Collections.IList interface, not System.Collections.IList. Casting to
        // System.Collections.IList therefore fails and indexing would otherwise be impossible.
        // Discover the members by reflection so this works on every runtime and interop generation.
        private static readonly string[] CountMemberNames = { "Count", "Length", "_size", "count", "length" };
        private static readonly string[] IndexerMethodNames = { "get_Item", "System_Collections_IList_get_Item" };
        private static readonly string[] IndexerSetterNames = { "set_Item", "System_Collections_IList_set_Item" };

        /// <summary>
        /// Find a member describing the number of elements in an IL2CPP collection.
        /// </summary>
        internal static MemberInfo FindCountMember(Type type)
        {
            if (type == null) return null;
            for (int i = 0; i < CountMemberNames.Length; i++)
            {
                MemberInfo member = FindReadableMember(type, CountMemberNames[i], true);
                if (member != null && GetMemberType(member) == typeof(int)) return member;
            }
            return null;
        }

        /// <summary>
        /// Find an int-indexed getter on an IL2CPP collection.
        /// </summary>
        internal static MethodInfo FindIndexerGetter(Type type)
        {
            if (type == null) return null;
            for (int i = 0; i < IndexerMethodNames.Length; i++)
            {
                MethodInfo[] candidates = type.GetMethods(Flags(true));
                for (int c = 0; c < candidates.Length; c++)
                {
                    MethodInfo method = candidates[c];
                    if (!string.Equals(method.Name, IndexerMethodNames[i], StringComparison.Ordinal) || method.IsStatic) continue;
                    ParameterInfo[] parameters = method.GetParameters();
                    if (parameters.Length != 1 || parameters[0].ParameterType != typeof(int)) continue;
                    if (method.ReturnType == typeof(void)) continue;
                    return method;
                }
            }
            return null;
        }

        /// <summary>
        /// Find an int-indexed setter on an IL2CPP collection.
        /// </summary>
        internal static MethodInfo FindIndexerSetter(Type type)
        {
            if (type == null) return null;
            for (int i = 0; i < IndexerSetterNames.Length; i++)
            {
                MethodInfo[] candidates = type.GetMethods(Flags(true));
                for (int c = 0; c < candidates.Length; c++)
                {
                    MethodInfo method = candidates[c];
                    if (!string.Equals(method.Name, IndexerSetterNames[i], StringComparison.Ordinal) || method.IsStatic) continue;
                    ParameterInfo[] parameters = method.GetParameters();
                    if (parameters.Length != 2 || parameters[0].ParameterType != typeof(int)) continue;
                    return method;
                }
            }
            return null;
        }

        /// <summary>
        /// Element type of an IL2CPP collection, used to convert values on the write path.
        /// </summary>
        internal static Type GetIl2CppElementType(Type type, MethodInfo indexer)
        {
            if (indexer != null && indexer.ReturnType != typeof(void)) return indexer.ReturnType;
            if (type != null && type.IsGenericType)
            {
                Type[] arguments = type.GetGenericArguments();
                if (arguments.Length == 1) return arguments[0];
            }
            if (type != null)
            {
                Type[] interfaces = type.GetInterfaces();
                for (int i = 0; i < interfaces.Length; i++)
                {
                    if (interfaces[i].IsGenericType && interfaces[i].GetGenericArguments().Length == 1)
                    {
                        Type definition = interfaces[i].GetGenericTypeDefinition();
                        string name = definition.FullName ?? definition.Name;
                        if (name.IndexOf("IList", StringComparison.Ordinal) >= 0) return interfaces[i].GetGenericArguments()[0];
                    }
                }
            }
            return typeof(object);
        }

        /// <summary>
        /// True when the value looks like an indexable IL2CPP collection.
        /// </summary>
        internal static bool IsIl2CppIndexable(object owner, Type type)
        {
            if (owner == null || type == null) return false;
            string fullName = type.FullName ?? type.Name;
            bool il2cppCollection = fullName.StartsWith("Il2Cpp", StringComparison.Ordinal)
                || fullName.StartsWith("Il2CppInterop.", StringComparison.Ordinal);
            if (!il2cppCollection) return false;
            return FindIndexerGetter(type) != null;
        }

        internal static string GetGameObjectPath(GameObject go)
        {
            if (go == null) return null;
            string path = go.name;
            Transform parent = go.transform.parent;
            int guard = 0;
            while (parent != null && guard++ < 256)
            {
                path = parent.name + "/" + path;
                parent = parent.parent;
            }
            return path;
        }

        /// <summary>
        /// True when the object belongs to UnityExplorer's or UniverseLib's own UI hierarchy.
        /// </summary>
        internal static bool IsExplorerObject(GameObject go)
        {
            if (go == null) return false;
            try
            {
                // UnityExplorer's UI lives under the UniverseLib canvas. Walk the ancestors
                // rather than reading transform.root, which is unreliable for IL2CPP wrappers.
                Transform current = go.transform;
                int guard = 0;
                while (current != null && guard++ < 256)
                {
                    if (string.Equals(current.name, ExplorerCanvasName, StringComparison.Ordinal)) return true;
                    current = current.parent;
                }
                return false;
            }
            catch { return false; }
        }

        internal static bool IsUnsafeMember(MemberInfo member)
        {
            if (member == null) return true;
            if (member.Name.StartsWith("NativeFieldInfoPtr_", StringComparison.Ordinal) || member.Name.StartsWith("NativeMethodInfoPtr_", StringComparison.Ordinal)) return true;
            try { return UnityExplorer.Runtime.UERuntimeHelper.IsBlacklisted(member); } catch { return false; }
        }
        internal static Exception Unwrap(Exception ex)
        {
            while (ex is TargetInvocationException && ex.InnerException != null) ex = ex.InnerException;
            return ex;
        }
    }
}
