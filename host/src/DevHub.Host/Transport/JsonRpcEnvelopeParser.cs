using System.Text.Json;
using DevHub.Core.Models.Rpc;

namespace DevHub.Host.Transport;

/// <summary>
/// JSON-RPC 信封解析器。
/// </summary>
internal static class JsonRpcEnvelopeParser
{
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
        if (root.TryGetProperty("params", out var paramsElement))
        {
            if (paramsElement.ValueKind is not JsonValueKind.Object and not JsonValueKind.Array and not JsonValueKind.Null)
            {
                errorResponse = TransportResponseFactory.CreateErrorResponse(-32600, "invalid_request", requestId);
                return false;
            }

            requestParams = paramsElement.Clone();
        }

        request = new JsonRpcRequest
        {
            Id = requestId,
            Method = methodElement.GetString()!,
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

                if (idElement.TryGetDouble(out var doubleValue))
                {
                    id = doubleValue;
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
