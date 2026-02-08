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
    /// 尝试读取可选的 scope 字段。
    /// </summary>
    /// <param name="element">参数对象。</param>
    /// <param name="propertyName">字段名。</param>
    /// <param name="invalidReason">非法时使用的 reason。</param>
    /// <param name="scope">解析成功时的 scope 值（null 表示 Global）。</param>
    /// <param name="errorData">解析失败时的错误附加数据。</param>
    /// <returns>解析成功返回 true，否则返回 false。</returns>
    public static bool TryGetOptionalScope(
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
        if (scope is null || scope == string.Empty || scope == "global")
        {
            errorData = new { reason = invalidReason };
            return false;
        }

        return true;
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
}
