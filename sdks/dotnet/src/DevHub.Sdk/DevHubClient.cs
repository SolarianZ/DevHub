using DevHub.Sdk.Internal;
using DevHub.Sdk.Models;
using Newtonsoft.Json.Linq;
using System.Collections.Concurrent;

namespace DevHub.Sdk;

/// <summary>
/// DevHub HTTP JSON-RPC 客户端。
/// </summary>
public sealed class DevHubClient : IAsyncDisposable
{
    private readonly IDevHubHttpTransport _transport;
    private readonly ConcurrentDictionary<string, RegisteredInstanceState> _registeredInstances = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, InvocationLeaseState> _invocationLeases = new(StringComparer.Ordinal);
    private readonly object _registrationIdentityLock = new();
    private readonly Dictionary<string, RegistrationIdentity> _registrationIdentities = new(StringComparer.Ordinal);
    private bool _disposed;

    private DevHubClient(DevHubClientOptions options, DevHubRuntimeConnectionInfo connectionInfo, IDevHubHttpTransport transport)
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
        return await FromRuntimeAsync(options, dependencies: null, cancellationToken).ConfigureAwait(false);
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
        var connectionInfo = await dependencies.RuntimeResolver.ResolveAsync(clonedOptions, cancellationToken).ConfigureAwait(false);
        RuntimeDiscovery.ValidateConnectionInfo(connectionInfo, "runtimeResolver");
        var transport = dependencies.TransportFactory.Create(clonedOptions, connectionInfo);
        return new DevHubClient(clonedOptions, connectionInfo, transport);
    }

    internal static async Task<DevHubClient> FromRuntimeAsync(
        DevHubClientOptions options,
        HttpMessageHandler handler,
        Func<string>? requestIdFactory = null,
        CancellationToken cancellationToken = default)
    {
        return await FromRuntimeAsync(
            options,
            new DevHubClientDependencies
            {
                TransportFactory = new TestHttpTransportFactory(handler, requestIdFactory)
            },
            cancellationToken).ConfigureAwait(false);
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
        return await ReadOnlyRpcExecutor.PingAsync(_transport.SendAsync, echo, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 调用 <c>hub.getVersion</c> 获取当前 Host 版本。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>Host 返回的版本字符串。</returns>
    public async Task<string> GetHostVersionAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return await ReadOnlyRpcExecutor.GetHostVersionAsync(_transport.SendAsync, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 检查当前 SDK 与 Host 的版本兼容性。
    /// 优先调用 <c>hub.getVersion</c>；若 Host 返回 <c>method_not_found</c>，则回退到 <see cref="Runtime"/>.<see cref="HubRuntime.HubVersion"/>。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>兼容性检查结果。</returns>
    public async Task<VersionCompatibilityResult> CheckVersionCompatibilityAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return await VersionCompatibilityEvaluator.CheckAsync(_transport.SendAsync, Runtime.HubVersion, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 调用 <c>hub.apps.listDefinitions</c>。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>应用定义列表。</returns>
    public async Task<IReadOnlyList<AppDefinition>> ListDefinitionsAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return await ListDefinitionsAsync(new ListDefinitionsRequest(), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 调用 <c>hub.apps.listDefinitions</c>。
    /// </summary>
    /// <param name="request">过滤参数。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>应用定义列表。</returns>
    public async Task<IReadOnlyList<AppDefinition>> ListDefinitionsAsync(
        ListDefinitionsRequest request,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return await ReadOnlyRpcExecutor.ListDefinitionsAsync(_transport.SendAsync, request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 调用 <c>hub.apps.getDefinition</c>。
    /// </summary>
    /// <param name="appId">应用标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>应用定义。</returns>
    public async Task<AppDefinition> GetDefinitionAsync(string appId, CancellationToken cancellationToken = default)
    {
        return await GetDefinitionAsync(appId, string.Empty, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 调用 <c>hub.apps.getDefinition</c>。
    /// </summary>
    /// <param name="appId">应用标识。</param>
    /// <param name="scope">Definition 作用域。空字符串表示 Global Definition。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>应用定义。</returns>
    public async Task<AppDefinition> GetDefinitionAsync(
        string appId,
        string scope,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return await ReadOnlyRpcExecutor.GetDefinitionAsync(_transport.SendAsync, appId, scope, cancellationToken).ConfigureAwait(false);
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
            cancellationToken).ConfigureAwait(false);

        var errorsElement = ResponsePayloadReader.EnsurePropertyExists(
            result,
            "hub.apps.validateDefinition.result",
            "errors",
            JTokenType.Array);
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
            cancellationToken).ConfigureAwait(false);
        ResponsePayloadReader.ValidateAppDefinitionElement(
            ResponsePayloadReader.EnsurePropertyExists(result, "hub.apps.upsertDefinition.result", "definition", JTokenType.Object),
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
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task DeleteDefinitionAsync(string appId, CancellationToken cancellationToken = default)
    {
        await DeleteDefinitionAsync(appId, string.Empty, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 调用 <c>hub.apps.deleteDefinition</c>。
    /// </summary>
    /// <param name="appId">应用标识。</param>
    /// <param name="scope">Definition 作用域。空字符串表示 Global Definition。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task DeleteDefinitionAsync(
        string appId,
        string scope,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var result = await _transport.SendAsync(
            "hub.apps.deleteDefinition",
            RequestPayloadFactory.BuildDeleteDefinitionParams(appId, scope),
            cancellationToken).ConfigureAwait(false);
        var payload = ResponsePayloadReader.DeserializeRequired<OkOnlyContract>(result, "hub.apps.deleteDefinition.result");
        ResponsePayloadReader.EnsureOk(payload.Ok, "hub.apps.deleteDefinition.result");
    }

    /// <summary>
    /// 调用 <c>hub.apps.registerInstance</c>。
    /// </summary>
    /// <param name="instance">实例注册载荷。</param>
    /// <param name="password">实例密码。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>注册结果。</returns>
    public async Task<RegisterInstanceResult> RegisterInstanceAsync(
        AppInstanceRegistration instance,
        string password,
        CancellationToken cancellationToken = default)
    {
        return await RegisterInstanceAsync(instance, password, launchId: null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 调用 <c>hub.apps.registerInstance</c>。
    /// </summary>
    /// <param name="instance">实例注册载荷。</param>
    /// <param name="password">实例密码。</param>
    /// <param name="launchId">Host 通过 <c>DEVHUB_LAUNCH_ID</c> 传入的启动请求标识；自主注册时为 <see langword="null"/>。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>注册结果。</returns>
    public async Task<RegisterInstanceResult> RegisterInstanceAsync(
        AppInstanceRegistration instance,
        string password,
        string? launchId,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        CompatibilityGuards.ThrowIfNull(instance, nameof(instance));
        CompatibilityGuards.ThrowIfNullOrWhiteSpace(password, nameof(password));
        EnsureRegisterIdentityDoesNotDrift(instance);
        var result = await _transport.SendAsync(
            "hub.apps.registerInstance",
            RequestPayloadFactory.BuildRegisterInstanceParams(instance, password, launchId),
            cancellationToken).ConfigureAwait(false);
        ResponsePayloadReader.ValidateAppInstanceElement(
            ResponsePayloadReader.EnsurePropertyExists(result, "hub.apps.registerInstance.result", "instance", JTokenType.Object),
            "hub.apps.registerInstance.result.instance");
        ResponsePayloadReader.EnsurePropertyExists(
            result,
            "hub.apps.registerInstance.result",
            "instanceSessionToken",
            JTokenType.String);

        var payload = ResponsePayloadReader.DeserializeRequired<RegisterInstanceContract>(result, "hub.apps.registerInstance.result");
        ResponsePayloadReader.EnsureOk(payload.Ok, "hub.apps.registerInstance.result");
        ResponsePayloadReader.EnsureNotNull(payload.Instance, "hub.apps.registerInstance.result", "instance");
        ResponsePayloadReader.EnsureNotEmpty(payload.InstanceSessionToken, "hub.apps.registerInstance.result", "instanceSessionToken");
        ResponsePayloadReader.EnsureInstanceIdValue(payload.Instance.InstanceId, "hub.apps.registerInstance.result", "instance.instanceId");
        ResponsePayloadReader.EnsureAppIdValue(payload.Instance.AppId, "hub.apps.registerInstance.result", "instance.appId");
        ResponsePayloadReader.EnsureTimestamp(payload.Instance.RegisteredAtUtc, "hub.apps.registerInstance.result", "instance.registeredAtUtc");
        ResponsePayloadReader.EnsureTimestamp(payload.Instance.LastSeenUtc, "hub.apps.registerInstance.result", "instance.lastSeenUtc");
        var registerResult = new RegisterInstanceResult
        {
            Instance = payload.Instance,
            InstanceSessionToken = payload.InstanceSessionToken
        };
        _registeredInstances[payload.Instance.InstanceId] = new RegisteredInstanceState(password, payload.InstanceSessionToken);
        RememberRegisteredInstance(payload.Instance);
        return registerResult;
    }

    /// <summary>
    /// 调用 <c>hub.apps.heartbeat</c>。
    /// </summary>
    /// <param name="instanceId">实例标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>服务端返回的最后在线时间。</returns>
    public async Task<DateTimeOffset> HeartbeatAsync(string instanceId, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var instanceSessionToken = ResolveInstanceSessionToken(instanceId);
        return await HeartbeatAsync(instanceId, instanceSessionToken, cancellationToken).ConfigureAwait(false);
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
        var result = await _transport.SendAsync(
            "hub.apps.heartbeat",
            RequestPayloadFactory.BuildHeartbeatParams(instanceId, instanceSessionToken),
            cancellationToken).ConfigureAwait(false);
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
        var resolvedSessionToken = ResolveUnregisterCredential(instanceId, instanceSessionToken);
        var result = await _transport.SendAsync(
            "hub.apps.unregisterInstance",
            RequestPayloadFactory.BuildUnregisterParams(instanceId, resolvedSessionToken),
            cancellationToken).ConfigureAwait(false);
        var payload = ResponsePayloadReader.DeserializeRequired<OkOnlyContract>(result, "hub.apps.unregisterInstance.result");
        ResponsePayloadReader.EnsureOk(payload.Ok, "hub.apps.unregisterInstance.result");
        _registeredInstances.TryRemove(instanceId, out _);
        ForgetRegisteredInstance(instanceId);
    }

    /// <summary>
    /// 调用 <c>hub.apps.listInstances</c>。
    /// </summary>
    /// <param name="request">过滤参数。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>实例列表。</returns>
    public async Task<IReadOnlyList<AppInstance>> ListInstancesAsync(ListInstancesRequest? request = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return await ReadOnlyRpcExecutor.ListInstancesAsync(
            _transport.SendAsync,
            request ?? new ListInstancesRequest(),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 调用 <c>hub.apps.getInstance</c>。
    /// </summary>
    /// <param name="instanceId">实例标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>实例快照。</returns>
    public async Task<AppInstance> GetInstanceAsync(string instanceId, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return await ReadOnlyRpcExecutor.GetInstanceAsync(_transport.SendAsync, instanceId, cancellationToken).ConfigureAwait(false);
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
        var result = await _transport.SendAsync("hub.apps.launch", RequestPayloadFactory.BuildLaunchParams(request), cancellationToken).ConfigureAwait(false);
        var payload = ResponsePayloadReader.DeserializeRequired<LaunchResult>(result, "hub.apps.launch.result");
        ResponsePayloadReader.EnsureOk(payload.Ok, "hub.apps.launch.result");
        ResponsePayloadReader.EnsureNotEmpty(payload.Status, "hub.apps.launch.result", "status");
        if (!string.Equals(payload.Status, "started", StringComparison.Ordinal) &&
            !string.Equals(payload.Status, "starting", StringComparison.Ordinal) &&
            !string.Equals(payload.Status, "already_running", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("hub.apps.launch.result 返回结果非法：status 取值不受支持。");
        }

        if (string.Equals(payload.Status, "started", StringComparison.Ordinal) ||
            string.Equals(payload.Status, "starting", StringComparison.Ordinal))
        {
            ResponsePayloadReader.EnsureNotEmpty(payload.LaunchId, "hub.apps.launch.result", "launchId");
            ResponsePayloadReader.EnsureNotEmpty(payload.DedupeKey, "hub.apps.launch.result", "dedupeKey");
        }
        else
        {
            var hasInstance = !string.IsNullOrWhiteSpace(payload.InstanceId);
            var hasLaunchRecord = !string.IsNullOrWhiteSpace(payload.LaunchId) && !string.IsNullOrWhiteSpace(payload.DedupeKey);
            if (!hasInstance && !hasLaunchRecord)
            {
                throw new InvalidOperationException("hub.apps.launch.result 返回结果非法：already_running 必须包含 instanceId 或 launchId + dedupeKey。");
            }
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
        var result = await _transport.SendAsync("hub.invoke.notify", RequestPayloadFactory.BuildNotifyParams(request), cancellationToken).ConfigureAwait(false);
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
        var result = await _transport.SendAsync("hub.invoke.request", RequestPayloadFactory.BuildRequestParams(request), cancellationToken).ConfigureAwait(false);
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
        if (string.IsNullOrWhiteSpace(request.InstanceSessionToken))
        {
            request.InstanceSessionToken = ResolveInstanceSessionToken(request.InstanceId);
        }

        var result = await _transport.SendAsync("hub.invoke.poll", RequestPayloadFactory.BuildPollParams(request), cancellationToken).ConfigureAwait(false);
        var itemsElement = ResponsePayloadReader.EnsurePropertyExists(result, "hub.invoke.poll.result", "items", JTokenType.Array);

        var index = 0;
        foreach (var itemElement in itemsElement.Children())
        {
            ResponsePayloadReader.ValidateInvocationElement(itemElement, $"hub.invoke.poll.result.items[{index}]");
            index++;
        }

        var payload = ResponsePayloadReader.DeserializeRequired<PollResult>(result, "hub.invoke.poll.result");
        ResponsePayloadReader.EnsureOk(payload.Ok, "hub.invoke.poll.result");
        ResponsePayloadReader.EnsureTimestamp(payload.ServerTimeUtc, "hub.invoke.poll.result", "serverTimeUtc");
        ResponsePayloadReader.EnsureNotNull(payload.Items, "hub.invoke.poll.result", "items");
        CacheInvocationLeases(request.InstanceId, payload.Items);
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
        if (string.IsNullOrWhiteSpace(request.InstanceSessionToken))
        {
            request.InstanceSessionToken = ResolveInstanceSessionToken(request.InstanceId);
        }

        if (string.IsNullOrWhiteSpace(request.LeaseToken))
        {
            request.LeaseToken = ResolveInvocationLeaseToken(request.InstanceId, request.InvocationId);
        }

        var result = await _transport.SendAsync("hub.invoke.respond", RequestPayloadFactory.BuildRespondParams(request), cancellationToken).ConfigureAwait(false);
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
        await _transport.DisposeAsync().ConfigureAwait(false);
    }

    private void ThrowIfDisposed()
    {
        CompatibilityGuards.ThrowIfDisposed(_disposed, this);
    }

    private void EnsureRegisterIdentityDoesNotDrift(AppInstanceRegistration instance)
    {
        var identity = RegistrationIdentity.From(instance);

        lock (_registrationIdentityLock)
        {
            if (_registrationIdentities.TryGetValue(identity.InstanceId, out var existing) &&
                (!string.Equals(existing.AppId, identity.AppId, StringComparison.Ordinal) ||
                 !string.Equals(existing.Scope, identity.Scope, StringComparison.Ordinal)))
            {
                throw new ArgumentException("当前客户端已将该 instanceId 关联到不同 appId 或 scope。", nameof(instance));
            }
        }
    }

    private void RememberRegisteredInstance(AppInstance instance)
    {
        var identity = RegistrationIdentity.From(instance);

        lock (_registrationIdentityLock)
        {
            _registrationIdentities[identity.InstanceId] = identity;
        }
    }

    private void ForgetRegisteredInstance(string instanceId)
    {
        var validatedInstanceId = ProtocolIdentifier.EnsureInstanceId(instanceId, nameof(instanceId));

        lock (_registrationIdentityLock)
        {
            _registrationIdentities.Remove(validatedInstanceId);
        }
    }

    private void CacheInvocationLeases(string instanceId, IReadOnlyList<Invocation> invocations)
    {
        var validatedInstanceId = ProtocolIdentifier.EnsureInstanceId(instanceId, nameof(instanceId));

        foreach (var invocation in invocations)
        {
            if (invocation?.Delivery is null || string.IsNullOrWhiteSpace(invocation.Delivery.LeaseToken))
            {
                continue;
            }

            var resolvedInstanceId = invocation.Target is not null && !string.IsNullOrWhiteSpace(invocation.Target.InstanceId)
                ? invocation.Target.InstanceId!
                : validatedInstanceId;
            var key = BuildInvocationLeaseKey(resolvedInstanceId, invocation.InvocationId);
            _invocationLeases[key] = new InvocationLeaseState(invocation.Delivery.LeaseToken);
        }
    }

    private string ResolveInstanceSessionToken(string instanceId)
    {
        CompatibilityGuards.ThrowIfNullOrWhiteSpace(instanceId, nameof(instanceId));
        if (_registeredInstances.TryGetValue(instanceId, out var state))
        {
            return state.InstanceSessionToken;
        }

        throw new InvalidOperationException($"未找到实例 {instanceId} 的会话令牌，请先通过 RegisterInstanceAsync 注册并保留返回结果。");
    }

    private string ResolveInvocationLeaseToken(string instanceId, string invocationId)
    {
        var key = BuildInvocationLeaseKey(instanceId, invocationId);
        if (_invocationLeases.TryGetValue(key, out var state))
        {
            return state.LeaseToken;
        }

        throw new InvalidOperationException($"未找到调用 {invocationId} 的租约令牌，请先通过 PollAsync 拉取对应调用，或在 RespondRequest 中显式提供 LeaseToken。");
    }

    private string ResolveUnregisterCredential(string instanceId, string credential)
    {
        CompatibilityGuards.ThrowIfNullOrWhiteSpace(instanceId, nameof(instanceId));
        CompatibilityGuards.ThrowIfNullOrWhiteSpace(credential, nameof(credential));

        if (_registeredInstances.TryGetValue(instanceId, out var state) &&
            string.Equals(credential, state.Password, StringComparison.Ordinal))
        {
            return state.InstanceSessionToken;
        }

        return credential;
    }

    private static string BuildInvocationLeaseKey(string instanceId, string invocationId)
    {
        return $"{ProtocolIdentifier.EnsureInstanceId(instanceId, nameof(instanceId))}\n{ProtocolIdentifier.EnsureInvocationId(invocationId, nameof(invocationId))}";
    }

    private sealed class RegisteredInstanceState
    {
        public RegisteredInstanceState(string password, string instanceSessionToken)
        {
            Password = password;
            InstanceSessionToken = instanceSessionToken;
        }

        public string Password { get; }

        public string InstanceSessionToken { get; }
    }

    private sealed class InvocationLeaseState
    {
        public InvocationLeaseState(string leaseToken)
        {
            LeaseToken = leaseToken;
        }

        public string LeaseToken { get; }
    }

    private sealed class RegistrationIdentity
    {
        private RegistrationIdentity(string instanceId, string appId, string scope)
        {
            InstanceId = instanceId;
            AppId = appId;
            Scope = scope;
        }

        internal string InstanceId { get; }

        internal string AppId { get; }

        internal string Scope { get; }

        internal static RegistrationIdentity From(AppInstanceRegistration instance)
        {
            return new RegistrationIdentity(
                ProtocolIdentifier.EnsureInstanceId(instance.InstanceId, nameof(AppInstanceRegistration.InstanceId)),
                ProtocolIdentifier.EnsureAppId(instance.AppId, nameof(AppInstanceRegistration.AppId)),
                ScopeContract.EnsureScopedString(instance.Scope, nameof(instance.Scope)));
        }

        internal static RegistrationIdentity From(AppInstance instance)
        {
            return new RegistrationIdentity(
                ProtocolIdentifier.EnsureInstanceId(instance.InstanceId, nameof(AppInstance.InstanceId)),
                ProtocolIdentifier.EnsureAppId(instance.AppId, nameof(AppInstance.AppId)),
                ScopeContract.EnsureScopedString(instance.Scope, nameof(instance.Scope)));
        }
    }

    private sealed class TestHttpTransportFactory : IDevHubHttpTransportFactory
    {
        private readonly HttpMessageHandler _handler;
        private readonly Func<string>? _requestIdFactory;

        public TestHttpTransportFactory(HttpMessageHandler handler, Func<string>? requestIdFactory)
        {
            _handler = handler ?? throw new ArgumentNullException(nameof(handler));
            _requestIdFactory = requestIdFactory;
        }

        public IDevHubHttpTransport Create(DevHubClientOptions options, DevHubRuntimeConnectionInfo connectionInfo)
        {
            return JsonRpcHttpTransport.Create(options, connectionInfo, _handler, _requestIdFactory);
        }
    }
}
