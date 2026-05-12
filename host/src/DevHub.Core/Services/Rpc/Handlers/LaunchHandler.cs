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
        if (!string.Equals(request.Method, HubRpcMethods.HubAppsLaunch, StringComparison.Ordinal))
        {
            return RpcErrorFactory.MethodNotFound(request.Id);
        }

        if (!RpcParamReader.TryReadParamsObject(request, out var paramsElement, out var invalidParams))
        {
            LogParameterRejected(request.Id, appId: null, scope: null, dedupeKeyPresent: false, waitForRegisterMs: null, "params_not_object");
            return invalidParams;
        }

        if (!RpcParamReader.TryGetRequiredAppId(paramsElement, "appId", out var appId))
        {
            LogParameterRejected(request.Id, appId: null, scope: null, dedupeKeyPresent: HasStringProperty(paramsElement, "dedupeKey"), waitForRegisterMs: null, "invalid_app_id");
            return RpcErrorFactory.InvalidParams(request.Id);
        }

        if (!RpcParamReader.TryGetRequiredScope(
                paramsElement,
                "scope",
                "invalid_scope",
                out var scope,
                out var scopeErrorData))
        {
            LogParameterRejected(request.Id, appId, scope: null, dedupeKeyPresent: HasStringProperty(paramsElement, "dedupeKey"), waitForRegisterMs: null, "invalid_scope");
            return RpcErrorFactory.Create(request.Id, -32602, "invalid_params", scopeErrorData);
        }

        if (!TryParseWaitForRegisterMs(paramsElement, out var waitForRegisterMs))
        {
            LogParameterRejected(request.Id, appId, scope, dedupeKeyPresent: HasStringProperty(paramsElement, "dedupeKey"), waitForRegisterMs: null, "invalid_wait_for_register_ms");
            return RpcErrorFactory.InvalidParams(request.Id);
        }

        if (!RpcParamReader.TryGetOptionalString(paramsElement, "dedupeKey", out var dedupeKey))
        {
            LogParameterRejected(request.Id, appId, scope, dedupeKeyPresent: false, waitForRegisterMs, "invalid_dedupe_key");
            return RpcErrorFactory.InvalidParams(request.Id);
        }

        var dedupeKeyPresent = !string.IsNullOrWhiteSpace(dedupeKey);
        _logger.LogInformation(
            "接受 launch 请求，RequestId: {RequestId}, AppId: {AppId}, Scope: {Scope}, DedupeKeyPresent: {DedupeKeyPresent}, WaitForRegisterMs: {WaitForRegisterMs}",
            request.Id,
            appId,
            scope,
            dedupeKeyPresent,
            waitForRegisterMs);

        var launchResult = await _launchCoordinator.LaunchAsync(appId, scope, dedupeKey, waitForRegisterMs, cancellationToken);

        if (!launchResult.Ok)
        {
            _logger.LogWarning(
                "映射 launch 失败，RequestId: {RequestId}, AppId: {AppId}, Scope: {Scope}, DedupeKeyPresent: {DedupeKeyPresent}, WaitForRegisterMs: {WaitForRegisterMs}, ErrorCode: {ErrorCode}, ErrorReason: {ErrorReason}",
                request.Id,
                appId,
                scope,
                dedupeKeyPresent,
                waitForRegisterMs,
                launchResult.ErrorCode ?? -32603,
                ExtractErrorReason(launchResult.ErrorData));

            return RpcErrorFactory.Create(
                request.Id,
                launchResult.ErrorCode ?? -32603,
                launchResult.ErrorMessage ?? "internal_error",
                launchResult.ErrorData);
        }

        _logger.LogInformation(
            "映射 launch 成功，RequestId: {RequestId}, AppId: {AppId}, Scope: {Scope}, DedupeKeyPresent: {DedupeKeyPresent}, WaitForRegisterMs: {WaitForRegisterMs}, Status: {Status}, Pid: {Pid}",
            request.Id,
            appId,
            scope,
            dedupeKeyPresent,
            waitForRegisterMs,
            launchResult.Status,
            launchResult.Pid);

        var result = new Dictionary<string, object?>
        {
            ["ok"] = true,
            ["status"] = launchResult.Status
        };

        if (launchResult.Pid.HasValue)
        {
            result["pid"] = launchResult.Pid.Value;
        }

        if (!string.IsNullOrWhiteSpace(launchResult.LaunchId))
        {
            result["launchId"] = launchResult.LaunchId;
        }

        if (!string.IsNullOrWhiteSpace(launchResult.DedupeKey))
        {
            result["dedupeKey"] = launchResult.DedupeKey;
        }

        if (!string.IsNullOrWhiteSpace(launchResult.InstanceId))
        {
            result["instanceId"] = launchResult.InstanceId;
        }

        return new JsonRpcResponse
        {
            Id = request.Id,
            Result = result
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

    private void LogParameterRejected(
        object? requestId,
        string? appId,
        string? scope,
        bool dedupeKeyPresent,
        int? waitForRegisterMs,
        string reason)
    {
        _logger.LogWarning(
            "拒绝 launch 参数，RequestId: {RequestId}, AppId: {AppId}, Scope: {Scope}, DedupeKeyPresent: {DedupeKeyPresent}, WaitForRegisterMs: {WaitForRegisterMs}, ErrorCode: {ErrorCode}, ErrorReason: {ErrorReason}",
            requestId,
            appId,
            scope,
            dedupeKeyPresent,
            waitForRegisterMs,
            -32602,
            reason);
    }

    private static bool HasStringProperty(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(property.GetString());
    }

    private static string? ExtractErrorReason(object? errorData)
    {
        if (errorData is null)
        {
            return null;
        }

        var element = JsonSerializer.SerializeToElement(errorData);
        return element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty("reason", out var reasonElement)
            && reasonElement.ValueKind == JsonValueKind.String
                ? reasonElement.GetString()
                : null;
    }
}
