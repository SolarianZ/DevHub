using DevHub.Core.Models.Rpc;
using DevHub.Core.Services.Rpc;

namespace DevHub.Host.Transport;

/// <summary>
/// Host 传输层 JSON-RPC 响应工厂。
/// </summary>
internal static class TransportResponseFactory
{
    /// <summary>
    /// 创建标准 JSON-RPC 错误响应。
    /// </summary>
    internal static JsonRpcResponse CreateErrorResponse(int code, string message, object? id, object? data = null)
    {
        return RpcErrorFactory.Create(id, code, message, data);
    }

    /// <summary>
    /// 创建事件订阅成功响应。
    /// </summary>
    internal static JsonRpcResponse CreateSubscribeSuccessResponse(object? id, string subscriptionId)
    {
        return new JsonRpcResponse
        {
            Id = id,
            Result = new
            {
                ok = true,
                subscriptionId
            }
        };
    }

    /// <summary>
    /// 创建取消订阅成功响应。
    /// </summary>
    internal static JsonRpcResponse CreateUnsubscribeSuccessResponse(object? id)
    {
        return new JsonRpcResponse
        {
            Id = id,
            Result = new
            {
                ok = true
            }
        };
    }
}
