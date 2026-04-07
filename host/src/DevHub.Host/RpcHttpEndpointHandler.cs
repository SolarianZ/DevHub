using DevHub.Core.Models.Rpc;
using DevHub.Core.Services.Rpc;
using DevHub.Host.Runtime;
using DevHub.Host.Transport;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DevHub.Host;

/// <summary>
/// HTTP RPC 端点处理器。
/// </summary>
public class RpcHttpEndpointHandler
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly RpcRouter _rpcRouter;
    private readonly HostRuntimeArtifactManager _runtimeArtifactManager;
    private readonly ILogger<RpcHttpEndpointHandler> _logger;

    /// <summary>
    /// 初始化 HTTP RPC 端点处理器。
    /// </summary>
    /// <param name="rpcRouter">RPC 路由器。</param>
    /// <param name="runtimeArtifactManager">Host 运行时产物管理器。</param>
    /// <param name="logger">日志记录器。</param>
    public RpcHttpEndpointHandler(
        RpcRouter rpcRouter,
        HostRuntimeArtifactManager runtimeArtifactManager,
        ILogger<RpcHttpEndpointHandler> logger)
    {
        _rpcRouter = rpcRouter;
        _runtimeArtifactManager = runtimeArtifactManager;
        _logger = logger;
    }

    /// <summary>
    /// 处理 /rpc POST 请求。
    /// </summary>
    /// <param name="request">HTTP 请求。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>处理结果。</returns>
    public async Task<IResult> HandleAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        string? clientId = null;
        object? requestId = null;
        string? method = null;
        var suppressJsonRpcResponse = false;

        try
        {
            var currentPort = request.HttpContext.Connection.LocalPort;
            _runtimeArtifactManager.EnsureRuntimeArtifacts(currentPort > 0 ? currentPort : null);

            request.Headers.TryGetValue("X-DevHub-ClientId", out var clientIdValue);
            clientId = clientIdValue;

            if (!HttpTransportRequestValidator.TryValidateContentType(request.ContentType, requestId: null, out var contentTypeError))
            {
                _logger.LogWarning("HTTP Content-Type 校验失败，ClientId: {ClientId}, ErrorCode: {ErrorCode}, ErrorMessage: {ErrorMessage}",
                    clientId, contentTypeError.Error?.Code, contentTypeError.Error?.Message);
                return Results.Json(contentTypeError, JsonOptions);
            }

            using var reader = new StreamReader(request.Body);
            var body = await reader.ReadToEndAsync(cancellationToken);

            _logger.LogDebug("收到RPC请求，客户端ID: {ClientId}，请求体长度: {BodyLength}", clientId, body.Length);

            JsonDocument requestDocument;
            try
            {
                requestDocument = JsonDocument.Parse(body);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "JSON 解析失败，返回 parse_error，ClientId: {ClientId}", clientId);
                return Results.Json(TransportResponseFactory.CreateErrorResponse(-32700, "parse_error", null), JsonOptions);
            }

            using (requestDocument)
            {
                var root = requestDocument.RootElement;
                if (root.ValueKind == JsonValueKind.Array)
                {
                    _logger.LogWarning("收到批量请求，按规范拒绝，ClientId: {ClientId}", clientId);
                    return Results.Json(TransportResponseFactory.CreateErrorResponse(-32600, "invalid_request", null), JsonOptions);
                }

                if (root.ValueKind != JsonValueKind.Object)
                {
                    _logger.LogWarning("收到非对象 JSON-RPC 根节点，ClientId: {ClientId}", clientId);
                    return Results.Json(TransportResponseFactory.CreateErrorResponse(-32600, "invalid_request", null), JsonOptions);
                }

                if (!JsonRpcEnvelopeParser.TryParse(root, out var rpcRequest, out var requestErrorResponse))
                {
                    _logger.LogWarning("JSON-RPC 信封无效，ClientId: {ClientId}", clientId);
                    return Results.Json(requestErrorResponse, JsonOptions);
                }

                requestId = rpcRequest.Id;
                method = rpcRequest.Method;
                suppressJsonRpcResponse = rpcRequest.Id is null;

                _logger.LogInformation("处理RPC请求: {Method}, RequestId: {RequestId}, ClientId: {ClientId}",
                    rpcRequest.Method, rpcRequest.Id, clientId);

                var requestHeaders = request.Headers.ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value.ToString(),
                    StringComparer.OrdinalIgnoreCase);
                if (!HttpTransportRequestValidator.TryValidate(
                        request.ContentType,
                        requestHeaders,
                        _runtimeArtifactManager.GetToken,
                        rpcRequest.Id,
                        out var errorResponse,
                        out var validatedClientId,
                        out var validatedClientSessionId,
                        _logger))
                {
                    _logger.LogWarning("请求头校验失败，Method: {Method}, RequestId: {RequestId}, ClientId: {ClientId}, ErrorCode: {ErrorCode}, ErrorMessage: {ErrorMessage}",
                        rpcRequest.Method, rpcRequest.Id, clientId, errorResponse.Error?.Code, errorResponse.Error?.Message);

                    if (suppressJsonRpcResponse)
                    {
                        return Results.Empty;
                    }

                    return Results.Json(errorResponse, JsonOptions);
                }

                rpcRequest.ClientId = validatedClientId;
                rpcRequest.ClientSessionId = validatedClientSessionId;

                if (JsonRpcEnvelopeParser.IsHubMethodParamsArray(rpcRequest))
                {
                    _logger.LogWarning("hub.* 方法参数为数组，返回 invalid_params，Method: {Method}, RequestId: {RequestId}",
                        rpcRequest.Method, rpcRequest.Id);

                    if (suppressJsonRpcResponse)
                    {
                        return Results.Empty;
                    }

                    return Results.Json(TransportResponseFactory.CreateErrorResponse(-32602, "invalid_params", rpcRequest.Id), JsonOptions);
                }

                if (TransportMethodPolicy.IsWebSocketOnlyMethod(rpcRequest.Method))
                {
                    _logger.LogWarning("HTTP 调用了 WS-only 方法，返回 not_supported，Method: {Method}, RequestId: {RequestId}",
                        rpcRequest.Method, rpcRequest.Id);

                    if (suppressJsonRpcResponse)
                    {
                        return Results.Empty;
                    }

                    return Results.Json(
                        TransportResponseFactory.CreateErrorResponse(
                            -32099,
                            "not_supported",
                            rpcRequest.Id,
                            new { reason = "transport_mismatch", expected = "ws" }),
                        JsonOptions);
                }

                var response = await _rpcRouter.RouteAsync(rpcRequest, cancellationToken);
                if (suppressJsonRpcResponse)
                {
                    stopwatch.Stop();
                    _logger.LogInformation("通知请求已处理（无 id，不返回 JSON-RPC 响应）: {Method}, ClientId: {ClientId}, 处理时间: {ElapsedMilliseconds}ms",
                        rpcRequest.Method, clientId, stopwatch.ElapsedMilliseconds);
                    return Results.Empty;
                }

                stopwatch.Stop();
                _logger.LogInformation("RPC请求处理成功: {Method}, RequestId: {RequestId}, ClientId: {ClientId}, 处理时间: {ElapsedMilliseconds}ms",
                    rpcRequest.Method, rpcRequest.Id, clientId, stopwatch.ElapsedMilliseconds);

                _logger.LogDebug("RPC响应已生成，Method: {Method}, RequestId: {RequestId}, 响应长度: {ResponseLength}",
                    rpcRequest.Method, rpcRequest.Id, JsonSerializer.Serialize(response, JsonOptions).Length);
                return Results.Json(response, JsonOptions);
            }
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.LogError(ex, "处理RPC请求时发生未捕获的异常，Method: {Method}, RequestId: {RequestId}, ClientId: {ClientId}, 处理时间: {ElapsedMilliseconds}ms",
                method, requestId, clientId, stopwatch.ElapsedMilliseconds);

            if (suppressJsonRpcResponse)
            {
                return Results.Empty;
            }

            return Results.Json(TransportResponseFactory.CreateErrorResponse(-32603, "internal_error", requestId), JsonOptions);
        }
    }
}
