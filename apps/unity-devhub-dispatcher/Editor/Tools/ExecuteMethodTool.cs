using System;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;

namespace DevHubDispatcher.Editor
{
    internal sealed class ExecuteMethodTool : DevHubBuiltInToolBase
    {
        public const string FixedToolId = "execute-method";

        internal ExecuteMethodTool() : base(FixedToolId) { }

        protected override object Execute(JToken payload)
        {
            JObject request = RequireObjectPayload(payload, "payload");
            string typeName = RequireField(request, "TypeName");
            string methodName = RequireField(request, "MethodName");

            Type targetType = ResolveType(typeName);
            MethodInfo method = ResolveMethod(targetType, methodName);
            return method.Invoke(null, null);
        }

        private static string RequireField(JObject payload, string fieldName)
        {
            if (payload.TryGetValue(fieldName, StringComparison.Ordinal, out JToken fieldValue))
            {
                return RequireNonEmptyString(fieldValue, fieldName);
            }

            throw new ArgumentException(fieldName + " 必须是非空字符串。", nameof(payload));
        }

        private static Type ResolveType(string typeName)
        {
            Type resolvedType = Type.GetType(typeName, false, false);
            if (resolvedType != null)
            {
                return resolvedType;
            }

            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int index = 0; index < assemblies.Length; index++)
            {
                resolvedType = assemblies[index].GetType(typeName, false, false);
                if (resolvedType != null)
                {
                    return resolvedType;
                }
            }

            throw new InvalidOperationException("类型或方法不满足精确匹配的可调用约束。");
        }

        private static MethodInfo ResolveMethod(Type targetType, string methodName)
        {
            BindingFlags bindingFlags = BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy;
            MethodInfo targetMethod = targetType.GetMethods(bindingFlags)
                                                .FirstOrDefault(method => !method.ContainsGenericParameters &&
                                                    string.Equals(method.Name, methodName, StringComparison.Ordinal) &&
                                                    method.GetParameters().Length == 0);
            if (targetMethod != null)
                return targetMethod;

            throw new InvalidOperationException("类型或方法不满足精确匹配的可调用约束。");
        }
    }
}