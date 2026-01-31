using DevHub.Core.Models.Rpc;
using System.Text.Json;
using DevHub.Core.Services;

namespace DevHub.Core.Services.Rpc.Handlers;

/// <summary>
/// 处理hub.ping方法的RPC处理器
/// </summary>
public class HubPingHandler : IRpcHandler
{
    private readonly ILoggerService _logger;

    public HubPingHandler(ILoggerService logger)
    {
        _logger = logger;
    }

    public string Method => "hub.ping";

    public Task<JsonRpcResponse> HandleAsync(JsonRpcRequest request, CancellationToken cancellationToken)
    {
        _logger.Debug("收到 hub.ping 请求，RequestId: {RequestId}, 参数: {Params}", request.Id, JsonSerializer.Serialize(request.Params));

        var response = new JsonRpcResponse
        {
            Id = request.Id,
            Result = new
            {
                ok = true,
                serverTimeUtc = DateTime.UtcNow.ToString("O")
            }
        };

        _logger.Information("处理 hub.ping 请求成功，RequestId: {RequestId}", request.Id);
        _logger.Debug("hub.ping 响应内容: {Response}", JsonSerializer.Serialize(response));
        return Task.FromResult(response);
    }
}
