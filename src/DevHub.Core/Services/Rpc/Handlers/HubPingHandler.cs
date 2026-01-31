using DevHub.Core.Models.Rpc;

namespace DevHub.Core.Services.Rpc.Handlers;

/// <summary>
/// 处理hub.ping方法的RPC处理器
/// </summary>
public class HubPingHandler : IRpcHandler
{
    public string Method => "hub.ping";

    public Task<JsonRpcResponse> HandleAsync(JsonRpcRequest request, CancellationToken cancellationToken)
    {
        var response = new JsonRpcResponse
        {
            Id = request.Id,
            Result = new
            {
                ok = true,
                serverTimeUtc = DateTime.UtcNow.ToString("O")
            }
        };

        return Task.FromResult(response);
    }
}
