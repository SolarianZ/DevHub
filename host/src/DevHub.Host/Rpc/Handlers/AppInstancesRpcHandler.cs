using System.Text.Json;
using System.Text.RegularExpressions;
using DevHub.Core.Models;
using DevHub.Core.Models.Rpc;
using DevHub.Core.Services;
using DevHub.Core.Services.Abstractions;
using DevHub.Core.Services.Events;
using DevHub.Core.Services.Rpc;
using DevHub.Host.Transport;

namespace DevHub.Host.Rpc.Handlers;

/// <summary>
/// Host 侧的应用实例 RPC 适配处理器。
/// </summary>
public sealed class AppInstancesRpcHandler : IRpcHandler
{
    private static readonly Regex InstanceIdPattern = new("^[a-zA-Z0-9._:-]+$", RegexOptions.Compiled);

    private readonly AppRegistry _appRegistry;
    private readonly IHubEventPublisher? _eventPublisher;
    private readonly IClock _clock;
    private readonly ILogger<AppInstancesRpcHandler> _logger;

    /// <summary>
    /// 初始化处理器。
    /// </summary>
    public AppInstancesRpcHandler(
        AppRegistry appRegistry,
        IClock clock,
        ILogger<AppInstancesRpcHandler> logger,
        IHubEventPublisher? eventPublisher = null)
    {
        _appRegistry = appRegistry;
        _clock = clock;
        _eventPublisher = eventPublisher;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Method => "hub.apps";

    /// <inheritdoc />
    public Task<JsonRpcResponse> HandleAsync(JsonRpcRequest request, CancellationToken cancellationToken)
    {
        return request.Method switch
        {
            HubRpcMethods.HubAppsRegisterInstance => Task.FromResult(RegisterInstance(request)),
            HubRpcMethods.HubAppsHeartbeat => Task.FromResult(Heartbeat(request)),
            HubRpcMethods.HubAppsUnregisterInstance => Task.FromResult(UnregisterInstance(request)),
            HubRpcMethods.HubAppsListInstances => Task.FromResult(ListInstances(request)),
            _ => Task.FromResult(TransportResponseFactory.CreateErrorResponse(-32601, "method_not_found", request.Id))
        };
    }

    private JsonRpcResponse RegisterInstance(JsonRpcRequest request)
    {
        if (!RpcRequestParameterReader.TryReadParamsObject(request, out var paramsElement))
        {
            return TransportResponseFactory.CreateErrorResponse(-32602, "invalid_params", request.Id);
        }

        if (!RpcRequestParameterReader.TryGetRequiredString(paramsElement, "password", out var password))
        {
            return TransportResponseFactory.CreateErrorResponse(-32602, "invalid_params", request.Id);
        }

        if (!paramsElement.TryGetProperty("instance", out var instanceElement) || instanceElement.ValueKind != JsonValueKind.Object)
        {
            return TransportResponseFactory.CreateErrorResponse(-32602, "invalid_params", request.Id);
        }

        if (!TryParseInstanceRegistration(instanceElement, out var instance, out var errorData))
        {
            return TransportResponseFactory.CreateErrorResponse(-32602, "invalid_params", request.Id, errorData);
        }

        if (!_appRegistry.TryRegisterInstance(instance, password, out var registeredInstance, out var passwordMismatch))
        {
            if (passwordMismatch)
            {
                return TransportResponseFactory.CreateErrorResponse(
                    -32002,
                    "forbidden",
                    request.Id,
                    new
                    {
                        reason = "instance_password_mismatch",
                        instanceId = instance.InstanceId
                    });
            }

            return TransportResponseFactory.CreateErrorResponse(-32603, "internal_error", request.Id);
        }

        PublishInstanceEvent(HubEventTypes.AppInstanceRegistered, registeredInstance.AppId, registeredInstance.InstanceId, registeredInstance.Scope);

        return new JsonRpcResponse
        {
            Id = request.Id,
            Result = new
            {
                ok = true,
                instance = registeredInstance
            }
        };
    }

    private JsonRpcResponse Heartbeat(JsonRpcRequest request)
    {
        if (!RpcRequestParameterReader.TryReadParamsObject(request, out var paramsElement))
        {
            return TransportResponseFactory.CreateErrorResponse(-32602, "invalid_params", request.Id);
        }

        if (!RpcRequestParameterReader.TryGetRequiredString(paramsElement, "instanceId", out var instanceId))
        {
            return TransportResponseFactory.CreateErrorResponse(-32602, "invalid_params", request.Id);
        }

        if (!_appRegistry.Heartbeat(instanceId, out var lastSeenUtc))
        {
            return TransportResponseFactory.CreateErrorResponse(
                -32010,
                "instance_not_found",
                request.Id,
                new
                {
                    reason = "unknown_instance",
                    instanceId
                });
        }

        return new JsonRpcResponse
        {
            Id = request.Id,
            Result = new
            {
                ok = true,
                lastSeenUtc = lastSeenUtc.ToString("O")
            }
        };
    }

    private JsonRpcResponse UnregisterInstance(JsonRpcRequest request)
    {
        if (!RpcRequestParameterReader.TryReadParamsObject(request, out var paramsElement))
        {
            return TransportResponseFactory.CreateErrorResponse(-32602, "invalid_params", request.Id);
        }

        if (!RpcRequestParameterReader.TryGetRequiredString(paramsElement, "instanceId", out var instanceId)
            || !RpcRequestParameterReader.TryGetRequiredString(paramsElement, "password", out var password))
        {
            return TransportResponseFactory.CreateErrorResponse(-32602, "invalid_params", request.Id);
        }

        if (!_appRegistry.TryUnregisterInstance(instanceId, password, out var removedInstance, out var passwordMismatch))
        {
            if (passwordMismatch)
            {
                return TransportResponseFactory.CreateErrorResponse(
                    -32002,
                    "forbidden",
                    request.Id,
                    new
                    {
                        reason = "instance_password_mismatch",
                        instanceId
                    });
            }

            return TransportResponseFactory.CreateErrorResponse(-32603, "internal_error", request.Id);
        }

        if (removedInstance is not null)
        {
            PublishInstanceEvent(HubEventTypes.AppInstanceUnregistered, removedInstance.AppId, removedInstance.InstanceId, removedInstance.Scope);
        }

        return new JsonRpcResponse
        {
            Id = request.Id,
            Result = new
            {
                ok = true
            }
        };
    }

    private JsonRpcResponse ListInstances(JsonRpcRequest request)
    {
        string? appId = null;
        string? scope = null;
        var includeAllScopes = false;
        var includeOffline = false;

        if (request.Params is JsonElement paramsElement)
        {
            if (paramsElement.ValueKind != JsonValueKind.Object)
            {
                return TransportResponseFactory.CreateErrorResponse(-32602, "invalid_params", request.Id);
            }

            if (paramsElement.TryGetProperty("appId", out var appIdProperty))
            {
                if (appIdProperty.ValueKind != JsonValueKind.String)
                {
                    return TransportResponseFactory.CreateErrorResponse(-32602, "invalid_params", request.Id);
                }

                appId = appIdProperty.GetString();
            }

            if (paramsElement.TryGetProperty("includeAllScopes", out var includeAllScopesProperty))
            {
                if (includeAllScopesProperty.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
                {
                    return TransportResponseFactory.CreateErrorResponse(-32602, "invalid_params", request.Id);
                }

                includeAllScopes = includeAllScopesProperty.GetBoolean();
            }

            if (!RpcRequestParameterReader.TryGetOptionalScope(paramsElement, "scope", "invalid_scope", out scope, out var scopeErrorData))
            {
                if (!includeAllScopes)
                {
                    return TransportResponseFactory.CreateErrorResponse(-32602, "invalid_params", request.Id, scopeErrorData);
                }
            }

            if (includeAllScopes)
            {
                scope = null;
            }

            if (paramsElement.TryGetProperty("includeOffline", out var includeOfflineProperty))
            {
                if (includeOfflineProperty.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
                {
                    return TransportResponseFactory.CreateErrorResponse(-32602, "invalid_params", request.Id);
                }

                includeOffline = includeOfflineProperty.GetBoolean();
            }
        }
        else if (request.Params is not null)
        {
            return TransportResponseFactory.CreateErrorResponse(-32602, "invalid_params", request.Id);
        }

        var instances = _appRegistry.ListInstances(appId, scope, includeAllScopes, includeOffline).ToList();
        return new JsonRpcResponse
        {
            Id = request.Id,
            Result = new
            {
                ok = true,
                instances
            }
        };
    }

    private bool TryParseInstanceRegistration(JsonElement instanceElement, out AppInstance instance, out object? errorData)
    {
        instance = null!;
        errorData = null;

        if (!instanceElement.TryGetProperty("instanceId", out var instanceIdProperty) || instanceIdProperty.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var instanceId = instanceIdProperty.GetString();
        if (string.IsNullOrWhiteSpace(instanceId) || instanceId.Length > 256 || !InstanceIdPattern.IsMatch(instanceId))
        {
            return false;
        }

        if (!instanceElement.TryGetProperty("appId", out var appIdProperty) || appIdProperty.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var appId = appIdProperty.GetString();
        if (string.IsNullOrWhiteSpace(appId))
        {
            return false;
        }

        if (!instanceElement.TryGetProperty("pid", out var pidProperty) || pidProperty.ValueKind != JsonValueKind.Number || !pidProperty.TryGetInt32(out var pid) || pid < 1)
        {
            return false;
        }

        if (!RpcRequestParameterReader.TryGetOptionalScope(instanceElement, "scope", "invalid_scope", out var scope, out errorData))
        {
            return false;
        }

        if (!instanceElement.TryGetProperty("invoke", out var invokeProperty) || invokeProperty.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!invokeProperty.TryGetProperty("poll", out var pollProperty)
            || !invokeProperty.TryGetProperty("respond", out var respondProperty)
            || pollProperty.ValueKind is not JsonValueKind.True and not JsonValueKind.False
            || respondProperty.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
        {
            return false;
        }

        Dictionary<string, object?>? meta = null;
        if (instanceElement.TryGetProperty("meta", out var metaProperty))
        {
            if (metaProperty.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            meta = JsonSerializer.Deserialize<Dictionary<string, object?>>(metaProperty.GetRawText());
        }

        instance = new AppInstance
        {
            InstanceId = instanceId,
            AppId = appId,
            Scope = scope,
            Pid = pid,
            Invoke = new InvokeCapability
            {
                Poll = pollProperty.GetBoolean(),
                Respond = respondProperty.GetBoolean()
            },
            Meta = meta
        };

        return true;
    }

    private void PublishInstanceEvent(string eventType, string appId, string instanceId, string? scope)
    {
        _eventPublisher?.Publish(new HubEventMessage
        {
            Type = eventType,
            TimeUtc = _clock.UtcNow,
            Payload = new
            {
                appId,
                instanceId,
                scope
            }
        });
    }
}
