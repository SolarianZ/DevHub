using System;
using DevHub.Sdk.Models;
using Newtonsoft.Json.Linq;

namespace DevHubDispatcher.Editor
{
    internal static class DevHubDispatcherHostRequestFactory
    {
        internal static JObject BuildEnvelope(string toolId, JToken payload)
        {
            return new JObject
            {
                ["toolId"] = toolId,
                ["payload"] = payload == null ? JValue.CreateNull() : payload.DeepClone()
            };
        }

        internal static bool TryBuildInvokeRequest(string appId, string method, JObject envelope, DevHubDispatcherSendOptions options, out InvokeRequest request, out string error)
        {
            request = null;
            error = null;

            if (string.IsNullOrWhiteSpace(appId))
            {
                error = "appId 不能为空。";
                return false;
            }

            if (string.IsNullOrWhiteSpace(method))
            {
                error = "method 不能为空。";
                return false;
            }

            try
            {
                request = new InvokeRequest
                {
                    AppId = appId,
                    Method = method,
                    Args = envelope
                };

                if (options == null)
                {
                    return true;
                }

                if (!string.IsNullOrEmpty(options.Scope) || !string.IsNullOrEmpty(options.InstanceId))
                {
                    if (!TryNormalizeOptionalString(options.Scope, "options.Scope", out string scope, out error))
                    {
                        request = null;
                        return false;
                    }

                    if (!TryNormalizeOptionalString(options.InstanceId, "options.InstanceId", out string instanceId, out error))
                    {
                        request = null;
                        return false;
                    }

                    request.Target = new InvocationTarget
                    {
                        Scope = scope,
                        InstanceId = instanceId
                    };
                }

                if (options.TtlMs.HasValue || options.WaitTimeoutMs.HasValue || options.QueueIfOffline.HasValue || options.AutoLaunch.HasValue)
                {
                    request.Options = new InvocationOptions
                    {
                        TtlMs = options.TtlMs,
                        WaitTimeoutMs = options.WaitTimeoutMs,
                        QueueIfOffline = options.QueueIfOffline,
                        AutoLaunch = options.AutoLaunch
                    };
                }

                return true;
            }
            catch (ArgumentException ex)
            {
                request = null;
                error = ex.Message;
                return false;
            }
        }

        private static bool TryNormalizeOptionalString(string value, string parameterName, out string normalizedValue, out string error)
        {
            if (value == null)
            {
                normalizedValue = null;
                error = null;
                return true;
            }

            if (string.IsNullOrWhiteSpace(value))
            {
                normalizedValue = null;
                error = parameterName + " 不能是空白字符串。";
                return false;
            }

            normalizedValue = value;
            error = null;
            return true;
        }
    }
}
