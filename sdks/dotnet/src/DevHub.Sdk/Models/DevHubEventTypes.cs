namespace DevHub.Sdk.Models;

/// <summary>
/// DevHub 协议定义的事件类型便捷访问器。
/// </summary>
public static class DevHubEventTypes
{
    /// <summary>
    /// 应用定义已新增或更新。
    /// </summary>
    public static DevHubEventType AppDefinitionUpserted => DevHubEventType.AppDefinitionUpserted;

    /// <summary>
    /// 应用定义已删除。
    /// </summary>
    public static DevHubEventType AppDefinitionDeleted => DevHubEventType.AppDefinitionDeleted;

    /// <summary>
    /// 应用实例已注册。
    /// </summary>
    public static DevHubEventType AppInstanceRegistered => DevHubEventType.AppInstanceRegistered;

    /// <summary>
    /// 应用实例已注销。
    /// </summary>
    public static DevHubEventType AppInstanceUnregistered => DevHubEventType.AppInstanceUnregistered;

    /// <summary>
    /// 调用已入队。
    /// </summary>
    public static DevHubEventType InvocationQueued => DevHubEventType.InvocationQueued;

    /// <summary>
    /// 调用已投递。
    /// </summary>
    public static DevHubEventType InvocationDelivered => DevHubEventType.InvocationDelivered;

    /// <summary>
    /// 调用已完成。
    /// </summary>
    public static DevHubEventType InvocationCompleted => DevHubEventType.InvocationCompleted;

    /// <summary>
    /// 调用已失败。
    /// </summary>
    public static DevHubEventType InvocationFailed => DevHubEventType.InvocationFailed;

    /// <summary>
    /// 所有受支持事件类型。
    /// </summary>
    public static IReadOnlyList<DevHubEventType> All => DevHubEventType.All;
}
