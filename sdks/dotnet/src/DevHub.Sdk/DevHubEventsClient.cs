using System.Runtime.CompilerServices;
using System.Threading.Channels;
using DevHub.Sdk.Internal;
using DevHub.Sdk.Models;
using Newtonsoft.Json.Linq;

namespace DevHub.Sdk;

/// <summary>
/// DevHub WebSocket 客户端。
/// 支持事件订阅以及协议允许的 WS 只读方法。
/// </summary>
public sealed class DevHubEventsClient : IAsyncDisposable
{
    private readonly DevHubClientOptions _options;
    private readonly DevHubRuntimeConnectionInfo _connectionInfo;
    private readonly IDevHubWebSocketSession _session;
    private Channel<DevHubEvent> _eventChannel;
    private bool _authenticated;
    private bool _eventStreamAvailable;
    private bool _disposed;

    private DevHubEventsClient(
        DevHubClientOptions options,
        DevHubRuntimeConnectionInfo connectionInfo,
        IDevHubWebSocketSession session)
    {
        _options = options;
        _connectionInfo = connectionInfo;
        _session = session;
        _eventChannel = CreateEventChannel();
    }

    /// <summary>
    /// 客户端选项。
    /// </summary>
    public DevHubClientOptions Options => _options;

    /// <summary>
    /// 当前连接的运行时信息。
    /// </summary>
    public HubRuntime Runtime => _connectionInfo.Runtime;

    /// <summary>
    /// 通过运行时发现信息创建客户端。
    /// </summary>
    /// <param name="options">客户端选项。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>客户端实例。</returns>
    public static async Task<DevHubEventsClient> FromRuntimeAsync(DevHubClientOptions options, CancellationToken cancellationToken = default)
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
    public static async Task<DevHubEventsClient> FromRuntimeAsync(
        DevHubClientOptions options,
        DevHubEventsClientDependencies? dependencies,
        CancellationToken cancellationToken = default)
    {
        var clonedOptions = options?.Clone() ?? throw new ArgumentNullException(nameof(options));
        clonedOptions.Validate();

        dependencies ??= new DevHubEventsClientDependencies();
        var connectionInfo = await dependencies.RuntimeResolver.ResolveAsync(clonedOptions, cancellationToken).ConfigureAwait(false);

        DevHubEventsClient? client = null;
        var session = dependencies.SessionFactory.Create(new DevHubWebSocketSessionOptions
        {
            WebSocketEndpoint = connectionInfo.WebSocketEndpoint,
            RequestTimeout = clonedOptions.RequestTimeout,
            OnEvent = paramsElement => client!.HandleEvent(paramsElement),
            OnTerminated = error => client?.HandleTermination(error)
        });

        client = new DevHubEventsClient(clonedOptions, connectionInfo, session);
        return client;
    }

    internal static async Task<DevHubEventsClient> FromRuntimeAsync(
        DevHubClientOptions options,
        IWebSocketConnectionFactory connectionFactory,
        Func<string>? requestIdFactory = null,
        CancellationToken cancellationToken = default)
    {
        return await FromRuntimeAsync(
            options,
            new DevHubEventsClientDependencies
            {
                SessionFactory = new TestWebSocketSessionFactory(connectionFactory, requestIdFactory)
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 执行 WS 认证。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task AuthenticateAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (_authenticated)
        {
            throw new InvalidOperationException("当前 WebSocket 客户端已完成认证。");
        }

        try
        {
            var result = await _session.SendRequestAsync(
                "hub.ws.authenticate",
                new Dictionary<string, object?>
                {
                    ["token"] = _connectionInfo.Token,
                    ["protocolVersion"] = _options.ProtocolVersion,
                    ["clientId"] = _options.ClientId,
                    ["clientSessionId"] = _options.ClientSessionId.ToString("D")
                },
                cancellationToken).ConfigureAwait(false);

            var payload = ResponsePayloadReader.DeserializeRequired<AuthenticateResultContract>(result, "hub.ws.authenticate.result");

            if (!payload.Ok || payload.ProtocolVersion != 1)
            {
                throw new InvalidOperationException("hub.ws.authenticate 返回结果非法。");
            }

            _eventChannel = CreateEventChannel();
            _authenticated = true;
            _eventStreamAvailable = true;
        }
        catch
        {
            try
            {
                await _session.DisconnectAsync("authenticate_failed", CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
            }

            throw;
        }
    }

    /// <summary>
    /// 通过 WebSocket 调用 <c>hub.ping</c>。
    /// </summary>
    /// <param name="echo">可选回显参数。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>Ping 结果。</returns>
    public async Task<PingResult> PingAsync(object? echo = null, CancellationToken cancellationToken = default)
    {
        EnsureAuthenticated();
        return await ReadOnlyRpcExecutor.PingAsync(_session.SendRequestAsync, echo, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 通过 WebSocket 调用 <c>hub.getVersion</c> 获取当前 Host 版本。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>Host 返回的版本字符串。</returns>
    public async Task<string> GetHostVersionAsync(CancellationToken cancellationToken = default)
    {
        EnsureAuthenticated();
        return await ReadOnlyRpcExecutor.GetHostVersionAsync(_session.SendRequestAsync, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 检查当前 SDK 与 Host 的版本兼容性。
    /// 优先调用 <c>hub.getVersion</c>；若 Host 返回 <c>method_not_found</c>，则回退到 <see cref="Runtime"/>.<see cref="HubRuntime.HubVersion"/>。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>兼容性检查结果。</returns>
    public async Task<VersionCompatibilityResult> CheckVersionCompatibilityAsync(CancellationToken cancellationToken = default)
    {
        EnsureAuthenticated();
        return await VersionCompatibilityEvaluator.CheckAsync(_session.SendRequestAsync, Runtime.HubVersion, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 通过 WebSocket 调用 <c>hub.apps.listDefinitions</c>。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>应用定义列表。</returns>
    public async Task<IReadOnlyList<AppDefinition>> ListDefinitionsAsync(CancellationToken cancellationToken = default)
    {
        EnsureAuthenticated();
        return await ListDefinitionsAsync(new ListDefinitionsRequest(), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 通过 WebSocket 调用 <c>hub.apps.listDefinitions</c>。
    /// </summary>
    /// <param name="request">过滤参数。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>应用定义列表。</returns>
    public async Task<IReadOnlyList<AppDefinition>> ListDefinitionsAsync(
        ListDefinitionsRequest request,
        CancellationToken cancellationToken = default)
    {
        EnsureAuthenticated();
        return await ReadOnlyRpcExecutor.ListDefinitionsAsync(_session.SendRequestAsync, request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 通过 WebSocket 调用 <c>hub.apps.getDefinition</c>。
    /// </summary>
    /// <param name="appId">应用标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>应用定义。</returns>
    public async Task<AppDefinition> GetDefinitionAsync(string appId, CancellationToken cancellationToken = default)
    {
        return await GetDefinitionAsync(appId, string.Empty, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 通过 WebSocket 调用 <c>hub.apps.getDefinition</c>。
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
        EnsureAuthenticated();
        return await ReadOnlyRpcExecutor.GetDefinitionAsync(_session.SendRequestAsync, appId, scope, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 通过 WebSocket 调用 <c>hub.apps.listInstances</c>。
    /// </summary>
    /// <param name="request">过滤参数。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>实例列表。</returns>
    public async Task<IReadOnlyList<AppInstance>> ListInstancesAsync(
        ListInstancesRequest? request = null,
        CancellationToken cancellationToken = default)
    {
        EnsureAuthenticated();
        return await ReadOnlyRpcExecutor.ListInstancesAsync(
            _session.SendRequestAsync,
            request ?? new ListInstancesRequest(),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 通过 WebSocket 调用 <c>hub.apps.getInstance</c>。
    /// </summary>
    /// <param name="instanceId">实例标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>实例快照。</returns>
    public async Task<AppInstance> GetInstanceAsync(string instanceId, CancellationToken cancellationToken = default)
    {
        EnsureAuthenticated();
        return await ReadOnlyRpcExecutor.GetInstanceAsync(_session.SendRequestAsync, instanceId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 获取当前会话内匹配条件的已放弃请求数量。
    /// 该操作仅维护本地状态，不会发送网络请求，也不会修改认证或订阅状态。
    /// </summary>
    /// <param name="filter">可选过滤条件。</param>
    /// <returns>当前匹配的已放弃请求数量。</returns>
    public int GetAbandonedRequestCount(AbandonedRequestFilter? filter = null)
    {
        ThrowIfDisposed();
        return _session.GetAbandonedRequestCount(filter);
    }

    /// <summary>
    /// 清理当前会话内匹配条件的已放弃请求记录。
    /// 该操作仅维护本地状态，不会发送网络请求，也不会修改认证或订阅状态。
    /// </summary>
    /// <param name="filter">可选过滤条件。</param>
    /// <returns>本次实际移除的记录数量。</returns>
    public int ClearAbandonedRequests(AbandonedRequestFilter? filter = null)
    {
        ThrowIfDisposed();
        return _session.ClearAbandonedRequests(filter);
    }

    /// <summary>
    /// 订阅事件。
    /// </summary>
    /// <param name="types">事件类型列表；为空或 <see langword="null"/> 表示订阅全部。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>订阅标识。</returns>
    public async Task<string> SubscribeAsync(IEnumerable<DevHubEventType>? types = null, CancellationToken cancellationToken = default)
    {
        EnsureAuthenticated();

        object? parameters = null;
        if (types is not null)
        {
            var typeArray = types.ToArray();
            if (typeArray.Any(static item => !item.IsSupported))
            {
                throw new ArgumentException("types 只能包含受支持的 DevHub 事件类型。", nameof(types));
            }

            if (typeArray.Length > 0)
            {
                parameters = new Dictionary<string, object?>
                {
                    ["types"] = typeArray.Select(static item => item.Value).ToArray()
                };
            }
        }

        var result = await _session.SendRequestAsync("hub.events.subscribe", parameters, cancellationToken).ConfigureAwait(false);
        var payload = ResponsePayloadReader.DeserializeRequired<SubscribeResultContract>(result, "hub.events.subscribe.result");

        if (!payload.Ok || string.IsNullOrWhiteSpace(payload.SubscriptionId))
        {
            throw new InvalidOperationException("hub.events.subscribe 返回结果非法。");
        }

        return payload.SubscriptionId;
    }

    /// <summary>
    /// 取消订阅事件。
    /// </summary>
    /// <param name="subscriptionId">订阅标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task UnsubscribeAsync(string subscriptionId, CancellationToken cancellationToken = default)
    {
        EnsureAuthenticated();
        CompatibilityGuards.ThrowIfNullOrWhiteSpace(subscriptionId, nameof(subscriptionId));

        var result = await _session.SendRequestAsync(
            "hub.events.unsubscribe",
            new Dictionary<string, object?>
            {
                ["subscriptionId"] = subscriptionId
            },
            cancellationToken).ConfigureAwait(false);

        var payload = ResponsePayloadReader.DeserializeRequired<OkOnlyContract>(result, "hub.events.unsubscribe.result");

        if (!payload.Ok)
        {
            throw new InvalidOperationException("hub.events.unsubscribe 返回结果非法。");
        }
    }

    /// <summary>
    /// 读取服务端事件流。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>事件异步序列。</returns>
    public IAsyncEnumerable<DevHubEvent> ReadEventsAsync(CancellationToken cancellationToken = default)
    {
        EnsureEventStreamAvailable();
        return ReadEventsCore(_eventChannel, cancellationToken);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _authenticated = false;
        _eventStreamAvailable = false;
        _eventChannel.Writer.TryComplete();
        await _session.DisposeAsync().ConfigureAwait(false);
    }

    private void HandleEvent(JObject paramsElement)
    {
        var evt = ResponsePayloadReader.DeserializeRequired<DevHubEvent>(paramsElement, "hub.event.params");
        ValidateEvent(evt);

        if (!_eventChannel.Writer.TryWrite(evt))
        {
            throw new InvalidOperationException("当前事件流不可用。");
        }
    }

    private void HandleTermination(Exception? terminalException)
    {
        if (_disposed)
        {
            return;
        }

        _authenticated = false;
        _eventChannel.Writer.TryComplete(terminalException);
    }

    private static async IAsyncEnumerable<DevHubEvent> ReadEventsCore(
        Channel<DevHubEvent> eventChannel,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        while (await eventChannel.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            while (eventChannel.Reader.TryRead(out var evt))
            {
                yield return evt;
            }
        }
    }

    private static void ValidateEvent(DevHubEvent evt)
    {
        if (string.IsNullOrWhiteSpace(evt.SubscriptionId))
        {
            throw new InvalidOperationException("hub.event.params.subscriptionId 非法。");
        }

        if (!evt.Type.IsSupported)
        {
            throw new InvalidOperationException("hub.event.params.type 非法。");
        }

        if (evt.TimeUtc == default)
        {
            throw new InvalidOperationException("hub.event.params.timeUtc 非法。");
        }

        ResponsePayloadReader.ValidateEventPayload(evt, "hub.event.params");
    }

    private void EnsureAuthenticated()
    {
        ThrowIfDisposed();
        if (!_authenticated)
        {
            throw new InvalidOperationException("当前 WebSocket 客户端尚未认证。");
        }
    }

    private void EnsureEventStreamAvailable()
    {
        ThrowIfDisposed();
        if (!_eventStreamAvailable)
        {
            EnsureAuthenticated();
        }
    }

    private void ThrowIfDisposed()
    {
        CompatibilityGuards.ThrowIfDisposed(_disposed, this);
    }

    private static Channel<DevHubEvent> CreateEventChannel()
    {
        return Channel.CreateUnbounded<DevHubEvent>();
    }

    private sealed class AuthenticateResultContract
    {
        public bool Ok { get; set; }

        public int ProtocolVersion { get; set; }
    }

    private sealed class SubscribeResultContract
    {
        public bool Ok { get; set; }

        public string SubscriptionId { get; set; } = string.Empty;
    }

    private sealed class TestWebSocketSessionFactory : IDevHubWebSocketSessionFactory
    {
        private readonly IWebSocketConnectionFactory _connectionFactory;
        private readonly Func<string>? _requestIdFactory;

        public TestWebSocketSessionFactory(IWebSocketConnectionFactory connectionFactory, Func<string>? requestIdFactory)
        {
            _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
            _requestIdFactory = requestIdFactory;
        }

        public IDevHubWebSocketSession Create(DevHubWebSocketSessionOptions options)
        {
            return new JsonRpcWebSocketSession(options, _connectionFactory, _requestIdFactory);
        }
    }
}
