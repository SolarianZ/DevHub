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
                    Message = "方法未找到"
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
                    Message = "方法未找到"
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

            var parameters = request.Params as Dictionary<string, object>;
            if (parameters == null || !parameters.TryGetValue("instance", out var instanceObj) || instanceObj == null)
            {
                _logger.Warning("hub.apps.registerInstance方法参数无效: 缺少instance, RequestId: {RequestId}", request.Id);
                return Task.FromResult(new JsonRpcResponse
                {
                    Id = request.Id,
                    Error = new JsonRpcError
                    {
                        Code = -32602,
                        Message = "无效参数"
                    }
                });
            }

            var instanceJson = JsonSerializer.Serialize(instanceObj);
            var instance = JsonSerializer.Deserialize<AppInstance>(instanceJson);

            if (instance == null || string.IsNullOrEmpty(instance.InstanceId) || string.IsNullOrEmpty(instance.AppId))
            {
                _logger.Warning("hub.apps.registerInstance方法参数无效: instance数据不完整, RequestId: {RequestId}", request.Id);
                return Task.FromResult(new JsonRpcResponse
                {
                    Id = request.Id,
                    Error = new JsonRpcError
                    {
                        Code = -32602,
                        Message = "无效参数"
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
                        Message = "无效参数: scope 不能为 'global'"
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
                    Message = "内部错误"
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

            var parameters = request.Params as Dictionary<string, object>;
            if (parameters == null || !parameters.TryGetValue("instanceId", out var instanceIdObj) || instanceIdObj == null)
            {
                _logger.Warning("hub.apps.heartbeat方法参数无效: 缺少instanceId, RequestId: {RequestId}", request.Id);
                return Task.FromResult(new JsonRpcResponse
                {
                    Id = request.Id,
                    Error = new JsonRpcError
                    {
                        Code = -32602,
                        Message = "无效参数"
                    }
                });
            }

            var instanceId = instanceIdObj?.ToString();
            if (string.IsNullOrEmpty(instanceId))
            {
                _logger.Warning("hub.apps.heartbeat方法参数无效: instanceId为空, RequestId: {RequestId}", request.Id);
                return Task.FromResult(new JsonRpcResponse
                {
                    Id = request.Id,
                    Error = new JsonRpcError
                    {
                        Code = -32602,
                        Message = "无效参数"
                    }
                });
            }

            _logger.Debug("尝试更新实例心跳，InstanceId: {InstanceId}, RequestId: {RequestId}", instanceId, request.Id);
            if (!_appRegistry.Heartbeat(instanceId))
            {
                _logger.Warning("实例心跳更新失败: 未找到实例 {InstanceId}, RequestId: {RequestId}", instanceId, request.Id);
                return Task.FromResult(new JsonRpcResponse
                {
                    Id = request.Id,
                    Error = new JsonRpcError
                    {
                        Code = -32010,
                        Message = "实例未找到"
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
                    serverTimeUtc = DateTime.UtcNow.ToString("O")
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
                    Message = "内部错误"
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

            var parameters = request.Params as Dictionary<string, object>;
            if (parameters == null || !parameters.TryGetValue("instanceId", out var instanceIdObj) || instanceIdObj == null)
            {
                _logger.Warning("hub.apps.unregisterInstance方法参数无效: 缺少instanceId, RequestId: {RequestId}", request.Id);
                return Task.FromResult(new JsonRpcResponse
                {
                    Id = request.Id,
                    Error = new JsonRpcError
                    {
                        Code = -32602,
                        Message = "无效参数"
                    }
                });
            }

            var instanceId = instanceIdObj?.ToString();
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
                    Message = "内部错误"
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

            var parameters = request.Params as Dictionary<string, object>;
            string? appId = null;
            string? scope = null;
            bool includeAllScopes = false;

            if (parameters != null)
            {
                if (parameters.TryGetValue("appId", out var appIdObj) && appIdObj != null)
                {
                    appId = appIdObj.ToString();
                }

                if (parameters.TryGetValue("scope", out var scopeObj))
                {
                    scope = scopeObj?.ToString();
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
                                Message = "无效参数: scope 不能为 'global'"
                            }
                        });
                    }
                }

                if (parameters.TryGetValue("includeAllScopes", out var includeAllScopesObj) && includeAllScopesObj is bool value)
                {
                    includeAllScopes = value;
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
                    Message = "内部错误"
                }
            });
        }
    }
}
