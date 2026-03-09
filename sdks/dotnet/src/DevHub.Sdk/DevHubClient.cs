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
        return DeserializeRequired<PingResult>(result, "hub.ping.result");
    }

    /// <summary>
    /// 调用 <c>hub.apps.listDefinitions</c>。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>应用定义列表。</returns>
    public async Task<IReadOnlyList<AppDefinition>> ListDefinitionsAsync(CancellationToken cancellationToken = default)
    {
        var result = await _transport.SendAsync("hub.apps.listDefinitions", null, cancellationToken);
        var payload = DeserializeRequired<ListDefinitionsContract>(result, "hub.apps.listDefinitions.result");
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
        var payload = DeserializeRequired<GetDefinitionContract>(result, "hub.apps.getDefinition.result");
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
        var payload = DeserializeRequired<RegisterInstanceContract>(result, "hub.apps.registerInstance.result");
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
        var payload = DeserializeRequired<HeartbeatContract>(result, "hub.apps.heartbeat.result");
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
        _ = DeserializeRequired<OkOnlyContract>(result, "hub.apps.unregisterInstance.result");
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
        var payload = DeserializeRequired<ListInstancesContract>(result, "hub.apps.listInstances.result");
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
        return DeserializeRequired<LaunchResult>(result, "hub.apps.launch.result");
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
        return DeserializeRequired<NotifyResult>(result, "hub.invoke.notify.result");
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
        return DeserializeRequired<RequestResult>(result, "hub.invoke.request.result");
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
        return DeserializeRequired<PollResult>(result, "hub.invoke.poll.result");
    }

    /// <summary>
    /// 调用 <c>hub.invoke.respond</c>。
    /// </summary>
    /// <param name="request">响应请求。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task RespondAsync(RespondRequest request, CancellationToken cancellationToken = default)
    {
        var result = await _transport.SendAsync("hub.invoke.respond", RequestPayloadFactory.BuildRespondParams(request), cancellationToken);
        _ = DeserializeRequired<OkOnlyContract>(result, "hub.invoke.respond.result");
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        return _transport.DisposeAsync();
    }

    private static T DeserializeRequired<T>(JsonElement result, string location)
    {
        var value = JsonSerializer.Deserialize<T>(result.GetRawText(), DevHubJson.SerializerOptions);
        return value ?? throw new InvalidOperationException($"无法解析 {location}。");
    }

    private sealed class OkOnlyContract
    {
        public bool Ok { get; set; }
    }

    private sealed class ListDefinitionsContract
    {
        public bool Ok { get; set; }

        public List<AppDefinition> Definitions { get; set; } = [];
    }

    private sealed class GetDefinitionContract
    {
        public bool Ok { get; set; }

        public AppDefinition Definition { get; set; } = new();
    }

    private sealed class RegisterInstanceContract
    {
        public bool Ok { get; set; }

        public AppInstance Instance { get; set; } = new();
    }

    private sealed class HeartbeatContract
    {
        public bool Ok { get; set; }

        public DateTimeOffset LastSeenUtc { get; set; }
    }

    private sealed class ListInstancesContract
    {
        public bool Ok { get; set; }

        public List<AppInstance> Instances { get; set; } = [];
    }
}
