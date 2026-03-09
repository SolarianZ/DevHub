namespace DevHub.Sdk.Models;

/// <summary>
/// DevHub 协议定义的事件类型常量。
/// </summary>
public static class DevHubEventTypes
{
    /// <summary>
    /// 应用实例已注册。
    /// </summary>
    public const string AppInstanceRegistered = "app.instance.registered";

    /// <summary>
    /// 应用实例已注销。
    /// </summary>
    public const string AppInstanceUnregistered = "app.instance.unregistered";

    /// <summary>
    /// 调用已入队。
    /// </summary>
    public const string InvocationQueued = "invocation.queued";

    /// <summary>
    /// 调用已投递。
    /// </summary>
    public const string InvocationDelivered = "invocation.delivered";

    /// <summary>
    /// 调用已完成。
    /// </summary>
    public const string InvocationCompleted = "invocation.completed";

    /// <summary>
    /// 调用已失败。
    /// </summary>
    public const string InvocationFailed = "invocation.failed";
}
