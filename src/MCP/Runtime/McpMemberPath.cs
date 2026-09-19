using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;

namespace UnityExplorer.MCP.Runtime
{
    internal static class McpMemberPath
    {
        private enum TokenKind { Member, Index }

        private sealed class Token
        {
            internal TokenKind Kind;
            internal string Name;
            internal int Index;
        }

        private sealed class AccessFrame
        {
            internal object Owner;
            internal Type OwnerType;
            internal Token Token;
            internal Type ValueType;
        }

        internal static object Read(object target, Type targetType, string path, bool includeNonPublic)
        {
            List<Token> tokens = Parse(path);
            object current = target;
            Type currentType = targetType;
            for (int i = 0; i < tokens.Count; i++)
            {
                // Mirror the guard on the write path: dereferencing a null owner would
                // otherwise surface as a raw TargetException instead of a coded error.
                if (current == null && tokens[i].Kind == TokenKind.Member)
                    throw new McpCommandException("null_member", "Member path reached null before " + Describe(tokens[i]) + ".");

                Type declaredType;
                current = ReadToken(current, currentType, tokens[i], includeNonPublic, out declaredType);
                currentType = current == null ? declaredType : McpReflection.GetActualType(current);
            }
            return current;
        }

        internal static object Write(object target, Type targetType, string path, McpJsonValue input, McpValueCodec codec, bool includeNonPublic)
        {
            List<Token> tokens = Parse(path);
            List<AccessFrame> frames = new List<AccessFrame>();
            object current = target;
            Type currentType = targetType;

            for (int i = 0; i < tokens.Count - 1; i++)
            {
                Type childType;
                object child = ReadToken(current, currentType, tokens[i], includeNonPublic, out childType);
                if (child == null)
                    throw new McpCommandException("null_member", "Member path reached null before " + Describe(tokens[i + 1]) + ".");
                AccessFrame frame = new AccessFrame();
                frame.Owner = current;
                frame.OwnerType = currentType;
                frame.Token = tokens[i];
                frame.ValueType = childType;
                frames.Add(frame);
                current = child;
                currentType = McpReflection.GetActualType(child);
            }

            Token leaf = tokens[tokens.Count - 1];
            Type leafType = GetWritableTokenType(current, currentType, leaf, includeNonPublic);
            object converted = codec.ConvertTo(input, leafType);
            WriteToken(current, currentType, leaf, converted, includeNonPublic);

            // Reflection mutates a boxed struct, not the struct stored in its parent. Propagate
            // changed value types back through fields/properties/arrays/lists until a reference
            // boundary is reached.
            object updatedChild = current;
            for (int i = frames.Count - 1; i >= 0; i--)
            {
                AccessFrame frame = frames[i];
                if (!frame.ValueType.IsValueType) break;
                WriteToken(frame.Owner, frame.OwnerType, frame.Token, updatedChild, includeNonPublic);
                updatedChild = frame.Owner;
            }

            return Read(target, targetType, path, includeNonPublic);
        }

        private static object ReadToken(object owner, Type ownerType, Token token, bool includeNonPublic, out Type valueType)
        {
            if (token.Kind == TokenKind.Member)
            {
                MemberInfo member = McpReflection.FindReadableMember(ownerType, token.Name, includeNonPublic);
                if (member == null || McpReflection.IsUnsafeMember(member))
                    throw MemberNotFound(ownerType, token.Name, false);
                valueType = McpReflection.GetMemberType(member);
                return McpReflection.GetMemberValue(member, owner);
            }

            if (owner == null) throw new McpCommandException("null_member", "Cannot index a null value.");
            Array array = owner as Array;
            if (array != null)
            {
                ValidateIndex(token.Index, array.Length);
                valueType = ownerType.GetElementType() ?? array.GetType().GetElementType() ?? typeof(object);
                return array.GetValue(token.Index);
            }
            IList list = owner as IList;
            if (list != null)
            {
                ValidateIndex(token.Index, list.Count);
                valueType = GetListElementType(ownerType, list, token.Index);
                return list[token.Index];
            }
            // IL2CPP collections implement a shadow IList that is unrelated to System.Collections.IList,
            // so fall back to reflected Count + int indexer access.
            object element;
            if (TryReadIl2CppIndex(owner, ownerType, token.Index, out element, out valueType))
                return element;
            throw new McpCommandException("not_indexable", "Value of type " + ownerType.FullName + " is not an array or IList.");
        }

        private static bool TryReadIl2CppIndex(object owner, Type ownerType, int index, out object value, out Type valueType)
        {
            value = null;
            valueType = typeof(object);
            if (!McpReflection.IsIl2CppIndexable(owner, ownerType)) return false;
            MemberInfo countMember = McpReflection.FindCountMember(ownerType);
            if (countMember == null) return false;
            int count;
            try { count = Convert.ToInt32(McpReflection.GetMemberValue(countMember, owner)); }
            catch { return false; }
            ValidateIndex(index, count);
            MethodInfo getter = McpReflection.FindIndexerGetter(ownerType);
            if (getter == null) return false;
            valueType = McpReflection.GetIl2CppElementType(ownerType, getter);
            value = getter.Invoke(McpReflection.GetDeclaringInstance(getter, owner), new object[] { index });
            return true;
        }

        private static bool TryWriteIl2CppIndex(object owner, Type ownerType, int index, object value, out Type valueType)
        {
            valueType = typeof(object);
            if (!McpReflection.IsIl2CppIndexable(owner, ownerType)) return false;
            MemberInfo countMember = McpReflection.FindCountMember(ownerType);
            if (countMember == null) return false;
            int count;
            try { count = Convert.ToInt32(McpReflection.GetMemberValue(countMember, owner)); }
            catch { return false; }
            ValidateIndex(index, count);
            MethodInfo setter = McpReflection.FindIndexerSetter(ownerType);
            if (setter == null) throw new McpCommandException("read_only_collection", "Collection of type " + ownerType.FullName + " exposes no writable int indexer.");
            valueType = McpReflection.GetIl2CppElementType(ownerType, setter);
            setter.Invoke(McpReflection.GetDeclaringInstance(setter, owner), new object[] { index, value });
            return true;
        }

        private static Type GetWritableTokenType(object owner, Type ownerType, Token token, bool includeNonPublic)
        {
            if (token.Kind == TokenKind.Member)
            {
                MemberInfo member = McpReflection.FindWritableMember(ownerType, token.Name, includeNonPublic);
                if (member == null || McpReflection.IsUnsafeMember(member))
                    throw MemberNotFound(ownerType, token.Name, true);
                return McpReflection.GetMemberType(member);
            }
            if (owner == null) throw new McpCommandException("null_member", "Cannot index a null value.");
            Array array = owner as Array;
            if (array != null)
            {
                ValidateIndex(token.Index, array.Length);
                return ownerType.GetElementType() ?? array.GetType().GetElementType() ?? typeof(object);
            }
            IList list = owner as IList;
            if (list != null)
            {
                ValidateIndex(token.Index, list.Count);
                if (list.IsReadOnly) throw new McpCommandException("read_only_collection", "The IList is read-only.");
                return GetListElementType(ownerType, list, token.Index);
            }
            // IL2CPP collections use a shadow IList; resolve the element type from the reflected indexer.
            if (McpReflection.IsIl2CppIndexable(owner, ownerType))
            {
                MemberInfo countMember = McpReflection.FindCountMember(ownerType);
                if (countMember != null) ValidateIndex(token.Index, Convert.ToInt32(McpReflection.GetMemberValue(countMember, owner)));
                MethodInfo setter = McpReflection.FindIndexerSetter(ownerType);
                if (setter == null) throw new McpCommandException("read_only_collection", "Collection of type " + ownerType.FullName + " exposes no writable int indexer.");
                return McpReflection.GetIl2CppElementType(ownerType, setter);
            }
            throw new McpCommandException("not_indexable", "Value of type " + ownerType.FullName + " is not an array or IList.");
        }

        private static void WriteToken(object owner, Type ownerType, Token token, object value, bool includeNonPublic)
        {
            if (token.Kind == TokenKind.Member)
            {
                MemberInfo member = McpReflection.FindWritableMember(ownerType, token.Name, includeNonPublic);
                if (member == null || McpReflection.IsUnsafeMember(member))
                    throw MemberNotFound(ownerType, token.Name, true);
                McpReflection.SetMemberValue(member, owner, value);
                return;
            }
            Array array = owner as Array;
            if (array != null) { ValidateIndex(token.Index, array.Length); array.SetValue(value, token.Index); return; }
            IList list = owner as IList;
            if (list != null)
            {
                ValidateIndex(token.Index, list.Count);
                if (list.IsReadOnly) throw new McpCommandException("read_only_collection", "The IList is read-only.");
                list[token.Index] = value;
                return;
            }
            if (TryWriteIl2CppIndex(owner, ownerType, token.Index, value, out _)) return;
            throw new McpCommandException("not_indexable", "Value of type " + ownerType.FullName + " is not an array or IList.");
        }

        private static List<Token> Parse(string path)
        {
            if (string.IsNullOrEmpty(path)) throw new McpCommandException("invalid_member_path", "Member path is required.");
            List<Token> tokens = new List<Token>();
            int position = 0;
            while (position < path.Length)
            {
                int start = position;
                while (position < path.Length && path[position] != '.' && path[position] != '[') position++;
                if (position > start)
                {
                    Token member = new Token(); member.Kind = TokenKind.Member; member.Name = path.Substring(start, position - start); tokens.Add(member);
                }
                else if (position >= path.Length || path[position] != '[')
                    throw InvalidPath(path, position);

                while (position < path.Length && path[position] == '[')
                {
                    int close = path.IndexOf(']', position + 1);
                    if (close < 0) throw InvalidPath(path, position);
                    int index;
                    if (!int.TryParse(path.Substring(position + 1, close - position - 1), out index) || index < 0)
                        throw new McpCommandException("invalid_member_path", "Collection index must be a non-negative integer in path: " + path);
                    Token indexed = new Token(); indexed.Kind = TokenKind.Index; indexed.Index = index; tokens.Add(indexed);
                    position = close + 1;
                }

                if (position < path.Length)
                {
                    if (path[position] != '.' || position + 1 >= path.Length) throw InvalidPath(path, position);
                    position++;
                }
            }
            if (tokens.Count == 0) throw InvalidPath(path, 0);
            return tokens;
        }

        private static Type GetListElementType(Type type, IList list, int index)
        {
            if (type.IsGenericType)
            {
                Type[] arguments = type.GetGenericArguments();
                if (arguments.Length == 1) return arguments[0];
            }
            Type[] interfaces = type.GetInterfaces();
            for (int i = 0; i < interfaces.Length; i++)
                if (interfaces[i].IsGenericType && interfaces[i].GetGenericTypeDefinition() == typeof(IList<>))
                    return interfaces[i].GetGenericArguments()[0];
            object existing = index >= 0 && index < list.Count ? list[index] : null;
            return existing == null ? typeof(object) : existing.GetType();
        }

        private static void ValidateIndex(int index, int count)
        {
            if (index < 0 || index >= count)
                throw new McpCommandException("index_out_of_range", "Collection index " + index + " is outside the valid range 0.." + (count - 1) + ".");
        }

        private static McpCommandException InvalidPath(string path, int position)
        {
            return new McpCommandException("invalid_member_path", "Invalid member path near position " + position + ": " + path);
        }

        private static McpCommandException MemberNotFound(Type ownerType, string name, bool writable)
        {
            string operation = writable ? "Writable field or property" : "Readable member";
            return new McpCommandException(
                "member_not_found",
                operation + " was not found on resolved runtime type " +
                (ownerType == null ? "<null>" : ownerType.FullName) + ": " + name);
        }

        private static string Describe(Token token)
        {
            return token.Kind == TokenKind.Member ? token.Name : "[" + token.Index + "]";
        }
    }
}
