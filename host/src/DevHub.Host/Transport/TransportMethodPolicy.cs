using DevHub.Core.Services;

namespace DevHub.Host.Transport;

/// <summary>
/// Host 传输边界方法策略。
/// </summary>
internal static class TransportMethodPolicy
{
    /// <summary>
    /// 判断方法是否支持 JSON-RPC notification。
    /// </summary>
    internal static bool SupportsNotification(string method)
    {
        return method is
            HubRpcMethods.HubPing or
            HubRpcMethods.HubGetVersion or
            HubRpcMethods.HubAppsListDefinitions or
            HubRpcMethods.HubAppsGetDefinition or
            HubRpcMethods.HubAppsHeartbeat or
            HubRpcMethods.HubAppsUnregisterInstance or
            HubRpcMethods.HubAppsListInstances or
            HubRpcMethods.HubAppsGetInstance or
            HubRpcMethods.HubEventsUnsubscribe;
    }

    /// <summary>
    /// 判断已知方法是否必须携带 JSON-RPC request id。
    /// </summary>
    internal static bool RequiresRequestId(string method)
    {
        return IsKnownHubMethod(method) && !SupportsNotification(method);
    }

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

    private static bool IsKnownHubMethod(string method)
    {
        return method is
            HubRpcMethods.HubPing or
            HubRpcMethods.HubGetVersion or
            HubRpcMethods.HubWsAuthenticate or
            HubRpcMethods.HubEventsSubscribe or
            HubRpcMethods.HubEventsUnsubscribe or
            HubRpcMethods.HubAppsListDefinitions or
            HubRpcMethods.HubAppsGetDefinition or
            HubRpcMethods.HubAppsValidateDefinition or
            HubRpcMethods.HubAppsUpsertDefinition or
            HubRpcMethods.HubAppsDeleteDefinition or
            HubRpcMethods.HubAppsRegisterInstance or
            HubRpcMethods.HubAppsHeartbeat or
            HubRpcMethods.HubAppsUnregisterInstance or
            HubRpcMethods.HubAppsListInstances or
            HubRpcMethods.HubAppsGetInstance or
            HubRpcMethods.HubAppsLaunch or
            HubRpcMethods.HubInvokeNotify or
            HubRpcMethods.HubInvokeRequest or
            HubRpcMethods.HubInvokePoll or
            HubRpcMethods.HubInvokeRespond;
    }
}
