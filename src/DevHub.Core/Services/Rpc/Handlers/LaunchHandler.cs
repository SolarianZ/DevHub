using DevHub.Core.Models.Rpc;
using DevHub.Core.Services.Invocation;
using DevHub.Core.Services.Rpc;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace DevHub.Core.Services.Rpc.Handlers;

/// <summary>
/// 处理应用启动 RPC。
/// </summary>
public class LaunchHandler : IRpcHandler
{
    private readonly LaunchCoordinator _launchCoordinator;
    private readonly ILogger<LaunchHandler> _logger;

    /// <summary>
    /// 初始化处理器。
    /// </summary>
    public LaunchHandler(LaunchCoordinator launchCoordinator, ILogger<LaunchHandler> logger)
    {
        _launchCoordinator = launchCoordinator;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Method => "hub.apps.launch";

    /// <inheritdoc />
    public async Task<JsonRpcResponse> HandleAsync(JsonRpcRequest request, CancellationToken cancellationToken)
    {
        if (!RpcParamReader.TryReadParamsObject(request, out var paramsElement, out var invalidParams))
        {
            return invalidParams;
        }

        if (!RpcParamReader.TryGetRequiredString(paramsElement, "appId", out var appId))
        {
            return RpcErrorFactory.InvalidParams(request.Id);
        }

        if (!RpcParamReader.TryGetOptionalScope(
                paramsElement,
                "scope",
                "invalid_scope",
                out var scope,
                out var scopeErrorData))
        {
            return RpcErrorFactory.Create(request.Id, -32602, "invalid_params", scopeErrorData);
        }

        if (!TryParseWaitForRegisterMs(paramsElement, out var waitForRegisterMs))
        {
            return RpcErrorFactory.InvalidParams(request.Id);
        }

        var dedupeKey = TryGetOptionalString(paramsElement, "dedupeKey");
        var launchResult = await _launchCoordinator.LaunchAsync(appId, scope, dedupeKey, waitForRegisterMs, cancellationToken);

        if (!launchResult.Ok)
        {
            return RpcErrorFactory.Create(
                request.Id,
                launchResult.ErrorCode ?? -32603,
                launchResult.ErrorMessage ?? "internal_error",
                launchResult.ErrorData);
        }

        return new JsonRpcResponse
        {
            Id = request.Id,
            Result = new
            {
                ok = true,
                status = launchResult.Status,
                pid = launchResult.Pid,
                launchId = launchResult.LaunchId
            }
        };
    }

    private static bool TryParseWaitForRegisterMs(JsonElement element, out int waitForRegisterMs)
    {
        waitForRegisterMs = 0;
        if (!element.TryGetProperty("waitForRegisterMs", out var waitElement))
        {
            return true;
        }

        if (waitElement.ValueKind != JsonValueKind.Number || !waitElement.TryGetInt32(out waitForRegisterMs))
        {
            return false;
        }

        return waitForRegisterMs >= 0;
    }

    private static string? TryGetOptionalString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        if (property.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (property.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return property.GetString();
    }

}
