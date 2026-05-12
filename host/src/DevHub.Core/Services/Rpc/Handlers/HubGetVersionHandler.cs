using DevHub.Core.Models.Rpc;
using DevHub.Core.Services.Abstractions;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace DevHub.Core.Services.Rpc.Handlers;

/// <summary>
/// 处理 <c>hub.getVersion</c> 方法的 RPC 处理器。
/// </summary>
public sealed class HubGetVersionHandler : IRpcHandler
{
    private readonly IHubVersionSource _hubVersionSource;
    private readonly ILogger<HubGetVersionHandler> _logger;

    /// <summary>
    /// 初始化 <c>hub.getVersion</c> RPC 处理器。
    /// </summary>
    public HubGetVersionHandler(IHubVersionSource hubVersionSource, ILogger<HubGetVersionHandler> logger)
    {
        _hubVersionSource = hubVersionSource ?? throw new ArgumentNullException(nameof(hubVersionSource));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public string Method => HubRpcMethods.HubGetVersion;

    /// <inheritdoc />
    public Task<JsonRpcResponse> HandleAsync(JsonRpcRequest request, CancellationToken cancellationToken)
    {
        if (!string.Equals(request.Method, HubRpcMethods.HubGetVersion, StringComparison.Ordinal))
        {
            return Task.FromResult(RpcErrorFactory.MethodNotFound(request.Id));
        }

        _logger.LogDebug(
            "收到 hub.getVersion 请求，RequestId: {RequestId}, 参数: {Params}",
            request.Id,
            RpcLogJsonSerializer.Serialize(request.Params));

        if (!AcceptsParams(request.Params))
        {
            _logger.LogWarning("hub.getVersion 参数非法，RequestId: {RequestId}", request.Id);
            return Task.FromResult(RpcErrorFactory.InvalidParams(request.Id));
        }

        var response = new JsonRpcResponse
        {
            Id = request.Id,
            Result = new Dictionary<string, object?>
            {
                ["ok"] = true,
                ["version"] = _hubVersionSource.CurrentVersion
            }
        };

        _logger.LogInformation("处理 hub.getVersion 请求成功，RequestId: {RequestId}", request.Id);
        _logger.LogDebug("hub.getVersion 响应内容: {Response}", RpcLogJsonSerializer.Serialize(response));
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
            if (paramsElement.ValueKind == JsonValueKind.Null)
            {
                return true;
            }

            if (paramsElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var enumerator = paramsElement.EnumerateObject();
            return !enumerator.MoveNext();
        }

        if (parameters is IDictionary<string, object?> dictionary)
        {
            return dictionary.Count == 0;
        }

        return false;
    }
}
