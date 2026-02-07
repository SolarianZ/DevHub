using DevHub.Core.Models.Rpc;
using Microsoft.Extensions.Logging;

namespace DevHub.Core.Services.Rpc.Handlers;

/// <summary>
/// 处理 launch 延后策略。
/// </summary>
public class LaunchHandler : IRpcHandler
{
    private readonly ILogger<LaunchHandler> _logger;

    /// <summary>
    /// 初始化处理器。
    /// </summary>
    public LaunchHandler(ILogger<LaunchHandler> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public string Method => "hub.apps.launch";

    /// <inheritdoc />
    public Task<JsonRpcResponse> HandleAsync(JsonRpcRequest request, CancellationToken cancellationToken)
    {
        _logger.LogWarning("hub.apps.launch 在本阶段尚未实现，返回 not_supported。RequestId: {RequestId}", request.Id);
        return Task.FromResult(new JsonRpcResponse
        {
            Id = request.Id,
            Error = new JsonRpcError
            {
                Code = -32099,
                Message = "not_supported",
                Data = new { reason = "launch_deferred" }
            }
        });
    }
}

