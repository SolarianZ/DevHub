using System.Text.Json;
using DevHub.Core.Models;
using DevHub.Core.Models.Rpc;
using DevHub.Core.Services;
using DevHub.Core.Services.Rpc;
using DevHub.Host.Transport;

namespace DevHub.Host.Rpc.Handlers;

/// <summary>
/// Host 侧的应用定义 RPC 适配处理器。
/// </summary>
public sealed class AppDefinitionsRpcHandler : IRpcHandler
{
    private readonly IDefinitionProvider _definitionProvider;
    private readonly IDefinitionManager _definitionManager;
    private readonly ILogger<AppDefinitionsRpcHandler> _logger;

    /// <summary>
    /// 初始化处理器。
    /// </summary>
    public AppDefinitionsRpcHandler(
        IDefinitionProvider definitionProvider,
        IDefinitionManager definitionManager,
        ILogger<AppDefinitionsRpcHandler> logger)
    {
        _definitionProvider = definitionProvider;
        _definitionManager = definitionManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Method => "hub.apps";

    /// <inheritdoc />
    public Task<JsonRpcResponse> HandleAsync(JsonRpcRequest request, CancellationToken cancellationToken)
    {
        return request.Method switch
        {
            HubRpcMethods.HubAppsListDefinitions => Task.FromResult(ListDefinitions(request)),
            HubRpcMethods.HubAppsGetDefinition => Task.FromResult(GetDefinition(request)),
            HubRpcMethods.HubAppsValidateDefinition => Task.FromResult(ValidateDefinition(request)),
            HubRpcMethods.HubAppsUpsertDefinition => Task.FromResult(UpsertDefinition(request)),
            HubRpcMethods.HubAppsDeleteDefinition => Task.FromResult(DeleteDefinition(request)),
            _ => Task.FromResult(TransportResponseFactory.CreateErrorResponse(-32601, "method_not_found", request.Id))
        };
    }

    private JsonRpcResponse ListDefinitions(JsonRpcRequest request)
    {
        _definitionProvider.Refresh();
        var definitions = _definitionProvider.GetAllDefinitions();

        return new JsonRpcResponse
        {
            Id = request.Id,
            Result = new
            {
                ok = true,
                definitions
            }
        };
    }

    private JsonRpcResponse GetDefinition(JsonRpcRequest request)
    {
        if (!RpcRequestParameterReader.TryReadParamsObject(request, out var paramsElement))
        {
            return TransportResponseFactory.CreateErrorResponse(-32602, "invalid_params", request.Id);
        }

        if (!RpcRequestParameterReader.TryGetRequiredString(paramsElement, "appId", out var appId)
            || !AppDefinitionValidator.IsValidAppId(appId))
        {
            return TransportResponseFactory.CreateErrorResponse(-32602, "invalid_params", request.Id);
        }

        _definitionProvider.Refresh();
        var definition = _definitionProvider.GetDefinition(appId);
        if (definition is null)
        {
            return TransportResponseFactory.CreateErrorResponse(
                -32014,
                "app_definition_not_found",
                request.Id,
                new { appId });
        }

        return new JsonRpcResponse
        {
            Id = request.Id,
            Result = new
            {
                ok = true,
                definition
            }
        };
    }

    private JsonRpcResponse ValidateDefinition(JsonRpcRequest request)
    {
        if (!TryReadDefinition(request, out var definition, out var validationResult, out var errorResponse))
        {
            return errorResponse!;
        }

        validationResult = validationResult.Valid
            ? _definitionManager.Validate(definition!)
            : validationResult;

        return new JsonRpcResponse
        {
            Id = request.Id,
            Result = new
            {
                ok = true,
                valid = validationResult.Valid,
                errors = validationResult.Errors
            }
        };
    }

    private JsonRpcResponse UpsertDefinition(JsonRpcRequest request)
    {
        if (!TryReadDefinition(request, out var definition, out var validationResult, out var errorResponse))
        {
            return errorResponse!;
        }

        if (!validationResult.Valid)
        {
            return TransportResponseFactory.CreateErrorResponse(
                -32602,
                "invalid_params",
                request.Id,
                new
                {
                    reason = "definition_invalid",
                    errors = validationResult.Errors
                });
        }

        if (!_definitionManager.TryUpsert(definition!, out var storedDefinition, out var upsertValidationResult))
        {
            return TransportResponseFactory.CreateErrorResponse(
                -32602,
                "invalid_params",
                request.Id,
                new
                {
                    reason = "definition_invalid",
                    errors = upsertValidationResult.Errors
                });
        }

        return new JsonRpcResponse
        {
            Id = request.Id,
            Result = new
            {
                ok = true,
                definition = storedDefinition
            }
        };
    }

    private JsonRpcResponse DeleteDefinition(JsonRpcRequest request)
    {
        if (!RpcRequestParameterReader.TryReadParamsObject(request, out var paramsElement))
        {
            return TransportResponseFactory.CreateErrorResponse(-32602, "invalid_params", request.Id);
        }

        if (!RpcRequestParameterReader.TryGetRequiredString(paramsElement, "appId", out var appId)
            || !AppDefinitionValidator.IsValidAppId(appId))
        {
            return TransportResponseFactory.CreateErrorResponse(-32602, "invalid_params", request.Id);
        }

        if (!_definitionManager.Delete(appId))
        {
            return TransportResponseFactory.CreateErrorResponse(
                -32014,
                "app_definition_not_found",
                request.Id,
                new { appId });
        }

        return new JsonRpcResponse
        {
            Id = request.Id,
            Result = new
            {
                ok = true
            }
        };
    }

    private bool TryReadDefinition(
        JsonRpcRequest request,
        out AppDefinition? definition,
        out AppDefinitionValidationResult validationResult,
        out JsonRpcResponse? errorResponse)
    {
        definition = null;
        validationResult = null!;
        errorResponse = null;

        if (!RpcRequestParameterReader.TryReadParamsObject(request, out var paramsElement))
        {
            errorResponse = TransportResponseFactory.CreateErrorResponse(-32602, "invalid_params", request.Id);
            return false;
        }

        if (!paramsElement.TryGetProperty("definition", out var definitionElement) || definitionElement.ValueKind != JsonValueKind.Object)
        {
            errorResponse = TransportResponseFactory.CreateErrorResponse(-32602, "invalid_params", request.Id);
            return false;
        }

        _ = AppDefinitionTransportParser.TryParse(definitionElement, out definition, out validationResult);
        return true;
    }
}
