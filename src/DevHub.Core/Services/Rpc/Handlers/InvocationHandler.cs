using System.Text.Json;
using DevHub.Core.Models;
using DevHub.Core.Models.Rpc;
using DevHub.Core.Services.Invocation;
using Microsoft.Extensions.Logging;
using InvocationModel = DevHub.Core.Models.Invocation;

namespace DevHub.Core.Services.Rpc.Handlers;

/// <summary>
/// 处理 Invocation 相关 RPC。
/// </summary>
public class InvocationHandler : IRpcHandler
{
    private const int DefaultNotifyTtlMs = 60000;
    private const int LeaseSeconds = 30;

    private readonly AppRegistry _appRegistry;
    private readonly DefinitionLoader _definitionLoader;
    private readonly InvocationRoutingService _routingService;
    private readonly InvocationStore _store;
    private readonly ILogger<InvocationHandler> _logger;

    /// <summary>
    /// 初始化处理器。
    /// </summary>
    public InvocationHandler(
        AppRegistry appRegistry,
        DefinitionLoader definitionLoader,
        InvocationRoutingService routingService,
        InvocationStore store,
        ILogger<InvocationHandler> logger)
    {
        _appRegistry = appRegistry;
        _definitionLoader = definitionLoader;
        _routingService = routingService;
        _store = store;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Method => "hub.invoke";

    /// <inheritdoc />
    public Task<JsonRpcResponse> HandleAsync(JsonRpcRequest request, CancellationToken cancellationToken)
    {
        return request.Method switch
        {
            "hub.invoke.notify" => NotifyAsync(request),
            "hub.invoke.poll" => PollAsync(request, cancellationToken),
            "hub.invoke.respond" => RespondAsync(request),
            "hub.invoke.request" => Task.FromResult(NotSupported(request.Id, "request_deferred")),
            _ => Task.FromResult(MethodNotFound(request.Id))
        };
    }

    private Task<JsonRpcResponse> NotifyAsync(JsonRpcRequest request)
    {
        if (!TryReadParamsObject(request, out var paramsElement, out var paramsError))
        {
            return Task.FromResult(paramsError);
        }

        if (!TryGetRequiredString(paramsElement, "appId", out var appId) ||
            !TryGetRequiredString(paramsElement, "method", out var method))
        {
            return Task.FromResult(InvalidParams(request.Id));
        }

        if (!TryParseTarget(paramsElement, out var target, out var targetError))
        {
            return Task.FromResult(CreateError(request.Id, -32602, "invalid_params", targetError));
        }

        if (!TryParseNotifyOptions(paramsElement, target, out var options, out var optionErrorResponse))
        {
            optionErrorResponse.Id = request.Id;
            return Task.FromResult(optionErrorResponse);
        }

        _definitionLoader.Load();
        var definition = _definitionLoader.GetDefinition(appId);
        if (definition is not null && definition.Capabilities?.Rpc == false)
        {
            return Task.FromResult(CreateError(request.Id, -32002, "forbidden", new { reason = "rpc_disabled" }));
        }

        var candidates = _routingService.GetOnlineCandidates(appId, target);
        if (candidates.Count == 0)
        {
            if (!options.QueueIfOffline)
            {
                return Task.FromResult(CreateError(request.Id, -32010, "instance_not_found", new { reason = "offline_no_queue" }));
            }

            if (definition is null)
            {
                return Task.FromResult(CreateError(request.Id, -32010, "instance_not_found", new { reason = "offline_no_queue" }));
            }

            if (options.AutoLaunch)
            {
                return Task.FromResult(CreateError(request.Id, -32099, "not_supported", new { reason = "auto_launch_deferred" }));
            }
        }

        var invocation = new InvocationModel
        {
            InvocationId = $"invk-{Guid.NewGuid():N}",
            AppId = appId,
            Target = target,
            Method = method,
            Args = paramsElement.TryGetProperty("args", out var argsElement)
                ? JsonSerializer.Deserialize<object>(argsElement.GetRawText())
                : new Dictionary<string, object?>(),
            Kind = InvocationKind.Notify,
            CreatedAtUtc = DateTime.UtcNow,
            Options = options,
            Delivery = new InvocationDelivery
            {
                LeaseSeconds = LeaseSeconds,
                Attempt = 1
            },
            Caller = new InvocationCaller
            {
                ClientId = "unknown",
                ClientSessionId = Guid.Empty.ToString("D")
            },
            State = InvocationState.Created
        };

        _store.CreateInvocation(invocation, hasOnlineCandidates: candidates.Count > 0);

        return Task.FromResult(new JsonRpcResponse
        {
            Id = request.Id,
            Result = new
            {
                ok = true,
                invocationId = invocation.InvocationId
            }
        });
    }

    private async Task<JsonRpcResponse> PollAsync(JsonRpcRequest request, CancellationToken cancellationToken)
    {
        if (!TryReadParamsObject(request, out var paramsElement, out var paramsError))
        {
            return paramsError;
        }

        if (!TryGetRequiredString(paramsElement, "instanceId", out var instanceId))
        {
            return InvalidParams(request.Id);
        }

        var maxCount = 10;
        if (paramsElement.TryGetProperty("maxCount", out var maxCountElement))
        {
            if (maxCountElement.ValueKind != JsonValueKind.Number || !maxCountElement.TryGetInt32(out maxCount) || maxCount is < 1 or > 100)
            {
                return InvalidParams(request.Id);
            }
        }

        var waitMs = 25000;
        if (paramsElement.TryGetProperty("waitMs", out var waitMsElement))
        {
            if (waitMsElement.ValueKind != JsonValueKind.Number || !waitMsElement.TryGetInt32(out waitMs) || waitMs < 0)
            {
                return InvalidParams(request.Id);
            }
        }

        var instance = _appRegistry.GetInstance(instanceId);
        if (instance is null)
        {
            return CreateError(request.Id, -32010, "instance_not_found", new { reason = "unknown_instance", instanceId });
        }

        if (instance.Invoke?.Poll != true)
        {
            return CreateError(request.Id, -32002, "forbidden", new { reason = "poll_not_enabled", instanceId });
        }

        _appRegistry.Heartbeat(instanceId, out _);

        var items = await _store.PollAsync(instance, maxCount, waitMs, cancellationToken);
        return new JsonRpcResponse
        {
            Id = request.Id,
            Result = new
            {
                ok = true,
                serverTimeUtc = DateTime.UtcNow.ToString("O"),
                items = items.Select(i => new
                {
                    invocationId = i.InvocationId,
                    appId = i.AppId,
                    target = i.Target,
                    method = i.Method,
                    args = i.Args,
                    kind = i.Kind.ToString().ToLowerInvariant(),
                    createdAtUtc = i.CreatedAtUtc.ToString("O"),
                    caller = i.Caller,
                    delivery = i.Delivery,
                    options = new
                    {
                        ttlMs = i.Options.TtlMs
                    }
                }).ToArray()
            }
        };
    }

    private Task<JsonRpcResponse> RespondAsync(JsonRpcRequest request)
    {
        if (!TryReadParamsObject(request, out var paramsElement, out var paramsError))
        {
            return Task.FromResult(paramsError);
        }

        if (!TryGetRequiredString(paramsElement, "instanceId", out var instanceId) ||
            !TryGetRequiredString(paramsElement, "invocationId", out var invocationId))
        {
            return Task.FromResult(InvalidParams(request.Id));
        }

        var hasValue = paramsElement.TryGetProperty("value", out var valueElement);
        var hasError = paramsElement.TryGetProperty("error", out var errorElement);
        if (hasValue == hasError)
        {
            return Task.FromResult(InvalidParams(request.Id));
        }

        var instance = _appRegistry.GetInstance(instanceId);
        if (instance is null)
        {
            return Task.FromResult(CreateError(request.Id, -32010, "instance_not_found", new { reason = "unknown_instance", instanceId }));
        }

        if (instance.Invoke?.Respond != true)
        {
            return Task.FromResult(CreateError(request.Id, -32002, "forbidden", new { reason = "respond_not_enabled", instanceId }));
        }

        _appRegistry.Heartbeat(instanceId, out _);

        var value = hasValue ? JsonSerializer.Deserialize<object>(valueElement.GetRawText()) : null;
        var error = hasError ? JsonSerializer.Deserialize<object>(errorElement.GetRawText()) : null;

        var status = _store.Respond(instanceId, invocationId, value, error);
        return status switch
        {
            InvocationRespondStatus.Success => Task.FromResult(new JsonRpcResponse
            {
                Id = request.Id,
                Result = new { ok = true }
            }),
            InvocationRespondStatus.NotFound => Task.FromResult(CreateError(request.Id, -32011, "invocation_expired", new { invocationId })),
            InvocationRespondStatus.Expired => Task.FromResult(CreateError(request.Id, -32011, "invocation_expired", new { invocationId })),
            InvocationRespondStatus.DeliveryConflict => Task.FromResult(CreateError(request.Id, -32030, "delivery_conflict", new { invocationId })),
            _ => Task.FromResult(CreateError(request.Id, -32603, "internal_error"))
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

    private static bool TryParseTarget(JsonElement paramsElement, out InvocationTarget target, out object? errorData)
    {
        target = new InvocationTarget { Scope = null, InstanceId = null };
        errorData = null;

        if (!paramsElement.TryGetProperty("target", out var targetElement))
        {
            return true;
        }

        if (targetElement.ValueKind != JsonValueKind.Object)
        {
            errorData = new { reason = "invalid_target" };
            return false;
        }

        string? scope = null;
        if (targetElement.TryGetProperty("scope", out var scopeElement))
        {
            if (scopeElement.ValueKind == JsonValueKind.String)
            {
                scope = scopeElement.GetString();
            }
            else if (scopeElement.ValueKind != JsonValueKind.Null)
            {
                errorData = new { reason = "invalid_target_scope" };
                return false;
            }

            if (scope == string.Empty || scope == "global")
            {
                errorData = new { reason = "invalid_target_scope" };
                return false;
            }
        }

        string? instanceId = null;
        if (targetElement.TryGetProperty("instanceId", out var instanceIdElement))
        {
            if (instanceIdElement.ValueKind == JsonValueKind.String)
            {
                instanceId = instanceIdElement.GetString();
                if (string.IsNullOrWhiteSpace(instanceId))
                {
                    errorData = new { reason = "invalid_target_instance" };
                    return false;
                }
            }
            else if (instanceIdElement.ValueKind != JsonValueKind.Null)
            {
                errorData = new { reason = "invalid_target_instance" };
                return false;
            }
        }

        target = new InvocationTarget { Scope = scope, InstanceId = instanceId };
        return true;
    }

    private static bool TryParseNotifyOptions(
        JsonElement paramsElement,
        InvocationTarget target,
        out InvocationOptions options,
        out JsonRpcResponse errorResponse)
    {
        options = new InvocationOptions
        {
            TtlMs = DefaultNotifyTtlMs,
            QueueIfOffline = true,
            AutoLaunch = string.IsNullOrWhiteSpace(target.InstanceId)
        };

        if (paramsElement.TryGetProperty("options", out var optionsElement))
        {
            if (optionsElement.ValueKind != JsonValueKind.Object)
            {
                errorResponse = InvalidParams(null);
                return false;
            }

            if (optionsElement.TryGetProperty("ttlMs", out var ttlElement))
            {
                if (ttlElement.ValueKind != JsonValueKind.Number || !ttlElement.TryGetInt32(out var ttlMs) || ttlMs < 1000)
                {
                    errorResponse = InvalidParams(null);
                    return false;
                }

                options.TtlMs = ttlMs;
            }

            if (optionsElement.TryGetProperty("queueIfOffline", out var queueElement))
            {
                if (queueElement.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
                {
                    errorResponse = InvalidParams(null);
                    return false;
                }

                options.QueueIfOffline = queueElement.GetBoolean();
            }

            if (optionsElement.TryGetProperty("autoLaunch", out var autoLaunchElement))
            {
                if (autoLaunchElement.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
                {
                    errorResponse = InvalidParams(null);
                    return false;
                }

                options.AutoLaunch = autoLaunchElement.GetBoolean();
            }
        }

        if (!string.IsNullOrWhiteSpace(target.InstanceId) && options.AutoLaunch)
        {
            errorResponse = InvalidParams(null);
            return false;
        }

        if (options.AutoLaunch && !options.QueueIfOffline)
        {
            errorResponse = InvalidParams(null);
            return false;
        }

        errorResponse = null!;
        return true;
    }

    private static JsonRpcResponse MethodNotFound(object? id)
    {
        return new JsonRpcResponse
        {
            Id = id,
            Error = new JsonRpcError
            {
                Code = -32601,
                Message = "method_not_found"
            }
        };
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

    private static JsonRpcResponse NotSupported(object? id, string reason)
    {
        return CreateError(id, -32099, "not_supported", new { reason });
    }

    private static JsonRpcResponse CreateError(object? id, int code, string message, object? data = null)
    {
        return new JsonRpcResponse
        {
            Id = id,
            Error = new JsonRpcError
            {
                Code = code,
                Message = message,
                Data = data
            }
        };
    }
}
