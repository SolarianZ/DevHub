namespace DevHub.Core.Services.Events;

/// <summary>
/// 传输无关的 Hub 领域事件发布器。
/// </summary>
public interface IHubEventPublisher
{
    /// <summary>
    /// 发布一条领域事件。
    /// </summary>
    /// <param name="message">待发布的事件消息。</param>
    void Publish(HubEventMessage message);
}
