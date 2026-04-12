using System.Text.Json;
using DevHub.Core.Models.Rpc;
using DevHub.Core.Services;
using DevHub.Core.Services.Abstractions;
using DevHub.Core.Services.Rpc;

namespace DevHub.Host.Rpc.Handlers;

/// <summary>
/// Host 侧的 <c>hub.ping</c> 适配处理器。
/// </summary>
public sealed class HubPingRpcHandler : IRpcHandler
{
    private readonly IClock _clock;
    private readonly ILogger<HubPingRpcHandler> _logger;

    /// <summary>
    /// 初始化处理器。
    /// </summary>
    /// <param name="clock">系统时钟。</param>
    /// <param name="logger">日志记录器。</param>
    public HubPingRpcHandler(IClock clock, ILogger<HubPingRpcHandler> logger)
    {
        _clock = clock;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Method => HubRpcMethods.HubPing;

    /// <inheritdoc />
    public Task<JsonRpcResponse> HandleAsync(JsonRpcRequest request, CancellationToken cancellationToken)
    {
        _logger.LogDebug("收到 hub.ping 请求，RequestId: {RequestId}", request.Id);

        var result = new Dictionary<string, object?>
        {
            ["ok"] = true,
            ["serverTimeUtc"] = _clock.UtcNow.ToString("O")
        };

        if (TryReadEcho(request.Params, out var echo))
        {
            result["echo"] = echo;
        }

        return Task.FromResult(new JsonRpcResponse
        {
            Id = request.Id,
            Result = result
        });
    }

    private static bool TryReadEcho(object? parameters, out object? echo)
    {
        echo = null;

        if (parameters is JsonElement paramsElement
            && paramsElement.ValueKind == JsonValueKind.Object
            && paramsElement.TryGetProperty("echo", out var echoElement))
        {
            echo = echoElement.Clone();
            return true;
        }

        if (parameters is IDictionary<string, object?> dictionary
            && dictionary.TryGetValue("echo", out var dictionaryEcho))
        {
            echo = dictionaryEcho;
            return true;
        }

        return false;
    }
}
