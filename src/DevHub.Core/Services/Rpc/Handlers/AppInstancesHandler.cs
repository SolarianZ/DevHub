using DevHub.Core.Models;
using DevHub.Core.Models.Rpc;
using System.Text.Json;
using DevHub.Core.Services;

namespace DevHub.Core.Services.Rpc.Handlers;

/// <summary>
/// 处理应用程序实例相关方法的RPC处理器
/// </summary>
public class AppInstancesHandler : IRpcHandler
{
    private readonly AppRegistry _appRegistry;
    private readonly ILoggerService _logger;

    public AppInstancesHandler(AppRegistry appRegistry, ILoggerService logger)
    {
        _appRegistry = appRegistry;
        _logger = logger;
    }

    public string Method => "hub.apps";

    public async Task<JsonRpcResponse> HandleAsync(JsonRpcRequest request, CancellationToken cancellationToken)
    {
        _logger.Debug("收到应用程序实例相关RPC请求: {Method}, RequestId: {RequestId}", request.Method, request.Id);

        var methodParts = request.Method.Split('.');
        if (methodParts.Length < 3)
        {
            _logger.Warning("RPC方法格式无效: {Method}, RequestId: {RequestId}", request.Method, request.Id);
            return new JsonRpcResponse
            {
                Id = request.Id,
                Error = new JsonRpcError
                {
                    Code = -32601,
                    Message = "method_not_found"
                }
            };
        }

        var subMethod = methodParts[2];

        return subMethod switch
        {
            "registerInstance" => await RegisterInstanceAsync(request, cancellationToken),
            "heartbeat" => await HeartbeatAsync(request, cancellationToken),
            "unregisterInstance" => await UnregisterInstanceAsync(request, cancellationToken),
            "listInstances" => await ListInstancesAsync(request, cancellationToken),
            _ => new JsonRpcResponse
            {
                Id = request.Id,
                Error = new JsonRpcError
                {
                    Code = -32601,
                    Message = "method_not_found"
                }
            }
        };
    }

    /// <summary>
    /// 处理hub.apps.registerInstance方法
    /// </summary>
    private Task<JsonRpcResponse> RegisterInstanceAsync(JsonRpcRequest request, CancellationToken cancellationToken)
    {
        try
        {
            _logger.Debug("处理hub.apps.registerInstance方法，RequestId: {RequestId}, 参数: {Params}", request.Id, JsonSerializer.Serialize(request.Params));

            if (request.Params is not JsonElement paramsElement || paramsElement.ValueKind != JsonValueKind.Object)
            {
                _logger.Warning("hub.apps.registerInstance方法参数无效: 缺少参数或参数不是对象, RequestId: {RequestId}", request.Id);
                return Task.FromResult(new JsonRpcResponse
                {
                    Id = request.Id,
                    Error = new JsonRpcError
                    {
                        Code = -32602,
                        Message = "invalid_params"
                    }
                });
            }

            if (!paramsElement.TryGetProperty("instance", out var instanceProperty) || instanceProperty.ValueKind != JsonValueKind.Object)
            {
                _logger.Warning("hub.apps.registerInstance方法参数无效: 缺少instance或instance不是对象, RequestId: {RequestId}", request.Id);
                return Task.FromResult(new JsonRpcResponse
                {
                    Id = request.Id,
                    Error = new JsonRpcError
                    {
                        Code = -32602,
                        Message = "invalid_params"
                    }
                });
            }

            var instance = JsonSerializer.Deserialize<AppInstance>(instanceProperty.GetRawText());

            if (instance == null || string.IsNullOrEmpty(instance.InstanceId) || string.IsNullOrEmpty(instance.AppId))
            {
                _logger.Warning("hub.apps.registerInstance方法参数无效: instance数据不完整, RequestId: {RequestId}", request.Id);
                return Task.FromResult(new JsonRpcResponse
                {
                    Id = request.Id,
                    Error = new JsonRpcError
                    {
                        Code = -32602,
                        Message = "invalid_params"
                    }
                });
            }

            // 验证 scope 参数
            if (instance.Scope == "global")
            {
                _logger.Warning("hub.apps.registerInstance方法参数无效: scope 不能为 'global', RequestId: {RequestId}", request.Id);
                return Task.FromResult(new JsonRpcResponse
                {
                    Id = request.Id,
                    Error = new JsonRpcError
                    {
                        Code = -32602,
                        Message = "invalid_params",
                        Data = new { reason = "scope_cannot_be_global" }
                    }
                });
            }

            _logger.Debug("尝试注册应用程序实例，InstanceId: {InstanceId}, AppId: {AppId}, Scope: {Scope}, PID: {PID}, RequestId: {RequestId}",
                instance.InstanceId, instance.AppId, instance.Scope, instance.Pid, request.Id);
            _appRegistry.RegisterInstance(instance);
            _logger.Information("成功注册应用程序实例，InstanceId: {InstanceId}, AppId: {AppId}, Scope: {Scope}, PID: {PID}, RequestId: {RequestId}",
                instance.InstanceId, instance.AppId, instance.Scope, instance.Pid, request.Id);

            var response = new JsonRpcResponse
            {
                Id = request.Id,
                Result = new
                {
                    ok = true
                }
            };

            _logger.Debug("hub.apps.registerInstance方法响应: {Response}, RequestId: {RequestId}", JsonSerializer.Serialize(response), request.Id);
            return Task.FromResult(response);
        }
        catch (Exception ex)
        {
            _logger?.Error(ex, "处理hub.apps.registerInstance方法失败，RequestId: {RequestId}, 参数: {Params}", request.Id, JsonSerializer.Serialize(request.Params));
            return Task.FromResult(new JsonRpcResponse
            {
                Id = request.Id,
                Error = new JsonRpcError
                {
                    Code = -32603,
                    Message = "internal_error"
                }
            });
        }
    }

    /// <summary>
    /// 处理hub.apps.heartbeat方法
    /// </summary>
    private Task<JsonRpcResponse> HeartbeatAsync(JsonRpcRequest request, CancellationToken cancellationToken)
    {
        try
        {
            _logger.Debug("处理hub.apps.heartbeat方法，RequestId: {RequestId}, 参数: {Params}", request.Id, JsonSerializer.Serialize(request.Params));

            if (request.Params is not JsonElement paramsElement || paramsElement.ValueKind != JsonValueKind.Object)
            {
                _logger.Warning("hub.apps.heartbeat方法参数无效: 缺少参数或参数不是对象, RequestId: {RequestId}", request.Id);
                return Task.FromResult(new JsonRpcResponse
                {
                    Id = request.Id,
                    Error = new JsonRpcError
                    {
                        Code = -32602,
                        Message = "invalid_params"
                    }
                });
            }

            if (!paramsElement.TryGetProperty("instanceId", out var instanceIdProperty) || instanceIdProperty.ValueKind != JsonValueKind.String)
            {
                _logger.Warning("hub.apps.heartbeat方法参数无效: 缺少instanceId或instanceId不是字符串, RequestId: {RequestId}", request.Id);
                return Task.FromResult(new JsonRpcResponse
                {
                    Id = request.Id,
                    Error = new JsonRpcError
                    {
                        Code = -32602,
                        Message = "invalid_params"
                    }
                });
            }

            var instanceId = instanceIdProperty.GetString();
            if (string.IsNullOrEmpty(instanceId))
            {
                _logger.Warning("hub.apps.heartbeat方法参数无效: instanceId为空, RequestId: {RequestId}", request.Id);
                return Task.FromResult(new JsonRpcResponse
                {
                    Id = request.Id,
                    Error = new JsonRpcError
                    {
                        Code = -32602,
                        Message = "invalid_params"
                    }
                });
            }

            _logger.Debug("尝试更新实例心跳，InstanceId: {InstanceId}, RequestId: {RequestId}", instanceId, request.Id);
            if (!_appRegistry.Heartbeat(instanceId, out var lastSeenUtc))
            {
                _logger.Warning("实例心跳更新失败: 未找到实例 {InstanceId}, RequestId: {RequestId}", instanceId, request.Id);
                return Task.FromResult(new JsonRpcResponse
                {
                    Id = request.Id,
                    Error = new JsonRpcError
                    {
                        Code = -32010,
                        Message = "instance_not_found",
                        Data = new { instanceId = instanceId }
                    }
                });
            }

            _logger.Information("实例心跳更新成功，InstanceId: {InstanceId}, RequestId: {RequestId}", instanceId, request.Id);
            var response = new JsonRpcResponse
            {
                Id = request.Id,
                Result = new
                {
                    ok = true,
                    lastSeenUtc = lastSeenUtc.ToString("O")
                }
            };

            _logger.Debug("hub.apps.heartbeat方法响应: {Response}, RequestId: {RequestId}", JsonSerializer.Serialize(response), request.Id);
            return Task.FromResult(response);
        }
        catch (Exception ex)
        {
            _logger?.Error(ex, "处理hub.apps.heartbeat方法失败，RequestId: {RequestId}, 参数: {Params}", request.Id, JsonSerializer.Serialize(request.Params));
            return Task.FromResult(new JsonRpcResponse
            {
                Id = request.Id,
                Error = new JsonRpcError
                {
                    Code = -32603,
                    Message = "internal_error"
                }
            });
        }
    }

    /// <summary>
    /// 处理hub.apps.unregisterInstance方法
    /// </summary>
    private Task<JsonRpcResponse> UnregisterInstanceAsync(JsonRpcRequest request, CancellationToken cancellationToken)
    {
        try
        {
            _logger.Debug("处理hub.apps.unregisterInstance方法，RequestId: {RequestId}, 参数: {Params}", request.Id, JsonSerializer.Serialize(request.Params));

            if (request.Params is not JsonElement paramsElement || paramsElement.ValueKind != JsonValueKind.Object)
            {
                _logger.Warning("hub.apps.unregisterInstance方法参数无效: 缺少参数或参数不是对象, RequestId: {RequestId}", request.Id);
                return Task.FromResult(new JsonRpcResponse
                {
                    Id = request.Id,
                    Error = new JsonRpcError
                    {
                        Code = -32602,
                        Message = "invalid_params"
                    }
                });
            }

            if (!paramsElement.TryGetProperty("instanceId", out var instanceIdProperty) || instanceIdProperty.ValueKind != JsonValueKind.String)
            {
                _logger.Warning("hub.apps.unregisterInstance方法参数无效: 缺少instanceId或instanceId不是字符串, RequestId: {RequestId}", request.Id);
                return Task.FromResult(new JsonRpcResponse
                {
                    Id = request.Id,
                    Error = new JsonRpcError
                    {
                        Code = -32602,
                        Message = "invalid_params"
                    }
                });
            }

            var instanceId = instanceIdProperty.GetString();
            if (!string.IsNullOrEmpty(instanceId))
            {
                _logger.Debug("尝试注销应用程序实例，InstanceId: {InstanceId}, RequestId: {RequestId}", instanceId, request.Id);
                _appRegistry.UnregisterInstance(instanceId);
                _logger.Information("成功注销应用程序实例，InstanceId: {InstanceId}, RequestId: {RequestId}", instanceId, request.Id);
            }

            var response = new JsonRpcResponse
            {
                Id = request.Id,
                Result = new
                {
                    ok = true
                }
            };

            _logger.Debug("hub.apps.unregisterInstance方法响应: {Response}, RequestId: {RequestId}", JsonSerializer.Serialize(response), request.Id);
            return Task.FromResult(response);
        }
        catch (Exception ex)
        {
            _logger?.Error(ex, "处理hub.apps.unregisterInstance方法失败，RequestId: {RequestId}, 参数: {Params}", request.Id, JsonSerializer.Serialize(request.Params));
            return Task.FromResult(new JsonRpcResponse
            {
                Id = request.Id,
                Error = new JsonRpcError
                {
                    Code = -32603,
                    Message = "internal_error"
                }
            });
        }
    }

    /// <summary>
    /// 处理hub.apps.listInstances方法
    /// </summary>
    private Task<JsonRpcResponse> ListInstancesAsync(JsonRpcRequest request, CancellationToken cancellationToken)
    {
        try
        {
            _logger.Debug("处理hub.apps.listInstances方法，RequestId: {RequestId}, 参数: {Params}", request.Id, JsonSerializer.Serialize(request.Params));

            string? appId = null;
            string? scope = null;
            bool includeAllScopes = false;

            if (request.Params is JsonElement paramsElement && paramsElement.ValueKind == JsonValueKind.Object)
            {
                if (paramsElement.TryGetProperty("appId", out var appIdProperty) && appIdProperty.ValueKind == JsonValueKind.String)
                {
                    appId = appIdProperty.GetString();
                }

                if (paramsElement.TryGetProperty("scope", out var scopeProperty))
                {
                    if (scopeProperty.ValueKind == JsonValueKind.String)
                    {
                        scope = scopeProperty.GetString();
                    }
                    else if (scopeProperty.ValueKind == JsonValueKind.Null)
                    {
                        scope = null;
                    }

                    // 验证 scope 参数
                    if (scope == "global")
                    {
                        _logger.Warning("hub.apps.listInstances方法参数无效: scope 不能为 'global', RequestId: {RequestId}", request.Id);
                        return Task.FromResult(new JsonRpcResponse
                        {
                            Id = request.Id,
                            Error = new JsonRpcError
                            {
                                Code = -32602,
                                Message = "invalid_params",
                                Data = new { reason = "scope_cannot_be_global" }
                            }
                        });
                    }
                }

                if (paramsElement.TryGetProperty("includeAllScopes", out var includeAllScopesProperty) && includeAllScopesProperty.ValueKind == JsonValueKind.True || includeAllScopesProperty.ValueKind == JsonValueKind.False)
                {
                    includeAllScopes = includeAllScopesProperty.GetBoolean();
                }
            }

            _logger.Debug("尝试获取应用程序实例列表，AppId: {AppId}, Scope: {Scope}, IncludeAllScopes: {IncludeAllScopes}, RequestId: {RequestId}",
                appId, scope, includeAllScopes, request.Id);
            var instances = _appRegistry.ListInstances(appId, scope, includeAllScopes);

            var instancesList = instances.ToList();
            _logger.Information("成功获取应用程序实例列表，数量: {Count}, RequestId: {RequestId}", instancesList.Count, request.Id);

            var response = new JsonRpcResponse
            {
                Id = request.Id,
                Result = new
                {
                    ok = true,
                    instances = instancesList
                }
            };

            _logger.Debug("hub.apps.listInstances方法响应: {Response}, RequestId: {RequestId}", JsonSerializer.Serialize(response), request.Id);
            return Task.FromResult(response);
        }
        catch (Exception ex)
        {
            _logger?.Error(ex, "处理hub.apps.listInstances方法失败，RequestId: {RequestId}, 参数: {Params}", request.Id, JsonSerializer.Serialize(request.Params));
            return Task.FromResult(new JsonRpcResponse
            {
                Id = request.Id,
                Error = new JsonRpcError
                {
                    Code = -32603,
                    Message = "internal_error"
                }
            });
        }
    }
}
