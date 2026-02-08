using DevHub.Core.Models;
using DevHub.Core.Models.Rpc;
using DevHub.Core.Services;
using DevHub.Core.Services.Events;
using DevHub.Core.Services.Rpc;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DevHub.Core.Services.Rpc.Handlers;

/// <summary>
/// 处理应用程序实例相关方法的RPC处理器
/// </summary>
public class AppInstancesHandler : IRpcHandler
{
    private static readonly Regex InstanceIdPattern = new("^[a-zA-Z0-9._:-]+$", RegexOptions.Compiled);

    private readonly AppRegistry _appRegistry;
    private readonly HubEventBus? _eventBus;
    private readonly ILogger<AppInstancesHandler> _logger;

    /// <summary>
    /// 初始化应用实例 RPC 处理器。
    /// </summary>
    /// <param name="appRegistry">应用实例注册表。</param>
    /// <param name="logger">日志记录器。</param>
    /// <param name="eventBus">Hub 事件总线。</param>
    public AppInstancesHandler(AppRegistry appRegistry, ILogger<AppInstancesHandler> logger, HubEventBus? eventBus = null)
    {
        _appRegistry = appRegistry;
        _eventBus = eventBus;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Method => "hub.apps";

    /// <inheritdoc />
    public async Task<JsonRpcResponse> HandleAsync(JsonRpcRequest request, CancellationToken cancellationToken)
    {
        _logger.LogDebug("收到应用程序实例相关RPC请求: {Method}, RequestId: {RequestId}", request.Method, request.Id);

        return request.Method switch
        {
            "hub.apps.registerInstance" => await RegisterInstanceAsync(request, cancellationToken),
            "hub.apps.heartbeat" => await HeartbeatAsync(request, cancellationToken),
            "hub.apps.unregisterInstance" => await UnregisterInstanceAsync(request, cancellationToken),
            "hub.apps.listInstances" => await ListInstancesAsync(request, cancellationToken),
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
            _logger.LogDebug("处理hub.apps.registerInstance方法，RequestId: {RequestId}, 参数: {Params}", request.Id, JsonSerializer.Serialize(request.Params));

            if (request.Params is not JsonElement paramsElement || paramsElement.ValueKind != JsonValueKind.Object)
            {
                _logger.LogWarning("hub.apps.registerInstance参数无效: params 不是对象, RequestId: {RequestId}", request.Id);
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

            var registeredInstance = _appRegistry.RegisterInstance(instance);

            PublishInstanceEvent("app.instance.registered", registeredInstance.AppId, registeredInstance.InstanceId, registeredInstance.Scope);

            _logger.LogInformation("成功注册应用程序实例，InstanceId: {InstanceId}, AppId: {AppId}, Scope: {Scope}, PID: {PID}, RequestId: {RequestId}",
                registeredInstance.InstanceId, registeredInstance.AppId, registeredInstance.Scope, registeredInstance.Pid, request.Id);

            var response = new JsonRpcResponse
            {
                Id = request.Id,
                Result = new
                {
                    ok = true,
                    instance = registeredInstance
                }
            };

            _logger.LogDebug("hub.apps.registerInstance方法响应: {Response}, RequestId: {RequestId}", JsonSerializer.Serialize(response), request.Id);
            return Task.FromResult(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "处理hub.apps.registerInstance方法失败，RequestId: {RequestId}, 参数: {Params}", request.Id, JsonSerializer.Serialize(request.Params));
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
            _logger.LogDebug("处理hub.apps.heartbeat方法，RequestId: {RequestId}, 参数: {Params}", request.Id, JsonSerializer.Serialize(request.Params));

            if (request.Params is not JsonElement paramsElement || paramsElement.ValueKind != JsonValueKind.Object)
            {
                _logger.LogWarning("hub.apps.heartbeat参数无效: params 不是对象, RequestId: {RequestId}", request.Id);
                return Task.FromResult(RpcErrorFactory.InvalidParams(request.Id));
            }

            if (!paramsElement.TryGetProperty("instanceId", out var instanceIdProperty) || instanceIdProperty.ValueKind != JsonValueKind.String)
            {
                _logger.LogWarning("hub.apps.heartbeat参数无效: 缺少 instanceId 或非字符串, RequestId: {RequestId}", request.Id);
                return Task.FromResult(RpcErrorFactory.InvalidParams(request.Id));
            }

            var instanceId = instanceIdProperty.GetString();
            if (string.IsNullOrWhiteSpace(instanceId))
            {
                _logger.LogWarning("hub.apps.heartbeat参数无效: instanceId 为空, RequestId: {RequestId}", request.Id);
                return Task.FromResult(RpcErrorFactory.InvalidParams(request.Id));
            }

            _logger.LogDebug("尝试更新实例心跳，InstanceId: {InstanceId}, RequestId: {RequestId}", instanceId, request.Id);
            if (!_appRegistry.Heartbeat(instanceId, out var lastSeenUtc))
            {
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

            _logger.LogDebug("hub.apps.heartbeat方法响应: {Response}, RequestId: {RequestId}", JsonSerializer.Serialize(response), request.Id);
            return Task.FromResult(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "处理hub.apps.heartbeat方法失败，RequestId: {RequestId}, 参数: {Params}", request.Id, JsonSerializer.Serialize(request.Params));
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
            _logger.LogDebug("处理hub.apps.unregisterInstance方法，RequestId: {RequestId}, 参数: {Params}", request.Id, JsonSerializer.Serialize(request.Params));

            if (request.Params is not JsonElement paramsElement || paramsElement.ValueKind != JsonValueKind.Object)
            {
                _logger.LogWarning("hub.apps.unregisterInstance参数无效: params 不是对象, RequestId: {RequestId}", request.Id);
                return Task.FromResult(RpcErrorFactory.InvalidParams(request.Id));
            }

            if (!paramsElement.TryGetProperty("instanceId", out var instanceIdProperty) || instanceIdProperty.ValueKind != JsonValueKind.String)
            {
                _logger.LogWarning("hub.apps.unregisterInstance参数无效: 缺少 instanceId 或非字符串, RequestId: {RequestId}", request.Id);
                return Task.FromResult(RpcErrorFactory.InvalidParams(request.Id));
            }

            var instanceId = instanceIdProperty.GetString();
            if (string.IsNullOrWhiteSpace(instanceId))
            {
                _logger.LogWarning("hub.apps.unregisterInstance参数无效: instanceId 为空, RequestId: {RequestId}", request.Id);
                return Task.FromResult(RpcErrorFactory.InvalidParams(request.Id));
            }

            var existingInstance = _appRegistry.GetInstance(instanceId);
            _logger.LogDebug("尝试注销应用程序实例，InstanceId: {InstanceId}, RequestId: {RequestId}", instanceId, request.Id);
            var removed = _appRegistry.UnregisterInstance(instanceId);
            if (removed && existingInstance is not null)
            {
                PublishInstanceEvent("app.instance.unregistered", existingInstance.AppId, existingInstance.InstanceId, existingInstance.Scope);
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

            _logger.LogDebug("hub.apps.unregisterInstance方法响应: {Response}, RequestId: {RequestId}", JsonSerializer.Serialize(response), request.Id);
            return Task.FromResult(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "处理hub.apps.unregisterInstance方法失败，RequestId: {RequestId}, 参数: {Params}", request.Id, JsonSerializer.Serialize(request.Params));
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
            _logger.LogDebug("处理hub.apps.listInstances方法，RequestId: {RequestId}, 参数: {Params}", request.Id, JsonSerializer.Serialize(request.Params));

            string? appId = null;
            string? scope = null;
            var includeAllScopes = false;
            var includeOffline = false;

            if (request.Params is JsonElement paramsElement)
            {
                if (paramsElement.ValueKind != JsonValueKind.Object)
                {
                    _logger.LogWarning("hub.apps.listInstances参数无效: params 不是对象, RequestId: {RequestId}", request.Id);
                    return Task.FromResult(RpcErrorFactory.InvalidParams(request.Id));
                }

                if (paramsElement.TryGetProperty("appId", out var appIdProperty))
                {
                    if (appIdProperty.ValueKind != JsonValueKind.String)
                    {
                        _logger.LogWarning("hub.apps.listInstances参数无效: appId 不是字符串, RequestId: {RequestId}", request.Id);
                        return Task.FromResult(RpcErrorFactory.InvalidParams(request.Id));
                    }

                    appId = appIdProperty.GetString();
                }

                if (!RpcParamReader.TryGetOptionalScope(
                        paramsElement,
                        "scope",
                        "invalid_scope",
                        out scope,
                        out var scopeErrorData))
                {
                    _logger.LogWarning("hub.apps.listInstances参数无效: scope 非法, RequestId: {RequestId}", request.Id);
                    return Task.FromResult(RpcErrorFactory.Create(request.Id, -32602, "invalid_params", scopeErrorData));
                }

                if (paramsElement.TryGetProperty("includeAllScopes", out var includeAllScopesProperty))
                {
                    if (includeAllScopesProperty.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
                    {
                        _logger.LogWarning("hub.apps.listInstances参数无效: includeAllScopes 必须为布尔值, RequestId: {RequestId}", request.Id);
                        return Task.FromResult(RpcErrorFactory.InvalidParams(request.Id));
                    }

                    includeAllScopes = includeAllScopesProperty.GetBoolean();
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
            }

            _logger.LogDebug("尝试获取应用程序实例列表，AppId: {AppId}, Scope: {Scope}, IncludeAllScopes: {IncludeAllScopes}, IncludeOffline: {IncludeOffline}, RequestId: {RequestId}",
                appId, scope, includeAllScopes, includeOffline, request.Id);

            var instances = _appRegistry.ListInstances(appId, scope, includeAllScopes, includeOffline);
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

            _logger.LogDebug("hub.apps.listInstances方法响应: {Response}, RequestId: {RequestId}", JsonSerializer.Serialize(response), request.Id);
            return Task.FromResult(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "处理hub.apps.listInstances方法失败，RequestId: {RequestId}, 参数: {Params}", request.Id, JsonSerializer.Serialize(request.Params));
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

        if (!instanceElement.TryGetProperty("instanceId", out var instanceIdProperty) || instanceIdProperty.ValueKind != JsonValueKind.String)
        {
            _logger.LogWarning("hub.apps.registerInstance参数无效: 缺少 instanceId 或类型错误, RequestId: {RequestId}", requestId);
            return false;
        }

        var instanceId = instanceIdProperty.GetString();
        if (string.IsNullOrWhiteSpace(instanceId) || instanceId.Length > 256 || !InstanceIdPattern.IsMatch(instanceId))
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
        if (string.IsNullOrWhiteSpace(appId))
        {
            _logger.LogWarning("hub.apps.registerInstance参数无效: appId 为空, RequestId: {RequestId}", requestId);
            return false;
        }

        if (!instanceElement.TryGetProperty("pid", out var pidProperty) || pidProperty.ValueKind != JsonValueKind.Number || !pidProperty.TryGetInt32(out var pid) || pid < 1)
        {
            _logger.LogWarning("hub.apps.registerInstance参数无效: pid 不合法, RequestId: {RequestId}", requestId);
            return false;
        }

        if (!RpcParamReader.TryGetOptionalScope(
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
            }
        };

        return true;
    }

    private void PublishInstanceEvent(string eventType, string appId, string instanceId, string? scope)
    {
        if (_eventBus is null)
        {
            return;
        }

        _eventBus.Publish(new HubEventMessage
        {
            Type = eventType,
            TimeUtc = DateTime.UtcNow,
            Payload = new
            {
                appId,
                instanceId,
                scope
            }
        });
    }

}
