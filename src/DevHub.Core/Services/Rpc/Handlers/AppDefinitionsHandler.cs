using DevHub.Core.Models;
using DevHub.Core.Models.Rpc;

namespace DevHub.Core.Services.Rpc.Handlers;

/// <summary>
/// 处理应用程序定义相关方法的RPC处理器
/// </summary>
public class AppDefinitionsHandler : IRpcHandler
{
    private readonly DefinitionLoader _definitionLoader;

    public AppDefinitionsHandler(DefinitionLoader definitionLoader)
    {
        _definitionLoader = definitionLoader;
    }

    public string Method => "hub.apps";

    public async Task<JsonRpcResponse> HandleAsync(JsonRpcRequest request, CancellationToken cancellationToken)
    {
        // 根据具体方法路由到不同处理逻辑
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
            "listDefinitions" => await ListDefinitionsAsync(request, cancellationToken),
            "getDefinition" => await GetDefinitionAsync(request, cancellationToken),
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
    /// 处理hub.apps.listDefinitions方法
    /// </summary>
    private Task<JsonRpcResponse> ListDefinitionsAsync(JsonRpcRequest request, CancellationToken cancellationToken)
    {
        var definitions = _definitionLoader.GetAllDefinitions();

        var response = new JsonRpcResponse
        {
            Id = request.Id,
            Result = new
            {
                definitions = definitions
            }
        };

        return Task.FromResult(response);
    }

    /// <summary>
    /// 处理hub.apps.getDefinition方法
    /// </summary>
    private Task<JsonRpcResponse> GetDefinitionAsync(JsonRpcRequest request, CancellationToken cancellationToken)
    {
        // 解析参数
        var parameters = request.Params as Dictionary<string, object>;
        if (parameters == null || !parameters.TryGetValue("appId", out var appIdObj) || appIdObj == null)
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

        var appId = appIdObj.ToString();
        var definition = _definitionLoader.GetDefinition(appId);

        if (definition == null)
        {
            return Task.FromResult(new JsonRpcResponse
            {
                Id = request.Id,
                Error = new JsonRpcError
                {
                    Code = -32014,
                    Message = "应用程序定义未找到"
                }
            });
        }

        var response = new JsonRpcResponse
        {
            Id = request.Id,
            Result = definition
        };

        return Task.FromResult(response);
    }
}
