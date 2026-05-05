using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using DevHub.Sdk.Internal;
using DevHub.Sdk.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DevHub.Sdk;

/// <summary>
/// DevHub WebSocket 客户端。
/// 支持事件订阅以及协议允许的 WS 只读方法。
/// </summary>
public sealed class DevHubEventsClient : IAsyncDisposable
{
    internal const int DefaultEventBufferCapacity = 256;

    private readonly DevHubClientOptions _options;
    private readonly DevHubRuntimeConnectionInfo _connectionInfo;
    private readonly JsonRpcWebSocketSession _session;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _lifecycleLock;
    private Channel<DevHubEvent> _eventChannel;
    private volatile bool _authenticated;
    private volatile bool _eventStreamAvailable;
    private volatile bool _disposed;
    private int _activeReaderLease;

    private DevHubEventsClient(
        DevHubClientOptions options,
        DevHubRuntimeConnectionInfo connectionInfo,
        JsonRpcWebSocketSession session,
        ILogger? logger)
    {
        _options = options;
        _connectionInfo = connectionInfo;
        _session = session;
        _logger = logger ?? NullLogger.Instance;
        _lifecycleLock = new SemaphoreSlim(1, 1);
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
        return await FromRuntimeAsync(options, dependencies: null, cancellationToken);
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
        var loggerFactory = dependencies.LoggerFactory;
        var logger = loggerFactory.CreateLogger<DevHubEventsClient>();
        logger.LogInformation(
            "Starting DevHub runtime discovery for WebSocket client {ClientId}. DataDir override set: {HasDataDirOverride}.",
            clonedOptions.ClientId,
            !string.IsNullOrWhiteSpace(clonedOptions.DataDir));

        DevHubRuntimeConnectionInfo connectionInfo;
        try
        {
            connectionInfo = await dependencies.RuntimeResolver.ResolveAsync(clonedOptions, cancellationToken);
            RuntimeDiscovery.ValidateConnectionInfo(connectionInfo, "runtimeResolver");
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "DevHub runtime discovery failed for WebSocket client {ClientId}.",
                clonedOptions.ClientId);
            throw;
        }

        logger.LogInformation(
            "Resolved DevHub runtime for WebSocket client {ClientId}. WebSocketEndpoint: {WebSocketEndpoint}.",
            clonedOptions.ClientId,
            connectionInfo.WebSocketEndpoint);

        DevHubEventsClient? client = null;
        var session = new JsonRpcWebSocketSession(new DevHubWebSocketSessionOptions
        {
            WebSocketEndpoint = connectionInfo.WebSocketEndpoint,
            RequestTimeout = clonedOptions.RequestTimeout,
            OnEvent = paramsElement => client!.HandleEvent(paramsElement),
            OnTerminated = error => client?.HandleTermination(error),
            Logger = loggerFactory.CreateLogger<JsonRpcWebSocketSession>()
        });

        client = new DevHubEventsClient(clonedOptions, connectionInfo, session, logger);
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
            new DevHubEventsClientDependencies(),
            connectionFactory,
            requestIdFactory,
            cancellationToken);
    }

    internal static async Task<DevHubEventsClient> FromRuntimeAsync(
        DevHubClientOptions options,
        DevHubEventsClientDependencies dependencies,
        IWebSocketConnectionFactory connectionFactory,
        Func<string>? requestIdFactory,
        CancellationToken cancellationToken)
    {
        var clonedOptions = options?.Clone() ?? throw new ArgumentNullException(nameof(options));
        clonedOptions.Validate();

        dependencies ??= new DevHubEventsClientDependencies();
        var connectionInfo = await dependencies.RuntimeResolver.ResolveAsync(clonedOptions, cancellationToken);
        var loggerFactory = dependencies.LoggerFactory;
        var logger = loggerFactory.CreateLogger<DevHubEventsClient>();

        DevHubEventsClient? client = null;
        var session = new JsonRpcWebSocketSession(
            new DevHubWebSocketSessionOptions
            {
                WebSocketEndpoint = connectionInfo.WebSocketEndpoint,
                RequestTimeout = clonedOptions.RequestTimeout,
                OnEvent = paramsElement => client!.HandleEvent(paramsElement),
                OnTerminated = error => client?.HandleTermination(error),
                Logger = loggerFactory.CreateLogger<JsonRpcWebSocketSession>()
            },
            connectionFactory,
            requestIdFactory);

        client = new DevHubEventsClient(clonedOptions, connectionInfo, session, logger);
        return client;
    }

    /// <summary>
    /// 执行 WS 认证。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task AuthenticateAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _lifecycleLock.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            if (_authenticated)
            {
                throw new InvalidOperationException("当前 WebSocket 客户端已完成认证。");
            }

            _logger.LogInformation(
                "Authenticating DevHub WebSocket session for client {ClientId}. WebSocketEndpoint: {WebSocketEndpoint}.",
                _options.ClientId,
                _connectionInfo.WebSocketEndpoint);

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
                    cancellationToken);

                var payload = JsonSerializer.Deserialize<AuthenticateResultContract>(result.GetRawText(), DevHubJson.SerializerOptions)
                    ?? throw new InvalidOperationException("无法解析 hub.ws.authenticate 结果。");

                if (!payload.Ok || payload.ProtocolVersion != 1)
                {
                    throw new InvalidOperationException("hub.ws.authenticate 返回结果非法。");
                }

                _eventChannel = CreateEventChannel();
                _authenticated = true;
                _eventStreamAvailable = true;
                _logger.LogInformation(
                    "Authenticated DevHub WebSocket session for client {ClientId}.",
                    _options.ClientId);
            }
            catch (Exception exception)
            {
                ResetSessionState(new InvalidOperationException("当前 WebSocket 会话认证失败，连接已重置。", exception));
                _logger.LogWarning(
                    exception,
                    "DevHub WebSocket authentication failed for client {ClientId}. The current session will be discarded.",
                    _options.ClientId);
                await DisconnectSessionAsync("authenticate_failed");
                throw;
            }
        }
        finally
        {
            _lifecycleLock.Release();
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
        return await ReadOnlyRpcExecutor.PingAsync(_session.SendRequestAsync, echo, cancellationToken);
    }

    /// <summary>
    /// 通过 WebSocket 调用 <c>hub.getVersion</c> 获取当前 Host 版本。
    /// 该方法要求当前客户端已经完成认证，并只返回 RPC 的直接结果；旧 Host 若不支持该方法，会按既有 JSON-RPC 语义抛出异常。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>Host 返回的版本字符串。</returns>
    public async Task<string> GetHostVersionAsync(CancellationToken cancellationToken = default)
    {
        EnsureAuthenticated();
        return await ReadOnlyRpcExecutor.GetHostVersionAsync(_session.SendRequestAsync, cancellationToken);
    }

    /// <summary>
    /// 检查当前 SDK 与 Host 的版本兼容性。
    /// 该方法要求当前客户端已经完成认证，并优先调用 <c>hub.getVersion</c>；若 Host 返回 <c>method_not_found</c>，则回退到 <see cref="Runtime"/>.<see cref="HubRuntime.HubVersion"/>。
    /// 当任一版本缺失或无法解析为比较所需的语义化版本格式时，返回 <see cref="VersionCompatibilityStatus.Unknown"/>。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>兼容性检查结果。</returns>
    public async Task<VersionCompatibilityResult> CheckVersionCompatibilityAsync(CancellationToken cancellationToken = default)
    {
        EnsureAuthenticated();
        return await VersionCompatibilityEvaluator.CheckAsync(_session.SendRequestAsync, Runtime.HubVersion, cancellationToken);
    }

    /// <summary>
    /// 通过 WebSocket 调用 <c>hub.apps.listDefinitions</c>。
    /// </summary>
    /// <param name="request">过滤参数。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>应用定义列表。</returns>
    public async Task<IReadOnlyList<AppDefinition>> ListDefinitionsAsync(ListDefinitionsRequest request, CancellationToken cancellationToken = default)
    {
        EnsureAuthenticated();
        return await ReadOnlyRpcExecutor.ListDefinitionsAsync(_session.SendRequestAsync, request, cancellationToken);
    }

    /// <summary>
    /// 通过 WebSocket 调用 <c>hub.apps.getDefinition</c>。
    /// </summary>
    /// <param name="appId">应用标识。</param>
    /// <param name="scope">Definition 作用域。空字符串表示 Global Definition。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>应用定义。</returns>
    public async Task<AppDefinition> GetDefinitionAsync(string appId, string scope, CancellationToken cancellationToken = default)
    {
        EnsureAuthenticated();
        return await ReadOnlyRpcExecutor.GetDefinitionAsync(_session.SendRequestAsync, appId, scope, cancellationToken);
    }

    /// <summary>
    /// 通过 WebSocket 调用 <c>hub.apps.listInstances</c>。
    /// </summary>
    /// <param name="request">过滤参数。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>实例列表。</returns>
    public async Task<IReadOnlyList<AppInstance>> ListInstancesAsync(
        ListInstancesRequest request,
        CancellationToken cancellationToken = default)
    {
        EnsureAuthenticated();
        return await ReadOnlyRpcExecutor.ListInstancesAsync(_session.SendRequestAsync, request, cancellationToken);
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
        return await ReadOnlyRpcExecutor.GetInstanceAsync(_session.SendRequestAsync, instanceId, cancellationToken);
    }

    /// <summary>
    /// 获取当前会话内匹配条件的已放弃请求数量。
    /// 该操作仅维护本地状态，不会发送网络请求，也不会修改认证或订阅状态。
    /// </summary>
    /// <param name="filter">可选过滤条件；多个条件按逻辑与匹配。</param>
    /// <returns>当前匹配的已放弃请求数量。</returns>
    public int GetAbandonedRequestCount(AbandonedRequestFilter? filter = null)
    {
        ThrowIfDisposed();
        return _session.GetAbandonedRequestCount(filter);
    }

    /// <summary>
    /// 清理当前会话内匹配条件的已放弃请求记录。
    /// 该操作仅维护本地状态，不会发送网络请求，也不会修改认证或订阅状态。
    /// 若之后再收到已清理请求的迟到响应，将按未知 response id 的既有故障语义处理。
    /// </summary>
    /// <param name="filter">可选过滤条件；多个条件按逻辑与匹配。</param>
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

        return await ExecuteSubscriptionLifecycleRequestAsync(
            "hub.events.subscribe",
            parameters,
            static result =>
            {
                var payload = JsonSerializer.Deserialize<SubscribeResultContract>(result.GetRawText(), DevHubJson.SerializerOptions)
                    ?? throw new InvalidOperationException("无法解析 hub.events.subscribe 结果。");

                if (!payload.Ok || string.IsNullOrWhiteSpace(payload.SubscriptionId))
                {
                    throw new InvalidOperationException("hub.events.subscribe 返回结果非法。");
                }

                return payload.SubscriptionId;
            },
            cancellationToken);
    }

    /// <summary>
    /// 取消订阅事件。
    /// </summary>
    /// <param name="subscriptionId">订阅标识。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task UnsubscribeAsync(string subscriptionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subscriptionId);

        await ExecuteSubscriptionLifecycleRequestAsync(
            "hub.events.unsubscribe",
            new Dictionary<string, object?>
            {
                ["subscriptionId"] = subscriptionId
            },
            static result =>
            {
                var payload = JsonSerializer.Deserialize<OkOnlyContract>(result.GetRawText(), DevHubJson.SerializerOptions)
                    ?? throw new InvalidOperationException("无法解析 hub.events.unsubscribe 结果。");

                if (!payload.Ok)
                {
                    throw new InvalidOperationException("hub.events.unsubscribe 返回结果非法。");
                }

                return true;
            },
            cancellationToken);
    }

    /// <summary>
    /// 读取服务端事件流。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>事件异步序列。</returns>
    public async IAsyncEnumerable<DevHubEvent> ReadEventsAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        EnsureEventStreamAvailable();
        AcquireReaderLease();
        var eventChannel = _eventChannel;

        try
        {
            while (await eventChannel.Reader.WaitToReadAsync(cancellationToken))
            {
                while (eventChannel.Reader.TryRead(out var evt))
                {
                    yield return evt;
                }
            }
        }
        finally
        {
            ReleaseReaderLease();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ResetSessionState();
        await _session.DisposeAsync();
        _lifecycleLock.Dispose();
    }

    private void HandleEvent(JsonElement paramsElement)
    {
        var evt = JsonSerializer.Deserialize<DevHubEvent>(paramsElement.GetRawText(), DevHubJson.SerializerOptions)
            ?? throw new InvalidOperationException("无法解析 hub.event.params。");
        ValidateEvent(evt);

        if (!_eventChannel.Writer.TryWrite(evt))
        {
            _logger.LogError(
                "DevHub event buffer overflowed for client {ClientId}. SubscriptionId: {SubscriptionId}. EventType: {EventType}. BufferCapacity: {BufferCapacity}.",
                _options.ClientId,
                evt.SubscriptionId,
                evt.Type.Value,
                DefaultEventBufferCapacity);
            throw new InvalidOperationException("当前事件流缓冲已满，事件流已终止；请重新认证并重新订阅。");
        }
    }

    private void HandleTermination(Exception? terminalException)
    {
        if (_disposed)
        {
            return;
        }

        if (terminalException is null)
        {
            _logger.LogWarning(
                "DevHub WebSocket session terminated for client {ClientId}. Event stream is no longer available.",
                _options.ClientId);
        }
        else
        {
            _logger.LogError(
                terminalException,
                "DevHub WebSocket session terminated with an error for client {ClientId}. Event stream is no longer available.",
                _options.ClientId);
        }

        ResetSessionState(terminalException);
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
            throw new InvalidOperationException("当前事件流不可用；请先完成认证，若连接已终止则需重新认证并重新订阅。");
        }
    }

    private void AcquireReaderLease()
    {
        if (Interlocked.CompareExchange(ref _activeReaderLease, 1, 0) != 0)
        {
            throw new InvalidOperationException("同一个 DevHubEventsClient 实例一次只允许一个活动中的 ReadEventsAsync 读取器。");
        }
    }

    private void ReleaseReaderLease()
    {
        Volatile.Write(ref _activeReaderLease, 0);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private static Channel<DevHubEvent> CreateEventChannel()
    {
        return Channel.CreateBounded<DevHubEvent>(new BoundedChannelOptions(DefaultEventBufferCapacity)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait
        });
    }

    private async Task<T> ExecuteSubscriptionLifecycleRequestAsync<T>(
        string method,
        object? parameters,
        Func<JsonElement, T> parseResult,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await _lifecycleLock.WaitAsync(cancellationToken);
        try
        {
            EnsureAuthenticated();
            _logger.LogInformation(
                "Sending DevHub WebSocket subscription lifecycle request. Method: {Method}. ClientId: {ClientId}.",
                method,
                _options.ClientId);

            try
            {
                var result = await _session.SendRequestAsync(method, parameters, cancellationToken);
                var parsed = parseResult(result);
                _logger.LogInformation(
                    "Completed DevHub WebSocket subscription lifecycle request. Method: {Method}. ClientId: {ClientId}.",
                    method,
                    _options.ClientId);
                return parsed;
            }
            catch (OperationCanceledException exception)
            {
                await HandleAmbiguousSubscriptionStateAsync(method, exception);
                throw;
            }
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    private async Task HandleAmbiguousSubscriptionStateAsync(string method, OperationCanceledException exception)
    {
        _logger.LogWarning(
            exception,
            "DevHub WebSocket subscription lifecycle request became ambiguous. Method: {Method}. ClientId: {ClientId}. The current session will be discarded.",
            method,
            _options.ClientId);
        ResetSessionState(new InvalidOperationException(
            $"{method} 在结果返回前已超时或被取消；当前 WebSocket 会话已失效，必须重新认证并重新订阅。",
            exception));
        await DisconnectSessionAsync("subscription_state_ambiguous");
    }

    private async Task DisconnectSessionAsync(string reason)
    {
        try
        {
            await _session.DisconnectAsync(reason, CancellationToken.None);
        }
        catch (Exception exception)
        {
            _logger.LogDebug(
                exception,
                "DevHub WebSocket session disconnect raised an exception during cleanup. ClientId: {ClientId}. Reason: {Reason}.",
                _options.ClientId,
                reason);
        }
    }

    private void ResetSessionState(Exception? terminalException = null)
    {
        _authenticated = false;
        _eventStreamAvailable = false;
        _eventChannel.Writer.TryComplete(terminalException);
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
}
