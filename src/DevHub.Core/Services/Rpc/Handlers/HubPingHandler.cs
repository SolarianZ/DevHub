using DevHub.Core.Models.Rpc;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace DevHub.Core.Services.Rpc.Handlers;

/// <summary>
/// 处理hub.ping方法的RPC处理器
/// </summary>
public class HubPingHandler : IRpcHandler
{
    private readonly ILogger<HubPingHandler> _logger;

    /// <summary>
    /// 初始化 hub.ping RPC 处理器。
    /// </summary>
    /// <param name="logger">日志记录器。</param>
    public HubPingHandler(ILogger<HubPingHandler> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public string Method => "hub.ping";

    /// <inheritdoc />
    public Task<JsonRpcResponse> HandleAsync(JsonRpcRequest request, CancellationToken cancellationToken)
    {
        _logger.LogDebug("收到 hub.ping 请求，RequestId: {RequestId}, 参数: {Params}", request.Id, JsonSerializer.Serialize(request.Params));

        var response = new JsonRpcResponse
        {
            Id = request.Id,
            Result = new
            {
                ok = true,
                serverTimeUtc = DateTime.UtcNow.ToString("O")
            }
        };

        _logger.LogInformation("处理 hub.ping 请求成功，RequestId: {RequestId}", request.Id);
        _logger.LogDebug("hub.ping 响应内容: {Response}", JsonSerializer.Serialize(response));
        return Task.FromResult(response);
    }
}
