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
}
