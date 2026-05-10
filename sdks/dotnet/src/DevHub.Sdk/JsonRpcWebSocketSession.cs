using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using DevHub.Sdk.Internal;
using DevHub.Sdk.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DevHub.Sdk;

/// <summary>
/// DevHub WebSocket session 选项。
/// </summary>
internal sealed class DevHubWebSocketSessionOptions
{
    private Uri? _webSocketEndpoint;
    private Action<JsonElement>? _onEvent;
    private Action<Exception?>? _onTerminated;
    private ILogger? _logger;

    /// <summary>
    /// WebSocket 端点。
    /// </summary>
    public required Uri WebSocketEndpoint
    {
        get => _webSocketEndpoint ?? throw new InvalidOperationException("WebSocketEndpoint 尚未设置。");
        init => _webSocketEndpoint = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>
    /// 请求超时。
    /// </summary>
    public TimeSpan? RequestTimeout { get; init; }

    /// <summary>
    /// 收到 <c>hub.event</c> 通知时的回调。
    /// </summary>
    public required Action<JsonElement> OnEvent
    {
        get => _onEvent ?? throw new InvalidOperationException("OnEvent 尚未设置。");
        init => _onEvent = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>
    /// 连接终止时的回调。
    /// </summary>
    public required Action<Exception?> OnTerminated
    {
        get => _onTerminated ?? throw new InvalidOperationException("OnTerminated 尚未设置。");
        init => _onTerminated = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>
    /// 可选日志器。
    /// </summary>
    public ILogger Logger
    {
        get => _logger ?? NullLogger.Instance;
        init => _logger = value ?? NullLogger.Instance;
    }

    /// <summary>
    /// 当前时间提供器。
    /// </summary>
    internal Func<DateTimeOffset> UtcNowProvider { get; init; } = static () => DateTimeOffset.UtcNow;
}

/// <summary>
/// DevHub WebSocket session 抽象。
/// </summary>
internal interface IDevHubWebSocketSession : IAsyncDisposable
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
    Task<JsonElement> SendRequestAsync(string method, object? parameters, CancellationToken cancellationToken = default);

    /// <summary>
    /// 主动断开当前连接，但保留 session 以供后续重连复用。
    /// </summary>
    /// <param name="reason">关闭原因。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task DisconnectAsync(string reason, CancellationToken cancellationToken = default);
}

/// <summary>
/// DevHub WebSocket session 工厂。
/// </summary>
internal interface IDevHubWebSocketSessionFactory
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
internal sealed class JsonRpcWebSocketSessionFactory : IDevHubWebSocketSessionFactory
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
internal sealed class JsonRpcWebSocketSession : IDevHubWebSocketSession
{
    private static readonly TimeSpan AbandonedRequestRetention = TimeSpan.FromMinutes(5);

    private readonly DevHubWebSocketSessionOptions _options;
    private readonly IWebSocketConnectionFactory _connectionFactory;
    private readonly ILogger _logger;
    private readonly Func<string> _requestIdFactory;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pendingRequests;
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
    internal JsonRpcWebSocketSession(DevHubWebSocketSessionOptions options)
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
        _logger = options.Logger;
        _requestIdFactory = requestIdFactory ?? CreateRequestId;
        _pendingRequests = new ConcurrentDictionary<string, TaskCompletionSource<JsonElement>>(StringComparer.Ordinal);
        _abandonedRequests = new ConcurrentDictionary<string, AbandonedRequestEntry>(StringComparer.Ordinal);
        _connectionLock = new SemaphoreSlim(1, 1);
        _sendLock = new SemaphoreSlim(1, 1);
        _disposeCts = new CancellationTokenSource();
    }

    internal int GetAbandonedRequestCount(AbandonedRequestFilter? filter = null)
    {
        ThrowIfDisposed();
        ValidateAbandonedRequestFilter(filter);
        CleanupExpiredAbandonedRequests();
        var now = GetUtcNow();
        return _abandonedRequests.Values.Count(entry => MatchesAbandonedRequest(entry, filter, now));
    }

    internal int ClearAbandonedRequests(AbandonedRequestFilter? filter = null)
    {
        ThrowIfDisposed();
        ValidateAbandonedRequestFilter(filter);
        CleanupExpiredAbandonedRequests();
        var now = GetUtcNow();
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
        await _connectionLock.WaitAsync(linkedCts.Token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (_connection is not null)
            {
                return;
            }

            _logger.LogInformation(
                "Opening DevHub WebSocket session. WebSocketEndpoint: {WebSocketEndpoint}.",
                _options.WebSocketEndpoint);
            IWebSocketConnection connection;
            try
            {
                connection = await _connectionFactory.ConnectAsync(_options.WebSocketEndpoint, linkedCts.Token).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                _logger.LogError(
                    exception,
                    "Failed to open DevHub WebSocket session. WebSocketEndpoint: {WebSocketEndpoint}.",
                    _options.WebSocketEndpoint);
                throw;
            }

            var connectionReceiveLoopCts = CancellationTokenSource.CreateLinkedTokenSource(_disposeCts.Token);
            _connection = connection;
            _connectionReceiveLoopCts = connectionReceiveLoopCts;
            _receiverLoopTask = Task.Run(
                () => RunReceiveLoopAsync(connection, connectionReceiveLoopCts, connectionReceiveLoopCts.Token),
                CancellationToken.None);
            _logger.LogInformation(
                "Opened DevHub WebSocket session. WebSocketEndpoint: {WebSocketEndpoint}.",
                _options.WebSocketEndpoint);
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task<JsonElement> SendRequestAsync(string method, object? parameters, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        CleanupExpiredAbandonedRequests();

        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        var connection = _connection ?? throw new InvalidOperationException("当前 WebSocket 尚未建立连接。");
        var requestId = _requestIdFactory();
        var waiter = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pendingRequests.TryAdd(requestId, waiter))
        {
            throw new InvalidOperationException($"重复的请求标识：{requestId}");
        }

        try
        {
            var payload = JsonSerializer.Serialize(
                new JsonRpcRequestEnvelope
                {
                    Id = requestId,
                    Method = method,
                    Params = parameters
                },
                DevHubJson.SerializerOptions);

            await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await connection.SendTextAsync(payload, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _sendLock.Release();
            }

            using var linkedCts = CreateLinkedTokenSource(cancellationToken);
            return await waiter.Task.WaitAsync(linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            MarkPendingRequestAsAbandoned(requestId, method, parameters);
            _logger.LogWarning(
                "DevHub WebSocket request timed out or was canceled. Method: {Method}. RequestId: {RequestId}.",
                method,
                requestId);
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
        _logger.LogInformation(
            "Disconnecting DevHub WebSocket session. WebSocketEndpoint: {WebSocketEndpoint}. Reason: {Reason}.",
            _options.WebSocketEndpoint,
            reason);
        await _connectionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
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
            await DisposeConnectionAsync(connection, reason, cancellationToken).ConfigureAwait(false);

            if (receiverLoopTask is not null)
            {
                try
                {
                    await receiverLoopTask.ConfigureAwait(false);
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

        await _connectionLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
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
            await DisposeConnectionAsync(connection, "client_dispose", CancellationToken.None).ConfigureAwait(false);

            if (receiverLoopTask is not null)
            {
                try
                {
                    await receiverLoopTask.ConfigureAwait(false);
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
                var message = await connection.ReceiveAsync(cancellationToken).ConfigureAwait(false);
                if (message.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }

                if (message.MessageType != WebSocketMessageType.Text)
                {
                    throw new InvalidOperationException("WebSocket JSON-RPC 消息必须为文本。");
                }

                if (string.IsNullOrWhiteSpace(message.Text))
                {
                    throw new InvalidOperationException("WebSocket JSON-RPC 消息不能为空。");
                }

                using var document = JsonDocument.Parse(message.Text);
                var root = document.RootElement;
                ValidateIncomingEnvelope(root);

                if (TryHandleResponse(root, out var requestId, out var resultElement))
                {
                    CompletePendingRequest(requestId, root, resultElement);
                    continue;
                }

                if (TryGetEventParams(root, out var paramsElement))
                {
                    _options.OnEvent(paramsElement.Clone());
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
            await DisposeConnectionAsync(connection, "connection_closed", CancellationToken.None).ConfigureAwait(false);

            if (terminalException is null)
            {
                _logger.LogWarning(
                    "DevHub WebSocket session closed. WebSocketEndpoint: {WebSocketEndpoint}.",
                    _options.WebSocketEndpoint);
            }
            else
            {
                _logger.LogError(
                    terminalException,
                    "DevHub WebSocket session terminated with a protocol or transport error. WebSocketEndpoint: {WebSocketEndpoint}.",
                    _options.WebSocketEndpoint);
            }

            if (!_disposed && !_suppressNextTerminationCallback)
            {
                _options.OnTerminated(terminalException);
            }

            _suppressNextTerminationCallback = false;
            connectionReceiveLoopCts.Dispose();
        }
    }

    private void CompletePendingRequest(string requestId, JsonElement root, JsonElement resultElement)
    {
        if (!_pendingRequests.TryRemove(requestId, out var waiter))
        {
            CleanupExpiredAbandonedRequests();
            if (_abandonedRequests.ContainsKey(requestId))
            {
                return;
            }

            throw new InvalidOperationException("WebSocket JSON-RPC 响应 id 未匹配任何挂起请求。");
        }

        if (root.TryGetProperty("error", out var errorElement) && errorElement.ValueKind != JsonValueKind.Null)
        {
            var code = errorElement.GetProperty("code").GetInt32();
            var message = errorElement.GetProperty("message").GetString() ?? "internal_error";
            JsonElement? data = null;
            if (errorElement.TryGetProperty("data", out var dataElement))
            {
                data = dataElement.Clone();
            }

            waiter.TrySetException(new DevHubRpcException(code, message, data, requestId));
            return;
        }

        waiter.TrySetResult(resultElement.Clone());
    }

    private static bool TryHandleResponse(JsonElement root, out string requestId, out JsonElement resultElement)
    {
        requestId = string.Empty;
        resultElement = default;

        if (!root.TryGetProperty("id", out _))
        {
            return false;
        }

        requestId = ReadResponseId(root);

        var hasResult = root.TryGetProperty("result", out resultElement);
        var hasError = root.TryGetProperty("error", out var errorElement) && errorElement.ValueKind != JsonValueKind.Null;
        if (!hasResult && !hasError)
        {
            return false;
        }

        if (hasResult == hasError)
        {
            throw new InvalidOperationException("WebSocket JSON-RPC 响应必须且只能包含 result 或 error。");
        }

        if (hasResult && resultElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("WebSocket JSON-RPC result 必须为对象。");
        }

        return true;
    }

    private static bool TryGetEventParams(JsonElement root, out JsonElement paramsElement)
    {
        paramsElement = default;
        if (!root.TryGetProperty("method", out var methodElement) || methodElement.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        if (!string.Equals(methodElement.GetString(), "hub.event", StringComparison.Ordinal))
        {
            return false;
        }

        if (root.TryGetProperty("id", out _))
        {
            throw new InvalidOperationException("hub.event 必须为通知，禁止包含 id。");
        }

        if (!root.TryGetProperty("params", out paramsElement) || paramsElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("hub.event.params 非法。");
        }

        if (root.TryGetProperty("result", out _) ||
            root.TryGetProperty("error", out var errorElement) && errorElement.ValueKind != JsonValueKind.Null)
        {
            throw new InvalidOperationException("hub.event 通知禁止包含 result 或 error。");
        }

        return true;
    }

    private static void ThrowUnexpectedIncomingMessage(JsonElement root)
    {
        if (root.TryGetProperty("id", out _))
        {
            throw new InvalidOperationException("收到无法识别的 WebSocket JSON-RPC 响应。");
        }

        if (root.TryGetProperty("method", out var methodElement) && methodElement.ValueKind == JsonValueKind.String)
        {
            var method = methodElement.GetString() ?? string.Empty;
            throw new InvalidOperationException($"收到不受支持的 WebSocket 通知：{method}");
        }

        throw new InvalidOperationException("收到无法识别的 WebSocket JSON-RPC 消息。");
    }

    private static void ValidateIncomingEnvelope(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("WebSocket JSON-RPC 消息根必须为对象。");
        }

        if (!root.TryGetProperty("jsonrpc", out var jsonRpcElement) ||
            jsonRpcElement.ValueKind != JsonValueKind.String ||
            !string.Equals(jsonRpcElement.GetString(), "2.0", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("WebSocket JSON-RPC 消息的 jsonrpc 版本非法。");
        }
    }

    private static string ReadResponseId(JsonElement root)
    {
        return JsonRpcIdReader.ReadRequiredResponseId(root, "WebSocket JSON-RPC 响应");
    }

    private void MarkPendingRequestAsAbandoned(string requestId, string method, object? parameters)
    {
        if (_pendingRequests.TryRemove(requestId, out _))
        {
            _logger.LogWarning(
                "Tracked DevHub WebSocket request as abandoned after local cancellation. Method: {Method}. RequestId: {RequestId}.",
                method,
                requestId);
            _abandonedRequests[requestId] = new AbandonedRequestEntry(
                requestId,
                method,
                GetUtcNow(),
                TryExtractAppId(parameters));
        }
    }

    private void CleanupExpiredAbandonedRequests()
    {
        if (_abandonedRequests.IsEmpty)
        {
            return;
        }

        var now = GetUtcNow();
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
            var element = JsonSerializer.SerializeToElement(parameters, DevHubJson.SerializerOptions);
            if (element.ValueKind != JsonValueKind.Object ||
                !element.TryGetProperty("appId", out var appIdElement) ||
                appIdElement.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var appId = appIdElement.GetString();
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
            await connection.CloseAsync(WebSocketCloseStatus.NormalClosure, reason, timeoutCts.Token).ConfigureAwait(false);
        }
        catch
        {
        }

        await connection.DisposeAsync().ConfigureAwait(false);
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

    private DateTimeOffset GetUtcNow()
    {
        return _options.UtcNowProvider();
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private static string CreateRequestId()
    {
        return $"ws-{Guid.NewGuid():N}";
    }

    private sealed record AbandonedRequestEntry(
        string RequestId,
        string Method,
        DateTimeOffset AbandonedAt,
        string? AppId);

    private sealed class JsonRpcRequestEnvelope
    {
        public string Jsonrpc { get; set; } = "2.0";

        public string Id { get; set; } = string.Empty;

        public string Method { get; set; } = string.Empty;

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public object? Params { get; set; }
    }
}
