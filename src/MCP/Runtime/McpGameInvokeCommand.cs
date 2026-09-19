using System;
using System.Collections.Generic;
using System.Reflection;

namespace UnityExplorer.MCP.Runtime
{
    public sealed partial class McpGameCapabilityExecutor
    {
        private McpJsonValue Invoke(McpJsonValue command)
        {
            if (!options.AllowMethodInvocation) throw new McpCommandException("disabled", "Method invocation is disabled by policy.");
            object target; Type type; ResolveTarget(command, out target, out type); string methodName = RequiredString(command, "method"); McpJsonValue argsNode;
            if (!command.TryGet("args", out argsNode)) argsNode = McpJsonValue.Array(); if (argsNode.Kind != McpJsonValue.JsonKind.Array) throw new McpCommandException("invalid_command", "args must be an array.");
            // These were declared in the schema but never read: overload selection was ignored and
            // generic methods were skipped outright, so a generic method could not be invoked at
            // all even though the schema advertised how to supply its type arguments.
            string overload = command.GetString("overload", null);
            Type[] genericArguments = ReadGenericArguments(command);
            MethodInfo[] methods = type.GetMethods(McpReflection.Flags(options.IncludeNonPublicMembers)); List<string> failures = new List<string>();
            for (int i = 0; i < methods.Length; i++)
            {
                MethodInfo method = methods[i]; if (method.Name != methodName || McpReflection.IsUnsafeMember(method) || (target == null && !method.IsStatic)) continue;
                if (!MatchesOverload(method, overload)) continue;
                // A generic method must be closed before it can be invoked or its parameter types
                // inspected; an open one has no concrete signature to match against.
                if (method.ContainsGenericParameters)
                {
                    if (genericArguments == null) continue;
                    try { method = method.MakeGenericMethod(genericArguments); }
                    catch (Exception ex) { failures.Add(MethodSignature(method) + ": " + McpReflection.Unwrap(ex).Message); continue; }
                }
                else if (genericArguments != null) continue;
                ParameterInfo[] parameters = method.GetParameters(); if (!CanAccept(parameters, argsNode.ArrayValue.Count)) continue;
                try
                {
                    object[] args = BuildArguments(parameters, argsNode.ArrayValue); object returnValue = method.Invoke(McpReflection.GetDeclaringInstance(method, target), args); McpJsonValue r = McpJsonValue.Object();
                    r.ObjectValue["method"] = McpJsonValue.From(MethodSignature(method)); r.ObjectValue["returnValue"] = method.ReturnType == typeof(void) ? McpJsonValue.Null() : codec.Serialize(returnValue, 2, options.MaximumSerializedItems);
                    McpJsonValue outArgs = McpJsonValue.Array(); for (int p = 0; p < parameters.Length; p++) if (parameters[p].ParameterType.IsByRef || parameters[p].IsOut) outArgs.ArrayValue.Add(codec.Serialize(args[p], 1, 32));
                    if (outArgs.ArrayValue.Count > 0) r.ObjectValue["outArguments"] = outArgs; return r;
                }
                catch (Exception ex) { failures.Add(MethodSignature(method) + ": " + McpReflection.Unwrap(ex).Message); }
            }
            throw new McpCommandException("method_not_found", "No compatible overload succeeded for " + type.FullName + "." + methodName + (failures.Count == 0 ? "." : ". " + string.Join(" | ", failures.ToArray())));
        }

        /// <summary>
        /// Read the generic type arguments to close a generic method with, or null when none were
        /// supplied.
        /// </summary>
        private static Type[] ReadGenericArguments(McpJsonValue command)
        {
            McpJsonValue node;
            if (!command.TryGet("genericTypeArguments", out node) || node == null || node.IsNull) return null;
            if (node.Kind != McpJsonValue.JsonKind.Array) throw new McpCommandException("invalid_command", "generic_type_arguments must be an array of type names.");
            if (node.ArrayValue.Count == 0) return null;
            Type[] types = new Type[node.ArrayValue.Count];
            for (int i = 0; i < node.ArrayValue.Count; i++)
            {
                if (node.ArrayValue[i].Kind != McpJsonValue.JsonKind.String) throw new McpCommandException("invalid_command", "Each generic type argument must be a type name string.");
                types[i] = McpReflection.FindType(node.ArrayValue[i].StringValue);
            }
            return types;
        }

        /// <summary>
        /// Match an overload selector against a method. The selector is compared against the full
        /// signature and, more loosely, against the parameter type list, so a caller can
        /// disambiguate with either the whole signature or just its parameter types.
        /// </summary>
        private static bool MatchesOverload(MethodInfo method, string overload)
        {
            if (string.IsNullOrEmpty(overload)) return true;
            if (MethodSignature(method).IndexOf(overload, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            ParameterInfo[] parameters = method.GetParameters();
            string[] names = new string[parameters.Length];
            for (int i = 0; i < parameters.Length; i++) names[i] = parameters[i].ParameterType.FullName;
            return string.Join(",", names).IndexOf(overload, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private object[] BuildArguments(ParameterInfo[] parameters, IList<McpJsonValue> supplied)
        {
            object[] args = new object[parameters.Length];
            for (int i = 0; i < parameters.Length; i++)
            {
                Type t = parameters[i].ParameterType; if (t.IsByRef) t = t.GetElementType();
                if (i < supplied.Count) args[i] = codec.ConvertTo(supplied[i], t); else if (parameters[i].IsOptional) args[i] = parameters[i].DefaultValue; else if (parameters[i].IsOut) args[i] = t.IsValueType ? Activator.CreateInstance(t) : null; else throw new McpCommandException("argument_count", "Missing argument " + parameters[i].Name + ".");
            }
            return args;
        }
        private static bool CanAccept(ParameterInfo[] parameters, int supplied)
        {
            if (supplied > parameters.Length) return false; for (int i = supplied; i < parameters.Length; i++) if (!parameters[i].IsOptional && !parameters[i].IsOut) return false; return true;
        }
        private static string MethodSignature(MethodBase method)
        {
            ParameterInfo[] parameters = method.GetParameters(); string[] names = new string[parameters.Length]; for (int i = 0; i < parameters.Length; i++) names[i] = parameters[i].ParameterType.FullName; return method.DeclaringType.FullName + "." + method.Name + "(" + string.Join(",", names) + ")";
        }
    }
}
