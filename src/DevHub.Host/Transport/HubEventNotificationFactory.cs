using DevHub.Core.Services;
using DevHub.Core.Services.Events;

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
    /// <returns>可序列化匿名对象。</returns>
    public static object Create(HubEventDelivery delivery)
    {
        if (delivery.Payload is null)
        {
            return new
            {
                jsonrpc = "2.0",
                method = HubRpcMethods.HubEvent,
                @params = new
                {
                    subscriptionId = delivery.SubscriptionId,
                    type = delivery.Type,
                    timeUtc = delivery.TimeUtc.ToString("O")
                }
            };
        }

        return new
        {
            jsonrpc = "2.0",
            method = HubRpcMethods.HubEvent,
            @params = new
            {
                subscriptionId = delivery.SubscriptionId,
                type = delivery.Type,
                timeUtc = delivery.TimeUtc.ToString("O"),
                payload = delivery.Payload
            }
        };
    }
}
