using System.Text.Json;
using DevHub.Core.Models.Rpc;
using DevHub.Core.Services;

namespace DevHub.Host.Transport;

/// <summary>
/// JSON-RPC 信封解析器。
/// </summary>
internal static class JsonRpcEnvelopeParser
{
    /// <summary>
    /// 尝试提取可安全回传到 JSON-RPC 错误响应的请求 ID。
    /// </summary>
    internal static bool TryExtractResponseId(JsonElement root, out object? requestId)
    {
        requestId = null;

        return root.ValueKind == JsonValueKind.Object
            && TryExtractRequestId(root, out requestId);
    }

    /// <summary>
    /// 将 JSON 根节点解析为 JSON-RPC 请求模型。
    /// </summary>
    internal static bool TryParse(JsonElement root, out JsonRpcRequest request, out JsonRpcResponse errorResponse)
    {
        request = null!;

        var canUseRequestId = TryExtractRequestId(root, out var requestId);
        if (!root.TryGetProperty("jsonrpc", out var jsonRpcElement)
            || jsonRpcElement.ValueKind != JsonValueKind.String
            || !string.Equals(jsonRpcElement.GetString(), "2.0", StringComparison.Ordinal))
        {
            errorResponse = TransportResponseFactory.CreateErrorResponse(-32600, "invalid_request", canUseRequestId ? requestId : null);
            return false;
        }

        if (!root.TryGetProperty("method", out var methodElement)
            || methodElement.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(methodElement.GetString()))
        {
            errorResponse = TransportResponseFactory.CreateErrorResponse(-32600, "invalid_request", canUseRequestId ? requestId : null);
            return false;
        }

        if (root.TryGetProperty("id", out var idElement))
        {
            if (!TryConvertJsonRpcId(idElement, out requestId))
            {
                errorResponse = TransportResponseFactory.CreateErrorResponse(-32600, "invalid_request", null);
                return false;
            }
        }

        object? requestParams = null;
        var method = methodElement.GetString()!;

        if (root.TryGetProperty("params", out var paramsElement))
        {
            if (!IsAcceptedParamsValue(method, paramsElement))
            {
                errorResponse = TransportResponseFactory.CreateErrorResponse(-32600, "invalid_request", requestId);
                return false;
            }

            requestParams = paramsElement.Clone();
        }

        request = new JsonRpcRequest
        {
            Id = requestId,
            Method = method,
            Params = requestParams
        };

        errorResponse = null!;
        return true;
    }

    /// <summary>
    /// 判断 hub.* 方法是否传入了数组参数。
    /// </summary>
    internal static bool IsHubMethodParamsArray(JsonRpcRequest request)
    {
        if (!request.Method.StartsWith("hub.", StringComparison.Ordinal))
        {
            return false;
        }

        return request.Params is JsonElement paramsElement && paramsElement.ValueKind == JsonValueKind.Array;
    }

    private static bool IsAcceptedParamsValue(string method, JsonElement paramsElement)
    {
        if (string.Equals(method, HubRpcMethods.HubPing, StringComparison.Ordinal)
            || string.Equals(method, HubRpcMethods.HubGetVersion, StringComparison.Ordinal))
        {
            return paramsElement.ValueKind != JsonValueKind.Undefined;
        }

        return paramsElement.ValueKind is JsonValueKind.Object or JsonValueKind.Array;
    }

    private static bool TryExtractRequestId(JsonElement root, out object? requestId)
    {
        requestId = null;

        if (!root.TryGetProperty("id", out var idElement))
        {
            return false;
        }

        return TryConvertJsonRpcId(idElement, out requestId);
    }

    private static bool TryConvertJsonRpcId(JsonElement idElement, out object? id)
    {
        switch (idElement.ValueKind)
        {
            case JsonValueKind.String:
                id = idElement.GetString();
                return true;
            case JsonValueKind.Number:
                if (idElement.TryGetInt64(out var int64Value))
                {
                    id = int64Value;
                    return true;
                }

                id = null;
                return false;
            case JsonValueKind.Null:
                id = null;
                return false;
            default:
                id = null;
                return false;
        }
    }
}
