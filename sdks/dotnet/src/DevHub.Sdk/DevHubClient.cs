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
        var payload = DeserializeRequired<PingResult>(result, "hub.ping.result");
        EnsureOk(payload.Ok, "hub.ping.result");
        EnsureTimestamp(payload.ServerTimeUtc, "hub.ping.result", "serverTimeUtc");
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
        var definitionsElement = EnsurePropertyExists(result, "hub.apps.listDefinitions.result", "definitions", JsonValueKind.Array);
        var payload = DeserializeRequired<ListDefinitionsContract>(result, "hub.apps.listDefinitions.result");
        EnsureOk(payload.Ok, "hub.apps.listDefinitions.result");
        EnsureNotNull(payload.Definitions, "hub.apps.listDefinitions.result", "definitions");

        var index = 0;
        foreach (var definitionElement in definitionsElement.EnumerateArray())
        {
            ValidateAppDefinitionElement(definitionElement, $"hub.apps.listDefinitions.result.definitions[{index}]");
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
        ValidateAppDefinitionElement(
            EnsurePropertyExists(result, "hub.apps.getDefinition.result", "definition", JsonValueKind.Object),
            "hub.apps.getDefinition.result.definition");

        var payload = DeserializeRequired<GetDefinitionContract>(result, "hub.apps.getDefinition.result");
        EnsureOk(payload.Ok, "hub.apps.getDefinition.result");
        EnsureNotNull(payload.Definition, "hub.apps.getDefinition.result", "definition");
        EnsureNotEmpty(payload.Definition.AppId, "hub.apps.getDefinition.result", "definition.appId");
        EnsureNotEmpty(payload.Definition.DisplayName, "hub.apps.getDefinition.result", "definition.displayName");
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
        ValidateAppInstanceElement(
            EnsurePropertyExists(result, "hub.apps.registerInstance.result", "instance", JsonValueKind.Object),
            "hub.apps.registerInstance.result.instance");

        var payload = DeserializeRequired<RegisterInstanceContract>(result, "hub.apps.registerInstance.result");
        EnsureOk(payload.Ok, "hub.apps.registerInstance.result");
        EnsureNotNull(payload.Instance, "hub.apps.registerInstance.result", "instance");
        EnsureNotEmpty(payload.Instance.InstanceId, "hub.apps.registerInstance.result", "instance.instanceId");
        EnsureNotEmpty(payload.Instance.AppId, "hub.apps.registerInstance.result", "instance.appId");
        EnsureTimestamp(payload.Instance.RegisteredAtUtc, "hub.apps.registerInstance.result", "instance.registeredAtUtc");
        EnsureTimestamp(payload.Instance.LastSeenUtc, "hub.apps.registerInstance.result", "instance.lastSeenUtc");
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
        EnsureOk(payload.Ok, "hub.apps.heartbeat.result");
        EnsureTimestamp(payload.LastSeenUtc, "hub.apps.heartbeat.result", "lastSeenUtc");
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
        var payload = DeserializeRequired<OkOnlyContract>(result, "hub.apps.unregisterInstance.result");
        EnsureOk(payload.Ok, "hub.apps.unregisterInstance.result");
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
        var instancesElement = EnsurePropertyExists(result, "hub.apps.listInstances.result", "instances", JsonValueKind.Array);
        var payload = DeserializeRequired<ListInstancesContract>(result, "hub.apps.listInstances.result");
        EnsureOk(payload.Ok, "hub.apps.listInstances.result");
        EnsureNotNull(payload.Instances, "hub.apps.listInstances.result", "instances");

        var index = 0;
        foreach (var instanceElement in instancesElement.EnumerateArray())
        {
            ValidateAppInstanceElement(instanceElement, $"hub.apps.listInstances.result.instances[{index}]");
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
        var payload = DeserializeRequired<LaunchResult>(result, "hub.apps.launch.result");
        EnsureOk(payload.Ok, "hub.apps.launch.result");
        EnsureNotEmpty(payload.Status, "hub.apps.launch.result", "status");
        EnsureNotEmpty(payload.LaunchId, "hub.apps.launch.result", "launchId");
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
        var payload = DeserializeRequired<NotifyResult>(result, "hub.invoke.notify.result");
        EnsureOk(payload.Ok, "hub.invoke.notify.result");
        EnsureNotEmpty(payload.InvocationId, "hub.invoke.notify.result", "invocationId");
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
        EnsurePropertyExists(result, "hub.invoke.request.result", "value");
        var payload = DeserializeRequired<RequestResult>(result, "hub.invoke.request.result");
        EnsureOk(payload.Ok, "hub.invoke.request.result");
        EnsureNotEmpty(payload.InvocationId, "hub.invoke.request.result", "invocationId");
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
        var itemsElement = EnsurePropertyExists(result, "hub.invoke.poll.result", "items", JsonValueKind.Array);
        var payload = DeserializeRequired<PollResult>(result, "hub.invoke.poll.result");
        EnsureOk(payload.Ok, "hub.invoke.poll.result");
        EnsureTimestamp(payload.ServerTimeUtc, "hub.invoke.poll.result", "serverTimeUtc");
        EnsureNotNull(payload.Items, "hub.invoke.poll.result", "items");

        var index = 0;
        foreach (var itemElement in itemsElement.EnumerateArray())
        {
            ValidateInvocationElement(itemElement, $"hub.invoke.poll.result.items[{index}]");
            index++;
        }

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
        var payload = DeserializeRequired<OkOnlyContract>(result, "hub.invoke.respond.result");
        EnsureOk(payload.Ok, "hub.invoke.respond.result");
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

    private static void EnsureOk(bool ok, string location)
    {
        if (!ok)
        {
            throw new InvalidOperationException($"{location} 返回结果非法：ok 必须为 true。");
        }
    }

    private static JsonElement EnsurePropertyExists(JsonElement payload, string location, string propertyName, JsonValueKind? expectedKind = null)
    {
        if (!payload.TryGetProperty(propertyName, out var propertyValue))
        {
            throw new InvalidOperationException($"{location} 返回结果非法：{propertyName} 不能为空。");
        }

        if (expectedKind is { } kind && propertyValue.ValueKind != kind)
        {
            throw new InvalidOperationException($"{location} 返回结果非法：{propertyName} 类型非法。");
        }

        return propertyValue;
    }

    private static void EnsureTimestamp(DateTimeOffset? value, string location, string propertyName)
    {
        if (value is null || value.Value == default)
        {
            throw new InvalidOperationException($"{location} 返回结果非法：{propertyName} 不能为空默认值。");
        }
    }

    private static void EnsureNotEmpty(string? value, string location, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"{location} 返回结果非法：{propertyName} 不能为空。");
        }
    }

    private static void EnsureNotNull<T>(T? value, string location, string propertyName)
        where T : class
    {
        if (value is null)
        {
            throw new InvalidOperationException($"{location} 返回结果非法：{propertyName} 不能为空。");
        }
    }

    private static void ValidateAppDefinitionElement(JsonElement element, string location)
    {
        EnsureElementKind(element, location, JsonValueKind.Object);
        EnsureStringProperty(element, location, "appId");
        EnsureStringProperty(element, location, "displayName");

        if (element.TryGetProperty("launch", out var launchElement))
        {
            if (launchElement.ValueKind == JsonValueKind.Null)
            {
                return;
            }

            EnsureElementKind(launchElement, $"{location}.launch", JsonValueKind.Object);
            EnsureStringProperty(launchElement, $"{location}.launch", "exePath");
        }
    }

    private static void ValidateAppInstanceElement(JsonElement element, string location)
    {
        EnsureElementKind(element, location, JsonValueKind.Object);
        EnsureStringProperty(element, location, "instanceId");
        EnsureStringProperty(element, location, "appId");
        EnsurePositiveIntegerProperty(element, location, "pid");
        EnsureStringProperty(element, location, "registeredAtUtc");
        EnsureStringProperty(element, location, "lastSeenUtc");

        var invokeElement = EnsurePropertyExists(element, location, "invoke", JsonValueKind.Object);
        EnsureBooleanProperty(invokeElement, $"{location}.invoke", "poll");
        EnsureBooleanProperty(invokeElement, $"{location}.invoke", "respond");
    }

    private static void ValidateInvocationElement(JsonElement element, string location)
    {
        EnsureElementKind(element, location, JsonValueKind.Object);
        EnsureStringProperty(element, location, "invocationId");
        EnsureStringProperty(element, location, "appId");
        EnsurePropertyExists(element, location, "target", JsonValueKind.Object);
        EnsureStringProperty(element, location, "method");
        EnsureStringProperty(element, location, "kind");
        EnsureStringProperty(element, location, "createdAtUtc");

        var callerElement = EnsurePropertyExists(element, location, "caller", JsonValueKind.Object);
        EnsureStringProperty(callerElement, $"{location}.caller", "clientId");
        EnsureStringProperty(callerElement, $"{location}.caller", "clientSessionId");

        if (element.TryGetProperty("delivery", out var deliveryElement) && deliveryElement.ValueKind != JsonValueKind.Null)
        {
            EnsureElementKind(deliveryElement, $"{location}.delivery", JsonValueKind.Object);
            EnsurePositiveIntegerProperty(deliveryElement, $"{location}.delivery", "leaseSeconds");
            EnsurePositiveIntegerProperty(deliveryElement, $"{location}.delivery", "attempt");
        }
    }

    private static void EnsureElementKind(JsonElement element, string location, JsonValueKind expectedKind)
    {
        if (element.ValueKind != expectedKind)
        {
            throw new InvalidOperationException($"{location} 返回结果非法：JSON 类型非法。");
        }
    }

    private static void EnsureStringProperty(JsonElement element, string location, string propertyName)
    {
        var propertyValue = EnsurePropertyExists(element, location, propertyName, JsonValueKind.String);
        if (string.IsNullOrWhiteSpace(propertyValue.GetString()))
        {
            throw new InvalidOperationException($"{location} 返回结果非法：{propertyName} 不能为空。");
        }
    }

    private static void EnsureBooleanProperty(JsonElement element, string location, string propertyName)
    {
        var propertyValue = EnsurePropertyExists(element, location, propertyName);
        if (propertyValue.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
        {
            throw new InvalidOperationException($"{location} 返回结果非法：{propertyName} 类型非法。");
        }
    }

    private static void EnsurePositiveIntegerProperty(JsonElement element, string location, string propertyName)
    {
        var propertyValue = EnsurePropertyExists(element, location, propertyName, JsonValueKind.Number);
        if (!propertyValue.TryGetInt32(out var value) || value < 1)
        {
            throw new InvalidOperationException($"{location} 返回结果非法：{propertyName} 必须大于等于 1。");
        }
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
