using Microsoft.Extensions.Logging;
using DevHub.Core.Models.Rpc;
using System.Collections.Concurrent;

namespace DevHub.Core.Services.Rpc;

/// <summary>
/// RPC路由处理器
/// </summary>
public class RpcRouter
{
    private readonly ConcurrentDictionary<string, IRpcHandler> _handlers = new();
    private readonly ILogger<RpcRouter> _logger;

    public RpcRouter(IEnumerable<IRpcHandler> handlers, ILogger<RpcRouter> logger)
    {
        _logger = logger;

        foreach (var handler in handlers)
        {
            if (_handlers.TryAdd(handler.Method, handler))
            {
                _logger.LogInformation("已注册RPC处理器: {Method}", handler.Method);
            }
            else
            {
                _logger.LogWarning("RPC方法已存在: {Method}", handler.Method);
            }
        }
    }

    /// <summary>
    /// 路由RPC请求
    /// </summary>
    public async Task<JsonRpcResponse> RouteAsync(JsonRpcRequest request, CancellationToken cancellationToken)
    {
        if (_handlers.TryGetValue(request.Method, out var handler))
        {
            try
            {
                return await handler.HandleAsync(request, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "处理RPC请求失败: {Method}", request.Method);
                return CreateErrorResponse(request, -32603, "内部错误");
            }
        }
        else
        {
            _logger.LogWarning("未找到RPC方法: {Method}", request.Method);
            return CreateErrorResponse(request, -32601, "方法未找到");
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
