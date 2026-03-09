using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using System.Runtime.CompilerServices;
using DevHub.Sdk.Internal;
using DevHub.Sdk.Models;

namespace DevHub.Sdk;

/// <summary>
/// DevHub WebSocket 事件客户端。
/// </summary>
public sealed class DevHubEventsClient : IAsyncDisposable
{
    private readonly DevHubClientOptions _options;
    private readonly RuntimeConnectionInfo _connectionInfo;
    private readonly IWebSocketConnectionFactory _connectionFactory;
    private readonly Func<string> _requestIdFactory;
    private readonly Channel<DevHubEvent> _eventChannel;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pendingRequests;
    private readonly SemaphoreSlim _sendLock;
    private readonly CancellationTokenSource _disposeCts;

    private IWebSocketConnection? _connection;
    private Task? _receiverLoopTask;
    private bool _authenticated;
    private bool _disposed;

    private DevHubEventsClient(
        DevHubClientOptions options,
        RuntimeConnectionInfo connectionInfo,
        IWebSocketConnectionFactory connectionFactory,
        Func<string>? requestIdFactory = null)
    {
        _options = options;
        _connectionInfo = connectionInfo;
        _connectionFactory = connectionFactory;
        _requestIdFactory = requestIdFactory ?? CreateRequestId;
        _eventChannel = Channel.CreateUnbounded<DevHubEvent>();
        _pendingRequests = new ConcurrentDictionary<string, TaskCompletionSource<JsonElement>>(StringComparer.Ordinal);
        _sendLock = new SemaphoreSlim(1, 1);
        _disposeCts = new CancellationTokenSource();
    }

    /// <summary>
    /// 通过运行时目录创建事件客户端。
    /// </summary>
    /// <param name="options">客户端选项。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>事件客户端实例。</returns>
    public static async Task<DevHubEventsClient> FromRuntimeAsync(DevHubClientOptions options, CancellationToken cancellationToken = default)
    {
        var clonedOptions = options?.Clone() ?? throw new ArgumentNullException(nameof(options));
        var connectionInfo = await RuntimeDiscovery.DiscoverAsync(clonedOptions, cancellationToken);
        return new DevHubEventsClient(clonedOptions, connectionInfo, new ClientWebSocketConnectionFactory());
    }

    internal static async Task<DevHubEventsClient> FromRuntimeAsync(
        DevHubClientOptions options,
        IWebSocketConnectionFactory connectionFactory,
        Func<string>? requestIdFactory = null,
        CancellationToken cancellationToken = default)
    {
        var clonedOptions = options?.Clone() ?? throw new ArgumentNullException(nameof(options));
        var connectionInfo = await RuntimeDiscovery.DiscoverAsync(clonedOptions, cancellationToken);
        return new DevHubEventsClient(clonedOptions, connectionInfo, connectionFactory, requestIdFactory);
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
            throw new InvalidOperationException("当前事件客户端已完成认证。");
        }

        await EnsureConnectedAsync(cancellationToken);

        try
        {
            var result = await SendRequestAsync(
                "hub.ws.authenticate",
                new Dictionary<string, object?>
                {
                    ["token"] = _connectionInfo.Token,
                    ["protocolVersion"] = _options.ProtocolVersion,
                    ["clientId"] = _options.ClientId,
                    ["clientSessionId"] = _options.ClientSessionId.ToString("D")
                },
                requireAuthenticated: false,
                cancellationToken);

            var payload = JsonSerializer.Deserialize<AuthenticateResultContract>(result.GetRawText(), DevHubJson.SerializerOptions)
                ?? throw new InvalidOperationException("无法解析 hub.ws.authenticate 结果。");

            if (!payload.Ok || payload.ProtocolVersion != 1)
            {
                throw new InvalidOperationException("hub.ws.authenticate 返回结果非法。");
            }

            _authenticated = true;
        }
        catch
        {
            await DisposeConnectionAsync();
            throw;
        }
    }

    /// <summary>
    /// 订阅事件。
    /// </summary>
    /// <param name="types">事件类型列表；空或 <see langword="null"/> 表示订阅全部。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>订阅标识。</returns>
    public async Task<string> SubscribeAsync(IEnumerable<string>? types = null, CancellationToken cancellationToken = default)
    {
        EnsureAuthenticated();

        object? parameters = null;
        if (types is not null)
        {
            var typeArray = types.ToArray();
            if (typeArray.Any(static item => item is null))
            {
                throw new ArgumentException("types 不能包含 null。", nameof(types));
            }

            if (typeArray.Any(static item => string.IsNullOrWhiteSpace(item)))
            {
                throw new ArgumentException("types 不能包含空白字符串。", nameof(types));
            }

            if (typeArray.Length > 0)
            {
                parameters = new Dictionary<string, object?>
                {
                    ["types"] = typeArray
                };
            }
        }

        var result = await SendRequestAsync("hub.events.subscribe", parameters, requireAuthenticated: true, cancellationToken);
        var payload = JsonSerializer.Deserialize<SubscribeResultContract>(result.GetRawText(), DevHubJson.SerializerOptions)
            ?? throw new InvalidOperationException("无法解析 hub.events.subscribe 结果。");

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
        ArgumentException.ThrowIfNullOrWhiteSpace(subscriptionId);

        var result = await SendRequestAsync(
            "hub.events.unsubscribe",
            new Dictionary<string, object?>
            {
                ["subscriptionId"] = subscriptionId
            },
            requireAuthenticated: true,
            cancellationToken);

        var payload = JsonSerializer.Deserialize<OkOnlyContract>(result.GetRawText(), DevHubJson.SerializerOptions)
            ?? throw new InvalidOperationException("无法解析 hub.events.unsubscribe 结果。");

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
        EnsureAuthenticated();

        return ReadEventsCore(cancellationToken);
    }

    private async IAsyncEnumerable<DevHubEvent> ReadEventsCore([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        while (await _eventChannel.Reader.WaitToReadAsync(cancellationToken))
        {
            while (_eventChannel.Reader.TryRead(out var evt))
            {
                yield return evt;
            }
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
        _disposeCts.Cancel();

        await DisposeConnectionAsync();

        if (_receiverLoopTask is not null)
        {
            try
            {
                await _receiverLoopTask;
            }
            catch
            {
            }
        }

        _eventChannel.Writer.TryComplete();
        _sendLock.Dispose();
        _disposeCts.Dispose();
    }

    private async Task EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        if (_connection is not null)
        {
            return;
        }

        using var linkedCts = CreateLinkedTokenSource(cancellationToken);
        _connection = await _connectionFactory.ConnectAsync(_connectionInfo.WebSocketEndpoint, linkedCts.Token);
        _receiverLoopTask = Task.Run(() => RunReceiveLoopAsync(_connection, _disposeCts.Token), CancellationToken.None);
    }

    private async Task<JsonElement> SendRequestAsync(string method, object? parameters, bool requireAuthenticated, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (requireAuthenticated)
        {
            EnsureAuthenticated();
        }

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
                new
                {
                    jsonrpc = "2.0",
                    id = requestId,
                    method,
                    @params = parameters
                },
                DevHubJson.SerializerOptions);

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
            return await waiter.Task.WaitAsync(linkedCts.Token);
        }
        catch
        {
            _pendingRequests.TryRemove(requestId, out _);
            throw;
        }
    }

    private async Task RunReceiveLoopAsync(IWebSocketConnection connection, CancellationToken cancellationToken)
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

                if (message.MessageType != WebSocketMessageType.Text || string.IsNullOrWhiteSpace(message.Text))
                {
                    continue;
                }

                using var document = JsonDocument.Parse(message.Text);
                var root = document.RootElement;

                if (TryGetRequestId(root, out var requestId) &&
                    (root.TryGetProperty("result", out var resultElement) || root.TryGetProperty("error", out _)))
                {
                    CompletePendingRequest(requestId, root, resultElement);
                    continue;
                }

                if (root.TryGetProperty("method", out var methodElement) &&
                    methodElement.ValueKind == JsonValueKind.String &&
                    string.Equals(methodElement.GetString(), "hub.event", StringComparison.Ordinal) &&
                    root.TryGetProperty("params", out var paramsElement))
                {
                    var evt = JsonSerializer.Deserialize<DevHubEvent>(paramsElement.GetRawText(), DevHubJson.SerializerOptions);
                    if (evt is not null)
                    {
                        await _eventChannel.Writer.WriteAsync(evt, cancellationToken);
                    }
                }
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
            _authenticated = false;
            foreach (var pendingRequest in _pendingRequests.ToArray())
            {
                if (_pendingRequests.TryRemove(pendingRequest.Key, out var pending))
                {
                    pending.TrySetException(terminalException ?? new InvalidOperationException("WebSocket 连接已关闭。"));
                }
            }

            _eventChannel.Writer.TryComplete(terminalException);
            await DisposeConnectionAsync();
        }
    }

    private void CompletePendingRequest(string requestId, JsonElement root, JsonElement resultElement)
    {
        if (!_pendingRequests.TryRemove(requestId, out var waiter))
        {
            return;
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

    private static bool TryGetRequestId(JsonElement root, out string requestId)
    {
        requestId = string.Empty;
        if (!root.TryGetProperty("id", out var idElement))
        {
            return false;
        }

        requestId = idElement.ValueKind == JsonValueKind.String
            ? idElement.GetString() ?? string.Empty
            : idElement.GetRawText();

        return !string.IsNullOrWhiteSpace(requestId);
    }

    private void EnsureAuthenticated()
    {
        ThrowIfDisposed();
        if (!_authenticated)
        {
            throw new InvalidOperationException("当前事件客户端尚未认证。");
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private async Task DisposeConnectionAsync()
    {
        if (_connection is null)
        {
            return;
        }

        var connection = _connection;
        _connection = null;

        try
        {
            using var cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            await connection.CloseAsync(WebSocketCloseStatus.NormalClosure, "client_dispose", cancellationTokenSource.Token);
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

    private static string CreateRequestId()
    {
        return $"ws-{Guid.NewGuid():N}";
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

    private sealed class OkOnlyContract
    {
        public bool Ok { get; set; }
    }
}
