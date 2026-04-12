using System.Text.Json;
using DevHub.Core.Models;
using DevHub.Core.Models.Rpc;

namespace DevHub.Host.Transport;

/// <summary>
/// Host 适配层的 RPC 参数读取工具。
/// </summary>
internal static class RpcRequestParameterReader
{
    internal static bool TryReadParamsObject(JsonRpcRequest request, out JsonElement paramsElement)
    {
        if (request.Params is JsonElement element && element.ValueKind == JsonValueKind.Object)
        {
            paramsElement = element;
            return true;
        }

        paramsElement = default;
        return false;
    }

    internal static bool TryGetRequiredString(JsonElement element, string propertyName, out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var candidate = property.GetString();
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        value = candidate;
        return true;
    }

    internal static bool TryGetOptionalString(JsonElement element, string propertyName, out string? value)
    {
        value = null;

        if (!element.TryGetProperty(propertyName, out var property))
        {
            return true;
        }

        if (property.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString();
        return true;
    }

    internal static bool TryGetOptionalScope(
        JsonElement element,
        string propertyName,
        string invalidReason,
        out string? scope,
        out object? errorData)
    {
        scope = null;
        errorData = null;

        if (!element.TryGetProperty(propertyName, out var scopeElement))
        {
            return true;
        }

        if (scopeElement.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (scopeElement.ValueKind != JsonValueKind.String)
        {
            errorData = new { reason = invalidReason };
            return false;
        }

        scope = scopeElement.GetString();
        if (scope is null)
        {
            errorData = new { reason = invalidReason };
            return false;
        }

        if (scope == string.Empty)
        {
            scope = null;
        }

        return true;
    }

    internal static bool TryParseInvocationTarget(JsonElement paramsElement, out InvocationTarget target, out object? errorData)
    {
        target = new InvocationTarget { Scope = null, InstanceId = null };
        errorData = null;

        if (!paramsElement.TryGetProperty("target", out var targetElement))
        {
            return true;
        }

        if (targetElement.ValueKind != JsonValueKind.Object)
        {
            errorData = new { reason = "invalid_target" };
            return false;
        }

        if (!TryGetOptionalScope(targetElement, "scope", "invalid_target_scope", out var scope, out errorData))
        {
            return false;
        }

        string? instanceId = null;
        if (targetElement.TryGetProperty("instanceId", out var instanceIdElement))
        {
            if (instanceIdElement.ValueKind == JsonValueKind.Null)
            {
                instanceId = null;
            }
            else if (instanceIdElement.ValueKind == JsonValueKind.String)
            {
                instanceId = instanceIdElement.GetString();
                if (string.IsNullOrWhiteSpace(instanceId))
                {
                    errorData = new { reason = "invalid_target_instance" };
                    return false;
                }
            }
            else
            {
                errorData = new { reason = "invalid_target_instance" };
                return false;
            }
        }

        target = new InvocationTarget
        {
            Scope = scope,
            InstanceId = instanceId
        };

        return true;
    }

    internal static bool TryParseRespondError(JsonElement errorElement, out object? error)
    {
        error = null;

        if (errorElement.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!errorElement.TryGetProperty("code", out var codeElement)
            || codeElement.ValueKind != JsonValueKind.Number
            || !codeElement.TryGetInt32(out var code))
        {
            return false;
        }

        if (!errorElement.TryGetProperty("message", out var messageElement)
            || messageElement.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var payload = new Dictionary<string, object?>
        {
            ["code"] = code,
            ["message"] = messageElement.GetString()
        };

        if (errorElement.TryGetProperty("data", out var dataElement))
        {
            if (dataElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            payload["data"] = JsonSerializer.Deserialize<object>(dataElement.GetRawText());
        }

        error = payload;
        return true;
    }
}
