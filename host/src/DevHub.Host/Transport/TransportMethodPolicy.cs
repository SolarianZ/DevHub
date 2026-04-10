using DevHub.Core.Services;

namespace DevHub.Host.Transport;

/// <summary>
/// Host 传输边界方法策略。
/// </summary>
internal static class TransportMethodPolicy
{
    /// <summary>
    /// 判断方法是否仅支持 HTTP。
    /// </summary>
    internal static bool IsHttpOnlyMethod(string method)
    {
        return method is
            HubRpcMethods.HubAppsValidateDefinition or
            HubRpcMethods.HubAppsUpsertDefinition or
            HubRpcMethods.HubAppsDeleteDefinition or
            HubRpcMethods.HubAppsRegisterInstance or
            HubRpcMethods.HubAppsHeartbeat or
            HubRpcMethods.HubAppsUnregisterInstance or
            HubRpcMethods.HubAppsLaunch or
            HubRpcMethods.HubInvokeNotify or
            HubRpcMethods.HubInvokeRequest or
            HubRpcMethods.HubInvokePoll or
            HubRpcMethods.HubInvokeRespond;
    }

    /// <summary>
    /// 判断方法是否仅支持 WebSocket。
    /// </summary>
    internal static bool IsWebSocketOnlyMethod(string method)
    {
        return method is
            HubRpcMethods.HubWsAuthenticate or
            HubRpcMethods.HubEventsSubscribe or
            HubRpcMethods.HubEventsUnsubscribe;
    }
}
