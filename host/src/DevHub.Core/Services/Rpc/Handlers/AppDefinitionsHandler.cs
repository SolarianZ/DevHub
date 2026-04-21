using DevHub.Core.Models.Rpc;
using DevHub.Core.Services;
using DevHub.Core.Services.Rpc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace DevHub.Core.Services.Rpc.Handlers;

/// <summary>
/// 处理应用程序定义相关方法的RPC处理器
/// </summary>
public class AppDefinitionsHandler : IRpcHandler
{
    private readonly IDefinitionProvider _definitionProvider;
    private readonly IDefinitionManager _definitionManager;
    private readonly ILogger<AppDefinitionsHandler> _logger;

    /// <summary>
    /// 初始化应用定义 RPC 处理器。
    /// </summary>
    /// <param name="definitionProvider">应用定义提供器。</param>
    /// <param name="definitionManager">定义管理服务。</param>
    /// <param name="logger">日志记录器。</param>
    [ActivatorUtilitiesConstructor]
    public AppDefinitionsHandler(
        IDefinitionProvider definitionProvider,
        IDefinitionManager definitionManager,
        ILogger<AppDefinitionsHandler> logger)
    {
        _definitionProvider = definitionProvider;
        _definitionManager = definitionManager;
        _logger = logger;
    }

    /// <summary>
    /// 初始化仅支持读取定义的 RPC 处理器。
    /// </summary>
    /// <param name="definitionProvider">应用定义提供器。</param>
    /// <param name="logger">日志记录器。</param>
    public AppDefinitionsHandler(IDefinitionProvider definitionProvider, ILogger<AppDefinitionsHandler> logger)
        : this(definitionProvider, UnsupportedDefinitionManager.Instance, logger)
    {
    }

    /// <inheritdoc />
    public string Method => "hub.apps";

    /// <inheritdoc />
    public async Task<JsonRpcResponse> HandleAsync(JsonRpcRequest request, CancellationToken cancellationToken)
    {
        _logger.LogDebug("收到应用程序定义相关RPC请求: {Method}, RequestId: {RequestId}", request.Method, request.Id);

        return request.Method switch
        {
            HubRpcMethods.HubAppsListDefinitions => await ListDefinitionsAsync(request, cancellationToken),
            HubRpcMethods.HubAppsGetDefinition => await GetDefinitionAsync(request, cancellationToken),
            HubRpcMethods.HubAppsValidateDefinition => await ValidateDefinitionAsync(request, cancellationToken),
            HubRpcMethods.HubAppsUpsertDefinition => await UpsertDefinitionAsync(request, cancellationToken),
            HubRpcMethods.HubAppsDeleteDefinition => await DeleteDefinitionAsync(request, cancellationToken),
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
            _definitionProvider.Refresh();

            var definitions = _definitionProvider.GetAllDefinitions();
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
            _definitionProvider.Refresh();

            if (!RpcParamReader.TryReadParamsObject(request, out var paramsElement, out var invalidParams))
            {
                _logger.LogWarning("hub.apps.getDefinition方法参数无效: 缺少参数或参数不是对象, RequestId: {RequestId}", request.Id);
                return Task.FromResult(invalidParams);
            }

            if (!RpcParamReader.TryGetRequiredString(paramsElement, "appId", out var appId))
            {
                _logger.LogWarning("hub.apps.getDefinition方法参数无效: 缺少appId或appId不是字符串, RequestId: {RequestId}", request.Id);
                return Task.FromResult(RpcErrorFactory.InvalidParams(request.Id));
            }

            if (!AppDefinitionValidator.IsValidAppId(appId))
            {
                _logger.LogWarning("hub.apps.getDefinition方法参数无效: appId 不符合格式要求, RequestId: {RequestId}", request.Id);
                return Task.FromResult(RpcErrorFactory.InvalidParams(request.Id));
            }

            if (!RpcParamReader.TryGetOptionalDefinitionScope(
                    paramsElement,
                    "scope",
                    "invalid_scope",
                    out var scope,
                    out var scopeErrorData))
            {
                return Task.FromResult(RpcErrorFactory.Create(request.Id, -32602, "invalid_params", scopeErrorData));
            }

            _logger.LogDebug("尝试获取应用程序定义，AppId: {AppId}, Scope: {Scope}, RequestId: {RequestId}", appId, scope, request.Id);
            var definition = _definitionProvider.GetDefinition(appId, scope);

            if (definition == null)
            {
                _logger.LogWarning("未找到应用程序定义，AppId: {AppId}, Scope: {Scope}, RequestId: {RequestId}", appId, scope, request.Id);
                return Task.FromResult(AppDefinitionNotFound(request.Id, appId, scope));
            }

            _logger.LogInformation("成功获取应用程序定义，AppId: {AppId}, Scope: {Scope}, RequestId: {RequestId}", appId, scope, request.Id);
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

    /// <summary>
    /// 处理 hub.apps.validateDefinition 方法。
    /// </summary>
    private Task<JsonRpcResponse> ValidateDefinitionAsync(JsonRpcRequest request, CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogDebug("处理hub.apps.validateDefinition方法，RequestId: {RequestId}, 参数: {Params}", request.Id, JsonSerializer.Serialize(request.Params));

            if (!TryGetDefinitionElement(request, out var definitionElement, out var error))
            {
                return Task.FromResult(error!);
            }

            var validationResult = _definitionManager.Validate(definitionElement);
            return Task.FromResult(new JsonRpcResponse
            {
                Id = request.Id,
                Result = new
                {
                    ok = true,
                    valid = validationResult.Valid,
                    errors = validationResult.Errors
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "处理hub.apps.validateDefinition方法失败，RequestId: {RequestId}, 参数: {Params}", request.Id, JsonSerializer.Serialize(request.Params));
            return Task.FromResult(RpcErrorFactory.InternalError(request.Id));
        }
    }

    /// <summary>
    /// 处理 hub.apps.upsertDefinition 方法。
    /// </summary>
    private Task<JsonRpcResponse> UpsertDefinitionAsync(JsonRpcRequest request, CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogDebug("处理hub.apps.upsertDefinition方法，RequestId: {RequestId}, 参数: {Params}", request.Id, JsonSerializer.Serialize(request.Params));

            if (!TryGetDefinitionElement(request, out var definitionElement, out var error))
            {
                return Task.FromResult(error!);
            }

            if (!_definitionManager.TryUpsert(definitionElement, out var definition, out var validationResult))
            {
                return Task.FromResult(RpcErrorFactory.Create(request.Id, -32602, "invalid_params", new
                {
                    reason = "definition_invalid",
                    errors = validationResult.Errors
                }));
            }

            return Task.FromResult(new JsonRpcResponse
            {
                Id = request.Id,
                Result = new
                {
                    ok = true,
                    definition
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "处理hub.apps.upsertDefinition方法失败，RequestId: {RequestId}, 参数: {Params}", request.Id, JsonSerializer.Serialize(request.Params));
            return Task.FromResult(RpcErrorFactory.InternalError(request.Id));
        }
    }

    /// <summary>
    /// 处理 hub.apps.deleteDefinition 方法。
    /// </summary>
    private Task<JsonRpcResponse> DeleteDefinitionAsync(JsonRpcRequest request, CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogDebug("处理hub.apps.deleteDefinition方法，RequestId: {RequestId}, 参数: {Params}", request.Id, JsonSerializer.Serialize(request.Params));

            if (!RpcParamReader.TryReadParamsObject(request, out var paramsElement, out var invalidParams))
            {
                return Task.FromResult(invalidParams);
            }

            if (!RpcParamReader.TryGetRequiredString(paramsElement, "appId", out var appId) || !AppDefinitionValidator.IsValidAppId(appId))
            {
                return Task.FromResult(RpcErrorFactory.InvalidParams(request.Id));
            }

            if (!RpcParamReader.TryGetOptionalDefinitionScope(
                    paramsElement,
                    "scope",
                    "invalid_scope",
                    out var scope,
                    out var scopeErrorData))
            {
                return Task.FromResult(RpcErrorFactory.Create(request.Id, -32602, "invalid_params", scopeErrorData));
            }

            if (!_definitionManager.Delete(appId, scope))
            {
                return Task.FromResult(AppDefinitionNotFound(request.Id, appId, scope));
            }

            return Task.FromResult(new JsonRpcResponse
            {
                Id = request.Id,
                Result = new
                {
                    ok = true
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "处理hub.apps.deleteDefinition方法失败，RequestId: {RequestId}, 参数: {Params}", request.Id, JsonSerializer.Serialize(request.Params));
            return Task.FromResult(RpcErrorFactory.InternalError(request.Id));
        }
    }

    private bool TryGetDefinitionElement(JsonRpcRequest request, out JsonElement definitionElement, out JsonRpcResponse? error)
    {
        definitionElement = default;
        error = null;

        if (!RpcParamReader.TryReadParamsObject(request, out var paramsElement, out var invalidParams))
        {
            error = invalidParams;
            return false;
        }

        if (!paramsElement.TryGetProperty("definition", out definitionElement) || definitionElement.ValueKind != JsonValueKind.Object)
        {
            error = RpcErrorFactory.InvalidParams(request.Id);
            return false;
        }

        return true;
    }

    private static JsonRpcResponse AppDefinitionNotFound(object? id, string appId, string? scope)
    {
        return RpcErrorFactory.Create(id, -32014, "app_definition_not_found", new AppDefinitionIdentityErrorData
        {
            AppId = appId,
            Scope = scope
        });
    }

    private sealed class UnsupportedDefinitionManager : IDefinitionManager
    {
        public static UnsupportedDefinitionManager Instance { get; } = new();

        public Models.AppDefinitionValidationResult Validate(JsonElement definitionElement)
        {
            throw new NotSupportedException("Definition management is not available in this handler instance.");
        }

        public Models.AppDefinitionValidationResult Validate(Models.AppDefinition definition)
        {
            throw new NotSupportedException("Definition management is not available in this handler instance.");
        }

        public bool TryUpsert(JsonElement definitionElement, out Models.AppDefinition? definition, out Models.AppDefinitionValidationResult validationResult)
        {
            throw new NotSupportedException("Definition management is not available in this handler instance.");
        }

        public bool TryUpsert(Models.AppDefinition definition, out Models.AppDefinition? storedDefinition, out Models.AppDefinitionValidationResult validationResult)
        {
            throw new NotSupportedException("Definition management is not available in this handler instance.");
        }

        public bool Delete(string appId, string? scope)
        {
            throw new NotSupportedException("Definition management is not available in this handler instance.");
        }
    }
}
