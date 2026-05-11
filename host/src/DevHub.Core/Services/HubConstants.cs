namespace DevHub.Core.Services;

/// <summary>
/// Hub 事件类型常量。
/// </summary>
public static class HubEventTypes
{
    public const string AppDefinitionUpserted = "app.definition.upserted";

    public const string AppDefinitionDeleted = "app.definition.deleted";

    public const string AppInstanceRegistered = "app.instance.registered";

    public const string AppInstanceUnregistered = "app.instance.unregistered";

    public const string InvocationQueued = "invocation.queued";

    public const string InvocationDelivered = "invocation.delivered";

    public const string InvocationCompleted = "invocation.completed";

    public const string InvocationFailed = "invocation.failed";
}

/// <summary>
/// Hub RPC 方法名常量。
/// </summary>
public static class HubRpcMethods
{
    [HubRpcMethod(HubRpcMethodCategory.Core, HubRpcMethodTransport.Http | HubRpcMethodTransport.WebSocket, supportsNotification: true)]
    public const string HubPing = "hub.ping";

    [HubRpcMethod(HubRpcMethodCategory.Core, HubRpcMethodTransport.Http | HubRpcMethodTransport.WebSocket, supportsNotification: true)]
    public const string HubGetVersion = "hub.getVersion";

    [HubRpcMethod(HubRpcMethodCategory.WebSocketSession, HubRpcMethodTransport.WebSocket, supportsNotification: false)]
    public const string HubWsAuthenticate = "hub.ws.authenticate";

    [HubRpcMethod(HubRpcMethodCategory.Events, HubRpcMethodTransport.WebSocket, supportsNotification: true, clientCallable: false)]
    public const string HubEvent = "hub.event";

    [HubRpcMethod(HubRpcMethodCategory.Events, HubRpcMethodTransport.WebSocket, supportsNotification: false)]
    public const string HubEventsSubscribe = "hub.events.subscribe";

    [HubRpcMethod(HubRpcMethodCategory.Events, HubRpcMethodTransport.WebSocket, supportsNotification: true)]
    public const string HubEventsUnsubscribe = "hub.events.unsubscribe";

    [HubRpcMethod(HubRpcMethodCategory.AppDefinitions, HubRpcMethodTransport.Http | HubRpcMethodTransport.WebSocket, supportsNotification: true)]
    public const string HubAppsListDefinitions = "hub.apps.listDefinitions";

    [HubRpcMethod(HubRpcMethodCategory.AppDefinitions, HubRpcMethodTransport.Http | HubRpcMethodTransport.WebSocket, supportsNotification: true)]
    public const string HubAppsGetDefinition = "hub.apps.getDefinition";

    [HubRpcMethod(HubRpcMethodCategory.AppDefinitions, HubRpcMethodTransport.Http, supportsNotification: false)]
    public const string HubAppsValidateDefinition = "hub.apps.validateDefinition";

    [HubRpcMethod(HubRpcMethodCategory.AppDefinitions, HubRpcMethodTransport.Http, supportsNotification: false)]
    public const string HubAppsUpsertDefinition = "hub.apps.upsertDefinition";

    [HubRpcMethod(HubRpcMethodCategory.AppDefinitions, HubRpcMethodTransport.Http, supportsNotification: false)]
    public const string HubAppsDeleteDefinition = "hub.apps.deleteDefinition";

    [HubRpcMethod(HubRpcMethodCategory.AppInstances, HubRpcMethodTransport.Http, supportsNotification: false)]
    public const string HubAppsRegisterInstance = "hub.apps.registerInstance";

    [HubRpcMethod(HubRpcMethodCategory.AppInstances, HubRpcMethodTransport.Http, supportsNotification: true)]
    public const string HubAppsHeartbeat = "hub.apps.heartbeat";

    [HubRpcMethod(HubRpcMethodCategory.AppInstances, HubRpcMethodTransport.Http, supportsNotification: true)]
    public const string HubAppsUnregisterInstance = "hub.apps.unregisterInstance";

    [HubRpcMethod(HubRpcMethodCategory.AppInstances, HubRpcMethodTransport.Http | HubRpcMethodTransport.WebSocket, supportsNotification: true)]
    public const string HubAppsListInstances = "hub.apps.listInstances";

    [HubRpcMethod(HubRpcMethodCategory.AppInstances, HubRpcMethodTransport.Http | HubRpcMethodTransport.WebSocket, supportsNotification: true)]
    public const string HubAppsGetInstance = "hub.apps.getInstance";

    [HubRpcMethod(HubRpcMethodCategory.AppLaunch, HubRpcMethodTransport.Http, supportsNotification: false)]
    public const string HubAppsLaunch = "hub.apps.launch";

    [HubRpcMethod(HubRpcMethodCategory.Invocation, HubRpcMethodTransport.Http, supportsNotification: false)]
    public const string HubInvokeNotify = "hub.invoke.notify";

    [HubRpcMethod(HubRpcMethodCategory.Invocation, HubRpcMethodTransport.Http, supportsNotification: false)]
    public const string HubInvokeRequest = "hub.invoke.request";

    [HubRpcMethod(HubRpcMethodCategory.Invocation, HubRpcMethodTransport.Http, supportsNotification: false)]
    public const string HubInvokePoll = "hub.invoke.poll";

    [HubRpcMethod(HubRpcMethodCategory.Invocation, HubRpcMethodTransport.Http, supportsNotification: false)]
    public const string HubInvokeRespond = "hub.invoke.respond";
}
