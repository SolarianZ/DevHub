using DevHub.Core.Models.Rpc;
using DevHub.Core.Services;
using DevHub.Core.Services.Rpc;
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

    /// <summary>
    /// 初始化应用定义 RPC 处理器。
    /// </summary>
    /// <param name="definitionLoader">应用定义加载器。</param>
    /// <param name="logger">日志记录器。</param>
    public AppDefinitionsHandler(DefinitionLoader definitionLoader, ILogger<AppDefinitionsHandler> logger)
    {
        _definitionLoader = definitionLoader;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Method => "hub.apps";

    /// <inheritdoc />
    public async Task<JsonRpcResponse> HandleAsync(JsonRpcRequest request, CancellationToken cancellationToken)
    {
        _logger.LogDebug("收到应用程序定义相关RPC请求: {Method}, RequestId: {RequestId}", request.Method, request.Id);

        return request.Method switch
        {
            "hub.apps.listDefinitions" => await ListDefinitionsAsync(request, cancellationToken),
            "hub.apps.getDefinition" => await GetDefinitionAsync(request, cancellationToken),
            _ => RpcErrorFactory.MethodNotFound(request.Id)
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
            return Task.FromResult(RpcErrorFactory.InternalError(request.Id));
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
                return Task.FromResult(RpcErrorFactory.InvalidParams(request.Id));
            }

            if (!paramsElement.TryGetProperty("appId", out var appIdProperty) || appIdProperty.ValueKind != JsonValueKind.String)
            {
                _logger.LogWarning("hub.apps.getDefinition方法参数无效: 缺少appId或appId不是字符串, RequestId: {RequestId}", request.Id);
                return Task.FromResult(RpcErrorFactory.InvalidParams(request.Id));
            }

            var appId = appIdProperty.GetString();
            if (string.IsNullOrWhiteSpace(appId))
            {
                _logger.LogWarning("hub.apps.getDefinition方法参数无效: appId 不能为空, RequestId: {RequestId}", request.Id);
                return Task.FromResult(RpcErrorFactory.InvalidParams(request.Id));
            }

            _logger.LogDebug("尝试获取应用程序定义，AppId: {AppId}, RequestId: {RequestId}", appId, request.Id);
            var definition = _definitionLoader.GetDefinition(appId);

            if (definition == null)
            {
                _logger.LogWarning("未找到应用程序定义，AppId: {AppId}, RequestId: {RequestId}", appId, request.Id);
                return Task.FromResult(AppDefinitionNotFound(request.Id, appId));
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
            return Task.FromResult(RpcErrorFactory.InternalError(request.Id));
        }
    }

    private static JsonRpcResponse AppDefinitionNotFound(object? id, string appId)
    {
        return RpcErrorFactory.Create(id, -32014, "app_definition_not_found", new { appId });
    }
}
