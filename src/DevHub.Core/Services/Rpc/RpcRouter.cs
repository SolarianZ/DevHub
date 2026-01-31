using DevHub.Core.Models.Rpc;
using System.Collections.Concurrent;
using System.Text.Json;
using DevHub.Core.Services;

namespace DevHub.Core.Services.Rpc;

/// <summary>
/// RPC路由处理器
/// </summary>
public class RpcRouter
{
    private readonly ConcurrentDictionary<string, List<IRpcHandler>> _handlers = new();
    private readonly ILoggerService _logger;

    public RpcRouter(IEnumerable<IRpcHandler> handlers, ILoggerService logger)
    {
        _logger = logger;
        _logger.Debug("开始注册RPC处理器，处理器数量: {Count}", handlers.Count());

        foreach (var handler in handlers)
        {
            if (_handlers.TryGetValue(handler.Method, out var existingHandlers))
            {
                existingHandlers.Add(handler);
                _logger.Information("已成功添加RPC处理器到现有前缀: {Method}", handler.Method);
            }
            else
            {
                _handlers.TryAdd(handler.Method, new List<IRpcHandler> { handler });
                _logger.Information("已成功注册RPC处理器前缀: {Method}", handler.Method);
            }
        }

        _logger.Debug("RPC处理器注册完成，注册的前缀数量: {Count}", _handlers.Count);
    }

    /// <summary>
    /// 路由RPC请求
    /// </summary>
    public async Task<JsonRpcResponse> RouteAsync(JsonRpcRequest request, CancellationToken cancellationToken)
    {
        _logger.Debug("尝试路由RPC请求: {Method}, RequestId: {RequestId}, 参数: {Params}",
            request.Method, request.Id, JsonSerializer.Serialize(request.Params));

        // 首先尝试精确匹配
        if (_handlers.TryGetValue(request.Method, out var exactHandlers))
        {
            foreach (var handler in exactHandlers)
            {
                _logger.Debug("找到精确匹配的RPC处理器: {HandlerMethod}", handler.Method);
                try
                {
                    var response = await handler.HandleAsync(request, cancellationToken);
                    _logger.Information("RPC请求处理成功: {Method}, RequestId: {RequestId}", request.Method, request.Id);
                    _logger.Debug("RPC响应内容: {Response}", JsonSerializer.Serialize(response));
                    return response;
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "处理RPC请求失败: {Method}, RequestId: {RequestId}, 参数: {Params}",
                        request.Method, request.Id, JsonSerializer.Serialize(request.Params));
                }
            }
            return CreateErrorResponse(request, -32603, "internal_error");
        }
        else
        {
            // 尝试前缀匹配（处理如 "hub.apps" 这样的前缀路由）
            var matchingPrefixes = _handlers.Where(h =>
                request.Method.StartsWith(h.Key + ".", StringComparison.OrdinalIgnoreCase)).ToList();

            foreach (var (prefix, handlers) in matchingPrefixes)
            {
                foreach (var handler in handlers)
                {
                    _logger.Debug("找到前缀匹配的RPC处理器: {HandlerMethod}", handler.Method);
                    try
                    {
                        var response = await handler.HandleAsync(request, cancellationToken);
                        // 如果处理器成功处理了请求，或者返回了除“方法未找到”以外的错误，直接返回响应
                        if (response.Error == null || response.Error.Code != -32601)
                        {
                            _logger.Information("RPC请求处理完成: {Method}, RequestId: {RequestId}, 响应码: {ResponseCode}", 
                                request.Method, request.Id, response.Error?.Code ?? 0);
                            return response;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.Error(ex, "处理RPC请求失败: {Method}, RequestId: {RequestId}, 参数: {Params}",
                            request.Method, request.Id, JsonSerializer.Serialize(request.Params));
                    }
                }
            }

            _logger.Warning("未找到RPC方法: {Method}, RequestId: {RequestId}, 可用前缀: {AvailablePrefixes}, 参数: {Params}",
                request.Method, request.Id, string.Join(", ", _handlers.Keys), JsonSerializer.Serialize(request.Params));
            return CreateErrorResponse(request, -32601, "method_not_found");
        }
    }

    /// <summary>
    /// 创建错误响应
    /// </summary>
    private JsonRpcResponse CreateErrorResponse(JsonRpcRequest request, int code, string message)
    {
        return new JsonRpcResponse
        {
            Id = request.Id,
            Error = new JsonRpcError
            {
                Code = code,
                Message = message
            }
        };
    }
}
