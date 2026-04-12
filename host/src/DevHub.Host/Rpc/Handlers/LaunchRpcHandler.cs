using System.Text.Json;
using DevHub.Core.Models.Rpc;
using DevHub.Core.Services;
using DevHub.Core.Services.Invocation;
using DevHub.Core.Services.Rpc;
using DevHub.Host.Transport;

namespace DevHub.Host.Rpc.Handlers;

/// <summary>
/// Host 侧的应用启动 RPC 适配处理器。
/// </summary>
public sealed class LaunchRpcHandler : IRpcHandler
{
    private readonly LaunchCoordinator _launchCoordinator;
    private readonly ILogger<LaunchRpcHandler> _logger;

    /// <summary>
    /// 初始化处理器。
    /// </summary>
    public LaunchRpcHandler(LaunchCoordinator launchCoordinator, ILogger<LaunchRpcHandler> logger)
    {
        _launchCoordinator = launchCoordinator;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Method => HubRpcMethods.HubAppsLaunch;

    /// <inheritdoc />
    public async Task<JsonRpcResponse> HandleAsync(JsonRpcRequest request, CancellationToken cancellationToken)
    {
        if (!RpcRequestParameterReader.TryReadParamsObject(request, out var paramsElement))
        {
            return TransportResponseFactory.CreateErrorResponse(-32602, "invalid_params", request.Id);
        }

        if (!RpcRequestParameterReader.TryGetRequiredString(paramsElement, "appId", out var appId))
        {
            return TransportResponseFactory.CreateErrorResponse(-32602, "invalid_params", request.Id);
        }

        if (!RpcRequestParameterReader.TryGetOptionalScope(paramsElement, "scope", "invalid_scope", out var scope, out var scopeErrorData))
        {
            return TransportResponseFactory.CreateErrorResponse(-32602, "invalid_params", request.Id, scopeErrorData);
        }

        if (!TryParseWaitForRegisterMs(paramsElement, out var waitForRegisterMs))
        {
            return TransportResponseFactory.CreateErrorResponse(-32602, "invalid_params", request.Id);
        }

        if (!RpcRequestParameterReader.TryGetOptionalString(paramsElement, "dedupeKey", out var dedupeKey))
        {
            return TransportResponseFactory.CreateErrorResponse(-32602, "invalid_params", request.Id);
        }

        var launchResult = await _launchCoordinator.LaunchAsync(appId, scope, dedupeKey, waitForRegisterMs, cancellationToken);
        if (!launchResult.Ok)
        {
            return TransportResponseFactory.CreateErrorResponse(
                launchResult.ErrorCode ?? -32603,
                launchResult.ErrorMessage ?? "internal_error",
                request.Id,
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
}
