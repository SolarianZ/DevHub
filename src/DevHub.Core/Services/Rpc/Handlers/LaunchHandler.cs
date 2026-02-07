using DevHub.Core.Models.Rpc;
using DevHub.Core.Services.Invocation;
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
        if (!TryReadParamsObject(request, out var paramsElement, out var invalidParams))
        {
            return invalidParams;
        }

        if (!TryGetRequiredString(paramsElement, "appId", out var appId))
        {
            return InvalidParams(request.Id);
        }

        if (!TryParseScope(paramsElement, out var scope))
        {
            return InvalidParams(request.Id);
        }

        if (!TryParseWaitForRegisterMs(paramsElement, out var waitForRegisterMs))
        {
            return InvalidParams(request.Id);
        }

        var dedupeKey = TryGetOptionalString(paramsElement, "dedupeKey");
        var launchResult = await _launchCoordinator.LaunchAsync(appId, scope, dedupeKey, waitForRegisterMs, cancellationToken);

        if (!launchResult.Ok)
        {
            return new JsonRpcResponse
            {
                Id = request.Id,
                Error = new JsonRpcError
                {
                    Code = launchResult.ErrorCode ?? -32603,
                    Message = launchResult.ErrorMessage ?? "internal_error",
                    Data = launchResult.ErrorData
                }
            };
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

    private static bool TryReadParamsObject(JsonRpcRequest request, out JsonElement paramsElement, out JsonRpcResponse error)
    {
        if (request.Params is JsonElement element && element.ValueKind == JsonValueKind.Object)
        {
            paramsElement = element;
            error = null!;
            return true;
        }

        paramsElement = default;
        error = InvalidParams(request.Id);
        return false;
    }

    private static bool TryGetRequiredString(JsonElement element, string propertyName, out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var str = property.GetString();
        if (string.IsNullOrWhiteSpace(str))
        {
            return false;
        }

        value = str;
        return true;
    }

    private static bool TryParseScope(JsonElement element, out string? scope)
    {
        scope = null;
        if (!element.TryGetProperty("scope", out var scopeElement))
        {
            return true;
        }

        if (scopeElement.ValueKind == JsonValueKind.Null)
        {
            scope = null;
            return true;
        }

        if (scopeElement.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        scope = scopeElement.GetString();
        if (scope == string.Empty || scope == "global")
        {
            return false;
        }

        return true;
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

    private static JsonRpcResponse InvalidParams(object? id)
    {
        return new JsonRpcResponse
        {
            Id = id,
            Error = new JsonRpcError
            {
                Code = -32602,
                Message = "invalid_params"
            }
        };
    }
}
