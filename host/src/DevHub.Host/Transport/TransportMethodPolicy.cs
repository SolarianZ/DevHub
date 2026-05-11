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
        return HubRpcMethodRegistry.SupportsNotification(method);
    }

    /// <summary>
    /// 判断已知方法是否必须携带 JSON-RPC request id。
    /// </summary>
    internal static bool RequiresRequestId(string method)
    {
        return HubRpcMethodRegistry.IsKnownClientMethod(method) && !SupportsNotification(method);
    }

    /// <summary>
    /// 判断方法是否仅支持 HTTP。
    /// </summary>
    internal static bool IsHttpOnlyMethod(string method)
    {
        return HubRpcMethodRegistry.HttpOnlyMethods.Contains(method);
    }

    /// <summary>
    /// 判断方法是否仅支持 WebSocket。
    /// </summary>
    internal static bool IsWebSocketOnlyMethod(string method)
    {
        return HubRpcMethodRegistry.WebSocketOnlyMethods.Contains(method);
    }
}
