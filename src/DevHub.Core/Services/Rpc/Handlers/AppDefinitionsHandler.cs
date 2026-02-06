using DevHub.Core.Models;
using DevHub.Core.Models.Rpc;
using DevHub.Core.Services;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace DevHub.Core.Services.Rpc.Handlers;

/// <summary>
/// 处理应用程序定义相关方法的RPC处理器
/// </summary>
public class AppDefinitionsHandler : IRpcHandler
{
    private readonly DefinitionLoader _definitionLoader;
    private readonly ILogger<AppDefinitionsHandler> _logger;

    public AppDefinitionsHandler(DefinitionLoader definitionLoader, ILogger<AppDefinitionsHandler> logger)
    {
        _definitionLoader = definitionLoader;
        _logger = logger;
    }

    public string Method => "hub.apps";

    public async Task<JsonRpcResponse> HandleAsync(JsonRpcRequest request, CancellationToken cancellationToken)
    {
        _logger.LogDebug("收到应用程序定义相关RPC请求: {Method}, RequestId: {RequestId}", request.Method, request.Id);

        // 根据具体方法路由到不同处理逻辑
        var methodParts = request.Method.Split('.');

        if (methodParts.Length < 3)
        {
            _logger.LogWarning("RPC方法格式无效: {Method}, RequestId: {RequestId}", request.Method, request.Id);
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
            _logger.LogDebug("处理hub.apps.listDefinitions方法，RequestId: {RequestId}, 参数: {Params}", request.Id, JsonSerializer.Serialize(request.Params));

            // 每次查询前重新加载，反映测试期间新增/修改的定义文件
            _definitionLoader.Load();

            var definitions = _definitionLoader.GetAllDefinitions();
            _logger.LogInformation("成功获取应用程序定义列表，数量: {Count}, RequestId: {RequestId}", definitions.Count, request.Id);

            var response = new JsonRpcResponse
            {
                Id = request.Id,
                Result = new
                {
                    ok = true,
                    definitions = definitions
                }
            };

            _logger.LogDebug("hub.apps.listDefinitions方法响应: {Response}, RequestId: {RequestId}", JsonSerializer.Serialize(response), request.Id);
            return Task.FromResult(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "处理hub.apps.listDefinitions方法失败，RequestId: {RequestId}, 参数: {Params}", request.Id, JsonSerializer.Serialize(request.Params));
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
            _logger.LogDebug("处理hub.apps.getDefinition方法，RequestId: {RequestId}, 参数: {Params}", request.Id, JsonSerializer.Serialize(request.Params));

            // 每次查询前重新加载，反映测试期间新增/修改的定义文件
            _definitionLoader.Load();

            // 解析参数
            if (request.Params is not JsonElement paramsElement || paramsElement.ValueKind != JsonValueKind.Object)
            {
                _logger.LogWarning("hub.apps.getDefinition方法参数无效: 缺少参数或参数不是对象, RequestId: {RequestId}", request.Id);
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

            if (!paramsElement.TryGetProperty("appId", out var appIdProperty) || appIdProperty.ValueKind != JsonValueKind.String)
            {
                _logger.LogWarning("hub.apps.getDefinition方法参数无效: 缺少appId或appId不是字符串, RequestId: {RequestId}", request.Id);
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

            var appId = appIdProperty.GetString();
            if (string.IsNullOrWhiteSpace(appId))
            {
                _logger.LogWarning("hub.apps.getDefinition方法参数无效: appId 不能为空, RequestId: {RequestId}", request.Id);
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

            _logger.LogDebug("尝试获取应用程序定义，AppId: {AppId}, RequestId: {RequestId}", appId, request.Id);
            var definition = _definitionLoader.GetDefinition(appId);

            if (definition == null)
            {
                _logger.LogWarning("未找到应用程序定义，AppId: {AppId}, RequestId: {RequestId}", appId, request.Id);
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

            _logger.LogInformation("成功获取应用程序定义，AppId: {AppId}, RequestId: {RequestId}", appId, request.Id);
            var response = new JsonRpcResponse
            {
                Id = request.Id,
                Result = new
                {
                    ok = true,
                    definition = definition
                }
            };

            _logger.LogDebug("hub.apps.getDefinition方法响应: {Response}, RequestId: {RequestId}", JsonSerializer.Serialize(response), request.Id);
            return Task.FromResult(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "处理hub.apps.getDefinition方法失败，RequestId: {RequestId}, 参数: {Params}", request.Id, JsonSerializer.Serialize(request.Params));
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
