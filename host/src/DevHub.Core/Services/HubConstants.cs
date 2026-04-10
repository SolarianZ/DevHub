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
    public const string HubPing = "hub.ping";

    public const string HubWsAuthenticate = "hub.ws.authenticate";

    public const string HubEvent = "hub.event";

    public const string HubEventsSubscribe = "hub.events.subscribe";

    public const string HubEventsUnsubscribe = "hub.events.unsubscribe";

    public const string HubAppsListDefinitions = "hub.apps.listDefinitions";

    public const string HubAppsGetDefinition = "hub.apps.getDefinition";

    public const string HubAppsValidateDefinition = "hub.apps.validateDefinition";

    public const string HubAppsUpsertDefinition = "hub.apps.upsertDefinition";

    public const string HubAppsDeleteDefinition = "hub.apps.deleteDefinition";

    public const string HubAppsRegisterInstance = "hub.apps.registerInstance";

    public const string HubAppsHeartbeat = "hub.apps.heartbeat";

    public const string HubAppsUnregisterInstance = "hub.apps.unregisterInstance";

    public const string HubAppsListInstances = "hub.apps.listInstances";

    public const string HubAppsLaunch = "hub.apps.launch";

    public const string HubInvokeNotify = "hub.invoke.notify";

    public const string HubInvokeRequest = "hub.invoke.request";

    public const string HubInvokePoll = "hub.invoke.poll";

    public const string HubInvokeRespond = "hub.invoke.respond";
}
