using System.Text.Json;
using DevHub.Core.Models.Rpc;
using DevHub.Core.Services.Events;

namespace DevHub.Host.Transport;

/// <summary>
/// 事件订阅请求参数解析器。
/// </summary>
internal static class EventSubscriptionRequestParser
{
    /// <summary>
    /// 读取事件订阅过滤列表。
    /// </summary>
    internal static bool TryReadSubscriptionTypes(JsonRpcRequest request, out IReadOnlyCollection<string>? types, out JsonRpcResponse errorResponse)
    {
        types = null;

        if (request.Params is null)
        {
            errorResponse = null!;
            return true;
        }

        if (request.Params is not JsonElement paramsElement)
        {
            errorResponse = TransportResponseFactory.CreateErrorResponse(-32602, "invalid_params", request.Id);
            return false;
        }

        if (paramsElement.ValueKind == JsonValueKind.Null)
        {
            errorResponse = null!;
            return true;
        }

        if (paramsElement.ValueKind != JsonValueKind.Object)
        {
            errorResponse = TransportResponseFactory.CreateErrorResponse(-32602, "invalid_params", request.Id);
            return false;
        }

        if (!paramsElement.TryGetProperty("types", out var typesElement) || typesElement.ValueKind == JsonValueKind.Null)
        {
            errorResponse = null!;
            return true;
        }

        if (typesElement.ValueKind != JsonValueKind.Array)
        {
            errorResponse = TransportResponseFactory.CreateErrorResponse(-32602, "invalid_params", request.Id);
            return false;
        }

        var parsedTypes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var typeElement in typesElement.EnumerateArray())
        {
            if (typeElement.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(typeElement.GetString()))
            {
                errorResponse = TransportResponseFactory.CreateErrorResponse(-32602, "invalid_params", request.Id);
                return false;
            }

            var eventType = typeElement.GetString()!;
            if (!HubEventBus.IsSupportedEventType(eventType))
            {
                errorResponse = TransportResponseFactory.CreateErrorResponse(-32602, "invalid_params", request.Id, new { reason = "unsupported_event_type", type = eventType });
                return false;
            }

            parsedTypes.Add(eventType);
        }

        types = parsedTypes.Count == 0 ? null : parsedTypes.ToArray();
        errorResponse = null!;
        return true;
    }

    /// <summary>
    /// 读取取消订阅请求参数。
    /// </summary>
    internal static bool TryReadUnsubscribeParam(JsonRpcRequest request, out string subscriptionId, out JsonRpcResponse errorResponse)
    {
        subscriptionId = string.Empty;

        if (request.Params is not JsonElement paramsElement
            || paramsElement.ValueKind != JsonValueKind.Object
            || !paramsElement.TryGetProperty("subscriptionId", out var subscriptionIdElement)
            || subscriptionIdElement.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(subscriptionIdElement.GetString()))
        {
            errorResponse = TransportResponseFactory.CreateErrorResponse(-32602, "invalid_params", request.Id);
            return false;
        }

        subscriptionId = subscriptionIdElement.GetString()!.Trim();
        errorResponse = null!;
        return true;
    }
}
