using DevHub.Core.Models;
using DevHub.Core.Models.Rpc;
using System.Text.Json;

namespace DevHub.Core.Services.Rpc.Handlers;

/// <summary>
/// 处理应用程序实例相关方法的RPC处理器
/// </summary>
public class AppInstancesHandler : IRpcHandler
{
    private readonly AppRegistry _appRegistry;

    public AppInstancesHandler(AppRegistry appRegistry)
    {
        _appRegistry = appRegistry;
    }

    public string Method => "hub.apps";

    public async Task<JsonRpcResponse> HandleAsync(JsonRpcRequest request, CancellationToken cancellationToken)
    {
        var methodParts = request.Method.Split('.');
        if (methodParts.Length < 3)
        {
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
            var parameters = request.Params as Dictionary<string, object>;
            if (parameters == null || !parameters.TryGetValue("instance", out var instanceObj) || instanceObj == null)
            {
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

            _appRegistry.RegisterInstance(instance);

            var response = new JsonRpcResponse
            {
                Id = request.Id,
                Result = new
                {
                    ok = true
                }
            };

            return Task.FromResult(response);
        }
        catch (Exception ex)
        {
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
            var parameters = request.Params as Dictionary<string, object>;
            if (parameters == null || !parameters.TryGetValue("instanceId", out var instanceIdObj) || instanceIdObj == null)
            {
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

            var instanceId = instanceIdObj.ToString();

            if (!_appRegistry.Heartbeat(instanceId))
            {
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

            var response = new JsonRpcResponse
            {
                Id = request.Id,
                Result = new
                {
                    ok = true,
                    serverTimeUtc = DateTime.UtcNow.ToString("O")
                }
            };

            return Task.FromResult(response);
        }
        catch (Exception ex)
        {
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
            var parameters = request.Params as Dictionary<string, object>;
            if (parameters == null || !parameters.TryGetValue("instanceId", out var instanceIdObj) || instanceIdObj == null)
            {
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

            var instanceId = instanceIdObj.ToString();
            _appRegistry.UnregisterInstance(instanceId);

            var response = new JsonRpcResponse
            {
                Id = request.Id,
                Result = new
                {
                    ok = true
                }
            };

            return Task.FromResult(response);
        }
        catch (Exception ex)
        {
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
                }

                if (parameters.TryGetValue("includeAllScopes", out var includeAllScopesObj) && includeAllScopesObj is bool value)
                {
                    includeAllScopes = value;
                }
            }

            var instances = _appRegistry.ListInstances(appId, scope, includeAllScopes);

            var response = new JsonRpcResponse
            {
                Id = request.Id,
                Result = new
                {
                    instances = instances
                }
            };

            return Task.FromResult(response);
        }
        catch (Exception ex)
        {
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
