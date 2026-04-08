using DevHub.Core.Services;
using DevHub.Core.Services.Events;
using System.Text.Json.Serialization;

namespace DevHub.Host.Transport;

/// <summary>
/// Hub 事件 WS 通知构造器。
/// </summary>
public static class HubEventNotificationFactory
{
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
                Payload = delivery.Payload
            }
        };
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
