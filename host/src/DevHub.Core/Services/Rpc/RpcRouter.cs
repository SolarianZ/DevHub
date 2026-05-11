using DevHub.Core.Models.Rpc;
using Microsoft.Extensions.Logging;

namespace DevHub.Core.Services.Rpc;

/// <summary>
/// RPC路由处理器
/// </summary>
public class RpcRouter
{
    private readonly Dictionary<string, List<IRpcHandler>> _handlers;
    private readonly IReadOnlyList<KeyValuePair<string, List<IRpcHandler>>> _orderedPrefixHandlers;
    private readonly ILogger<RpcRouter> _logger;
    private readonly RpcTestFaultInjectionPolicy? _faultInjectionPolicy;

    /// <summary>
    /// 初始化 RPC 路由器并注册所有处理器。
    /// </summary>
    /// <param name="handlers">可用的 RPC 处理器集合。</param>
    /// <param name="logger">日志记录器。</param>
    public RpcRouter(IEnumerable<IRpcHandler> handlers, ILogger<RpcRouter> logger, RpcTestFaultInjectionPolicy? faultInjectionPolicy = null)
    {
        _logger = logger;
        _faultInjectionPolicy = faultInjectionPolicy;
        var registrations = handlers.ToList();
        var registrationOrder = new Dictionary<string, int>(StringComparer.Ordinal);
        _handlers = new Dictionary<string, List<IRpcHandler>>(StringComparer.Ordinal);
        _logger.LogDebug("开始注册RPC处理器，处理器数量: {Count}", registrations.Count);

        for (var index = 0; index < registrations.Count; index++)
        {
            var handler = registrations[index];
            if (_handlers.TryGetValue(handler.Method, out var existingHandlers))
            {
                existingHandlers.Add(handler);
                _logger.LogInformation("已成功添加RPC处理器到现有前缀: {Method}", handler.Method);
            }
            else
            {
                _handlers[handler.Method] = [handler];
                registrationOrder[handler.Method] = index;
                _logger.LogInformation("已成功注册RPC处理器前缀: {Method}", handler.Method);
            }
        }

        _orderedPrefixHandlers = _handlers
            .OrderByDescending(static entry => entry.Key.Length)
            .ThenBy(entry => registrationOrder[entry.Key])
            .ToArray();

        _logger.LogDebug("RPC处理器注册完成，注册的前缀数量: {Count}", _handlers.Count);
    }

    /// <summary>
    /// 路由RPC请求
    /// </summary>
    public async Task<JsonRpcResponse> RouteAsync(JsonRpcRequest request, CancellationToken cancellationToken)
    {
        _logger.LogDebug("尝试路由RPC请求: {Method}, RequestId: {RequestId}, 参数: {Params}",
            request.Method, request.Id, RpcLogJsonSerializer.Serialize(request.Params));

        if (_faultInjectionPolicy?.ShouldForceInternalError(request.Id) == true)
        {
            _logger.LogWarning("测试故障注入已命中，强制返回 internal_error。Method: {Method}, RequestId: {RequestId}", request.Method, request.Id);
            return RpcErrorFactory.InternalError(request.Id);
        }

        if (_handlers.TryGetValue(request.Method, out var exactHandlers))
        {
            Exception? firstException = null;
            string? firstFailedMatch = null;
            var matchedHandlers = exactHandlers.Select(handler => $"exact:{handler.Method}").ToArray();

            foreach (var handler in exactHandlers)
            {
                _logger.LogDebug("找到精确匹配的RPC处理器: {HandlerMethod}", handler.Method);
                try
                {
                    var response = await handler.HandleAsync(request, cancellationToken);
                    _logger.LogInformation("RPC请求处理成功: {Method}, RequestId: {RequestId}", request.Method, request.Id);
                    _logger.LogDebug("RPC响应内容: {Response}", RpcLogJsonSerializer.Serialize(response));
                    return response;
                }
                catch (Exception ex)
                {
                    if (ex is OperationCanceledException && cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }

                    firstException ??= ex;
                    firstFailedMatch ??= $"exact:{handler.Method}";
                    _logger.LogError(ex, "处理RPC请求失败: {Method}, RequestId: {RequestId}, 参数: {Params}",
                        request.Method, request.Id, RpcLogJsonSerializer.Serialize(request.Params));
                }
            }

            return LogAndCreateInternalError(request, firstException, firstFailedMatch, matchedHandlers);
        }

        Exception? prefixException = null;
        string? firstFailedPrefixMatch = null;
        var matchedPrefixHandlers = new List<string>();

        foreach (var (prefix, handlers) in _orderedPrefixHandlers)
        {
            if (!request.Method.StartsWith(prefix + ".", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var handler in handlers)
            {
                matchedPrefixHandlers.Add($"{prefix}->{handler.Method}");
                _logger.LogDebug("找到前缀匹配的RPC处理器: {HandlerMethod}", handler.Method);
                try
                {
                    var response = await handler.HandleAsync(request, cancellationToken);
                    if (response.Error == null || response.Error.Code != -32601)
                    {
                        _logger.LogInformation("RPC请求处理完成: {Method}, RequestId: {RequestId}, 响应码: {ResponseCode}",
                            request.Method, request.Id, response.Error?.Code ?? 0);
                        return response;
                    }

                    _logger.LogDebug("前缀匹配处理器返回 method_not_found，继续尝试下一个处理器。Prefix: {Prefix}, HandlerMethod: {HandlerMethod}, RequestId: {RequestId}",
                        prefix, handler.Method, request.Id);
                }
                catch (Exception ex)
                {
                    if (ex is OperationCanceledException && cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }

                    prefixException ??= ex;
                    firstFailedPrefixMatch ??= $"{prefix}->{handler.Method}";
                    _logger.LogError(ex, "处理RPC请求失败: {Method}, RequestId: {RequestId}, 参数: {Params}",
                        request.Method, request.Id, RpcLogJsonSerializer.Serialize(request.Params));
                }
            }
        }

        if (prefixException is not null)
        {
            return LogAndCreateInternalError(request, prefixException, firstFailedPrefixMatch, matchedPrefixHandlers);
        }

        _logger.LogWarning("未找到RPC方法: {Method}, RequestId: {RequestId}, 可用前缀: {AvailablePrefixes}, 参数: {Params}",
            request.Method, request.Id, string.Join(", ", _handlers.Keys), RpcLogJsonSerializer.Serialize(request.Params));
        return RpcErrorFactory.Create(request.Id, -32601, "method_not_found");
    }

    private JsonRpcResponse LogAndCreateInternalError(
        JsonRpcRequest request,
        Exception? firstException,
        string? firstFailedMatch,
        IReadOnlyCollection<string> matchedHandlers)
    {
        _logger.LogError(
            firstException,
            "RPC 路由命中处理器后仍失败，最终返回 internal_error。Method: {Method}, RequestId: {RequestId}, MatchedHandlers: {MatchedHandlers}, FirstFailedMatch: {FirstFailedMatch}",
            request.Method,
            request.Id,
            matchedHandlers.Count > 0 ? string.Join(", ", matchedHandlers) : "<none>",
            firstFailedMatch ?? "<unknown>");
        return RpcErrorFactory.InternalError(request.Id);
    }
}
