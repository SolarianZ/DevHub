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
    private const int DefaultRequestTtlMs = 300000;
    private const int DefaultRequestWaitTimeoutMs = 120000;
    private const int LeaseSeconds = 30;

    private readonly AppRegistry _appRegistry;
    private readonly DefinitionLoader _definitionLoader;
    private readonly InvocationRoutingService _routingService;
    private readonly InvocationStore _store;
    private readonly InvocationRequestWaiter _requestWaiter;
    private readonly LaunchCoordinator _launchCoordinator;
    private readonly ILogger<InvocationHandler> _logger;

    /// <summary>
    /// 初始化处理器。
    /// </summary>
    public InvocationHandler(
        AppRegistry appRegistry,
        DefinitionLoader definitionLoader,
        InvocationRoutingService routingService,
        InvocationStore store,
        InvocationRequestWaiter requestWaiter,
        LaunchCoordinator launchCoordinator,
        ILogger<InvocationHandler> logger)
    {
        _appRegistry = appRegistry;
        _definitionLoader = definitionLoader;
        _routingService = routingService;
        _store = store;
        _requestWaiter = requestWaiter;
        _launchCoordinator = launchCoordinator;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Method => "hub.invoke";

    /// <inheritdoc />
    public Task<JsonRpcResponse> HandleAsync(JsonRpcRequest request, CancellationToken cancellationToken)
    {
        return request.Method switch
        {
            "hub.invoke.notify" => NotifyAsync(request, cancellationToken),
            "hub.invoke.request" => RequestAsync(request, cancellationToken),
            "hub.invoke.poll" => PollAsync(request, cancellationToken),
            "hub.invoke.respond" => RespondAsync(request),
            _ => Task.FromResult(MethodNotFound(request.Id))
        };
    }

    private async Task<JsonRpcResponse> NotifyAsync(JsonRpcRequest request, CancellationToken cancellationToken)
    {
        if (!TryReadParamsObject(request, out var paramsElement, out var paramsError))
        {
            return paramsError;
        }

        if (!TryGetRequiredString(paramsElement, "appId", out var appId) ||
            !TryGetRequiredString(paramsElement, "method", out var method))
        {
            return InvalidParams(request.Id);
        }

        if (!TryParseTarget(paramsElement, out var target, out var targetError))
        {
            return CreateError(request.Id, -32602, "invalid_params", targetError);
        }

        if (!TryParseNotifyOptions(paramsElement, target, out var options, out var optionErrorResponse))
        {
            optionErrorResponse.Id = request.Id;
            return optionErrorResponse;
        }

        _definitionLoader.Load();
        var definition = _definitionLoader.GetDefinition(appId);
        if (definition is not null && definition.Capabilities?.Rpc == false)
        {
            return CreateError(request.Id, -32002, "forbidden", new { reason = "rpc_disabled" });
        }

        var candidates = _routingService.GetOnlineCandidates(appId, target);
        if (candidates.Count == 0)
        {
            if (!options.QueueIfOffline)
            {
                return CreateError(
                    request.Id,
                    -32010,
                    "instance_not_found",
                    new { reason = ResolveNoCandidateReason(target) });
            }

            if (definition is null)
            {
                return CreateError(
                    request.Id,
                    -32010,
                    "instance_not_found",
                    new { reason = ResolveNoCandidateReason(target) });
            }

            if (options.AutoLaunch)
            {
                var launchResult = await _launchCoordinator.LaunchAsync(
                    appId,
                    target.Scope,
                    dedupeKey: null,
                    waitForRegisterMs: 0,
                    cancellationToken);

                if (!launchResult.Ok)
                {
                    return CreateError(
                        request.Id,
                        launchResult.ErrorCode ?? -32603,
                        launchResult.ErrorMessage ?? "internal_error",
                        launchResult.ErrorData);
                }
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
                ClientId = string.IsNullOrWhiteSpace(request.ClientId) ? "unknown" : request.ClientId,
                ClientSessionId = string.IsNullOrWhiteSpace(request.ClientSessionId)
                    ? Guid.Empty.ToString("D")
                    : request.ClientSessionId
            },
            State = InvocationState.Created
        };

        _store.CreateInvocation(invocation, hasOnlineCandidates: candidates.Count > 0);

        return new JsonRpcResponse
        {
            Id = request.Id,
            Result = new
            {
                ok = true,
                invocationId = invocation.InvocationId
            }
        };
    }

    private async Task<JsonRpcResponse> RequestAsync(JsonRpcRequest request, CancellationToken cancellationToken)
    {
        if (!TryReadParamsObject(request, out var paramsElement, out var paramsError))
        {
            return paramsError;
        }

        if (!TryGetRequiredString(paramsElement, "appId", out var appId) ||
            !TryGetRequiredString(paramsElement, "method", out var method))
        {
            return InvalidParams(request.Id);
        }

        if (!TryParseTarget(paramsElement, out var target, out var targetError))
        {
            return CreateError(request.Id, -32602, "invalid_params", targetError);
        }

        if (!TryParseRequestOptions(paramsElement, target, out var options, out var optionErrorResponse))
        {
            optionErrorResponse.Id = request.Id;
            return optionErrorResponse;
        }

        _definitionLoader.Load();
        var definition = _definitionLoader.GetDefinition(appId);
        if (definition is not null && definition.Capabilities?.Rpc == false)
        {
            return CreateError(request.Id, -32002, "forbidden", new { reason = "rpc_disabled" });
        }

        var candidates = _routingService.GetOnlineCandidates(appId, target);
        if (candidates.Count == 0)
        {
            if (!options.QueueIfOffline)
            {
                return CreateError(
                    request.Id,
                    -32010,
                    "instance_not_found",
                    new { reason = ResolveNoCandidateReason(target) });
            }

            if (definition is null)
            {
                return CreateError(
                    request.Id,
                    -32010,
                    "instance_not_found",
                    new { reason = ResolveNoCandidateReason(target) });
            }

            if (options.AutoLaunch)
            {
                var launchResult = await _launchCoordinator.LaunchAsync(
                    appId,
                    target.Scope,
                    dedupeKey: null,
                    waitForRegisterMs: 0,
                    cancellationToken);

                if (!launchResult.Ok)
                {
                    return CreateError(
                        request.Id,
                        launchResult.ErrorCode ?? -32603,
                        launchResult.ErrorMessage ?? "internal_error",
                        launchResult.ErrorData);
                }
            }
        }

        var createdAt = DateTime.UtcNow;
        var invocation = new InvocationModel
        {
            InvocationId = $"invk-{Guid.NewGuid():N}",
            AppId = appId,
            Target = target,
            Method = method,
            Args = paramsElement.TryGetProperty("args", out var argsElement)
                ? JsonSerializer.Deserialize<object>(argsElement.GetRawText())
                : new Dictionary<string, object?>(),
            Kind = InvocationKind.Request,
            CreatedAtUtc = createdAt,
            Options = options,
            Delivery = new InvocationDelivery
            {
                LeaseSeconds = LeaseSeconds,
                Attempt = 1
            },
            Caller = new InvocationCaller
            {
                ClientId = string.IsNullOrWhiteSpace(request.ClientId) ? "unknown" : request.ClientId,
                ClientSessionId = string.IsNullOrWhiteSpace(request.ClientSessionId)
                    ? Guid.Empty.ToString("D")
                    : request.ClientSessionId
            },
            State = InvocationState.Created
        };

        _store.CreateInvocation(invocation, hasOnlineCandidates: candidates.Count > 0);
        var waiterTask = _requestWaiter.Register(invocation.InvocationId);

        var ttlRemaining = Math.Max(1, invocation.Options.TtlMs - (int)(DateTime.UtcNow - createdAt).TotalMilliseconds);
        var waitRemaining = Math.Max(1, invocation.Options.WaitTimeoutMs ?? DefaultRequestWaitTimeoutMs);
        var timeoutWindowMs = Math.Min(ttlRemaining, waitRemaining);

        var completionTask = await Task.WhenAny(waiterTask, Task.Delay(timeoutWindowMs));

        if (completionTask != waiterTask)
        {
            var elapsedMs = (int)Math.Max(0, (DateTime.UtcNow - createdAt).TotalMilliseconds);
            var ttlReached = elapsedMs >= invocation.Options.TtlMs;

            if (ttlReached)
            {
                var markedExpired = _store.MarkExpired(invocation.InvocationId, DateTime.UtcNow);
                if (markedExpired)
                {
                    _requestWaiter.CompleteExpired(invocation.InvocationId, elapsedMs);
                }
                else
                {
                    var racedCompletion = await waiterTask;
                    return BuildRequestCompletionResponse(request.Id, invocation.InvocationId, racedCompletion);
                }

                return CreateError(request.Id, -32011, "invocation_expired", new
                {
                    invocationId = invocation.InvocationId,
                    elapsedMs
                });
            }

            var markedTimeout = _store.MarkTimeout(invocation.InvocationId, DateTime.UtcNow);
            if (markedTimeout)
            {
                _requestWaiter.CompleteTimeout(invocation.InvocationId, elapsedMs);
            }
            else
            {
                var racedCompletion = await waiterTask;
                return BuildRequestCompletionResponse(request.Id, invocation.InvocationId, racedCompletion);
            }

            return CreateError(request.Id, -32012, "invocation_timeout", new
            {
                invocationId = invocation.InvocationId,
                elapsedMs
            });
        }

        var completion = await waiterTask;
        return BuildRequestCompletionResponse(request.Id, invocation.InvocationId, completion);
    }

    private static JsonRpcResponse BuildRequestCompletionResponse(object? requestId, string invocationId, InvocationRequestCompletion completion)
    {
        return completion.Kind switch
        {
            InvocationRequestCompletionKind.Success => new JsonRpcResponse
            {
                Id = requestId,
                Result = new
                {
                    ok = true,
                    invocationId,
                    value = completion.Value
                }
            },
            InvocationRequestCompletionKind.Failed => CreateError(requestId, -32050, "invocation_failed", new
            {
                invocationId,
                calleeError = completion.CalleeError
            }),
            InvocationRequestCompletionKind.Timeout => CreateError(requestId, -32012, "invocation_timeout", new
            {
                invocationId,
                elapsedMs = completion.ElapsedMs ?? 0
            }),
            InvocationRequestCompletionKind.Expired => CreateError(requestId, -32011, "invocation_expired", new
            {
                invocationId,
                elapsedMs = completion.ElapsedMs ?? 0
            }),
            _ => CreateError(requestId, -32603, "internal_error")
        };
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

        if (_store.TryGet(invocationId, out var invocation) && invocation is not null && invocation.Kind == InvocationKind.Request)
        {
            switch (status)
            {
                case InvocationRespondStatus.Success:
                    if (error is null)
                    {
                        _requestWaiter.CompleteSuccess(invocationId, value);
                    }
                    else
                    {
                        _requestWaiter.CompleteFailure(invocationId, error);
                    }

                    break;
                case InvocationRespondStatus.Expired:
                    var elapsedMs = (int)Math.Max(0, (DateTime.UtcNow - invocation.CreatedAtUtc).TotalMilliseconds);
                    _requestWaiter.CompleteExpired(invocationId, elapsedMs);
                    break;
            }
        }

        return status switch
        {
            InvocationRespondStatus.Success => Task.FromResult(new JsonRpcResponse
            {
                Id = request.Id,
                Result = new { ok = true }
            }),
            InvocationRespondStatus.NotFound => Task.FromResult(CreateError(request.Id, -32011, "invocation_expired", new { invocationId })),
            InvocationRespondStatus.Expired => Task.FromResult(CreateError(request.Id, -32011, "invocation_expired", new { invocationId })),
            InvocationRespondStatus.DeliveryConflict => Task.FromResult(CreateError(
                request.Id,
                -32030,
                "delivery_conflict",
                BuildDeliveryConflictData(invocationId))),
            _ => Task.FromResult(CreateError(request.Id, -32603, "internal_error"))
        };
    }

    private object BuildDeliveryConflictData(string invocationId)
    {
        if (_store.TryGet(invocationId, out var current) && current is not null)
        {
            return new
            {
                invocationId,
                currentLeaseHolder = current.LeaseHolderInstanceId
            };
        }

        return new { invocationId };
    }

    private static bool TryParseRequestOptions(
        JsonElement paramsElement,
        InvocationTarget target,
        out InvocationOptions options,
        out JsonRpcResponse errorResponse)
    {
        options = new InvocationOptions
        {
            TtlMs = DefaultRequestTtlMs,
            WaitTimeoutMs = DefaultRequestWaitTimeoutMs,
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

            if (optionsElement.TryGetProperty("waitTimeoutMs", out var waitElement))
            {
                if (waitElement.ValueKind != JsonValueKind.Number || !waitElement.TryGetInt32(out var waitMs) || waitMs < 1)
                {
                    errorResponse = InvalidParams(null);
                    return false;
                }

                options.WaitTimeoutMs = waitMs;
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

        if (options.WaitTimeoutMs is null || options.WaitTimeoutMs > options.TtlMs)
        {
            errorResponse = InvalidParams(null);
            return false;
        }

        errorResponse = null!;
        return true;
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

    private static string ResolveNoCandidateReason(InvocationTarget target)
    {
        return string.IsNullOrWhiteSpace(target.InstanceId)
            ? "offline_no_queue"
            : "target_instance_missing";
    }
}
