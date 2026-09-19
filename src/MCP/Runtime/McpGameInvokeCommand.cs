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
            MethodInfo[] methods = type.GetMethods(McpReflection.Flags(options.IncludeNonPublicMembers)); List<string> failures = new List<string>();
            for (int i = 0; i < methods.Length; i++)
            {
                MethodInfo method = methods[i]; if (method.Name != methodName || method.ContainsGenericParameters || McpReflection.IsUnsafeMember(method) || (target == null && !method.IsStatic)) continue;
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
