namespace DevHub.Core.Services.Events;

/// <summary>
/// Hub 事件消息。
/// </summary>
public sealed class HubEventMessage
{
    /// <summary>
    /// 事件类型。
    /// </summary>
    public required string Type { get; init; }

    /// <summary>
    /// 事件发生时间（UTC）。
    /// </summary>
    public required DateTime TimeUtc { get; init; }

    /// <summary>
    /// 事件负载。
    /// </summary>
    public object? Payload { get; init; }
}

