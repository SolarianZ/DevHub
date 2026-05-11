using DevHub.Core.Models;
using DevHub.Core.Models.Rpc;
using System.Text.Json;

namespace DevHub.Core.Services.Rpc;

/// <summary>
/// RPC 参数读取工具。
/// </summary>
internal static class RpcParamReader
{
    /// <summary>
    /// 尝试读取对象类型的 params。
    /// </summary>
    /// <param name="request">RPC 请求。</param>
    /// <param name="paramsElement">解析后的对象参数。</param>
    /// <param name="error">读取失败时的错误响应。</param>
    /// <returns>读取成功返回 true，否则返回 false。</returns>
    public static bool TryReadParamsObject(JsonRpcRequest request, out JsonElement paramsElement, out JsonRpcResponse error)
    {
        if (request.Params is JsonElement element && element.ValueKind == JsonValueKind.Object)
        {
            paramsElement = element;
            error = null!;
            return true;
        }

        paramsElement = default;
        error = RpcErrorFactory.InvalidParams(request.Id);
        return false;
    }

    /// <summary>
    /// 尝试读取必填字符串字段。
    /// </summary>
    /// <param name="element">参数对象。</param>
    /// <param name="propertyName">字段名。</param>
    /// <param name="value">读取成功时返回字段值。</param>
    /// <returns>读取成功返回 true，否则返回 false。</returns>
    public static bool TryGetRequiredString(JsonElement element, string propertyName, out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var str = property.GetString();
        if (string.IsNullOrWhiteSpace(str))
        {
            return false;
        }

        value = str;
        return true;
    }

    /// <summary>
    /// 尝试读取必填的 canonical `appId` 字段。
    /// </summary>
    public static bool TryGetRequiredAppId(JsonElement element, string propertyName, out string value)
    {
        value = string.Empty;
        return TryGetRequiredString(element, propertyName, out value) && ProtocolIdentifier.IsValidAppId(value);
    }

    /// <summary>
    /// 尝试读取必填的 canonical `instanceId` 字段。
    /// </summary>
    public static bool TryGetRequiredInstanceId(JsonElement element, string propertyName, out string value)
    {
        value = string.Empty;
        return TryGetRequiredString(element, propertyName, out value) && ProtocolIdentifier.IsValidInstanceId(value);
    }

    /// <summary>
    /// 尝试读取可选字符串字段。
    /// </summary>
    /// <param name="element">参数对象。</param>
    /// <param name="propertyName">字段名。</param>
    /// <param name="value">读取结果；缺失或为 null 时返回 null。</param>
    /// <returns>字段缺失、为 null 或为字符串时返回 true，否则返回 false。</returns>
    public static bool TryGetOptionalString(JsonElement element, string propertyName, out string? value)
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

    /// <summary>
    /// 尝试读取必填的显式字符串 scope 字段。
    /// </summary>
    /// <param name="element">参数对象。</param>
    /// <param name="propertyName">字段名。</param>
    /// <param name="invalidReason">非法时使用的 reason。</param>
    /// <param name="scope">解析成功时返回的 scope 值。</param>
    /// <param name="errorData">解析失败时的错误附加数据。</param>
    /// <returns>解析成功返回 true，否则返回 false。</returns>
    public static bool TryGetRequiredScope(
        JsonElement element,
        string propertyName,
        string invalidReason,
        out string scope,
        out object? errorData)
    {
        scope = string.Empty;
        errorData = null;

        if (!element.TryGetProperty(propertyName, out var scopeElement))
        {
            errorData = BuildInvalidScopeErrorData(invalidReason);
            return false;
        }

        if (scopeElement.ValueKind != JsonValueKind.String)
        {
            errorData = BuildInvalidScopeErrorData(invalidReason);
            return false;
        }

        scope = scopeElement.GetString() ?? string.Empty;
        if (!ScopeContract.IsValidScopedString(scope))
        {
            errorData = BuildInvalidScopeErrorData(invalidReason);
            return false;
        }

        return true;
    }

    /// <summary>
    /// 尝试读取仅用于列表过滤的 scope 字段。
    /// </summary>
    /// <param name="element">参数对象。</param>
    /// <param name="propertyName">字段名。</param>
    /// <param name="invalidReason">非法时使用的 reason。</param>
    /// <param name="scope">解析成功时的 scope 过滤值；<see langword="null"/> 表示不按 scope 过滤。</param>
    /// <param name="errorData">解析失败时的错误附加数据。</param>
    /// <returns>解析成功返回 true，否则返回 false。</returns>
    public static bool TryGetRequiredListScope(
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
            errorData = BuildInvalidScopeErrorData(invalidReason);
            return false;
        }

        if (scopeElement.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (scopeElement.ValueKind != JsonValueKind.String)
        {
            errorData = BuildInvalidScopeErrorData(invalidReason);
            return false;
        }

        scope = scopeElement.GetString();
        if (!ScopeContract.IsValidScopedString(scope))
        {
            errorData = BuildInvalidScopeErrorData(invalidReason);
            return false;
        }

        return true;
    }

    /// <summary>
    /// 构造 scope 非法时的统一错误数据。
    /// </summary>
    /// <param name="invalidReason">错误 reason。</param>
    /// <returns>错误数据对象。</returns>
    internal static object BuildInvalidScopeErrorData(string invalidReason)
    {
        return new { reason = invalidReason };
    }

    /// <summary>
    /// 尝试解析 Invocation target 字段。
    /// </summary>
    /// <param name="paramsElement">RPC 参数对象。</param>
    /// <param name="target">解析结果。</param>
    /// <param name="errorData">解析失败时的错误附加数据。</param>
    /// <returns>解析成功返回 true，否则返回 false。</returns>
    public static bool TryParseInvocationTarget(JsonElement paramsElement, out InvocationTarget target, out object? errorData)
    {
        target = null!;
        errorData = null;

        if (!paramsElement.TryGetProperty("target", out var targetElement) || targetElement.ValueKind != JsonValueKind.Object)
        {
            errorData = new { reason = "invalid_target" };
            return false;
        }

        if (!TryGetRequiredScope(targetElement, "scope", "invalid_target_scope", out var scope, out errorData))
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
                if (!ProtocolIdentifier.IsValidInstanceId(instanceId))
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

    /// <summary>
    /// 尝试解析 invocation.respond 的 error 载荷。
    /// </summary>
    /// <param name="errorElement">error JSON 对象。</param>
    /// <param name="error">解析后的错误对象。</param>
    /// <returns>结构合法返回 true，否则返回 false。</returns>
    public static bool TryParseRespondError(JsonElement errorElement, out object? error)
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
            payload["data"] = JsonSerializer.Deserialize<object>(dataElement.GetRawText());
        }

        error = payload;
        return true;
    }
}
