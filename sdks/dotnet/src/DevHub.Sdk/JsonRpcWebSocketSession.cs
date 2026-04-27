using System.Collections.Concurrent;
using System.Linq;
using System.Net.WebSockets;
using DevHub.Sdk.Internal;
using DevHub.Sdk.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DevHub.Sdk;

/// <summary>
/// DevHub WebSocket session 选项。
/// </summary>
public sealed class DevHubWebSocketSessionOptions
{
    private Uri? _webSocketEndpoint;
    private Action<JObject>? _onEvent;
    private Action<Exception?>? _onTerminated;

    /// <summary>
    /// WebSocket 端点。
    /// </summary>
    public Uri WebSocketEndpoint
    {
        get => _webSocketEndpoint ?? throw new InvalidOperationException("WebSocketEndpoint 尚未设置。");
        set => _webSocketEndpoint = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>
    /// 请求超时。
    /// </summary>
    public TimeSpan? RequestTimeout { get; set; }

    /// <summary>
    /// 收到 <c>hub.event</c> 通知时的回调。
    /// </summary>
    public Action<JObject> OnEvent
    {
        get => _onEvent ?? throw new InvalidOperationException("OnEvent 尚未设置。");
        set => _onEvent = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>
    /// 连接终止时的回调。
    /// </summary>
    public Action<Exception?> OnTerminated
    {
        get => _onTerminated ?? throw new InvalidOperationException("OnTerminated 尚未设置。");
        set => _onTerminated = value ?? throw new ArgumentNullException(nameof(value));
    }
}

/// <summary>
/// DevHub WebSocket session 抽象。
/// </summary>
public interface IDevHubWebSocketSession : IAsyncDisposable
{
    /// <summary>
    /// 确保底层 WebSocket 已连接。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    Task EnsureConnectedAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 发送 JSON-RPC 请求并等待响应。
    /// </summary>
    /// <param name="method">方法名。</param>
    /// <param name="parameters">参数对象。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>响应中的 <c>result</c> 对象。</returns>
    Task<JObject> SendRequestAsync(string method, object? parameters, CancellationToken cancellationToken = default);

    /// <summary>
    /// 主动断开当前连接，但保留 session 以供后续重连复用。
    /// </summary>
    /// <param name="reason">关闭原因。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task DisconnectAsync(string reason, CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取当前会话内匹配条件的已放弃请求数量。
    /// </summary>
    /// <param name="filter">可选过滤条件。</param>
    /// <returns>当前匹配的已放弃请求数量。</returns>
    int GetAbandonedRequestCount(AbandonedRequestFilter? filter = null);

    /// <summary>
    /// 清理当前会话内匹配条件的已放弃请求记录。
    /// </summary>
    /// <param name="filter">可选过滤条件。</param>
    /// <returns>本次实际移除的记录数量。</returns>
    int ClearAbandonedRequests(AbandonedRequestFilter? filter = null);
}

/// <summary>
/// DevHub WebSocket session 工厂。
/// </summary>
public interface IDevHubWebSocketSessionFactory
{
    /// <summary>
    /// 创建 WebSocket session。
    /// </summary>
    /// <param name="options">session 选项。</param>
    /// <returns>session 实例。</returns>
    IDevHubWebSocketSession Create(DevHubWebSocketSessionOptions options);
}

/// <summary>
/// 默认的 JSON-RPC WebSocket session 工厂。
/// </summary>
public sealed class JsonRpcWebSocketSessionFactory : IDevHubWebSocketSessionFactory
{
    /// <inheritdoc />
    public IDevHubWebSocketSession Create(DevHubWebSocketSessionOptions options)
    {
        return new JsonRpcWebSocketSession(options);
    }
}

/// <summary>
/// 默认的 JSON-RPC WebSocket session 实现。
/// </summary>
public sealed class JsonRpcWebSocketSession : IDevHubWebSocketSession
{
    private static readonly TimeSpan AbandonedRequestRetention = TimeSpan.FromMinutes(5);

    private readonly DevHubWebSocketSessionOptions _options;
    private readonly IWebSocketConnectionFactory _connectionFactory;
    private readonly Func<string> _requestIdFactory;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JObject>> _pendingRequests;
    private readonly ConcurrentDictionary<string, AbandonedRequestEntry> _abandonedRequests;
    private readonly SemaphoreSlim _connectionLock;
    private readonly SemaphoreSlim _sendLock;
    private readonly CancellationTokenSource _disposeCts;

    private IWebSocketConnection? _connection;
    private CancellationTokenSource? _connectionReceiveLoopCts;
    private Task? _receiverLoopTask;
    private bool _suppressNextTerminationCallback;
    private bool _disposed;

    /// <summary>
    /// 初始化 JSON-RPC WebSocket session。
    /// </summary>
    /// <param name="options">session 选项。</param>
    public JsonRpcWebSocketSession(DevHubWebSocketSessionOptions options)
        : this(options, new ClientWebSocketConnectionFactory(), requestIdFactory: null)
    {
    }

    internal JsonRpcWebSocketSession(
        DevHubWebSocketSessionOptions options,
        IWebSocketConnectionFactory connectionFactory,
        Func<string>? requestIdFactory)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _requestIdFactory = requestIdFactory ?? CreateRequestId;
        _pendingRequests = new ConcurrentDictionary<string, TaskCompletionSource<JObject>>(StringComparer.Ordinal);
        _abandonedRequests = new ConcurrentDictionary<string, AbandonedRequestEntry>(StringComparer.Ordinal);
        _connectionLock = new SemaphoreSlim(1, 1);
        _sendLock = new SemaphoreSlim(1, 1);
        _disposeCts = new CancellationTokenSource();
    }

    /// <inheritdoc />
    public int GetAbandonedRequestCount(AbandonedRequestFilter? filter = null)
    {
        ThrowIfDisposed();
        ValidateAbandonedRequestFilter(filter);
        CleanupExpiredAbandonedRequests();
        var now = DateTimeOffset.UtcNow;
        return _abandonedRequests.Values.Count(entry => MatchesAbandonedRequest(entry, filter, now));
    }

    /// <inheritdoc />
    public int ClearAbandonedRequests(AbandonedRequestFilter? filter = null)
    {
        ThrowIfDisposed();
        ValidateAbandonedRequestFilter(filter);
        CleanupExpiredAbandonedRequests();
        var now = DateTimeOffset.UtcNow;
        var removed = 0;

        foreach (var abandonedRequest in _abandonedRequests.ToArray())
        {
            if (!MatchesAbandonedRequest(abandonedRequest.Value, filter, now))
            {
                continue;
            }

            if (_abandonedRequests.TryRemove(abandonedRequest.Key, out _))
            {
                removed++;
            }
        }

        return removed;
    }

    /// <inheritdoc />
    public async Task EnsureConnectedAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        using var linkedCts = CreateLinkedTokenSource(cancellationToken);
        await _connectionLock.WaitAsync(linkedCts.Token);
        try
        {
            ThrowIfDisposed();
            if (_connection is not null)
            {
                return;
            }

            var connection = await _connectionFactory.ConnectAsync(_options.WebSocketEndpoint, linkedCts.Token);
            var connectionReceiveLoopCts = CancellationTokenSource.CreateLinkedTokenSource(_disposeCts.Token);
            _connection = connection;
            _connectionReceiveLoopCts = connectionReceiveLoopCts;
            _receiverLoopTask = Task.Run(
                () => RunReceiveLoopAsync(connection, connectionReceiveLoopCts, connectionReceiveLoopCts.Token),
                CancellationToken.None);
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<JObject> SendRequestAsync(string method, object? parameters, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        CompatibilityGuards.ThrowIfNullOrWhiteSpace(method, nameof(method));
        CleanupExpiredAbandonedRequests();

        await EnsureConnectedAsync(cancellationToken);
        var connection = _connection ?? throw new InvalidOperationException("当前 WebSocket 尚未建立连接。");
        var requestId = _requestIdFactory();
        var waiter = new TaskCompletionSource<JObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pendingRequests.TryAdd(requestId, waiter))
        {
            throw new InvalidOperationException($"重复的请求标识：{requestId}");
        }

        try
        {
            var payload = DevHubJson.Serialize(
                new JsonRpcRequestEnvelope
                {
                    Id = requestId,
                    Method = method,
                    Params = parameters
                });

            await _sendLock.WaitAsync(cancellationToken);
            try
            {
                await connection.SendTextAsync(payload, cancellationToken);
            }
            finally
            {
                _sendLock.Release();
            }

            using var linkedCts = CreateLinkedTokenSource(cancellationToken);
            return await waiter.Task.WaitAsyncCompat(linkedCts.Token);
        }
        catch (OperationCanceledException)
        {
            MarkPendingRequestAsAbandoned(requestId, method, parameters);
            throw;
        }
        catch
        {
            _pendingRequests.TryRemove(requestId, out _);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task DisconnectAsync(string reason, CancellationToken cancellationToken = default)
    {
        await _connectionLock.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            _suppressNextTerminationCallback = true;

            if (_connection is null && _receiverLoopTask is null)
            {
                _suppressNextTerminationCallback = false;
                return;
            }

            FailPendingRequests(new InvalidOperationException("WebSocket 连接已关闭。"));
            _abandonedRequests.Clear();

            var connection = _connection;
            var receiverLoopTask = _receiverLoopTask;

            _connection = null;
            _receiverLoopTask = null;

            _connectionReceiveLoopCts?.Cancel();
            await DisposeConnectionAsync(connection, reason, cancellationToken);

            if (receiverLoopTask is not null)
            {
                try
                {
                    await receiverLoopTask;
                }
                catch
                {
                }
            }
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await _connectionLock.WaitAsync(CancellationToken.None);
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _disposeCts.Cancel();

            var connection = _connection;
            var receiverLoopTask = _receiverLoopTask;

            _connection = null;
            _receiverLoopTask = null;

            _connectionReceiveLoopCts?.Cancel();
            await DisposeConnectionAsync(connection, "client_dispose", CancellationToken.None);

            if (receiverLoopTask is not null)
            {
                try
                {
                    await receiverLoopTask;
                }
                catch
                {
                }
            }
        }
        finally
        {
            _connectionLock.Release();
        }

        FailPendingRequests(new ObjectDisposedException(nameof(JsonRpcWebSocketSession)));
        _abandonedRequests.Clear();

        _connectionLock.Dispose();
        _sendLock.Dispose();
        _disposeCts.Dispose();
    }

    private async Task RunReceiveLoopAsync(
        IWebSocketConnection connection,
        CancellationTokenSource connectionReceiveLoopCts,
        CancellationToken cancellationToken)
    {
        Exception? terminalException = null;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var message = await connection.ReceiveAsync(cancellationToken);
                if (message.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }

                if (message.MessageType != WebSocketMessageType.Text)
                {
                    throw new InvalidOperationException("WebSocket JSON-RPC 消息必须为文本。");
                }

                if (message.Text is not { } text || string.IsNullOrWhiteSpace(text))
                {
                    throw new InvalidOperationException("WebSocket JSON-RPC 消息不能为空。");
                }

                var root = DevHubJson.ParseObject(text);
                ValidateIncomingEnvelope(root);

                if (TryHandleResponse(root, out var requestId, out var resultElement, out var errorToken))
                {
                    CompletePendingRequest(requestId, resultElement, errorToken);
                    continue;
                }

                if (TryGetEventParams(root, out var paramsElement))
                {
                    _options.OnEvent((JObject)paramsElement!.DeepClone());
                    continue;
                }

                ThrowUnexpectedIncomingMessage(root);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            terminalException = ex;
        }
        finally
        {
            Interlocked.CompareExchange(ref _connection, null, connection);
            if (ReferenceEquals(_connectionReceiveLoopCts, connectionReceiveLoopCts))
            {
                _connectionReceiveLoopCts = null;
            }

            FailPendingRequests(terminalException ?? new InvalidOperationException("WebSocket 连接已关闭。"));
            _abandonedRequests.Clear();
            await DisposeConnectionAsync(connection, "connection_closed", CancellationToken.None);

            if (!_disposed && !_suppressNextTerminationCallback)
            {
                _options.OnTerminated(terminalException);
            }

            _suppressNextTerminationCallback = false;
            connectionReceiveLoopCts.Dispose();
        }
    }

    private void CompletePendingRequest(string requestId, JObject? resultElement, JToken? errorToken)
    {
        if (!_pendingRequests.TryRemove(requestId, out var waiter))
        {
            CleanupExpiredAbandonedRequests();
            if (_abandonedRequests.TryRemove(requestId, out _))
            {
                return;
            }

            throw new InvalidOperationException("WebSocket JSON-RPC 响应 id 未匹配任何挂起请求。");
        }

        if (errorToken is not null)
        {
            if (errorToken.Type != JTokenType.Object)
            {
                waiter.TrySetException(new InvalidOperationException("WebSocket JSON-RPC error 对象非法。"));
                return;
            }

            var errorObject = (JObject)errorToken;
            if (!errorObject.TryGetValue("code", out var codeToken) || !TryReadInt32(codeToken, out var code))
            {
                waiter.TrySetException(new InvalidOperationException("WebSocket JSON-RPC error.code 非法。"));
                return;
            }

            var message = errorObject.TryGetValue("message", out var messageToken) && messageToken.Type == JTokenType.String
                ? (string?)messageToken ?? "internal_error"
                : "internal_error";

            JToken? data = null;
            if (errorObject.TryGetValue("data", out var dataToken))
            {
                data = dataToken.DeepClone();
            }

            waiter.TrySetException(new DevHubRpcException(code, message, data, requestId));
            return;
        }

        waiter.TrySetResult((JObject)resultElement!.DeepClone());
    }

    private static bool TryHandleResponse(JObject root, out string requestId, out JObject? resultElement, out JToken? errorToken)
    {
        requestId = string.Empty;
        resultElement = null;
        errorToken = null;

        if (!root.TryGetValue("id", out _))
        {
            return false;
        }

        requestId = ReadResponseId(root);

        var hasResult = root.TryGetValue("result", out var resultToken);
        var hasError = root.TryGetValue("error", out errorToken) && errorToken.Type != JTokenType.Null;
        if (!hasResult && !hasError)
        {
            return false;
        }

        if (hasResult == hasError)
        {
            throw new InvalidOperationException("WebSocket JSON-RPC 响应必须且只能包含 result 或 error。");
        }

        if (hasResult && resultToken!.Type != JTokenType.Object)
        {
            throw new InvalidOperationException("WebSocket JSON-RPC result 必须为对象。");
        }

        if (hasResult)
        {
            resultElement = (JObject)resultToken!;
        }

        return true;
    }

    private static bool TryGetEventParams(JObject root, out JObject? paramsElement)
    {
        paramsElement = null;
        if (!root.TryGetValue("method", out var methodToken) || methodToken.Type != JTokenType.String)
        {
            return false;
        }

        if (!string.Equals((string?)methodToken, "hub.event", StringComparison.Ordinal))
        {
            return false;
        }

        if (root.TryGetValue("id", out _))
        {
            throw new InvalidOperationException("hub.event 必须为通知，禁止包含 id。");
        }

        if (!root.TryGetValue("params", out var paramsToken) || paramsToken.Type != JTokenType.Object)
        {
            throw new InvalidOperationException("hub.event.params 非法。");
        }

        if (root.TryGetValue("result", out _) ||
            root.TryGetValue("error", out var errorToken) && errorToken.Type != JTokenType.Null)
        {
            throw new InvalidOperationException("hub.event 通知禁止包含 result 或 error。");
        }

        paramsElement = (JObject)paramsToken;
        return true;
    }

    private static void ThrowUnexpectedIncomingMessage(JObject root)
    {
        if (root.TryGetValue("id", out _))
        {
            throw new InvalidOperationException("收到无法识别的 WebSocket JSON-RPC 响应。");
        }

        if (root.TryGetValue("method", out var methodToken) && methodToken.Type == JTokenType.String)
        {
            var method = (string?)methodToken ?? string.Empty;
            throw new InvalidOperationException($"收到不受支持的 WebSocket 通知：{method}");
        }

        throw new InvalidOperationException("收到无法识别的 WebSocket JSON-RPC 消息。");
    }

    private static void ValidateIncomingEnvelope(JObject root)
    {
        if (!root.TryGetValue("jsonrpc", out var jsonRpcToken) ||
            jsonRpcToken.Type != JTokenType.String ||
            !string.Equals((string?)jsonRpcToken, "2.0", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("WebSocket JSON-RPC 消息的 jsonrpc 版本非法。");
        }
    }

    private static string ReadResponseId(JObject root)
    {
        if (!root.TryGetValue("id", out var idToken))
        {
            throw new InvalidOperationException("WebSocket JSON-RPC 响应缺少 id 字段。");
        }

        return idToken.Type switch
        {
            JTokenType.String => (string?)idToken ?? string.Empty,
            JTokenType.Integer => Convert.ToString(((JValue)idToken).Value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
            JTokenType.Float => Convert.ToString(((JValue)idToken).Value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
            _ => throw new InvalidOperationException("WebSocket JSON-RPC 响应的 id 类型非法。")
        };
    }

    private static bool TryReadInt32(JToken token, out int value)
    {
        if (token.Type == JTokenType.Integer)
        {
            var numericValue = ((JValue)token).Value;
            switch (numericValue)
            {
                case int intValue:
                    value = intValue;
                    return true;
                case long longValue when longValue >= int.MinValue && longValue <= int.MaxValue:
                    value = (int)longValue;
                    return true;
                case short shortValue:
                    value = shortValue;
                    return true;
                case byte byteValue:
                    value = byteValue;
                    return true;
            }
        }

        value = default;
        return false;
    }

    private void MarkPendingRequestAsAbandoned(string requestId, string method, object? parameters)
    {
        if (_pendingRequests.TryRemove(requestId, out _))
        {
            _abandonedRequests[requestId] = new AbandonedRequestEntry(
                requestId,
                method,
                DateTimeOffset.UtcNow,
                TryExtractAppId(parameters));
        }
    }

    private void CleanupExpiredAbandonedRequests()
    {
        if (_abandonedRequests.IsEmpty)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        foreach (var abandonedRequest in _abandonedRequests)
        {
            if (abandonedRequest.Value.AbandonedAt + AbandonedRequestRetention <= now)
            {
                _abandonedRequests.TryRemove(abandonedRequest.Key, out _);
            }
        }
    }

    private static bool MatchesAbandonedRequest(
        AbandonedRequestEntry entry,
        AbandonedRequestFilter? filter,
        DateTimeOffset now)
    {
        if (filter is null)
        {
            return true;
        }

        if (filter.OlderThan is { } olderThan && now - entry.AbandonedAt < olderThan)
        {
            return false;
        }

        if (filter.AppId is { Length: > 0 } appId && !string.Equals(entry.AppId, appId, StringComparison.Ordinal))
        {
            return false;
        }

        if (filter.Method is { Length: > 0 } method && !string.Equals(entry.Method, method, StringComparison.Ordinal))
        {
            return false;
        }

        return true;
    }

    private static void ValidateAbandonedRequestFilter(AbandonedRequestFilter? filter)
    {
        if (filter is null)
        {
            return;
        }

        if (filter.OlderThan < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(filter), "OlderThan 不能小于零。");
        }

        if (filter.AppId is not null && string.IsNullOrWhiteSpace(filter.AppId))
        {
            throw new ArgumentException("AppId 不能为空白字符串。", nameof(filter));
        }

        if (filter.Method is not null && string.IsNullOrWhiteSpace(filter.Method))
        {
            throw new ArgumentException("Method 不能为空白字符串。", nameof(filter));
        }
    }

    private static string? TryExtractAppId(object? parameters)
    {
        if (parameters is null)
        {
            return null;
        }

        try
        {
            var token = DevHubJson.SerializeToToken(parameters);
            if (token is not JObject payload ||
                !payload.TryGetValue("appId", out var appIdToken) ||
                appIdToken.Type != JTokenType.String)
            {
                return null;
            }

            var appId = (string?)appIdToken;
            return ProtocolIdentifier.IsValidAppId(appId) ? appId : null;
        }
        catch
        {
            return null;
        }
    }

    private void FailPendingRequests(Exception exception)
    {
        foreach (var pendingRequest in _pendingRequests.ToArray())
        {
            if (_pendingRequests.TryRemove(pendingRequest.Key, out var pending))
            {
                pending.TrySetException(exception);
            }
        }
    }

    private static async Task DisposeConnectionAsync(
        IWebSocketConnection? connection,
        string reason,
        CancellationToken cancellationToken)
    {
        if (connection is null)
        {
            return;
        }

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(1));
            await connection.CloseAsync(WebSocketCloseStatus.NormalClosure, reason, timeoutCts.Token);
        }
        catch
        {
        }

        await connection.DisposeAsync();
    }

    private CancellationTokenSource CreateLinkedTokenSource(CancellationToken cancellationToken)
    {
        var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposeCts.Token);
        if (_options.RequestTimeout is { } requestTimeout)
        {
            linkedCts.CancelAfter(requestTimeout);
        }

        return linkedCts;
    }

    private void ThrowIfDisposed()
    {
        CompatibilityGuards.ThrowIfDisposed(_disposed, this);
    }

    private static string CreateRequestId()
    {
        return $"ws-{Guid.NewGuid():N}";
    }

    private sealed class AbandonedRequestEntry
    {
        public AbandonedRequestEntry(string requestId, string method, DateTimeOffset abandonedAt, string? appId)
        {
            RequestId = requestId;
            Method = method;
            AbandonedAt = abandonedAt;
            AppId = appId;
        }

        public string RequestId { get; }

        public string Method { get; }

        public DateTimeOffset AbandonedAt { get; }

        public string? AppId { get; }
    }

    private sealed class JsonRpcRequestEnvelope
    {
        public string Jsonrpc { get; set; } = "2.0";

        public string Id { get; set; } = string.Empty;

        public string Method { get; set; } = string.Empty;

        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public object? Params { get; set; }
    }
}
