using DevHub.Core.Models;
using DevHub.Core.Models.Rpc;
using DevHub.Core.Services;
using DevHub.Core.Services.Abstractions;
using DevHub.Core.Services.Events;
using DevHub.Core.Services.Invocation;
using DevHub.Core.Services.Rpc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace DevHub.Core.Services.Rpc.Handlers;

/// <summary>
/// 处理应用程序实例相关方法的RPC处理器
/// </summary>
public class AppInstancesHandler : IRpcHandler
{
    private readonly AppRegistry _appRegistry;
    private readonly IDefinitionProvider _definitionProvider;
    private readonly ILaunchRegistrationTracker _launchRegistrationTracker;
    private readonly IHubEventPublisher? _eventPublisher;
    private readonly IClock _clock;
    private readonly ILogger<AppInstancesHandler> _logger;

    /// <summary>
    /// 初始化应用实例 RPC 处理器。
    /// </summary>
    /// <param name="appRegistry">应用实例注册表。</param>
    /// <param name="definitionProvider">Definition 快照提供器。</param>
    /// <param name="launchRegistrationTracker">启动绑定跟踪器。</param>
    /// <param name="clock">系统时钟。</param>
    /// <param name="logger">日志记录器。</param>
    /// <param name="eventPublisher">Hub 事件发布器。</param>
    [ActivatorUtilitiesConstructor]
    public AppInstancesHandler(
        AppRegistry appRegistry,
        IDefinitionProvider definitionProvider,
        ILaunchRegistrationTracker launchRegistrationTracker,
        IClock clock,
        ILogger<AppInstancesHandler> logger,
        IHubEventPublisher? eventPublisher = null)
    {
        _appRegistry = appRegistry;
        _definitionProvider = definitionProvider;
        _launchRegistrationTracker = launchRegistrationTracker;
        _clock = clock;
        _eventPublisher = eventPublisher;
        _logger = logger;
    }

    /// <summary>
    /// 初始化应用实例 RPC 处理器。
    /// </summary>
    /// <param name="appRegistry">应用实例注册表。</param>
    /// <param name="clock">系统时钟。</param>
    /// <param name="logger">日志记录器。</param>
    /// <param name="eventPublisher">Hub 事件发布器。</param>
    public AppInstancesHandler(AppRegistry appRegistry, IClock clock, ILogger<AppInstancesHandler> logger, IHubEventPublisher? eventPublisher = null)
        : this(appRegistry, EmptyDefinitionProvider.Instance, NullLaunchRegistrationTracker.Instance, clock, logger, eventPublisher)
    {
    }

    /// <inheritdoc />
    public string Method => "hub.apps";

    /// <inheritdoc />
    public async Task<JsonRpcResponse> HandleAsync(JsonRpcRequest request, CancellationToken cancellationToken)
    {
        _logger.LogDebug("收到应用程序实例相关RPC请求: {Method}, RequestId: {RequestId}", request.Method, request.Id);

        return request.Method switch
        {
            HubRpcMethods.HubAppsRegisterInstance => await RegisterInstanceAsync(request, cancellationToken),
            HubRpcMethods.HubAppsHeartbeat => await HeartbeatAsync(request, cancellationToken),
            HubRpcMethods.HubAppsUnregisterInstance => await UnregisterInstanceAsync(request, cancellationToken),
            HubRpcMethods.HubAppsListInstances => await ListInstancesAsync(request, cancellationToken),
            HubRpcMethods.HubAppsGetInstance => await GetInstanceAsync(request, cancellationToken),
            _ => RpcErrorFactory.MethodNotFound(request.Id)
        };
    }

    /// <summary>
    /// 处理hub.apps.registerInstance方法
    /// </summary>
    private Task<JsonRpcResponse> RegisterInstanceAsync(JsonRpcRequest request, CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogDebug("处理hub.apps.registerInstance方法，RequestId: {RequestId}, 参数: {Params}", request.Id, RpcLogJsonSerializer.Serialize(request.Params));

            if (request.Params is not JsonElement paramsElement || paramsElement.ValueKind != JsonValueKind.Object)
            {
                _logger.LogWarning("hub.apps.registerInstance参数无效: params 不是对象, RequestId: {RequestId}", request.Id);
                return Task.FromResult(RpcErrorFactory.InvalidParams(request.Id));
            }

            if (!RpcParamReader.TryGetRequiredString(paramsElement, "password", out var password))
            {
                _logger.LogWarning("hub.apps.registerInstance参数无效: 缺少 password 或类型错误, RequestId: {RequestId}", request.Id);
                return Task.FromResult(RpcErrorFactory.InvalidParams(request.Id));
            }

            if (!paramsElement.TryGetProperty("instance", out var instanceElement) || instanceElement.ValueKind != JsonValueKind.Object)
            {
                _logger.LogWarning("hub.apps.registerInstance参数无效: 缺少 instance 对象, RequestId: {RequestId}", request.Id);
                return Task.FromResult(RpcErrorFactory.InvalidParams(request.Id));
            }

            if (!TryParseInstanceRegistration(instanceElement, request.Id, out var instance, out var parseErrorData))
            {
                return Task.FromResult(RpcErrorFactory.Create(request.Id, -32602, "invalid_params", parseErrorData));
            }

            _logger.LogDebug("尝试注册应用程序实例，InstanceId: {InstanceId}, AppId: {AppId}, Scope: {Scope}, PID: {PID}, RequestId: {RequestId}",
                instance.InstanceId, instance.AppId, instance.Scope, instance.Pid, request.Id);

            if (!TryGetOptionalLaunchId(paramsElement, request.Id, out var launchId, out var launchIdErrorData))
            {
                return Task.FromResult(RpcErrorFactory.Create(request.Id, -32602, "invalid_params", launchIdErrorData));
            }

            var launchBindingValidation = _launchRegistrationTracker.ValidateRegistration(launchId, instance.AppId, instance.Scope);
            if (launchBindingValidation.Status == LaunchRegistrationValidationStatus.Mismatched)
            {
                _logger.LogWarning(
                    "注册应用程序实例失败: 启动绑定不匹配，InstanceId: {InstanceId}, AppId: {AppId}, Scope: {Scope}, RequestId: {RequestId}",
                    instance.InstanceId,
                    instance.AppId,
                    instance.Scope,
                    request.Id);
                return Task.FromResult(RpcErrorFactory.Forbidden(request.Id, launchBindingValidation.ErrorData));
            }

            if (!_appRegistry.TryRegisterInstance(
                    instance,
                    password,
                    out var registeredInstance,
                    out var instanceSessionToken,
                    out var registrationValidationStatus))
            {
                if (registrationValidationStatus == InstanceRegistrationValidationStatus.PasswordMismatch)
                {
                    _logger.LogWarning("注册应用程序实例失败: 实例密码不匹配，InstanceId: {InstanceId}, RequestId: {RequestId}", instance.InstanceId, request.Id);
                    return Task.FromResult(RpcErrorFactory.Forbidden(request.Id, new
                    {
                        reason = "instance_password_mismatch",
                        instanceId = instance.InstanceId
                    }));
                }

                if (registrationValidationStatus == InstanceRegistrationValidationStatus.IdentityMismatch)
                {
                    _logger.LogWarning(
                        "注册应用程序实例失败: 实例身份不匹配，InstanceId: {InstanceId}, AppId: {AppId}, Scope: {Scope}, RequestId: {RequestId}",
                        instance.InstanceId,
                        instance.AppId,
                        instance.Scope,
                        request.Id);
                    return Task.FromResult(RpcErrorFactory.Forbidden(request.Id, new
                    {
                        reason = "instance_identity_mismatch",
                        instanceId = instance.InstanceId
                    }));
                }

                return Task.FromResult(RpcErrorFactory.InternalError(request.Id));
            }

            _launchRegistrationTracker.RecordSuccessfulRegistration(launchId, registeredInstance);
            PublishInstanceEvent(HubEventTypes.AppInstanceRegistered, registeredInstance.AppId, registeredInstance.InstanceId, registeredInstance.Scope);

            _logger.LogInformation("成功注册应用程序实例，InstanceId: {InstanceId}, AppId: {AppId}, Scope: {Scope}, PID: {PID}, RequestId: {RequestId}",
                registeredInstance.InstanceId, registeredInstance.AppId, registeredInstance.Scope, registeredInstance.Pid, request.Id);

            var response = new JsonRpcResponse
            {
                Id = request.Id,
                Result = new
                {
                    ok = true,
                    instance = registeredInstance,
                    instanceSessionToken
                }
            };

            _logger.LogDebug("hub.apps.registerInstance方法响应: {Response}, RequestId: {RequestId}", RpcLogJsonSerializer.Serialize(response), request.Id);
            return Task.FromResult(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "处理hub.apps.registerInstance方法失败，RequestId: {RequestId}, 参数: {Params}", request.Id, RpcLogJsonSerializer.Serialize(request.Params));
            return Task.FromResult(RpcErrorFactory.InternalError(request.Id));
        }
    }

    /// <summary>
    /// 处理hub.apps.heartbeat方法
    /// </summary>
    private Task<JsonRpcResponse> HeartbeatAsync(JsonRpcRequest request, CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogDebug("处理hub.apps.heartbeat方法，RequestId: {RequestId}, 参数: {Params}", request.Id, RpcLogJsonSerializer.Serialize(request.Params));

            if (request.Params is not JsonElement paramsElement || paramsElement.ValueKind != JsonValueKind.Object)
            {
                _logger.LogWarning("hub.apps.heartbeat参数无效: params 不是对象, RequestId: {RequestId}", request.Id);
                return Task.FromResult(RpcErrorFactory.InvalidParams(request.Id));
            }

            if (!RpcParamReader.TryGetRequiredInstanceId(paramsElement, "instanceId", out var instanceId))
            {
                _logger.LogWarning("hub.apps.heartbeat参数无效: 缺少 instanceId、类型错误或格式非法, RequestId: {RequestId}", request.Id);
                return Task.FromResult(RpcErrorFactory.InvalidParams(request.Id));
            }

            if (!RpcParamReader.TryGetRequiredString(paramsElement, "instanceSessionToken", out var instanceSessionToken))
            {
                _logger.LogWarning("hub.apps.heartbeat参数无效: 缺少 instanceSessionToken 或非字符串, RequestId: {RequestId}", request.Id);
                return Task.FromResult(RpcErrorFactory.InvalidParams(request.Id));
            }

            _logger.LogDebug("尝试更新实例心跳，InstanceId: {InstanceId}, RequestId: {RequestId}", instanceId, request.Id);
            if (!_appRegistry.TryHeartbeat(instanceId, instanceSessionToken, out var lastSeenUtc, out var validationStatus))
            {
                if (validationStatus == InstanceSessionValidationStatus.TokenMismatch)
                {
                    _logger.LogWarning("实例心跳更新失败: 实例会话凭据不匹配，InstanceId: {InstanceId}, RequestId: {RequestId}", instanceId, request.Id);
                    return Task.FromResult(RpcErrorFactory.Forbidden(request.Id, new
                    {
                        reason = "instance_session_token_mismatch",
                        instanceId
                    }));
                }

                _logger.LogWarning("实例心跳更新失败: 未找到实例 {InstanceId}, RequestId: {RequestId}", instanceId, request.Id);
                return Task.FromResult(new JsonRpcResponse
                {
                    Id = request.Id,
                    Error = new JsonRpcError
                    {
                        Code = -32010,
                        Message = "instance_not_found",
                        Data = new
                        {
                            reason = "unknown_instance",
                            instanceId
                        }
                    }
                });
            }

            _logger.LogInformation("实例心跳更新成功，InstanceId: {InstanceId}, RequestId: {RequestId}", instanceId, request.Id);
            var response = new JsonRpcResponse
            {
                Id = request.Id,
                Result = new
                {
                    ok = true,
                    lastSeenUtc = lastSeenUtc.ToString("O")
                }
            };

            _logger.LogDebug("hub.apps.heartbeat方法响应: {Response}, RequestId: {RequestId}", RpcLogJsonSerializer.Serialize(response), request.Id);
            return Task.FromResult(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "处理hub.apps.heartbeat方法失败，RequestId: {RequestId}, 参数: {Params}", request.Id, RpcLogJsonSerializer.Serialize(request.Params));
            return Task.FromResult(RpcErrorFactory.InternalError(request.Id));
        }
    }

    /// <summary>
    /// 处理hub.apps.unregisterInstance方法
    /// </summary>
    private Task<JsonRpcResponse> UnregisterInstanceAsync(JsonRpcRequest request, CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogDebug("处理hub.apps.unregisterInstance方法，RequestId: {RequestId}, 参数: {Params}", request.Id, RpcLogJsonSerializer.Serialize(request.Params));

            if (request.Params is not JsonElement paramsElement || paramsElement.ValueKind != JsonValueKind.Object)
            {
                _logger.LogWarning("hub.apps.unregisterInstance参数无效: params 不是对象, RequestId: {RequestId}", request.Id);
                return Task.FromResult(RpcErrorFactory.InvalidParams(request.Id));
            }

            if (!RpcParamReader.TryGetRequiredInstanceId(paramsElement, "instanceId", out var instanceId))
            {
                _logger.LogWarning("hub.apps.unregisterInstance参数无效: 缺少 instanceId 或非字符串, RequestId: {RequestId}", request.Id);
                return Task.FromResult(RpcErrorFactory.InvalidParams(request.Id));
            }

            if (!RpcParamReader.TryGetRequiredString(paramsElement, "instanceSessionToken", out var instanceSessionToken))
            {
                _logger.LogWarning("hub.apps.unregisterInstance参数无效: 缺少 instanceSessionToken 或非字符串, RequestId: {RequestId}", request.Id);
                return Task.FromResult(RpcErrorFactory.InvalidParams(request.Id));
            }

            _logger.LogDebug("尝试注销应用程序实例，InstanceId: {InstanceId}, RequestId: {RequestId}", instanceId, request.Id);
            if (!_appRegistry.TryUnregisterInstance(instanceId, instanceSessionToken, out var removedInstance, out var validationStatus))
            {
                _logger.LogWarning("注销应用程序实例失败: 实例会话凭据不匹配，InstanceId: {InstanceId}, RequestId: {RequestId}", instanceId, request.Id);
                return Task.FromResult(RpcErrorFactory.Forbidden(request.Id, new
                {
                    reason = "instance_session_token_mismatch",
                    instanceId
                }));
            }

            if (removedInstance is not null)
            {
                PublishInstanceEvent(HubEventTypes.AppInstanceUnregistered, removedInstance.AppId, removedInstance.InstanceId, removedInstance.Scope);
            }

            _logger.LogInformation("注销应用程序实例完成（幂等），InstanceId: {InstanceId}, RequestId: {RequestId}", instanceId, request.Id);

            var response = new JsonRpcResponse
            {
                Id = request.Id,
                Result = new
                {
                    ok = true
                }
            };

            _logger.LogDebug("hub.apps.unregisterInstance方法响应: {Response}, RequestId: {RequestId}", RpcLogJsonSerializer.Serialize(response), request.Id);
            return Task.FromResult(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "处理hub.apps.unregisterInstance方法失败，RequestId: {RequestId}, 参数: {Params}", request.Id, RpcLogJsonSerializer.Serialize(request.Params));
            return Task.FromResult(RpcErrorFactory.InternalError(request.Id));
        }
    }

    /// <summary>
    /// 处理hub.apps.listInstances方法
    /// </summary>
    private Task<JsonRpcResponse> ListInstancesAsync(JsonRpcRequest request, CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogDebug("处理hub.apps.listInstances方法，RequestId: {RequestId}, 参数: {Params}", request.Id, RpcLogJsonSerializer.Serialize(request.Params));

            string? appId = null;
            string? scope = null;
            var includeOffline = false;

            if (!RpcParamReader.TryReadParamsObject(request, out var paramsElement, out var invalidParams))
            {
                _logger.LogWarning("hub.apps.listInstances参数无效: params 不是对象, RequestId: {RequestId}", request.Id);
                return Task.FromResult(invalidParams);
            }

            if (paramsElement.TryGetProperty("appId", out var appIdProperty))
            {
                if (appIdProperty.ValueKind != JsonValueKind.String)
                {
                    _logger.LogWarning("hub.apps.listInstances参数无效: appId 不是字符串, RequestId: {RequestId}", request.Id);
                    return Task.FromResult(RpcErrorFactory.InvalidParams(request.Id));
                }

                appId = appIdProperty.GetString();
                if (!ProtocolIdentifier.IsValidAppId(appId))
                {
                    _logger.LogWarning("hub.apps.listInstances参数无效: appId 不符合格式要求, RequestId: {RequestId}", request.Id);
                    return Task.FromResult(RpcErrorFactory.InvalidParams(request.Id));
                }
            }

            if (!RpcParamReader.TryGetRequiredListScope(
                    paramsElement,
                    "scope",
                    "invalid_scope",
                    out scope,
                    out var scopeErrorData))
            {
                _logger.LogWarning("hub.apps.listInstances参数无效: scope 非法, RequestId: {RequestId}", request.Id);
                return Task.FromResult(RpcErrorFactory.Create(request.Id, -32602, "invalid_params", scopeErrorData));
            }

            if (paramsElement.TryGetProperty("includeOffline", out var includeOfflineProperty))
            {
                if (includeOfflineProperty.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
                {
                    _logger.LogWarning("hub.apps.listInstances参数无效: includeOffline 必须为布尔值, RequestId: {RequestId}", request.Id);
                    return Task.FromResult(RpcErrorFactory.InvalidParams(request.Id));
                }

                includeOffline = includeOfflineProperty.GetBoolean();
            }

            _logger.LogDebug("尝试获取应用程序实例列表，AppId: {AppId}, Scope: {Scope}, IncludeOffline: {IncludeOffline}, RequestId: {RequestId}",
                appId, scope, includeOffline, request.Id);

            var instances = _appRegistry.ListInstances(appId, scope, includeOffline);
            var instancesList = instances.ToList();

            _logger.LogInformation("成功获取应用程序实例列表，数量: {Count}, RequestId: {RequestId}", instancesList.Count, request.Id);

            var response = new JsonRpcResponse
            {
                Id = request.Id,
                Result = new
                {
                    ok = true,
                    instances = instancesList
                }
            };

            _logger.LogDebug("hub.apps.listInstances方法响应: {Response}, RequestId: {RequestId}", RpcLogJsonSerializer.Serialize(response), request.Id);
            return Task.FromResult(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "处理hub.apps.listInstances方法失败，RequestId: {RequestId}, 参数: {Params}", request.Id, RpcLogJsonSerializer.Serialize(request.Params));
            return Task.FromResult(RpcErrorFactory.InternalError(request.Id));
        }
    }

    /// <summary>
    /// 处理hub.apps.getInstance方法
    /// </summary>
    private Task<JsonRpcResponse> GetInstanceAsync(JsonRpcRequest request, CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogDebug("处理hub.apps.getInstance方法，RequestId: {RequestId}, 参数: {Params}", request.Id, RpcLogJsonSerializer.Serialize(request.Params));

            if (!RpcParamReader.TryReadParamsObject(request, out var paramsElement, out var invalidParams))
            {
                _logger.LogWarning("hub.apps.getInstance参数无效: params 不是对象, RequestId: {RequestId}", request.Id);
                return Task.FromResult(invalidParams);
            }

            if (!RpcParamReader.TryGetRequiredInstanceId(paramsElement, "instanceId", out var instanceId))
            {
                _logger.LogWarning("hub.apps.getInstance参数无效: 缺少 instanceId 或非字符串, RequestId: {RequestId}", request.Id);
                return Task.FromResult(RpcErrorFactory.InvalidParams(request.Id));
            }

            _logger.LogDebug("尝试获取应用程序实例快照，InstanceId: {InstanceId}, RequestId: {RequestId}", instanceId, request.Id);
            var instance = _appRegistry.GetInstance(instanceId);
            if (instance is null)
            {
                _logger.LogWarning("未找到应用程序实例快照，InstanceId: {InstanceId}, RequestId: {RequestId}", instanceId, request.Id);
                return Task.FromResult(InstanceNotFound(request.Id, instanceId));
            }

            _logger.LogInformation("成功获取应用程序实例快照，InstanceId: {InstanceId}, RequestId: {RequestId}", instanceId, request.Id);
            var response = new JsonRpcResponse
            {
                Id = request.Id,
                Result = new
                {
                    ok = true,
                    instance
                }
            };

            _logger.LogDebug("hub.apps.getInstance方法响应: {Response}, RequestId: {RequestId}", RpcLogJsonSerializer.Serialize(response), request.Id);
            return Task.FromResult(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "处理hub.apps.getInstance方法失败，RequestId: {RequestId}, 参数: {Params}", request.Id, RpcLogJsonSerializer.Serialize(request.Params));
            return Task.FromResult(RpcErrorFactory.InternalError(request.Id));
        }
    }

    /// <summary>
    /// 解析并校验 instance 注册参数
    /// </summary>
    private bool TryParseInstanceRegistration(
        JsonElement instanceElement,
        object? requestId,
        out AppInstance instance,
        out object? errorData)
    {
        instance = null!;
        errorData = null;

        if (instanceElement.TryGetProperty("password", out _))
        {
            _logger.LogWarning("hub.apps.registerInstance参数无效: params.instance 不得包含 password, RequestId: {RequestId}", requestId);
            return false;
        }

        if (instanceElement.TryGetProperty("instanceSessionToken", out _))
        {
            _logger.LogWarning("hub.apps.registerInstance参数无效: params.instance 不得包含 instanceSessionToken, RequestId: {RequestId}", requestId);
            return false;
        }

        if (!instanceElement.TryGetProperty("instanceId", out var instanceIdProperty) || instanceIdProperty.ValueKind != JsonValueKind.String)
        {
            _logger.LogWarning("hub.apps.registerInstance参数无效: 缺少 instanceId 或类型错误, RequestId: {RequestId}", requestId);
            return false;
        }

        var instanceId = instanceIdProperty.GetString();
        if (!ProtocolIdentifier.IsValidInstanceId(instanceId))
        {
            _logger.LogWarning("hub.apps.registerInstance参数无效: instanceId 不合法, RequestId: {RequestId}", requestId);
            return false;
        }

        if (!instanceElement.TryGetProperty("appId", out var appIdProperty) || appIdProperty.ValueKind != JsonValueKind.String)
        {
            _logger.LogWarning("hub.apps.registerInstance参数无效: 缺少 appId 或类型错误, RequestId: {RequestId}", requestId);
            return false;
        }

        var appId = appIdProperty.GetString();
        if (!ProtocolIdentifier.IsValidAppId(appId))
        {
            _logger.LogWarning("hub.apps.registerInstance参数无效: appId 不符合格式要求, RequestId: {RequestId}", requestId);
            return false;
        }

        if (!instanceElement.TryGetProperty("pid", out var pidProperty) || pidProperty.ValueKind != JsonValueKind.Number || !pidProperty.TryGetInt32(out var pid) || pid < 1)
        {
            _logger.LogWarning("hub.apps.registerInstance参数无效: pid 不合法, RequestId: {RequestId}", requestId);
            return false;
        }

        if (!RpcParamReader.TryGetRequiredScope(
                instanceElement,
                "scope",
                "invalid_scope",
                out var scope,
                out errorData))
        {
            _logger.LogWarning("hub.apps.registerInstance参数无效: scope 非法, RequestId: {RequestId}", requestId);
            return false;
        }

        if (!instanceElement.TryGetProperty("invoke", out var invokeProperty) || invokeProperty.ValueKind != JsonValueKind.Object)
        {
            _logger.LogWarning("hub.apps.registerInstance参数无效: 缺少 invoke 或类型错误, RequestId: {RequestId}", requestId);
            return false;
        }

        if (!invokeProperty.TryGetProperty("poll", out var pollProperty) ||
            !invokeProperty.TryGetProperty("respond", out var respondProperty) ||
            pollProperty.ValueKind is not JsonValueKind.True and not JsonValueKind.False ||
            respondProperty.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
        {
            _logger.LogWarning("hub.apps.registerInstance参数无效: invoke.poll/respond 必须为布尔值, RequestId: {RequestId}", requestId);
            return false;
        }

        Dictionary<string, object?>? meta = null;
        if (instanceElement.TryGetProperty("meta", out var metaProperty))
        {
            if (metaProperty.ValueKind != JsonValueKind.Object)
            {
                _logger.LogWarning("hub.apps.registerInstance参数无效: meta 必须为对象, RequestId: {RequestId}", requestId);
                return false;
            }

            meta = JsonSerializer.Deserialize<Dictionary<string, object?>>(metaProperty.GetRawText());
        }

        instance = new AppInstance
        {
            InstanceId = instanceId!,
            AppId = appId!,
            Scope = scope!,
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

    private void PublishInstanceEvent(string eventType, string appId, string instanceId, string scope)
    {
        if (_eventPublisher is null)
        {
            return;
        }

        _eventPublisher.Publish(new HubEventMessage
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

    private bool TryGetOptionalLaunchId(JsonElement paramsElement, object? requestId, out string? launchId, out object? errorData)
    {
        launchId = null;
        errorData = null;

        if (!paramsElement.TryGetProperty("launchId", out var launchIdElement))
        {
            return true;
        }

        if (launchIdElement.ValueKind != JsonValueKind.String)
        {
            _logger.LogWarning("hub.apps.registerInstance参数无效: launchId 必须为非空字符串, RequestId: {RequestId}", requestId);
            errorData = new { reason = "invalid_launch_id" };
            return false;
        }

        var value = launchIdElement.GetString();
        if (string.IsNullOrWhiteSpace(value))
        {
            _logger.LogWarning("hub.apps.registerInstance参数无效: launchId 必须为非空字符串, RequestId: {RequestId}", requestId);
            errorData = new { reason = "invalid_launch_id" };
            return false;
        }

        launchId = value;
        return true;
    }

    private static JsonRpcResponse InstanceNotFound(object? id, string instanceId)
    {
        return RpcErrorFactory.Create(id, -32010, "instance_not_found", new
        {
            reason = "unknown_instance",
            instanceId
        });
    }

    private sealed class EmptyDefinitionProvider : IDefinitionProvider
    {
        public static EmptyDefinitionProvider Instance { get; } = new();

        public void Refresh()
        {
        }

        public IReadOnlyList<AppDefinition> GetAllDefinitions()
        {
            return Array.Empty<AppDefinition>();
        }

        public AppDefinition? GetDefinition(string appId, string scope)
        {
            return null;
        }

        public bool HasDefinitions(string appId)
        {
            return false;
        }
    }

}
