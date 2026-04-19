using System;
using Newtonsoft.Json.Linq;

namespace DevHubDispatcher.Editor
{
    internal abstract class DevHubBuiltInToolBase : IDevHubTool
    {
        protected DevHubBuiltInToolBase(string toolId)
        {
            if (string.IsNullOrWhiteSpace(toolId))
            {
                throw new ArgumentException("toolId 不能为空。", nameof(toolId));
            }

            ToolId = toolId;
        }

        public string ToolId { get; }

        public JToken HandleDevHubRequest(string method, JToken payload)
        {
            ValidateMethod(method);
            return NormalizeResult(Execute(payload));
        }

        public void HandleDevHubNotify(string method, JToken payload)
        {
            ValidateMethod(method);
            Execute(payload);
        }

        protected abstract object Execute(JToken payload);

        protected static JObject RequireObjectPayload(JToken payload, string parameterName)
        {
            if (payload is JObject obj)
            {
                return obj;
            }

            throw new ArgumentException(parameterName + " 必须是对象。", nameof(payload));
        }

        protected static string RequireNonEmptyString(JToken token, string parameterName)
        {
            if (token?.Type == JTokenType.String)
            {
                string value = token.Value<string>();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }

            throw new ArgumentException(parameterName + " 必须是非空字符串。", nameof(token));
        }

        protected static JToken NormalizeResult(object result)
        {
            if (result == null)
            {
                return JValue.CreateNull();
            }

            if (result is JToken token)
            {
                return token;
            }

            return JToken.FromObject(result);
        }

        private static void ValidateMethod(string method)
        {
            if (string.IsNullOrWhiteSpace(method))
            {
                throw new ArgumentException("method 不能为空。", nameof(method));
            }
        }
    }
}