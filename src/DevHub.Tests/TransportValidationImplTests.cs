namespace DevHub.Tests;

using System.Text.Json;
using DevHub.Core.Models.Rpc;
using DevHub.Host.Transport;

/// <summary>
/// 传输层校验内部实现白盒测试。
/// </summary>
[Trait("Category", "Impl")]
public class TransportValidationImplTests
{
    [Fact]
    public void Impl_4_2_HttpHeaders_ShouldValidateAndReturnTrimmedClientIdentity()
    {
        var headers = BuildValidHeaders();
        headers["X-DevHub-ClientId"] = " client-a ";
        headers["x-devhub-clientsessionid"] = " 11111111-1111-1111-1111-111111111111 ";

        var ok = DevHubTransportValidator.TryValidateHttpHeaders(
            "application/json; charset=utf-8",
            headers,
            () => "token-1",
            "req-1",
            out var errorResponse,
            out var clientId,
            out var clientSessionId);

        Assert.True(ok);
        Assert.Null(errorResponse);
        Assert.Equal("client-a", clientId);
        Assert.Equal("11111111-1111-1111-1111-111111111111", clientSessionId);
    }

    [Fact]
    public void Impl_6_1_TryBuildRpcRequest_FloatId_ShouldParseAsDouble()
    {
        var root = ParseJsonElement("""
        {
          "jsonrpc": "2.0",
          "id": 1.5,
          "method": "hub.ping"
        }
        """);

        var ok = DevHubTransportValidator.TryBuildRpcRequest(root, out var request, out var errorResponse);

        Assert.True(ok);
        Assert.Null(errorResponse);
        Assert.Equal(1.5, Assert.IsType<double>(request.Id));
    }

    [Fact]
    public void Impl_6_1_TryBuildRpcRequest_ValidEnvelope_ShouldReturnRpcRequest()
    {
        var root = ParseJsonElement("""
        {
          "jsonrpc": "2.0",
          "method": "hub.ping",
          "id": 7,
          "params": { "x": 1 }
        }
        """);

        var ok = DevHubTransportValidator.TryBuildRpcRequest(root, out var request, out var errorResponse);

        Assert.True(ok);
        Assert.Null(errorResponse);
        Assert.Equal("hub.ping", request.Method);
        Assert.Equal(7L, Assert.IsType<long>(request.Id));
        Assert.IsType<JsonElement>(request.Params);
    }

    [Fact]
    public void Impl_6_1_HubParamsArray_ShouldDetectHubMethodOnly()
    {
        var arrayParams = ParseJsonElement("[1,2,3]");

        var hubRequest = new JsonRpcRequest
        {
            Id = "req-hub-array",
            Method = "hub.ping",
            Params = arrayParams
        };

        var nonHubRequest = new JsonRpcRequest
        {
            Id = "req-non-hub-array",
            Method = "other.method",
            Params = arrayParams
        };

        Assert.True(DevHubTransportValidator.IsHubMethodParamsArray(hubRequest));
        Assert.False(DevHubTransportValidator.IsHubMethodParamsArray(nonHubRequest));
    }

    [Fact]
    public void Impl_6_2_IsHttpOnlyMethod_ShouldMatchTransportBoundary()
    {
        Assert.True(DevHubTransportValidator.IsHttpOnlyMethod("hub.invoke.request"));
        Assert.True(DevHubTransportValidator.IsHttpOnlyMethod("hub.apps.launch"));
        Assert.False(DevHubTransportValidator.IsHttpOnlyMethod("hub.events.subscribe"));
        Assert.False(DevHubTransportValidator.IsHttpOnlyMethod("hub.ws.authenticate"));
    }

    [Fact]
    public void Impl_6_2_IsWebSocketOnlyMethod_ShouldMatchTransportBoundary()
    {
        Assert.True(DevHubTransportValidator.IsWebSocketOnlyMethod("hub.ws.authenticate"));
        Assert.True(DevHubTransportValidator.IsWebSocketOnlyMethod("hub.events.subscribe"));
        Assert.True(DevHubTransportValidator.IsWebSocketOnlyMethod("hub.events.unsubscribe"));
        Assert.False(DevHubTransportValidator.IsWebSocketOnlyMethod("hub.ping"));
        Assert.False(DevHubTransportValidator.IsWebSocketOnlyMethod("hub.invoke.request"));
    }

    private static Dictionary<string, string> BuildValidHeaders()
    {
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["X-DevHub-Protocol"] = "1",
            ["X-DevHub-ClientId"] = "client-a",
            ["X-DevHub-ClientSessionId"] = "11111111-1111-1111-1111-111111111111",
            ["Authorization"] = "Bearer token-1"
        };
    }

    private static JsonElement ParseJsonElement(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
