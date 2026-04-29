namespace DevHub.Host.Tests;

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
    public void Impl_4_2_HttpHeaders_TokenProviderThrows_ShouldReturnInternalError()
    {
        var headers = BuildValidHeaders();

        var ok = HttpTransportRequestValidator.TryValidate(
            "application/json",
            headers,
            () => throw new InvalidOperationException("token provider failed"),
            "req-auth-provider-exception",
            out var errorResponse,
            out _,
            out _);

        Assert.False(ok);
        AssertError(errorResponse, -32603, "internal_error", "req-auth-provider-exception");
        Assert.Null(errorResponse.Error!.Data);
    }

    [Fact]
    public void Impl_4_2_HttpHeaders_ShouldValidateAndReturnTrimmedClientIdentity()
    {
        var headers = BuildValidHeaders();
        headers["X-DevHub-ClientId"] = " client-a ";
        headers["x-devhub-clientsessionid"] = " 11111111-1111-1111-1111-111111111111 ";

        var ok = HttpTransportRequestValidator.TryValidate(
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
    public void Impl_4_2_HttpHeaders_WhenAuthorizationMissing_ShouldKeepValidatedClientOutputsNull()
    {
        var headers = BuildValidHeaders();
        headers.Remove("Authorization");

        var ok = HttpTransportRequestValidator.TryValidate(
            "application/json",
            headers,
            () => "token-1",
            "req-auth-precedence",
            out var errorResponse,
            out var clientId,
            out var clientSessionId);

        Assert.False(ok);
        Assert.Null(clientId);
        Assert.Null(clientSessionId);
        AssertError(errorResponse, -32001, "unauthorized", "req-auth-precedence");

        var data = JsonSerializer.SerializeToElement(errorResponse.Error!.Data);
        Assert.Equal("missing_token", data.GetProperty("reason").GetString());
    }

    [Fact]
    public void Impl_4_2_HttpHeaders_WhenAuthorizationMissingAndProtocolInvalid_ShouldReturnUnauthorizedFirst()
    {
        var headers = BuildValidHeaders();
        headers.Remove("Authorization");
        headers["X-DevHub-Protocol"] = "2";

        var ok = HttpTransportRequestValidator.TryValidate(
            "application/json",
            headers,
            () => "token-1",
            "req-auth-priority",
            out var errorResponse,
            out var clientId,
            out var clientSessionId);

        Assert.False(ok);
        Assert.Null(clientId);
        Assert.Null(clientSessionId);
        AssertError(errorResponse, -32001, "unauthorized", "req-auth-priority");
    }

    [Fact]
    public void Impl_6_1_TryBuildRpcRequest_FloatId_ShouldReturnInvalidRequestWithNullId()
    {
        var root = ParseJsonElement("""
        {
          "jsonrpc": "2.0",
          "id": 1.5,
          "method": "hub.ping"
        }
        """);

        var ok = JsonRpcEnvelopeParser.TryParse(root, out var request, out var errorResponse);

        Assert.False(ok);
        Assert.Null(request);
        AssertError(errorResponse, -32600, "invalid_request", null);
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

        var ok = JsonRpcEnvelopeParser.TryParse(root, out var request, out var errorResponse);

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

        Assert.True(JsonRpcEnvelopeParser.IsHubMethodParamsArray(hubRequest));
        Assert.False(JsonRpcEnvelopeParser.IsHubMethodParamsArray(nonHubRequest));
    }

    [Fact]
    public void Impl_6_2_IsHttpOnlyMethod_ShouldMatchTransportBoundary()
    {
        Assert.True(TransportMethodPolicy.IsHttpOnlyMethod("hub.apps.validateDefinition"));
        Assert.True(TransportMethodPolicy.IsHttpOnlyMethod("hub.apps.upsertDefinition"));
        Assert.True(TransportMethodPolicy.IsHttpOnlyMethod("hub.apps.deleteDefinition"));
        Assert.True(TransportMethodPolicy.IsHttpOnlyMethod("hub.invoke.request"));
        Assert.True(TransportMethodPolicy.IsHttpOnlyMethod("hub.apps.launch"));
        Assert.False(TransportMethodPolicy.IsHttpOnlyMethod("hub.getVersion"));
        Assert.False(TransportMethodPolicy.IsHttpOnlyMethod("hub.events.subscribe"));
        Assert.False(TransportMethodPolicy.IsHttpOnlyMethod("hub.ws.authenticate"));
    }

    [Fact]
    public void Impl_6_2_IsWebSocketOnlyMethod_ShouldMatchTransportBoundary()
    {
        Assert.True(TransportMethodPolicy.IsWebSocketOnlyMethod("hub.ws.authenticate"));
        Assert.True(TransportMethodPolicy.IsWebSocketOnlyMethod("hub.events.subscribe"));
        Assert.True(TransportMethodPolicy.IsWebSocketOnlyMethod("hub.events.unsubscribe"));
        Assert.False(TransportMethodPolicy.IsWebSocketOnlyMethod("hub.getVersion"));
        Assert.False(TransportMethodPolicy.IsWebSocketOnlyMethod("hub.ping"));
        Assert.False(TransportMethodPolicy.IsWebSocketOnlyMethod("hub.invoke.request"));
    }

    [Fact]
    public void Impl_4_3_WsAuthenticate_TokenProviderThrows_ShouldReturnInternalErrorAndClose()
    {
        var request = CreateWsAuthenticateRequest(new
        {
            token = "token-1",
            protocolVersion = 1,
            clientId = "client-a",
            clientSessionId = "11111111-1111-1111-1111-111111111111"
        });

        var response = WebSocketAuthenticationProcessor.Authenticate(
            request,
            () => throw new InvalidOperationException("token provider failed"),
            (_, _) => true,
            out var authenticated,
            out _,
            out _,
            out var closeAfterResponse);

        Assert.False(authenticated);
        Assert.True(closeAfterResponse);
        AssertError(response, -32603, "internal_error", "ws-auth");
        Assert.Null(response.Error!.Data);
    }

    [Fact]
    public void Impl_4_3_WsAuthenticate_MarkAuthenticatedFailed_ShouldReturnInternalErrorAndClose()
    {
        var request = CreateWsAuthenticateRequest(new
        {
            token = "token-1",
            protocolVersion = 1,
            clientId = "client-a",
            clientSessionId = "11111111-1111-1111-1111-111111111111"
        });

        var response = WebSocketAuthenticationProcessor.Authenticate(
            request,
            () => "token-1",
            (_, _) => false,
            out var authenticated,
            out _,
            out _,
            out var closeAfterResponse);

        Assert.False(authenticated);
        Assert.True(closeAfterResponse);
        AssertError(response, -32603, "internal_error", "ws-auth");
    }

    [Fact]
    public void Impl_4_2_HttpHeaders_WithCaseSensitiveDictionary_ShouldSupportCaseInsensitiveLookup()
    {
        var headers = new Dictionary<string, string>
        {
            ["x-devhub-protocol"] = "1",
            ["x-devhub-clientid"] = "client-a",
            ["x-devhub-clientsessionid"] = "11111111-1111-1111-1111-111111111111",
            ["authorization"] = "Bearer token-1"
        };

        var ok = HttpTransportRequestValidator.TryValidate(
            "application/json",
            headers,
            () => "token-1",
            "req-case-sensitive-headers",
            out var errorResponse,
            out var clientId,
            out var clientSessionId);

        Assert.True(ok);
        Assert.Null(errorResponse);
        Assert.Equal("client-a", clientId);
        Assert.Equal("11111111-1111-1111-1111-111111111111", clientSessionId);
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

    private static JsonRpcRequest CreateWsAuthenticateRequest(object parameters)
    {
        return new JsonRpcRequest
        {
            Id = "ws-auth",
            Method = "hub.ws.authenticate",
            Params = JsonSerializer.SerializeToElement(parameters)
        };
    }

    private static JsonElement ParseJsonElement(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static void AssertError(JsonRpcResponse response, int code, string message, object? id)
    {
        Assert.NotNull(response.Error);
        Assert.Equal(code, response.Error.Code);
        Assert.Equal(message, response.Error.Message);
        Assert.Equal(id, response.Id);
    }
}
