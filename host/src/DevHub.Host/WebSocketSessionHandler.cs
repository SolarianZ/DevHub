using DevHub.Core.Models.Rpc;
using DevHub.Core.Services;
using DevHub.Core.Services.Events;
using DevHub.Core.Services.Rpc;
using DevHub.Host.Transport;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DevHub.Host;

/// <summary>
/// WebSocket 会话处理器。
/// </summary>
public class WebSocketSessionHandler
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly RpcRouter _rpcRouter;
    private readonly FileSystemManager _fileSystemManager;
    private readonly HubEventBus _eventBus;
    private readonly ILogger<WebSocketSessionHandler> _logger;

    /// <summary>
    /// 初始化 WebSocket 会话处理器。
    /// </summary>
    /// <param name="rpcRouter">RPC 路由器。</param>
    /// <param name="fileSystemManager">文件系统管理器。</param>
    /// <param name="eventBus">事件总线。</param>
    /// <param name="logger">日志记录器。</param>
    public WebSocketSessionHandler(
        RpcRouter rpcRouter,
        FileSystemManager fileSystemManager,
        HubEventBus eventBus,
        ILogger<WebSocketSessionHandler> logger)
    {
        _rpcRouter = rpcRouter;
        _fileSystemManager = fileSystemManager;
        _eventBus = eventBus;
        _logger = logger;
    }

    /// <summary>
    /// 处理 WS 入口请求。
    /// </summary>
    /// <param name="context">HTTP 上下文。</param>
    /// <param name="currentPort">当前监听端口。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>处理任务。</returns>
    public async Task HandleEndpointAsync(HttpContext context, int? currentPort, CancellationToken cancellationToken)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        _fileSystemManager.EnsureRuntimeArtifacts(currentPort > 0 ? currentPort : null);

        using var webSocket = await context.WebSockets.AcceptWebSocketAsync();
        await HandleWebSocketConnectionAsync(webSocket, cancellationToken);
    }

    /// <summary>
    /// 处理 WebSocket 连接生命周期。
    /// </summary>
    /// <param name="webSocket">WebSocket 对象。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    internal async Task HandleWebSocketConnectionAsync(WebSocket webSocket, CancellationToken cancellationToken)
    {
        var connectionId = $"conn-{Guid.NewGuid():N}";
        var isAuthenticated = false;
        var firstMessageProcessed = false;
        string? authenticatedClientId = null;
        string? authenticatedClientSessionId = null;

        _eventBus.RegisterConnection(connectionId);
        _logger.LogInformation("WS 连接已建立，ConnectionId: {ConnectionId}", connectionId);

        try
        {
            while (webSocket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                var receiveTask = ReceiveTextMessageAsync(webSocket, cancellationToken);

                while (!receiveTask.IsCompleted)
                {
                    await SendPendingHubEventsAsync(webSocket, connectionId, cancellationToken);
                    var completedTask = await Task.WhenAny(receiveTask, Task.Delay(50, cancellationToken));
                    if (completedTask == receiveTask)
                    {
                        break;
                    }
                }

                var receiveEnvelope = await receiveTask;
                if (receiveEnvelope.IsCloseFrame)
                {
                    _logger.LogInformation("WS 收到关闭帧，ConnectionId: {ConnectionId}", connectionId);
                    break;
                }

                if (!receiveEnvelope.IsTextFrame)
                {
                    _logger.LogWarning("WS 收到非文本帧，主动关闭连接，ConnectionId: {ConnectionId}", connectionId);
                    await CloseWebSocketAsync(webSocket, WebSocketCloseStatus.InvalidMessageType, "text_frame_required", cancellationToken);
                    break;
                }

                var messageText = receiveEnvelope.Text ?? string.Empty;
                _logger.LogDebug("收到 WS 消息，ConnectionId: {ConnectionId}, 消息长度: {MessageLength}", connectionId, messageText.Length);

                JsonDocument requestDocument;
                try
                {
                    requestDocument = JsonDocument.Parse(messageText);
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(ex, "WS JSON 解析失败，ConnectionId: {ConnectionId}", connectionId);
                    await SendWebSocketJsonAsync(webSocket, TransportResponseFactory.CreateErrorResponse(-32700, "parse_error", null), cancellationToken);

                    if (!isAuthenticated)
                    {
                        await CloseWebSocketAsync(webSocket, WebSocketCloseStatus.PolicyViolation, "parse_error", cancellationToken);
                        break;
                    }

                    continue;
                }

                using (requestDocument)
                {
                    var root = requestDocument.RootElement;
                    if (root.ValueKind == JsonValueKind.Array || root.ValueKind != JsonValueKind.Object)
                    {
                        await SendWebSocketJsonAsync(webSocket, TransportResponseFactory.CreateErrorResponse(-32600, "invalid_request", null), cancellationToken);
                        if (!isAuthenticated)
                        {
                            await CloseWebSocketAsync(webSocket, WebSocketCloseStatus.PolicyViolation, "invalid_request", cancellationToken);
                            break;
                        }

                        continue;
                    }

                    if (!JsonRpcEnvelopeParser.TryParse(root, out var rpcRequest, out var envelopeError))
                    {
                        await SendWebSocketJsonAsync(webSocket, envelopeError, cancellationToken);
                        if (!isAuthenticated)
                        {
                            await CloseWebSocketAsync(webSocket, WebSocketCloseStatus.PolicyViolation, "invalid_request", cancellationToken);
                            break;
                        }

                        continue;
                    }

                    if (!firstMessageProcessed)
                    {
                        firstMessageProcessed = true;

                        if (!string.Equals(rpcRequest.Method, HubRpcMethods.HubWsAuthenticate, StringComparison.Ordinal))
                        {
                            if (rpcRequest.Id is not null)
                            {
                                await SendWebSocketJsonAsync(
                                    webSocket,
                                    TransportResponseFactory.CreateErrorResponse(-32001, "unauthorized", rpcRequest.Id, new { reason = "missing_token" }),
                                    cancellationToken);
                            }

                            await CloseWebSocketAsync(webSocket, WebSocketCloseStatus.PolicyViolation, "authentication_required", cancellationToken);
                            break;
                        }

                        if (rpcRequest.Id is null)
                        {
                            await SendWebSocketJsonAsync(webSocket, TransportResponseFactory.CreateErrorResponse(-32600, "invalid_request", null), cancellationToken);
                            await CloseWebSocketAsync(webSocket, WebSocketCloseStatus.PolicyViolation, "auth_request_id_required", cancellationToken);
                            break;
                        }
                    }

                    if (!isAuthenticated && !string.Equals(rpcRequest.Method, HubRpcMethods.HubWsAuthenticate, StringComparison.Ordinal))
                    {
                        if (rpcRequest.Id is not null)
                        {
                            await SendWebSocketJsonAsync(
                                webSocket,
                                TransportResponseFactory.CreateErrorResponse(-32001, "unauthorized", rpcRequest.Id, new { reason = "missing_token" }),
                                cancellationToken);
                            await CloseWebSocketAsync(webSocket, WebSocketCloseStatus.PolicyViolation, "authentication_required", cancellationToken);
                        }
                        else
                        {
                            await CloseWebSocketAsync(webSocket, WebSocketCloseStatus.PolicyViolation, "authentication_required", cancellationToken);
                        }

                        break;
                    }

                    if (JsonRpcEnvelopeParser.IsHubMethodParamsArray(rpcRequest))
                    {
                        if (rpcRequest.Id is not null)
                        {
                            await SendWebSocketJsonAsync(webSocket, TransportResponseFactory.CreateErrorResponse(-32602, "invalid_params", rpcRequest.Id), cancellationToken);
                        }

                        continue;
                    }

                    JsonRpcResponse? response = null;
                    var closeAfterResponse = false;
                    (response, closeAfterResponse) = await DispatchWebSocketRpcAsync(
                        connectionId,
                        rpcRequest,
                        isAuthenticated,
                        authenticatedClientId,
                        authenticatedClientSessionId,
                        cancellationToken,
                        onAuthenticated: (nextClientId, nextSessionId) =>
                        {
                            isAuthenticated = true;
                            authenticatedClientId = nextClientId;
                            authenticatedClientSessionId = nextSessionId;
                        });

                    if (response is not null && rpcRequest.Id is not null)
                    {
                        await SendWebSocketJsonAsync(webSocket, response, cancellationToken);
                    }

                    if (closeAfterResponse)
                    {
                        await CloseWebSocketAsync(webSocket, WebSocketCloseStatus.PolicyViolation, "authentication_failed", cancellationToken);
                        break;
                    }
                }

                await SendPendingHubEventsAsync(webSocket, connectionId, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("WS 连接处理被取消，ConnectionId: {ConnectionId}", connectionId);
        }
        catch (WebSocketException ex)
        {
            _logger.LogWarning(ex, "WS 连接异常，ConnectionId: {ConnectionId}", connectionId);
        }
        finally
        {
            _eventBus.RemoveConnection(connectionId);
            await CloseWebSocketAsync(webSocket, WebSocketCloseStatus.NormalClosure, "connection_closed", CancellationToken.None);
            _logger.LogInformation("WS 连接已清理，ConnectionId: {ConnectionId}", connectionId);
        }
    }

    private async Task SendPendingHubEventsAsync(
        WebSocket webSocket,
        string connectionId,
        CancellationToken cancellationToken)
    {
        if (webSocket.State != WebSocketState.Open)
        {
            return;
        }

        var deliveries = _eventBus.DrainDeliveries(connectionId, maxCount: 32);
        foreach (var delivery in deliveries)
        {
            var notification = HubEventNotificationFactory.Create(delivery);

            await SendWebSocketJsonAsync(webSocket, notification, cancellationToken);
        }
    }

    private async Task<(JsonRpcResponse? Response, bool CloseAfterResponse)> DispatchWebSocketRpcAsync(
        string connectionId,
        JsonRpcRequest rpcRequest,
        bool isAuthenticated,
        string? authenticatedClientId,
        string? authenticatedClientSessionId,
        CancellationToken cancellationToken,
        Action<string?, string?> onAuthenticated)
    {
        switch (rpcRequest.Method)
        {
            case HubRpcMethods.HubWsAuthenticate:
                return HandleAuthenticate(
                    connectionId,
                    rpcRequest,
                    isAuthenticated,
                    onAuthenticated);

            case HubRpcMethods.HubEventsSubscribe:
                return HandleSubscribe(connectionId, rpcRequest);

            case HubRpcMethods.HubEventsUnsubscribe:
                return (HandleUnsubscribe(connectionId, rpcRequest), false);

            default:
                if (TransportMethodPolicy.IsHttpOnlyMethod(rpcRequest.Method))
                {
                    return (
                        TransportResponseFactory.CreateErrorResponse(
                            -32099,
                            "not_supported",
                            rpcRequest.Id,
                            new { reason = "transport_mismatch", expected = "http" }),
                        false);
                }

                rpcRequest.ClientId = authenticatedClientId;
                rpcRequest.ClientSessionId = authenticatedClientSessionId;
                return (await _rpcRouter.RouteAsync(rpcRequest, cancellationToken), false);
        }
    }

    private (JsonRpcResponse Response, bool CloseAfterResponse) HandleAuthenticate(
        string connectionId,
        JsonRpcRequest rpcRequest,
        bool isAuthenticated,
        Action<string?, string?> onAuthenticated)
    {
        if (isAuthenticated)
        {
            return (TransportResponseFactory.CreateErrorResponse(-32600, "invalid_request", rpcRequest.Id, new { reason = "already_authenticated" }), false);
        }

        var response = WebSocketAuthenticationProcessor.Authenticate(
            rpcRequest,
            _fileSystemManager.GetToken,
            (clientId, sessionId) => _eventBus.TryMarkAuthenticated(connectionId, clientId, sessionId),
            out var authenticated,
            out var nextClientId,
            out var nextClientSessionId,
            out var closeAfterResponse);

        if (authenticated)
        {
            onAuthenticated(nextClientId, nextClientSessionId);
            _logger.LogInformation(
                "WS 鉴权成功，ConnectionId: {ConnectionId}, ClientId: {ClientId}, SessionId: {SessionId}",
                connectionId,
                nextClientId,
                nextClientSessionId);
        }

        return (response, closeAfterResponse);
    }

    private (JsonRpcResponse Response, bool CloseAfterResponse) HandleSubscribe(string connectionId, JsonRpcRequest rpcRequest)
    {
        if (!EventSubscriptionRequestParser.TryReadSubscriptionTypes(rpcRequest, out var subscriptionTypes, out var subscribeError))
        {
            return (subscribeError, false);
        }

        if (!_eventBus.TrySubscribe(connectionId, subscriptionTypes, out var subscriptionId))
        {
            return (TransportResponseFactory.CreateErrorResponse(-32001, "unauthorized", rpcRequest.Id, new { reason = "missing_token" }), true);
        }

        return (TransportResponseFactory.CreateSubscribeSuccessResponse(rpcRequest.Id, subscriptionId), false);
    }

    private JsonRpcResponse HandleUnsubscribe(string connectionId, JsonRpcRequest rpcRequest)
    {
        if (!EventSubscriptionRequestParser.TryReadUnsubscribeParam(rpcRequest, out var subscriptionIdToRemove, out var unsubscribeError))
        {
            return unsubscribeError;
        }

        _eventBus.Unsubscribe(connectionId, subscriptionIdToRemove);
        return TransportResponseFactory.CreateUnsubscribeSuccessResponse(rpcRequest.Id);
    }

    private static async Task<WebSocketReceiveEnvelope> ReceiveTextMessageAsync(WebSocket webSocket, CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        using var stream = new MemoryStream();

        while (true)
        {
            var receiveResult = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
            if (receiveResult.MessageType == WebSocketMessageType.Close)
            {
                return new WebSocketReceiveEnvelope(true, false, null);
            }

            if (receiveResult.MessageType != WebSocketMessageType.Text)
            {
                return new WebSocketReceiveEnvelope(false, false, null);
            }

            if (receiveResult.Count > 0)
            {
                stream.Write(buffer, 0, receiveResult.Count);
            }

            if (receiveResult.EndOfMessage)
            {
                return new WebSocketReceiveEnvelope(false, true, Encoding.UTF8.GetString(stream.ToArray()));
            }
        }
    }

    private static async Task SendWebSocketJsonAsync(WebSocket webSocket, object payload, CancellationToken cancellationToken)
    {
        if (webSocket.State != WebSocketState.Open)
        {
            return;
        }

        var json = JsonSerializer.Serialize(payload, JsonOptions);
        var bytes = Encoding.UTF8.GetBytes(json);
        await webSocket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cancellationToken);
    }

    private async Task CloseWebSocketAsync(
        WebSocket webSocket,
        WebSocketCloseStatus closeStatus,
        string description,
        CancellationToken cancellationToken)
    {
        if (webSocket.State is not (WebSocketState.Open or WebSocketState.CloseReceived))
        {
            return;
        }

        try
        {
            await webSocket.CloseAsync(closeStatus, description, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "关闭 WS 连接时发生异常，状态: {State}, Description: {Description}", webSocket.State, description);
        }
    }

    private readonly record struct WebSocketReceiveEnvelope(bool IsCloseFrame, bool IsTextFrame, string? Text);
}
