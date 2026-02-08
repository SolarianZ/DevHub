namespace DevHub.Core.Services.Events;

/// <summary>
/// 事件投递描述。
/// </summary>
public sealed class HubEventDelivery
{
    /// <summary>
    /// 连接 ID。
    /// </summary>
    public required string ConnectionId { get; init; }

    /// <summary>
    /// 订阅 ID。
    /// </summary>
    public required string SubscriptionId { get; init; }

    /// <summary>
    /// 事件类型。
    /// </summary>
    public required string Type { get; init; }

    /// <summary>
    /// 事件时间（UTC）。
    /// </summary>
    public required DateTime TimeUtc { get; init; }

    /// <summary>
    /// 事件负载。
    /// </summary>
    public object? Payload { get; init; }
}

