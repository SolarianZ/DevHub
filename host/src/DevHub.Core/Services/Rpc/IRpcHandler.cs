using DevHub.Core.Models.Rpc;

namespace DevHub.Core.Services.Rpc;

/// <summary>
/// RPC处理器接口
/// </summary>
public interface IRpcHandler
{
    /// <summary>
    /// 获取当前处理器声明的路由键。
    /// 在 <see cref="RpcRouter"/> 中用于精确匹配或前缀匹配。
    /// </summary>
    string Method { get; }

    /// <summary>
    /// 鎸囩ず褰撳墠澶勭悊鍣ㄦ槸鍚﹀弬涓庡墠缂€璺敱銆?
    /// </summary>
    bool SupportsPrefixRouting => false;

    /// <summary>
    /// 异步处理一条 JSON-RPC 请求。
    /// </summary>
    /// <param name="request">已完成协议层校验并附带运行时上下文的请求对象。</param>
    /// <param name="cancellationToken">请求取消令牌。触发后应尽快停止耗时处理并返回。</param>
    /// <returns>符合 JSON-RPC 2.0 语义的响应对象（成功或错误）。</returns>
    /// <remarks>
    /// 实现方仅处理业务语义，不负责 HTTP 头鉴权、JSON-RPC 信封校验等接入层职责。
    /// </remarks>
    Task<JsonRpcResponse> HandleAsync(JsonRpcRequest request, CancellationToken cancellationToken);
}
