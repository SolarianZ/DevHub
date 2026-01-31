using DevHub.Core.Models;
using DevHub.Core.Models.Rpc;
using System.Text.Json;
using DevHub.Core.Services;

namespace DevHub.Core.Services.Rpc.Handlers;

/// <summary>
/// 处理应用程序定义相关方法的RPC处理器
/// </summary>
public class AppDefinitionsHandler : IRpcHandler
{
    private readonly DefinitionLoader _definitionLoader;
    private readonly ILoggerService _logger;

    public AppDefinitionsHandler(DefinitionLoader definitionLoader, ILoggerService logger)
    {
        _definitionLoader = definitionLoader;
        _logger = logger;
    }

    public string Method => "hub.apps";

    public async Task<JsonRpcResponse> HandleAsync(JsonRpcRequest request, CancellationToken cancellationToken)
    {
        _logger.Debug("收到应用程序定义相关RPC请求: {Method}, RequestId: {RequestId}", request.Method, request.Id);

        // 根据具体方法路由到不同处理逻辑
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
            "listDefinitions" => await ListDefinitionsAsync(request, cancellationToken),
            "getDefinition" => await GetDefinitionAsync(request, cancellationToken),
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
    /// 处理hub.apps.listDefinitions方法
    /// </summary>
    private Task<JsonRpcResponse> ListDefinitionsAsync(JsonRpcRequest request, CancellationToken cancellationToken)
    {
        try
        {
            _logger.Debug("处理hub.apps.listDefinitions方法，RequestId: {RequestId}, 参数: {Params}", request.Id, JsonSerializer.Serialize(request.Params));
            var definitions = _definitionLoader.GetAllDefinitions();
            _logger.Information("成功获取应用程序定义列表，数量: {Count}, RequestId: {RequestId}", definitions.Count, request.Id);

            var response = new JsonRpcResponse
            {
                Id = request.Id,
                Result = new
                {
                    ok = true,
                    definitions = definitions
                }
            };

            _logger.Debug("hub.apps.listDefinitions方法响应: {Response}, RequestId: {RequestId}", JsonSerializer.Serialize(response), request.Id);
            return Task.FromResult(response);
        }
        catch (Exception ex)
        {
            _logger?.Error(ex, "处理hub.apps.listDefinitions方法失败，RequestId: {RequestId}, 参数: {Params}", request.Id, JsonSerializer.Serialize(request.Params));
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
    /// 处理hub.apps.getDefinition方法
    /// </summary>
    private Task<JsonRpcResponse> GetDefinitionAsync(JsonRpcRequest request, CancellationToken cancellationToken)
    {
        try
        {
            _logger.Debug("处理hub.apps.getDefinition方法，RequestId: {RequestId}, 参数: {Params}", request.Id, JsonSerializer.Serialize(request.Params));

            // 解析参数
            var parameters = request.Params as Dictionary<string, object>;
            if (parameters == null || !parameters.TryGetValue("appId", out var appIdObj) || appIdObj == null)
            {
                _logger.Warning("hub.apps.getDefinition方法参数无效: 缺少appId, RequestId: {RequestId}", request.Id);
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

            var appId = appIdObj?.ToString();
            if (string.IsNullOrEmpty(appId))
            {
                _logger.Warning("hub.apps.getDefinition方法参数无效: appId为空, RequestId: {RequestId}", request.Id);
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

            _logger.Debug("尝试获取应用程序定义，AppId: {AppId}, RequestId: {RequestId}", appId, request.Id);
            var definition = _definitionLoader.GetDefinition(appId);

            if (definition == null)
            {
                _logger.Warning("未找到应用程序定义，AppId: {AppId}, RequestId: {RequestId}", appId, request.Id);
                return Task.FromResult(new JsonRpcResponse
                {
                    Id = request.Id,
                    Error = new JsonRpcError
                    {
                        Code = -32014,
                        Message = "app_definition_not_found",
                        Data = new { appId = appId }
                    }
                });
            }

            _logger.Information("成功获取应用程序定义，AppId: {AppId}, RequestId: {RequestId}", appId, request.Id);
            var response = new JsonRpcResponse
            {
                Id = request.Id,
                Result = new
                {
                    ok = true,
                    definition = definition
                }
            };

            _logger.Debug("hub.apps.getDefinition方法响应: {Response}, RequestId: {RequestId}", JsonSerializer.Serialize(response), request.Id);
            return Task.FromResult(response);
        }
        catch (Exception ex)
        {
            _logger?.Error(ex, "处理hub.apps.getDefinition方法失败，RequestId: {RequestId}, 参数: {Params}", request.Id, JsonSerializer.Serialize(request.Params));
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
