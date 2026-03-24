using DevHub.Core.Models.Rpc;

namespace DevHub.Core.Services.Rpc;

/// <summary>
/// RPC 标准错误响应工厂。
/// </summary>
public static class RpcErrorFactory
{
    /// <summary>
    /// 构建 JSON-RPC 错误响应。
    /// </summary>
    /// <param name="id">请求 ID。</param>
    /// <param name="code">错误码。</param>
    /// <param name="message">错误消息。</param>
    /// <param name="data">错误扩展数据。</param>
    /// <returns>统一格式的错误响应。</returns>
    public static JsonRpcResponse Create(object? id, int code, string message, object? data = null)
    {
        return new JsonRpcResponse
        {
            Id = id,
            Error = new JsonRpcError
            {
                Code = code,
                Message = message,
                Data = data
            }
        };
    }

    /// <summary>
    /// 构建 method_not_found 错误。
    /// </summary>
    public static JsonRpcResponse MethodNotFound(object? id)
    {
        return Create(id, -32601, "method_not_found");
    }

    /// <summary>
    /// 构建 invalid_params 错误。
    /// </summary>
    public static JsonRpcResponse InvalidParams(object? id)
    {
        return Create(id, -32602, "invalid_params");
    }

    /// <summary>
    /// 构建 internal_error 错误。
    /// </summary>
    public static JsonRpcResponse InternalError(object? id)
    {
        return Create(id, -32603, "internal_error");
    }

    /// <summary>
    /// 构建 forbidden 错误。
    /// </summary>
    /// <param name="id">请求 ID。</param>
    /// <param name="data">错误扩展数据。</param>
    public static JsonRpcResponse Forbidden(object? id, object? data = null)
    {
        return Create(id, -32002, "forbidden", data);
    }
}
