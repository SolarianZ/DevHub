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
    private bool _disposed;

    private DevHubClient(DevHubClientOptions options, DevHubRuntimeConnectionInfo connectionInfo, JsonRpcHttpTransport transport)
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

    internal DevHubRuntimeConnectionInfo ConnectionInfo { get; }

    /// <summary>
    /// 通过运行时发现信息创建客户端。
    /// </summary>
    /// <param name="options">客户端选项。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>客户端实例。</returns>
    public static async Task<DevHubClient> FromRuntimeAsync(DevHubClientOptions options, CancellationToken cancellationToken = default)
    {
        return await FromRuntimeAsync(options, dependencies: null, cancellationToken);
    }

    /// <summary>
    /// 通过运行时发现信息创建客户端，并允许注入公开扩展点。
    /// </summary>
    /// <param name="options">客户端选项。</param>
    /// <param name="dependencies">公开扩展点依赖项。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>客户端实例。</returns>
    public static async Task<DevHubClient> FromRuntimeAsync(
        DevHubClientOptions options,
        DevHubClientDependencies? dependencies,
        CancellationToken cancellationToken = default)
    {
        var clonedOptions = options?.Clone() ?? throw new ArgumentNullException(nameof(options));
        clonedOptions.Validate();

        dependencies ??= new DevHubClientDependencies();
        var connectionInfo = await dependencies.RuntimeResolver.ResolveAsync(clonedOptions, cancellationToken);
        var httpClient = dependencies.HttpClientProvider.CreateClient(clonedOptions, connectionInfo);
        var transport = new JsonRpcHttpTransport(httpClient, clonedOptions, connectionInfo, ownsHttpClient: true);
        return new DevHubClient(clonedOptions, connectionInfo, transport);
    }

    internal static async Task<DevHubClient> FromRuntimeAsync(
        DevHubClientOptions options,
        HttpMessageHandler handler,
        Func<string>? requestIdFactory = null,
        CancellationToken cancellationToken = default)
    {
        var clonedOptions = options?.Clone() ?? throw new ArgumentNullException(nameof(options));
        clonedOptions.Validate();

        var connectionInfo = await new FileSystemDevHubRuntimeResolver().ResolveAsync(clonedOptions, cancellationToken);
        var httpClient = JsonRpcHttpTransport.CreateHttpClient(handler);
        var transport = new JsonRpcHttpTransport(httpClient, clonedOptions, connectionInfo, requestIdFactory, ownsHttpClient: true);
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
        ThrowIfDisposed();
        return await ReadOnlyRpcExecutor.PingAsync(_transport.SendAsync, echo, cancellationToken);
    }

    /// <summary>
    /// 调用 <c>hub.apps.listDefinitions</c>。
    /// </summary>
    /// <param name="request">过滤参数。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>应用定义列表。</returns>
    public async Task<IReadOnlyList<AppDefinition>> ListDefinitionsAsync(ListDefinitionsRequest request, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return await ReadOnlyRpcExecutor.ListDefinitionsAsync(_transport.SendAsync, request, cancellationToken);
    }

    /// <summary>
    /// 调用 <c>hub.apps.getDefinition</c>。
    /// </summary>
    /// <param name="appId">应用标识。</param>
    /// <param name="scope">Definition 作用域。空字符串表示 Global Definition。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>应用定义。</returns>
    public async Task<AppDefinition> GetDefinitionAsync(string appId, string scope, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return await ReadOnlyRpcExecutor.GetDefinitionAsync(_transport.SendAsync, appId, scope, cancellationToken);
    }

    /// <summary>
    /// 调用 <c>hub.apps.validateDefinition</c>。
    /// </summary>
    /// <param name="definition">候选应用定义。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>定义校验结果。</returns>
    public async Task<DefinitionValidationResult> ValidateDefinitionAsync(
        AppDefinition definition,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var result = await _transport.SendAsync(
            "hub.apps.validateDefinition",
            RequestPayloadFactory.BuildValidateDefinitionParams(definition),
            cancellationToken);

        var errorsElement = ResponsePayloadReader.EnsurePropertyExists(
            result,
            "hub.apps.validateDefinition.result",
            "errors",
            JsonValueKind.Array);
        ResponsePayloadReader.ValidateValidationIssuesElement(errorsElement, "hub.apps.validateDefinition.result.errors");

        var payload = ResponsePayloadReader.DeserializeRequired<DefinitionValidationContract>(
            result,
            "hub.apps.validateDefinition.result");
        ResponsePayloadReader.EnsureOk(payload.Ok, "hub.apps.validateDefinition.result");
        ResponsePayloadReader.EnsureNotNull(payload.Errors, "hub.apps.validateDefinition.result", "errors");

        if (payload.Valid && payload.Errors.Count != 0)
        {
            throw new InvalidOperationException("hub.apps.validateDefinition.result 返回结果非法：valid=true 时 errors 必须为空。");
        }

        if (!payload.Valid && payload.Errors.Count == 0)
        {
            throw new InvalidOperationException("hub.apps.validateDefinition.result 返回结果非法：valid=false 时 errors 不能为空。");
        }

        return new DefinitionValidationResult
        {
            Ok = payload.Ok,
            Valid = payload.Valid,
            Errors = payload.Errors
        };
    }

    /// <summary>
    /// 调用 <c>hub.apps.upsertDefinition</c>。
    /// </summary>
    /// <param name="definition">待创建或更新的应用定义。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>最新生效的应用定义。</returns>
    public async Task<AppDefinition> UpsertDefinitionAsync(AppDefinition definition, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var result = await _transport.SendAsync(
            "hub.apps.upsertDefinition",
            RequestPayloadFactory.BuildUpsertDefinitionParams(definition),
            cancellationToken);
        ResponsePayloadReader.ValidateAppDefinitionElement(
            ResponsePayloadReader.EnsurePropertyExists(result, "hub.apps.upsertDefinition.result", "definition", JsonValueKind.Object),
            "hub.apps.upsertDefinition.result.definition");

        var payload = ResponsePayloadReader.DeserializeRequired<GetDefinitionContract>(result, "hub.apps.upsertDefinition.result");
        ResponsePayloadReader.EnsureOk(payload.Ok, "hub.apps.upsertDefinition.result");
        ResponsePayloadReader.EnsureNotNull(payload.Definition, "hub.apps.upsertDefinition.result", "definition");
        ResponsePayloadReader.EnsureNotEmpty(payload.Definition.AppId, "hub.apps.upsertDefinition.result", "definition.appId");
        ResponsePayloadReader.EnsureNotEmpty(payload.Definition.DisplayName, "hub.apps.upsertDefinition.result", "definition.displayName");
        return payload.Definition;
    }

    /// <summary>
    /// 调用 <c>hub.apps.deleteDefinition</c>。
    /// </summary>
    /// <param name="appId">应用标识。</param>
    /// <param name="scope">Definition 作用域。空字符串表示 Global Definition。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task DeleteDefinitionAsync(string appId, string scope, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var result = await _transport.SendAsync(
            "hub.apps.deleteDefinition",
            RequestPayloadFactory.BuildDeleteDefinitionParams(appId, scope),
            cancellationToken);
        var payload = ResponsePayloadReader.DeserializeRequired<OkOnlyContract>(result, "hub.apps.deleteDefinition.result");
        ResponsePayloadReader.EnsureOk(payload.Ok, "hub.apps.deleteDefinition.result");
    }

    /// <summary>
    /// 调用 <c>hub.apps.registerInstance</c>。
    /// </summary>
    /// <param name="instance">实例注册载荷。</param>
    /// <param name="password">实例密码。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>注册后的实例。返回值中的 <see cref="AppInstance.InstanceSessionToken"/> 可用于后续心跳、反注册与调用处理。</returns>
    public async Task<AppInstance> RegisterInstanceAsync(
        AppInstanceRegistration instance,
        string password,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentException.ThrowIfNullOrWhiteSpace(password);
        var result = await _transport.SendAsync(
            "hub.apps.registerInstance",
            RequestPayloadFactory.BuildRegisterInstanceParams(instance, password),
            cancellationToken);
        ResponsePayloadReader.ValidateAppInstanceElement(
            ResponsePayloadReader.EnsurePropertyExists(result, "hub.apps.registerInstance.result", "instance", JsonValueKind.Object),
            "hub.apps.registerInstance.result.instance");
        ResponsePayloadReader.EnsurePropertyExists(
            result,
            "hub.apps.registerInstance.result",
            "instanceSessionToken",
            JsonValueKind.String);

        var payload = ResponsePayloadReader.DeserializeRequired<RegisterInstanceContract>(result, "hub.apps.registerInstance.result");
        ResponsePayloadReader.EnsureOk(payload.Ok, "hub.apps.registerInstance.result");
        ResponsePayloadReader.EnsureNotNull(payload.Instance, "hub.apps.registerInstance.result", "instance");
        ResponsePayloadReader.EnsureNotEmpty(payload.InstanceSessionToken, "hub.apps.registerInstance.result", "instanceSessionToken");
        ResponsePayloadReader.EnsureNotEmpty(payload.Instance.InstanceId, "hub.apps.registerInstance.result", "instance.instanceId");
        ResponsePayloadReader.EnsureNotEmpty(payload.Instance.AppId, "hub.apps.registerInstance.result", "instance.appId");
        ResponsePayloadReader.EnsureTimestamp(payload.Instance.RegisteredAtUtc, "hub.apps.registerInstance.result", "instance.registeredAtUtc");
        ResponsePayloadReader.EnsureTimestamp(payload.Instance.LastSeenUtc, "hub.apps.registerInstance.result", "instance.lastSeenUtc");
        payload.Instance.InstanceSessionToken = payload.InstanceSessionToken;
        return payload.Instance;
    }

    /// <summary>
    /// 调用 <c>hub.apps.heartbeat</c>。
    /// </summary>
    /// <param name="instanceId">实例标识。</param>
    /// <param name="instanceSessionToken">实例会话令牌。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>服务端返回的最后在线时间。</returns>
    public async Task<DateTimeOffset> HeartbeatAsync(
        string instanceId,
        string instanceSessionToken,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceSessionToken);
        var result = await _transport.SendAsync(
            "hub.apps.heartbeat",
            RequestPayloadFactory.BuildHeartbeatParams(instanceId, instanceSessionToken),
            cancellationToken);
        var payload = ResponsePayloadReader.DeserializeRequired<HeartbeatContract>(result, "hub.apps.heartbeat.result");
        ResponsePayloadReader.EnsureOk(payload.Ok, "hub.apps.heartbeat.result");
        ResponsePayloadReader.EnsureTimestamp(payload.LastSeenUtc, "hub.apps.heartbeat.result", "lastSeenUtc");
        return payload.LastSeenUtc;
    }

    /// <summary>
    /// 调用 <c>hub.apps.unregisterInstance</c>。
    /// </summary>
    /// <param name="instanceId">实例标识。</param>
    /// <param name="instanceSessionToken">实例会话令牌。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task UnregisterInstanceAsync(
        string instanceId,
        string instanceSessionToken,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceSessionToken);
        var result = await _transport.SendAsync(
            "hub.apps.unregisterInstance",
            RequestPayloadFactory.BuildUnregisterParams(instanceId, instanceSessionToken),
            cancellationToken);
        var payload = ResponsePayloadReader.DeserializeRequired<OkOnlyContract>(result, "hub.apps.unregisterInstance.result");
        ResponsePayloadReader.EnsureOk(payload.Ok, "hub.apps.unregisterInstance.result");
    }

    /// <summary>
    /// 调用 <c>hub.apps.listInstances</c>。
    /// </summary>
    /// <param name="request">过滤参数。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>实例列表。</returns>
    public async Task<IReadOnlyList<AppInstance>> ListInstancesAsync(ListInstancesRequest request, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return await ReadOnlyRpcExecutor.ListInstancesAsync(_transport.SendAsync, request, cancellationToken);
    }

    /// <summary>
    /// 调用 <c>hub.apps.launch</c>。
    /// </summary>
    /// <param name="request">启动请求。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>启动结果。</returns>
    public async Task<LaunchResult> LaunchAsync(LaunchRequest request, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
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
        ThrowIfDisposed();
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
        ThrowIfDisposed();
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
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.InstanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.InstanceSessionToken);
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
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.InstanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.InstanceSessionToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.InvocationId);
        var result = await _transport.SendAsync("hub.invoke.respond", RequestPayloadFactory.BuildRespondParams(request), cancellationToken);
        var payload = ResponsePayloadReader.DeserializeRequired<OkOnlyContract>(result, "hub.invoke.respond.result");
        ResponsePayloadReader.EnsureOk(payload.Ok, "hub.invoke.respond.result");
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _transport.DisposeAsync();
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
