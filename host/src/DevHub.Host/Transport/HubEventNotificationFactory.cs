using DevHub.Core.Services;
using DevHub.Core.Services.Events;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace DevHub.Host.Transport;

/// <summary>
/// Hub 事件 WS 通知构造器。
/// </summary>
public static class HubEventNotificationFactory
{
    private static readonly JsonSerializerOptions PayloadJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    private static readonly JsonSerializerOptions TrimmedPayloadJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// 根据投递记录构造标准 <c>hub.event</c> JSON-RPC 通知负载。
    /// </summary>
    /// <param name="delivery">事件投递记录。</param>
    /// <returns>可序列化通知对象。</returns>
    public static HubEventNotification Create(HubEventDelivery delivery)
    {
        return new HubEventNotification
        {
            Params = new HubEventNotificationParams
            {
                SubscriptionId = delivery.SubscriptionId,
                Type = delivery.Type,
                TimeUtc = delivery.TimeUtc.ToString("O"),
                Payload = SerializePayload(delivery.Type, delivery.Payload)
            }
        };
    }

    private static object? SerializePayload(string eventType, object? payload)
    {
        return eventType switch
        {
            HubEventTypes.AppInstanceRegistered or
            HubEventTypes.AppInstanceUnregistered or
            HubEventTypes.AppDefinitionDeleted => SerializePayloadPreservingAllNulls(payload),
            HubEventTypes.AppDefinitionUpserted => SerializeDefinitionUpsertedPayload(payload),
            _ => ClonePayload(payload)
        };
    }

    private static object? SerializePayloadPreservingAllNulls(object? payload)
    {
        return payload switch
        {
            JsonDocument document => document.RootElement.Clone(),
            JsonElement element => element.Clone(),
            _ => JsonSerializer.SerializeToElement(payload, PayloadJsonOptions)
        };
    }

    private static object? SerializeDefinitionUpsertedPayload(object? payload)
    {
        if (payload is null)
        {
            return null;
        }

        var fullPayload = JsonSerializer.SerializeToElement(payload, PayloadJsonOptions);
        if (fullPayload.ValueKind != JsonValueKind.Object)
        {
            return ClonePayload(payload);
        }

        var trimmedPayloadNode = JsonSerializer.SerializeToNode(payload, TrimmedPayloadJsonOptions) as JsonObject;
        if (trimmedPayloadNode is null)
        {
            return fullPayload.Clone();
        }

        CopyProperty(fullPayload, trimmedPayloadNode, "scope");

        if (fullPayload.TryGetProperty("definition", out var definitionElement) &&
            definitionElement.ValueKind == JsonValueKind.Object)
        {
            if (trimmedPayloadNode["definition"] is not JsonObject definitionNode)
            {
                definitionNode = new JsonObject();
                trimmedPayloadNode["definition"] = definitionNode;
            }

            CopyProperty(definitionElement, definitionNode, "scope");
        }

        return JsonSerializer.SerializeToElement(trimmedPayloadNode, PayloadJsonOptions);
    }

    private static object? ClonePayload(object? payload)
    {
        return payload switch
        {
            JsonDocument document => document.RootElement.Clone(),
            JsonElement element => element.Clone(),
            _ => payload
        };
    }

    private static void CopyProperty(JsonElement source, JsonObject destination, string propertyName)
    {
        if (!source.TryGetProperty(propertyName, out var propertyValue))
        {
            return;
        }

        destination[propertyName] = JsonNode.Parse(propertyValue.GetRawText());
    }

    /// <summary>
    /// hub.event JSON-RPC 通知。
    /// </summary>
    public sealed class HubEventNotification
    {
        /// <summary>
        /// JSON-RPC 版本。
        /// </summary>
        [JsonPropertyName("jsonrpc")]
        public string JsonRpc { get; init; } = "2.0";

        /// <summary>
        /// 方法名。
        /// </summary>
        [JsonPropertyName("method")]
        public string Method { get; init; } = HubRpcMethods.HubEvent;

        /// <summary>
        /// 通知参数。
        /// </summary>
        [JsonPropertyName("params")]
        public required HubEventNotificationParams Params { get; init; }
    }

    /// <summary>
    /// hub.event 通知参数。
    /// </summary>
    public sealed class HubEventNotificationParams
    {
        /// <summary>
        /// 订阅 ID。
        /// </summary>
        [JsonPropertyName("subscriptionId")]
        public required string SubscriptionId { get; init; }

        /// <summary>
        /// 事件类型。
        /// </summary>
        [JsonPropertyName("type")]
        public required string Type { get; init; }

        /// <summary>
        /// 事件时间。
        /// </summary>
        [JsonPropertyName("timeUtc")]
        public required string TimeUtc { get; init; }

        /// <summary>
        /// 事件负载。
        /// </summary>
        [JsonPropertyName("payload")]
        [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
        public object? Payload { get; init; }
    }
}
