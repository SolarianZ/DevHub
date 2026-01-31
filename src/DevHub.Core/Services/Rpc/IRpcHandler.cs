using DevHub.Core.Models.Rpc;

namespace DevHub.Core.Services.Rpc;

/// <summary>
/// RPC处理器接口
/// </summary>
public interface IRpcHandler
{
    /// <summary>
    /// 支持的方法名称
    /// </summary>
    string Method { get; }

    /// <summary>
    /// 处理RPC请求
    /// </summary>
    Task<JsonRpcResponse> HandleAsync(JsonRpcRequest request, CancellationToken cancellationToken);
}
