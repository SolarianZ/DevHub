using System.Text.Json;
using DevHub.Core.Models;
using DevHub.Core.Models.Rpc;
using DevHub.Core.Services;
using DevHub.Core.Services.Abstractions;
using DevHub.Core.Services.Events;
using DevHub.Core.Services.Invocation;
using DevHub.Core.Services.Rpc;
using DevHub.Host.Transport;
using InvocationModel = DevHub.Core.Models.Invocation;

namespace DevHub.Host.Rpc.Handlers;

/// <summary>
/// Host 侧的调用链 RPC 适配处理器。
/// </summary>
public sealed class InvocationRpcHandler : IRpcHandler
{
    private const int DefaultNotifyTtlMs = 60000;
    private const int DefaultRequestTtlMs = 300000;
    private const int DefaultRequestWaitTimeoutMs = 120000;

    private enum InvocationMode
    {
        Notify,
        Request
    }

    private enum RequestTimeoutResolution
    {
        Timeout,
        Expired
    }

    private readonly record struct RequestWaitBudget(
        int TtlRemainingMs,
        int WaitRemainingMs,
        RequestTimeoutResolution TimeoutResolution);

    private readonly AppRegistry _appRegistry;
    private readonly IDefinitionProvider _definitionProvider;
    private readonly InvocationRoutingService _routingService;
    private readonly InvocationStore _store;
    private readonly InvocationRequestWaiter _requestWaiter;
    private readonly LaunchCoordinator _launchCoordinator;
    private readonly RuntimeTuningOptions _runtimeTuningOptions;
    private readonly IHubEventPublisher? _eventPublisher;
    private readonly IClock _clock;
    private readonly ILogger<InvocationRpcHandler> _logger;

    /// <summary>
    /// 初始化处理器。
    /// </summary>
    public InvocationRpcHandler(
        AppRegistry appRegistry,
        IDefinitionProvider definitionProvider,
        InvocationRoutingService routingService,
        InvocationStore store,
        InvocationRequestWaiter requestWaiter,
        LaunchCoordinator launchCoordinator,
        IClock clock,
        RuntimeTuningOptions runtimeTuningOptions,
        ILogger<InvocationRpcHandler> logger,
        IHubEventPublisher? eventPublisher = null)
    {
        _appRegistry = appRegistry;
        _definitionProvider = definitionProvider;
        _routingService = routingService;
        _store = store;
        _requestWaiter = requestWaiter;
        _launchCoordinator = launchCoordinator;
        _clock = clock;
        _runtimeTuningOptions = runtimeTuningOptions;
        _logger = logger;
        _eventPublisher = eventPublisher;
    }

    /// <inheritdoc />
    public string Method => "hub.invoke";

    /// <inheritdoc />
    public Task<JsonRpcResponse> HandleAsync(JsonRpcRequest request, CancellationToken cancellationToken)
    {
        return request.Method switch
        {
            HubRpcMethods.HubInvokeNotify => NotifyAsync(request, cancellationToken),
            HubRpcMethods.HubInvokeRequest => RequestAsync(request, cancellationToken),
            HubRpcMethods.HubInvokePoll => PollAsync(request, cancellationToken),
            HubRpcMethods.HubInvokeRespond => Task.FromResult(Respond(request)),
            _ => Task.FromResult(TransportResponseFactory.CreateErrorResponse(-32601, "method_not_found", request.Id))
        };
    }

    private async Task<JsonRpcResponse> NotifyAsync(JsonRpcRequest request, CancellationToken cancellationToken)
    {
        var enqueueResult = await BuildAndEnqueueInvocationAsync(request, InvocationMode.Notify, cancellationToken);
        if (enqueueResult.ErrorResponse is not null)
        {
            return enqueueResult.ErrorResponse;
        }

        return new JsonRpcResponse
        {
            Id = request.Id,
            Result = new
            {
                ok = true,
                invocationId = enqueueResult.Invocation!.InvocationId
            }
        };
    }

    private async Task<JsonRpcResponse> RequestAsync(JsonRpcRequest request, CancellationToken cancellationToken)
    {
        var enqueueResult = await BuildAndEnqueueInvocationAsync(request, InvocationMode.Request, cancellationToken);
        if (enqueueResult.ErrorResponse is not null)
        {
            return enqueueResult.ErrorResponse;
        }

        var invocation = enqueueResult.Invocation!;
        var waiterTask = enqueueResult.WaiterTask!;

        var waitBudget = BuildRequestWaitBudget(invocation);
        var timeoutWindowMs = Math.Min(waitBudget.TtlRemainingMs, waitBudget.WaitRemainingMs);
        if (timeoutWindowMs <= 0)
        {
            if (waiterTask.IsCompleted)
            {
                var completion = await waiterTask;
                return BuildRequestCompletionResponse(request.Id, invocation.InvocationId, completion);
            }

            return await HandleRequestTimeoutAsync(
                request,
                invocation,
                waiterTask,
                Task.CompletedTask,
                timeoutWindowMs,
                waitBudget.TimeoutResolution,
                cancellationToken);
        }

        var timeoutTask = Task.Delay(timeoutWindowMs, cancellationToken);
        var completionTask = await Task.WhenAny(waiterTask, timeoutTask);
        if (completionTask == waiterTask)
        {
            var completion = await waiterTask;
            return BuildRequestCompletionResponse(request.Id, invocation.InvocationId, completion);
        }

        return await HandleRequestTimeoutAsync(
            request,
            invocation,
            waiterTask,
            timeoutTask,
            timeoutWindowMs,
            waitBudget.TimeoutResolution,
            cancellationToken);
    }

    private async Task<JsonRpcResponse> HandleRequestTimeoutAsync(
        JsonRpcRequest request,
        InvocationModel invocation,
        Task<InvocationRequestCompletion> waiterTask,
        Task timeoutTask,
        int timeoutWindowMs,
        RequestTimeoutResolution timeoutResolution,
        CancellationToken cancellationToken)
    {
        var elapsedMs = (int)Math.Max(0, (_clock.UtcNow - invocation.CreatedAtUtc).TotalMilliseconds);
        var timeoutElapsedMs = timeoutTask.IsCanceled ? elapsedMs : Math.Max(elapsedMs, timeoutWindowMs);
        var ttlReached = elapsedMs >= invocation.Options.TtlMs;
        var preferExpired = timeoutResolution == RequestTimeoutResolution.Expired;

        if (cancellationToken.IsCancellationRequested)
        {
            if (ttlReached)
            {
                _store.MarkExpired(invocation.InvocationId, _clock.UtcNow);
                _requestWaiter.CompleteExpired(invocation.InvocationId, elapsedMs);
                _requestWaiter.Cleanup(invocation.InvocationId);

                return TransportResponseFactory.CreateErrorResponse(
                    -32011,
                    "invocation_expired",
                    request.Id,
                    new
                    {
                        invocationId = invocation.InvocationId,
                        elapsedMs
                    });
            }

            _store.MarkTimeout(invocation.InvocationId, _clock.UtcNow);
            _requestWaiter.CompleteTimeout(invocation.InvocationId, elapsedMs);
            _requestWaiter.Cleanup(invocation.InvocationId);

            return TransportResponseFactory.CreateErrorResponse(
                -32012,
                "invocation_timeout",
                request.Id,
                new
                {
                    invocationId = invocation.InvocationId,
                    elapsedMs
                });
        }

        if (ttlReached || preferExpired)
        {
            var markedExpired = _store.MarkExpired(invocation.InvocationId, _clock.UtcNow);
            if (markedExpired)
            {
                _requestWaiter.CompleteExpired(invocation.InvocationId, elapsedMs);
            }
            else
            {
                var racedCompletion = await waiterTask;
                return BuildRequestCompletionResponse(request.Id, invocation.InvocationId, racedCompletion);
            }

            return TransportResponseFactory.CreateErrorResponse(
                -32011,
                "invocation_expired",
                request.Id,
                new
                {
                    invocationId = invocation.InvocationId,
                    elapsedMs
                });
        }

        var markedTimeout = _store.MarkTimeout(invocation.InvocationId, _clock.UtcNow);
        if (markedTimeout)
        {
            _requestWaiter.CompleteTimeout(invocation.InvocationId, timeoutElapsedMs);
        }
        else
        {
            var racedCompletion = await waiterTask;
            return BuildRequestCompletionResponse(request.Id, invocation.InvocationId, racedCompletion);
        }

        return TransportResponseFactory.CreateErrorResponse(
            -32012,
            "invocation_timeout",
            request.Id,
            new
            {
                invocationId = invocation.InvocationId,
                elapsedMs = timeoutElapsedMs
            });
    }

    private RequestWaitBudget BuildRequestWaitBudget(InvocationModel invocation)
    {
        var elapsedSinceCreatedMs = GetElapsedSinceCreatedMs(invocation);
        var ttlRemainingMs = GetRemainingBudgetMs(invocation.Options.TtlMs, elapsedSinceCreatedMs);
        var waitTimeoutMs = invocation.Options.WaitTimeoutMs ?? DefaultRequestWaitTimeoutMs;
        var waitRemainingMs = GetRemainingBudgetMs(waitTimeoutMs, elapsedSinceCreatedMs);

        return new RequestWaitBudget(
            ttlRemainingMs,
            waitRemainingMs,
            waitRemainingMs < ttlRemainingMs
                ? RequestTimeoutResolution.Timeout
                : RequestTimeoutResolution.Expired);
    }

    private int GetElapsedSinceCreatedMs(InvocationModel invocation)
    {
        return (int)Math.Max(0, (_clock.UtcNow - invocation.CreatedAtUtc).TotalMilliseconds);
    }

    private static int GetRemainingBudgetMs(int totalBudgetMs, int elapsedSinceCreatedMs)
    {
        return Math.Max(0, totalBudgetMs - elapsedSinceCreatedMs);
    }

    private async Task<InvocationBuildResult> BuildAndEnqueueInvocationAsync(
        JsonRpcRequest request,
        InvocationMode mode,
        CancellationToken cancellationToken)
    {
        if (!RpcRequestParameterReader.TryReadParamsObject(request, out var paramsElement))
        {
            return new InvocationBuildResult(null, TransportResponseFactory.CreateErrorResponse(-32602, "invalid_params", request.Id));
        }

        if (!RpcRequestParameterReader.TryGetRequiredString(paramsElement, "appId", out var appId)
            || !RpcRequestParameterReader.TryGetRequiredString(paramsElement, "method", out var method))
        {
            return new InvocationBuildResult(null, TransportResponseFactory.CreateErrorResponse(-32602, "invalid_params", request.Id));
        }

        if (!RpcRequestParameterReader.TryParseInvocationTarget(paramsElement, out var target, out var targetError))
        {
            return new InvocationBuildResult(
                null,
                TransportResponseFactory.CreateErrorResponse(-32602, "invalid_params", request.Id, targetError));
        }

        if (!TryParseInvocationOptions(mode, paramsElement, target, request.Id, out var options, out var optionErrorResponse))
        {
            return new InvocationBuildResult(null, optionErrorResponse);
        }

        _definitionProvider.Refresh();
        var definition = _definitionProvider.GetDefinition(appId);
        if (definition is not null && definition.Capabilities?.Rpc == false)
        {
            return new InvocationBuildResult(
                null,
                TransportResponseFactory.CreateErrorResponse(
                    -32002,
                    "forbidden",
                    request.Id,
                    new { reason = "rpc_disabled" }));
        }

        var candidates = _routingService.GetOnlineCandidates(appId, target);
        LogRouteDecision(request.Method, appId, target, candidates.Count);
        if (candidates.Count == 0)
        {
            if (!options.QueueIfOffline)
            {
                return new InvocationBuildResult(
                    null,
                    TransportResponseFactory.CreateErrorResponse(
                        -32010,
                        "instance_not_found",
                        request.Id,
                        new { reason = ResolveNoCandidateReason(target) }));
            }

            if (definition is null)
            {
                return new InvocationBuildResult(
                    null,
                    TransportResponseFactory.CreateErrorResponse(
                        -32010,
                        "instance_not_found",
                        request.Id,
                        new { reason = ResolveNoCandidateReason(target) }));
            }

            if (options.AutoLaunch)
            {
                var launchResult = await _launchCoordinator.LaunchAsync(appId, target.Scope, dedupeKey: null, waitForRegisterMs: 0, cancellationToken);
                if (!launchResult.Ok)
                {
                    return new InvocationBuildResult(
                        null,
                        TransportResponseFactory.CreateErrorResponse(
                            launchResult.ErrorCode ?? -32603,
                            launchResult.ErrorMessage ?? "internal_error",
                            request.Id,
                            launchResult.ErrorData));
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
                : null,
            Kind = mode == InvocationMode.Notify ? InvocationKind.Notify : InvocationKind.Request,
            CreatedAtUtc = _clock.UtcNow,
            Options = options,
            Delivery = new InvocationDelivery
            {
                LeaseSeconds = _runtimeTuningOptions.LeaseSeconds,
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

        Task<InvocationRequestCompletion>? waiterTask = null;
        if (mode == InvocationMode.Request)
        {
            waiterTask = _requestWaiter.Register(invocation.InvocationId);
        }

        var created = _store.TryCreateInvocation(
            invocation,
            hasOnlineCandidates: candidates.Count > 0,
            _runtimeTuningOptions.PendingInvocationsLimit,
            out var activeInvocationCount);
        if (!created)
        {
            if (waiterTask is not null)
            {
                _requestWaiter.Cleanup(invocation.InvocationId);
            }

            return new InvocationBuildResult(
                null,
                TransportResponseFactory.CreateErrorResponse(
                    -32040,
                    "rate_limited",
                    request.Id,
                    new
                    {
                        reason = "pending_invocations_limit_exceeded",
                        limit = _runtimeTuningOptions.PendingInvocationsLimit,
                        active = activeInvocationCount
                    }));
        }

        PublishInvocationLifecycleEvent(HubEventTypes.InvocationQueued, invocation, null, error: null);
        return new InvocationBuildResult(invocation, null, waiterTask);
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
                    value = completion.Value ?? JsonSerializer.SerializeToElement((object?)null)
                }
            },
            InvocationRequestCompletionKind.Failed => TransportResponseFactory.CreateErrorResponse(
                -32050,
                "invocation_failed",
                requestId,
                new
                {
                    invocationId,
                    calleeError = completion.CalleeError
                }),
            InvocationRequestCompletionKind.Timeout => TransportResponseFactory.CreateErrorResponse(
                -32012,
                "invocation_timeout",
                requestId,
                new
                {
                    invocationId,
                    elapsedMs = completion.ElapsedMs ?? 0
                }),
            InvocationRequestCompletionKind.Expired => TransportResponseFactory.CreateErrorResponse(
                -32011,
                "invocation_expired",
                requestId,
                new
                {
                    invocationId,
                    elapsedMs = completion.ElapsedMs ?? 0
                }),
            _ => TransportResponseFactory.CreateErrorResponse(-32603, "internal_error", requestId)
        };
    }

    private async Task<JsonRpcResponse> PollAsync(JsonRpcRequest request, CancellationToken cancellationToken)
    {
        if (!RpcRequestParameterReader.TryReadParamsObject(request, out var paramsElement))
        {
            return TransportResponseFactory.CreateErrorResponse(-32602, "invalid_params", request.Id);
        }

        if (!RpcRequestParameterReader.TryGetRequiredString(paramsElement, "instanceId", out var instanceId))
        {
            return TransportResponseFactory.CreateErrorResponse(-32602, "invalid_params", request.Id);
        }

        var maxCount = 10;
        if (paramsElement.TryGetProperty("maxCount", out var maxCountElement))
        {
            if (maxCountElement.ValueKind != JsonValueKind.Number || !maxCountElement.TryGetInt32(out maxCount) || maxCount is < 1 or > 100)
            {
                return TransportResponseFactory.CreateErrorResponse(-32602, "invalid_params", request.Id);
            }
        }

        var waitMs = 25000;
        if (paramsElement.TryGetProperty("waitMs", out var waitMsElement))
        {
            if (waitMsElement.ValueKind != JsonValueKind.Number || !waitMsElement.TryGetInt32(out waitMs) || waitMs < 0)
            {
                return TransportResponseFactory.CreateErrorResponse(-32602, "invalid_params", request.Id);
            }
        }

        var instance = _appRegistry.GetInstance(instanceId);
        if (instance is null)
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

        if (!instance.Invoke.Poll)
        {
            return TransportResponseFactory.CreateErrorResponse(
                -32002,
                "forbidden",
                request.Id,
                new
                {
                    reason = "poll_not_enabled",
                    instanceId
                });
        }

        _appRegistry.Heartbeat(instanceId, out _);

        var items = await _store.PollAsync(instance, maxCount, waitMs, cancellationToken);
        return new JsonRpcResponse
        {
            Id = request.Id,
            Result = new
            {
                ok = true,
                serverTimeUtc = _clock.UtcNow.ToString("O"),
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

    private JsonRpcResponse Respond(JsonRpcRequest request)
    {
        if (!RpcRequestParameterReader.TryReadParamsObject(request, out var paramsElement))
        {
            return TransportResponseFactory.CreateErrorResponse(-32602, "invalid_params", request.Id);
        }

        if (!RpcRequestParameterReader.TryGetRequiredString(paramsElement, "instanceId", out var instanceId)
            || !RpcRequestParameterReader.TryGetRequiredString(paramsElement, "invocationId", out var invocationId))
        {
            return TransportResponseFactory.CreateErrorResponse(-32602, "invalid_params", request.Id);
        }

        var hasValue = paramsElement.TryGetProperty("value", out var valueElement);
        var hasError = paramsElement.TryGetProperty("error", out var errorElement);
        if (hasValue == hasError)
        {
            return TransportResponseFactory.CreateErrorResponse(-32602, "invalid_params", request.Id);
        }

        object? value;
        object? error;
        if (hasValue)
        {
            value = JsonSerializer.Deserialize<object>(valueElement.GetRawText());
            error = null;
        }
        else
        {
            value = null;
            if (!RpcRequestParameterReader.TryParseRespondError(errorElement, out error))
            {
                return TransportResponseFactory.CreateErrorResponse(-32602, "invalid_params", request.Id);
            }
        }

        var instance = _appRegistry.GetInstance(instanceId);
        if (instance is null)
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

        if (!instance.Invoke.Respond)
        {
            return TransportResponseFactory.CreateErrorResponse(
                -32002,
                "forbidden",
                request.Id,
                new
                {
                    reason = "respond_not_enabled",
                    instanceId
                });
        }

        _appRegistry.Heartbeat(instanceId, out _);

        var status = _store.Respond(instanceId, invocationId, value, error);
        if (_store.TryGet(invocationId, out var invocation) && invocation is not null)
        {
            if (status == InvocationRespondStatus.Success)
            {
                if (error is null)
                {
                    PublishInvocationLifecycleEvent(HubEventTypes.InvocationCompleted, invocation, instanceId, error: null);
                }
                else
                {
                    PublishInvocationLifecycleEvent(HubEventTypes.InvocationFailed, invocation, instanceId, error);
                }
            }

            if (invocation.Kind == InvocationKind.Request)
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
                        var elapsedMs = (int)Math.Max(0, (_clock.UtcNow - invocation.CreatedAtUtc).TotalMilliseconds);
                        _requestWaiter.CompleteExpired(invocationId, elapsedMs);
                        break;
                }
            }
        }

        return status switch
        {
            InvocationRespondStatus.Success => new JsonRpcResponse
            {
                Id = request.Id,
                Result = new { ok = true }
            },
            InvocationRespondStatus.NotFound => TransportResponseFactory.CreateErrorResponse(
                -32011,
                "invocation_expired",
                request.Id,
                new
                {
                    invocationId,
                    reason = "unknown_invocation"
                }),
            InvocationRespondStatus.Expired => TransportResponseFactory.CreateErrorResponse(
                -32011,
                "invocation_expired",
                request.Id,
                new { invocationId }),
            InvocationRespondStatus.DeliveryConflict => TransportResponseFactory.CreateErrorResponse(
                -32030,
                "delivery_conflict",
                request.Id,
                BuildDeliveryConflictData(invocationId)),
            _ => TransportResponseFactory.CreateErrorResponse(-32603, "internal_error", request.Id)
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

    private void PublishInvocationLifecycleEvent(string eventType, InvocationModel invocation, string? instanceId, object? error)
    {
        _eventPublisher?.Publish(new HubEventMessage
        {
            Type = eventType,
            TimeUtc = _clock.UtcNow,
            Payload = new
            {
                invocationId = invocation.InvocationId,
                appId = invocation.AppId,
                instanceId,
                scope = invocation.Target.Scope,
                error
            }
        });
    }

    private static bool TryParseInvocationOptions(
        InvocationMode mode,
        JsonElement paramsElement,
        InvocationTarget target,
        object? requestId,
        out InvocationOptions options,
        out JsonRpcResponse errorResponse)
    {
        var isRequestMode = mode == InvocationMode.Request;
        options = new InvocationOptions
        {
            TtlMs = isRequestMode ? DefaultRequestTtlMs : DefaultNotifyTtlMs,
            WaitTimeoutMs = isRequestMode ? DefaultRequestWaitTimeoutMs : null,
            QueueIfOffline = true,
            AutoLaunch = target.InstanceId is null
        };

        if (paramsElement.TryGetProperty("options", out var optionsElement))
        {
            if (optionsElement.ValueKind != JsonValueKind.Object)
            {
                errorResponse = TransportResponseFactory.CreateErrorResponse(-32602, "invalid_params", requestId);
                return false;
            }

            if (optionsElement.TryGetProperty("ttlMs", out var ttlElement))
            {
                if (ttlElement.ValueKind != JsonValueKind.Number || !ttlElement.TryGetInt32(out var ttlMs) || ttlMs < 1000)
                {
                    errorResponse = TransportResponseFactory.CreateErrorResponse(-32602, "invalid_params", requestId);
                    return false;
                }

                options.TtlMs = ttlMs;
            }

            if (optionsElement.TryGetProperty("waitTimeoutMs", out var waitElement))
            {
                if (!isRequestMode || waitElement.ValueKind != JsonValueKind.Number || !waitElement.TryGetInt32(out var waitMs) || waitMs < 1)
                {
                    errorResponse = TransportResponseFactory.CreateErrorResponse(-32602, "invalid_params", requestId);
                    return false;
                }

                options.WaitTimeoutMs = waitMs;
            }

            if (optionsElement.TryGetProperty("queueIfOffline", out var queueElement))
            {
                if (queueElement.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
                {
                    errorResponse = TransportResponseFactory.CreateErrorResponse(-32602, "invalid_params", requestId);
                    return false;
                }

                options.QueueIfOffline = queueElement.GetBoolean();
            }

            if (optionsElement.TryGetProperty("autoLaunch", out var autoLaunchElement))
            {
                if (autoLaunchElement.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
                {
                    errorResponse = TransportResponseFactory.CreateErrorResponse(-32602, "invalid_params", requestId);
                    return false;
                }

                options.AutoLaunch = autoLaunchElement.GetBoolean();
            }
        }

        if (target.InstanceId is not null && options.AutoLaunch)
        {
            errorResponse = TransportResponseFactory.CreateErrorResponse(-32602, "invalid_params", requestId);
            return false;
        }

        if (options.AutoLaunch && !options.QueueIfOffline)
        {
            errorResponse = TransportResponseFactory.CreateErrorResponse(-32602, "invalid_params", requestId);
            return false;
        }

        if (isRequestMode && (options.WaitTimeoutMs is null || options.WaitTimeoutMs > options.TtlMs))
        {
            errorResponse = TransportResponseFactory.CreateErrorResponse(-32602, "invalid_params", requestId);
            return false;
        }

        errorResponse = null!;
        return true;
    }

    private sealed record InvocationBuildResult(
        InvocationModel? Invocation,
        JsonRpcResponse? ErrorResponse,
        Task<InvocationRequestCompletion>? WaiterTask = null);

    private static string ResolveNoCandidateReason(InvocationTarget target)
    {
        return target.InstanceId is null
            ? "offline_no_queue"
            : "target_instance_missing";
    }

    private void LogRouteDecision(string methodName, string appId, InvocationTarget target, int candidateCount)
    {
        var matchedScope = target.Scope is null ? "global" : "explicit";
        _logger.LogInformation(
            "Invocation 路由决策: method={method}, appId={appId}, target.scope={targetScope}, target.instanceId={targetInstanceId}, candidateCount={candidateCount}, matchedScope={matchedScope}",
            methodName,
            appId,
            target.Scope,
            target.InstanceId,
            candidateCount,
            matchedScope);
    }
}
