using System.Text.Json;
using DevHub.Sdk.Internal;
using DevHub.Sdk.Models;

namespace DevHub.Sdk;

/// <summary>
/// DevHub HTTP JSON-RPC 客户端。
/// </summary>
public sealed class DevHubClient : IAsyncDisposable
{
    private readonly JsonRpcHttpTransport _transport;

    private DevHubClient(DevHubClientOptions options, RuntimeConnectionInfo connectionInfo, JsonRpcHttpTransport transport)
    {
        Options = options;
        ConnectionInfo = connectionInfo;
        _transport = transport;
    }

    /// <summary>
    /// 客户端选项。
    /// </summary>
    public DevHubClientOptions Options { get; }

    /// <summary>
    /// 当前连接的运行时信息。
    /// </summary>
    public HubRuntime Runtime => ConnectionInfo.Runtime;

    internal RuntimeConnectionInfo ConnectionInfo { get; }

    /// <summary>
    /// 通过运行时目录创建客户端。
    /// </summary>
    /// <param name="options">客户端选项。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>客户端实例。</returns>
    public static async Task<DevHubClient> FromRuntimeAsync(DevHubClientOptions options, CancellationToken cancellationToken = default)
    {
        var clonedOptions = options?.Clone() ?? throw new ArgumentNullException(nameof(options));
        var connectionInfo = await RuntimeDiscovery.DiscoverAsync(clonedOptions, cancellationToken);
        var transport = JsonRpcHttpTransport.Create(clonedOptions, connectionInfo);
        return new DevHubClient(clonedOptions, connectionInfo, transport);
    }

    internal static async Task<DevHubClient> FromRuntimeAsync(
        DevHubClientOptions options,
        HttpMessageHandler handler,
        Func<string>? requestIdFactory = null,
        CancellationToken cancellationToken = default)
    {
        var clonedOptions = options?.Clone() ?? throw new ArgumentNullException(nameof(options));
        var connectionInfo = await RuntimeDiscovery.DiscoverAsync(clonedOptions, cancellationToken);
        var transport = JsonRpcHttpTransport.Create(clonedOptions, connectionInfo, handler, requestIdFactory);
        return new DevHubClient(clonedOptions, connectionInfo, transport);
    }

    /// <summary>
    /// 调用 <c>hub.ping</c>。
    /// </summary>
    /// <param name="echo">可选回显参数。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>Ping 结果。</returns>
    public async Task<PingResult> PingAsync(object? echo = null, CancellationToken cancellationToken = default)
    {
        object? parameters = echo is null ? null : new Dictionary<string, object?> { ["echo"] = echo };
        var result = await _transport.SendAsync("hub.ping", parameters, cancellationToken);
        var payload = ResponsePayloadReader.DeserializeRequired<PingResult>(result, "hub.ping.result");
        ResponsePayloadReader.EnsureOk(payload.Ok, "hub.ping.result");
        ResponsePayloadReader.EnsureTimestamp(payload.ServerTimeUtc, "hub.ping.result", "serverTimeUtc");
        return payload;
    }

    /// <summary>
    /// 调用 <c>hub.apps.listDefinitions</c>。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>应用定义列表。</returns>
    public async Task<IReadOnlyList<AppDefinition>> ListDefinitionsAsync(CancellationToken cancellationToken = default)
    {
        var result = await _transport.SendAsync("hub.apps.listDefinitions", null, cancellationToken);
        var definitionsElement = ResponsePayloadReader.EnsurePropertyExists(result, "hub.apps.listDefinitions.result", "definitions", JsonValueKind.Array);
        var payload = ResponsePayloadReader.DeserializeRequired<ListDefinitionsContract>(result, "hub.apps.listDefinitions.result");
        ResponsePayloadReader.EnsureOk(payload.Ok, "hub.apps.listDefinitions.result");
        ResponsePayloadReader.EnsureNotNull(payload.Definitions, "hub.apps.listDefinitions.result", "definitions");

        var index = 0;
        foreach (var definitionElement in definitionsElement.EnumerateArray())
        {
            ResponsePayloadReader.ValidateAppDefinitionElement(definitionElement, $"hub.apps.listDefinitions.result.definitions[{index}]");
            index++;
        }

        return payload.Definitions;
    }

    /// <summary>
    /// 调用 <c>hub.apps.getDefinition</c>。
    /// </summary>
    /// <param name="appId">应用标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>应用定义。</returns>
    public async Task<AppDefinition> GetDefinitionAsync(string appId, CancellationToken cancellationToken = default)
    {
        var result = await _transport.SendAsync("hub.apps.getDefinition", RequestPayloadFactory.BuildGetDefinitionParams(appId), cancellationToken);
        ResponsePayloadReader.ValidateAppDefinitionElement(
            ResponsePayloadReader.EnsurePropertyExists(result, "hub.apps.getDefinition.result", "definition", JsonValueKind.Object),
            "hub.apps.getDefinition.result.definition");

        var payload = ResponsePayloadReader.DeserializeRequired<GetDefinitionContract>(result, "hub.apps.getDefinition.result");
        ResponsePayloadReader.EnsureOk(payload.Ok, "hub.apps.getDefinition.result");
        ResponsePayloadReader.EnsureNotNull(payload.Definition, "hub.apps.getDefinition.result", "definition");
        ResponsePayloadReader.EnsureNotEmpty(payload.Definition.AppId, "hub.apps.getDefinition.result", "definition.appId");
        ResponsePayloadReader.EnsureNotEmpty(payload.Definition.DisplayName, "hub.apps.getDefinition.result", "definition.displayName");
        return payload.Definition;
    }

    /// <summary>
    /// 调用 <c>hub.apps.registerInstance</c>。
    /// </summary>
    /// <param name="instance">实例注册载荷。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>注册后的实例。</returns>
    public async Task<AppInstance> RegisterInstanceAsync(AppInstanceRegistration instance, CancellationToken cancellationToken = default)
    {
        var result = await _transport.SendAsync("hub.apps.registerInstance", RequestPayloadFactory.BuildRegisterInstanceParams(instance), cancellationToken);
        ResponsePayloadReader.ValidateAppInstanceElement(
            ResponsePayloadReader.EnsurePropertyExists(result, "hub.apps.registerInstance.result", "instance", JsonValueKind.Object),
            "hub.apps.registerInstance.result.instance");

        var payload = ResponsePayloadReader.DeserializeRequired<RegisterInstanceContract>(result, "hub.apps.registerInstance.result");
        ResponsePayloadReader.EnsureOk(payload.Ok, "hub.apps.registerInstance.result");
        ResponsePayloadReader.EnsureNotNull(payload.Instance, "hub.apps.registerInstance.result", "instance");
        ResponsePayloadReader.EnsureNotEmpty(payload.Instance.InstanceId, "hub.apps.registerInstance.result", "instance.instanceId");
        ResponsePayloadReader.EnsureNotEmpty(payload.Instance.AppId, "hub.apps.registerInstance.result", "instance.appId");
        ResponsePayloadReader.EnsureTimestamp(payload.Instance.RegisteredAtUtc, "hub.apps.registerInstance.result", "instance.registeredAtUtc");
        ResponsePayloadReader.EnsureTimestamp(payload.Instance.LastSeenUtc, "hub.apps.registerInstance.result", "instance.lastSeenUtc");
        return payload.Instance;
    }

    /// <summary>
    /// 调用 <c>hub.apps.heartbeat</c>。
    /// </summary>
    /// <param name="instanceId">实例标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>服务端返回的最后在线时间。</returns>
    public async Task<DateTimeOffset> HeartbeatAsync(string instanceId, CancellationToken cancellationToken = default)
    {
        var result = await _transport.SendAsync("hub.apps.heartbeat", RequestPayloadFactory.BuildHeartbeatParams(instanceId), cancellationToken);
        var payload = ResponsePayloadReader.DeserializeRequired<HeartbeatContract>(result, "hub.apps.heartbeat.result");
        ResponsePayloadReader.EnsureOk(payload.Ok, "hub.apps.heartbeat.result");
        ResponsePayloadReader.EnsureTimestamp(payload.LastSeenUtc, "hub.apps.heartbeat.result", "lastSeenUtc");
        return payload.LastSeenUtc;
    }

    /// <summary>
    /// 调用 <c>hub.apps.unregisterInstance</c>。
    /// </summary>
    /// <param name="instanceId">实例标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task UnregisterInstanceAsync(string instanceId, CancellationToken cancellationToken = default)
    {
        var result = await _transport.SendAsync("hub.apps.unregisterInstance", RequestPayloadFactory.BuildUnregisterParams(instanceId), cancellationToken);
        var payload = ResponsePayloadReader.DeserializeRequired<OkOnlyContract>(result, "hub.apps.unregisterInstance.result");
        ResponsePayloadReader.EnsureOk(payload.Ok, "hub.apps.unregisterInstance.result");
    }

    /// <summary>
    /// 调用 <c>hub.apps.listInstances</c>。
    /// </summary>
    /// <param name="request">过滤参数。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>实例列表。</returns>
    public async Task<IReadOnlyList<AppInstance>> ListInstancesAsync(ListInstancesRequest? request = null, CancellationToken cancellationToken = default)
    {
        var result = await _transport.SendAsync("hub.apps.listInstances", RequestPayloadFactory.BuildListInstancesParams(request), cancellationToken);
        var instancesElement = ResponsePayloadReader.EnsurePropertyExists(result, "hub.apps.listInstances.result", "instances", JsonValueKind.Array);
        var payload = ResponsePayloadReader.DeserializeRequired<ListInstancesContract>(result, "hub.apps.listInstances.result");
        ResponsePayloadReader.EnsureOk(payload.Ok, "hub.apps.listInstances.result");
        ResponsePayloadReader.EnsureNotNull(payload.Instances, "hub.apps.listInstances.result", "instances");

        var index = 0;
        foreach (var instanceElement in instancesElement.EnumerateArray())
        {
            ResponsePayloadReader.ValidateAppInstanceElement(instanceElement, $"hub.apps.listInstances.result.instances[{index}]");
            index++;
        }

        return payload.Instances;
    }

    /// <summary>
    /// 调用 <c>hub.apps.launch</c>。
    /// </summary>
    /// <param name="request">启动请求。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>启动结果。</returns>
    public async Task<LaunchResult> LaunchAsync(LaunchRequest request, CancellationToken cancellationToken = default)
    {
        var result = await _transport.SendAsync("hub.apps.launch", RequestPayloadFactory.BuildLaunchParams(request), cancellationToken);
        var payload = ResponsePayloadReader.DeserializeRequired<LaunchResult>(result, "hub.apps.launch.result");
        ResponsePayloadReader.EnsureOk(payload.Ok, "hub.apps.launch.result");
        ResponsePayloadReader.EnsureNotEmpty(payload.Status, "hub.apps.launch.result", "status");
        ResponsePayloadReader.EnsureNotEmpty(payload.LaunchId, "hub.apps.launch.result", "launchId");
        if (!string.Equals(payload.Status, "started", StringComparison.Ordinal) &&
            !string.Equals(payload.Status, "starting", StringComparison.Ordinal) &&
            !string.Equals(payload.Status, "already_running", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("hub.apps.launch.result 返回结果非法：status 取值不受支持。");
        }

        return payload;
    }

    /// <summary>
    /// 调用 <c>hub.invoke.notify</c>。
    /// </summary>
    /// <param name="request">调用请求。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>通知结果。</returns>
    public async Task<NotifyResult> NotifyAsync(InvokeRequest request, CancellationToken cancellationToken = default)
    {
        var result = await _transport.SendAsync("hub.invoke.notify", RequestPayloadFactory.BuildNotifyParams(request), cancellationToken);
        var payload = ResponsePayloadReader.DeserializeRequired<NotifyResult>(result, "hub.invoke.notify.result");
        ResponsePayloadReader.EnsureOk(payload.Ok, "hub.invoke.notify.result");
        ResponsePayloadReader.EnsureNotEmpty(payload.InvocationId, "hub.invoke.notify.result", "invocationId");
        return payload;
    }

    /// <summary>
    /// 调用 <c>hub.invoke.request</c>。
    /// </summary>
    /// <param name="request">调用请求。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>请求结果。</returns>
    public async Task<RequestResult> RequestAsync(InvokeRequest request, CancellationToken cancellationToken = default)
    {
        var result = await _transport.SendAsync("hub.invoke.request", RequestPayloadFactory.BuildRequestParams(request), cancellationToken);
        ResponsePayloadReader.EnsurePropertyExists(result, "hub.invoke.request.result", "value");
        var payload = ResponsePayloadReader.DeserializeRequired<RequestResult>(result, "hub.invoke.request.result");
        ResponsePayloadReader.EnsureOk(payload.Ok, "hub.invoke.request.result");
        ResponsePayloadReader.EnsureNotEmpty(payload.InvocationId, "hub.invoke.request.result", "invocationId");
        return payload;
    }

    /// <summary>
    /// 调用 <c>hub.invoke.poll</c>。
    /// </summary>
    /// <param name="request">轮询请求。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>轮询结果。</returns>
    public async Task<PollResult> PollAsync(PollRequest request, CancellationToken cancellationToken = default)
    {
        var result = await _transport.SendAsync("hub.invoke.poll", RequestPayloadFactory.BuildPollParams(request), cancellationToken);
        var itemsElement = ResponsePayloadReader.EnsurePropertyExists(result, "hub.invoke.poll.result", "items", JsonValueKind.Array);

        var index = 0;
        foreach (var itemElement in itemsElement.EnumerateArray())
        {
            ResponsePayloadReader.ValidateInvocationElement(itemElement, $"hub.invoke.poll.result.items[{index}]");
            index++;
        }

        var payload = ResponsePayloadReader.DeserializeRequired<PollResult>(result, "hub.invoke.poll.result");
        ResponsePayloadReader.EnsureOk(payload.Ok, "hub.invoke.poll.result");
        ResponsePayloadReader.EnsureTimestamp(payload.ServerTimeUtc, "hub.invoke.poll.result", "serverTimeUtc");
        ResponsePayloadReader.EnsureNotNull(payload.Items, "hub.invoke.poll.result", "items");
        return payload;
    }

    /// <summary>
    /// 调用 <c>hub.invoke.respond</c>。
    /// </summary>
    /// <param name="request">响应请求。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task RespondAsync(RespondRequest request, CancellationToken cancellationToken = default)
    {
        var result = await _transport.SendAsync("hub.invoke.respond", RequestPayloadFactory.BuildRespondParams(request), cancellationToken);
        var payload = ResponsePayloadReader.DeserializeRequired<OkOnlyContract>(result, "hub.invoke.respond.result");
        ResponsePayloadReader.EnsureOk(payload.Ok, "hub.invoke.respond.result");
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        return _transport.DisposeAsync();
    }
}
