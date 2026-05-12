using DevHub.Core.Models.Rpc;
using DevHub.Core.Services;
using DevHub.Core.Services.Abstractions;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace DevHub.Core.Services.Rpc.Handlers;

/// <summary>
/// 处理 <c>hub.ping</c> 方法的 RPC 处理器。
/// </summary>
public class HubPingHandler : IRpcHandler
{
    private readonly IClock _clock;
    private readonly ILogger<HubPingHandler> _logger;

    /// <summary>
    /// 初始化 <c>hub.ping</c> RPC 处理器。
    /// </summary>
    public HubPingHandler(IClock clock, ILogger<HubPingHandler> logger)
    {
        _clock = clock;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Method => HubRpcMethods.HubPing;

    /// <inheritdoc />
    public Task<JsonRpcResponse> HandleAsync(JsonRpcRequest request, CancellationToken cancellationToken)
    {
        if (!string.Equals(request.Method, HubRpcMethods.HubPing, StringComparison.Ordinal))
        {
            return Task.FromResult(RpcErrorFactory.MethodNotFound(request.Id));
        }

        _logger.LogDebug(
            "收到 hub.ping 请求，RequestId: {RequestId}, 参数: {Params}",
            request.Id,
            RpcLogJsonSerializer.Serialize(request.Params));

        if (!AcceptsParams(request.Params))
        {
            _logger.LogWarning("hub.ping 参数非法，RequestId: {RequestId}", request.Id);
            return Task.FromResult(RpcErrorFactory.InvalidParams(request.Id));
        }

        var result = new Dictionary<string, object?>
        {
            ["ok"] = true,
            ["serverTimeUtc"] = _clock.UtcNow.ToString("O")
        };

        if (TryReadEcho(request.Params, out var echo))
        {
            result["echo"] = echo;
        }

        var response = new JsonRpcResponse
        {
            Id = request.Id,
            Result = result
        };

        _logger.LogInformation("处理 hub.ping 请求成功，RequestId: {RequestId}", request.Id);
        _logger.LogDebug("hub.ping 响应内容: {Response}", RpcLogJsonSerializer.Serialize(response));
        return Task.FromResult(response);
    }

    private static bool AcceptsParams(object? parameters)
    {
        if (parameters is null)
        {
            return true;
        }

        if (parameters is JsonElement paramsElement)
        {
            return paramsElement.ValueKind is JsonValueKind.Null or JsonValueKind.Object;
        }

        return parameters is IDictionary<string, object?>;
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
