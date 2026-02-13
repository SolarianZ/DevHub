namespace DevHub.Tests;

using System.Text.Json;
using DevHub.Core.Models.Rpc;
using DevHub.Host.Transport;

/// <summary>
/// 传输层与 JSON-RPC 信封校验白盒测试。
/// </summary>
[Trait("Category", "Spec")]
public class TransportValidationTests
{
    [Fact]
    [Trait("SpecRef", "4.2")]
    public void Spec_4_2_HttpHeaders_ShouldValidateAndReturnTrimmedClientIdentity()
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
    [Trait("SpecRef", "4.2")]
    public void Spec_4_2_HttpHeaders_MissingAuthorization_ShouldReturnUnauthorizedMissingToken()
    {
        var headers = BuildValidHeaders();
        headers.Remove("Authorization");

        var ok = DevHubTransportValidator.TryValidateHttpHeaders(
            "application/json",
            headers,
            () => "token-1",
            "req-auth-missing",
            out var errorResponse,
            out _,
            out _);

        Assert.False(ok);
        AssertError(errorResponse, -32001, "unauthorized", "req-auth-missing");
        var data = JsonSerializer.SerializeToElement(errorResponse.Error!.Data);
        Assert.Equal("missing_token", data.GetProperty("reason").GetString());
    }

    [Fact]
    [Trait("SpecRef", "4.2")]
    public void Spec_4_2_HttpHeaders_InvalidToken_ShouldReturnUnauthorizedInvalidToken()
    {
        var headers = BuildValidHeaders();
        headers["Authorization"] = "Bearer bad-token";

        var ok = DevHubTransportValidator.TryValidateHttpHeaders(
            "application/json",
            headers,
            () => "token-1",
            "req-auth-invalid",
            out var errorResponse,
            out _,
            out _);

        Assert.False(ok);
        AssertError(errorResponse, -32001, "unauthorized", "req-auth-invalid");
        var data = JsonSerializer.SerializeToElement(errorResponse.Error!.Data);
        Assert.Equal("invalid_token", data.GetProperty("reason").GetString());
    }

    [Fact]
    [Trait("SpecRef", "4.2")]
    public void Spec_4_2_HttpHeaders_TokenProviderThrows_ShouldReturnUnauthorizedInvalidToken()
    {
        var headers = BuildValidHeaders();

        var ok = DevHubTransportValidator.TryValidateHttpHeaders(
            "application/json",
            headers,
            () => throw new InvalidOperationException("token provider failed"),
            "req-auth-provider-exception",
            out var errorResponse,
            out _,
            out _);

        Assert.False(ok);
        AssertError(errorResponse, -32001, "unauthorized", "req-auth-provider-exception");
        var data = JsonSerializer.SerializeToElement(errorResponse.Error!.Data);
        Assert.Equal("invalid_token", data.GetProperty("reason").GetString());
    }

    [Fact]
    [Trait("SpecRef", "4.2")]
    public void Spec_4_2_HttpHeaders_MissingProtocol_ShouldReturnNotSupportedMissing()
    {
        var headers = BuildValidHeaders();
        headers.Remove("X-DevHub-Protocol");

        var ok = DevHubTransportValidator.TryValidateHttpHeaders(
            "application/json",
            headers,
            () => "token-1",
            "req-protocol-missing",
            out var errorResponse,
            out _,
            out _);

        Assert.False(ok);
        AssertError(errorResponse, -32099, "not_supported", "req-protocol-missing");
        var data = JsonSerializer.SerializeToElement(errorResponse.Error!.Data);
        Assert.Equal(1, data.GetProperty("expected").GetInt32());
        Assert.Equal("missing", data.GetProperty("reason").GetString());
    }

    [Fact]
    [Trait("SpecRef", "4.2")]
    public void Spec_4_2_HttpHeaders_ProtocolMismatch_ShouldReturnNotSupportedMismatch()
    {
        var headers = BuildValidHeaders();
        headers["X-DevHub-Protocol"] = "2";

        var ok = DevHubTransportValidator.TryValidateHttpHeaders(
            "application/json",
            headers,
            () => "token-1",
            "req-protocol-mismatch",
            out var errorResponse,
            out _,
            out _);

        Assert.False(ok);
        AssertError(errorResponse, -32099, "not_supported", "req-protocol-mismatch");
        var data = JsonSerializer.SerializeToElement(errorResponse.Error!.Data);
        Assert.Equal(1, data.GetProperty("expected").GetInt32());
        Assert.Equal("2", data.GetProperty("received").GetString());
        Assert.Equal("mismatch", data.GetProperty("reason").GetString());
    }

    [Fact]
    [Trait("SpecRef", "4.2")]
    public void Spec_4_2_HttpHeaders_MissingClientId_ShouldReturnInvalidRequest()
    {
        var headers = BuildValidHeaders();
        headers.Remove("X-DevHub-ClientId");

        var ok = DevHubTransportValidator.TryValidateHttpHeaders(
            "application/json",
            headers,
            () => "token-1",
            "req-client-id",
            out var errorResponse,
            out _,
            out _);

        Assert.False(ok);
        AssertError(errorResponse, -32600, "invalid_request", "req-client-id");
        var data = JsonSerializer.SerializeToElement(errorResponse.Error!.Data);
        Assert.Equal("missing_header", data.GetProperty("reason").GetString());
        Assert.Equal("X-DevHub-ClientId", data.GetProperty("header").GetString());
    }

    [Fact]
    [Trait("SpecRef", "4.2")]
    public void Spec_4_2_HttpHeaders_MissingClientSessionId_ShouldReturnInvalidRequest()
    {
        var headers = BuildValidHeaders();
        headers.Remove("X-DevHub-ClientSessionId");

        var ok = DevHubTransportValidator.TryValidateHttpHeaders(
            "application/json",
            headers,
            () => "token-1",
            "req-client-session-id",
            out var errorResponse,
            out _,
            out _);

        Assert.False(ok);
        AssertError(errorResponse, -32600, "invalid_request", "req-client-session-id");
        var data = JsonSerializer.SerializeToElement(errorResponse.Error!.Data);
        Assert.Equal("missing_header", data.GetProperty("reason").GetString());
        Assert.Equal("X-DevHub-ClientSessionId", data.GetProperty("header").GetString());
    }

    [Fact]
    [Trait("SpecRef", "4.2")]
    public void Spec_4_2_HttpHeaders_InvalidSessionIdFormat_ShouldReturnInvalidRequest()
    {
        var headers = BuildValidHeaders();
        headers["X-DevHub-ClientSessionId"] = "not-a-guid";

        var ok = DevHubTransportValidator.TryValidateHttpHeaders(
            "application/json",
            headers,
            () => "token-1",
            "req-session-id",
            out var errorResponse,
            out _,
            out _);

        Assert.False(ok);
        AssertError(errorResponse, -32600, "invalid_request", "req-session-id");
        var data = JsonSerializer.SerializeToElement(errorResponse.Error!.Data);
        Assert.Equal("invalid_header", data.GetProperty("reason").GetString());
        Assert.Equal("X-DevHub-ClientSessionId", data.GetProperty("header").GetString());
    }

    [Fact]
    [Trait("SpecRef", "4.2")]
    public void Spec_4_2_HttpHeaders_InvalidContentType_ShouldReturnInvalidRequest()
    {
        var headers = BuildValidHeaders();

        var ok = DevHubTransportValidator.TryValidateHttpHeaders(
            "text/plain",
            headers,
            () => "token-1",
            "req-content-type",
            out var errorResponse,
            out _,
            out _);

        Assert.False(ok);
        AssertError(errorResponse, -32600, "invalid_request", "req-content-type");
        var data = JsonSerializer.SerializeToElement(errorResponse.Error!.Data);
        Assert.Equal("invalid_content_type", data.GetProperty("reason").GetString());
        Assert.Equal("text/plain", data.GetProperty("received").GetString());
    }

    [Fact]
    [Trait("SpecRef", "4.2")]
    public void Spec_4_2_HttpHeaders_MissingContentType_ShouldReturnInvalidRequest()
    {
        var headers = BuildValidHeaders();

        var ok = DevHubTransportValidator.TryValidateHttpHeaders(
            null,
            headers,
            () => "token-1",
            "req-empty-content-type",
            out var errorResponse,
            out _,
            out _);

        Assert.False(ok);
        AssertError(errorResponse, -32600, "invalid_request", "req-empty-content-type");
    }

    [Fact]
    [Trait("SpecRef", "4.3")]
    public void Spec_4_3_WsAuthenticate_MissingToken_ShouldReturnUnauthorizedAndClose()
    {
        var request = CreateWsAuthenticateRequest(new
        {
            protocolVersion = 1,
            clientId = "client-a",
            clientSessionId = "11111111-1111-1111-1111-111111111111"
        });

        var response = DevHubTransportValidator.HandleWsAuthenticate(
            request,
            () => "token-1",
            (_, _) => true,
            out var authenticated,
            out _,
            out _,
            out var closeAfterResponse);

        Assert.False(authenticated);
        Assert.True(closeAfterResponse);
        AssertError(response, -32001, "unauthorized", "ws-auth");
        var data = JsonSerializer.SerializeToElement(response.Error!.Data);
        Assert.Equal("missing_token", data.GetProperty("reason").GetString());
    }

    [Fact]
    [Trait("SpecRef", "4.3")]
    public void Spec_4_3_WsAuthenticate_ParamsNotObject_ShouldReturnInvalidParams()
    {
        var request = new JsonRpcRequest
        {
            Id = "ws-auth-params",
            Method = "hub.ws.authenticate",
            Params = JsonSerializer.SerializeToElement("bad")
        };

        var response = DevHubTransportValidator.HandleWsAuthenticate(
            request,
            () => "token-1",
            (_, _) => true,
            out var authenticated,
            out _,
            out _,
            out var closeAfterResponse);

        Assert.False(authenticated);
        Assert.False(closeAfterResponse);
        AssertError(response, -32602, "invalid_params", "ws-auth-params");
    }

    [Fact]
    [Trait("SpecRef", "4.3")]
    public void Spec_4_3_WsAuthenticate_EmptyToken_ShouldReturnUnauthorizedAndClose()
    {
        var request = CreateWsAuthenticateRequest(new
        {
            token = " ",
            protocolVersion = 1,
            clientId = "client-a",
            clientSessionId = "11111111-1111-1111-1111-111111111111"
        });

        var response = DevHubTransportValidator.HandleWsAuthenticate(
            request,
            () => "token-1",
            (_, _) => true,
            out var authenticated,
            out _,
            out _,
            out var closeAfterResponse);

        Assert.False(authenticated);
        Assert.True(closeAfterResponse);
        AssertError(response, -32001, "unauthorized", "ws-auth");
    }

    [Fact]
    [Trait("SpecRef", "4.3")]
    public void Spec_4_3_WsAuthenticate_MissingProtocol_ShouldReturnNotSupportedAndClose()
    {
        var request = CreateWsAuthenticateRequest(new
        {
            token = "token-1",
            clientId = "client-a",
            clientSessionId = "11111111-1111-1111-1111-111111111111"
        });

        var response = DevHubTransportValidator.HandleWsAuthenticate(
            request,
            () => "token-1",
            (_, _) => true,
            out var authenticated,
            out _,
            out _,
            out var closeAfterResponse);

        Assert.False(authenticated);
        Assert.True(closeAfterResponse);
        AssertError(response, -32099, "not_supported", "ws-auth");
    }

    [Fact]
    [Trait("SpecRef", "4.3")]
    public void Spec_4_3_WsAuthenticate_MissingClientId_ShouldReturnInvalidParams()
    {
        var request = CreateWsAuthenticateRequest(new
        {
            token = "token-1",
            protocolVersion = 1,
            clientSessionId = "11111111-1111-1111-1111-111111111111"
        });

        var response = DevHubTransportValidator.HandleWsAuthenticate(
            request,
            () => "token-1",
            (_, _) => true,
            out var authenticated,
            out _,
            out _,
            out var closeAfterResponse);

        Assert.False(authenticated);
        Assert.False(closeAfterResponse);
        AssertError(response, -32602, "invalid_params", "ws-auth");
    }

    [Fact]
    [Trait("SpecRef", "4.3")]
    public void Spec_4_3_WsAuthenticate_MissingClientSessionId_ShouldReturnInvalidParams()
    {
        var request = CreateWsAuthenticateRequest(new
        {
            token = "token-1",
            protocolVersion = 1,
            clientId = "client-a"
        });

        var response = DevHubTransportValidator.HandleWsAuthenticate(
            request,
            () => "token-1",
            (_, _) => true,
            out var authenticated,
            out _,
            out _,
            out var closeAfterResponse);

        Assert.False(authenticated);
        Assert.False(closeAfterResponse);
        AssertError(response, -32602, "invalid_params", "ws-auth");
    }

    [Fact]
    [Trait("SpecRef", "4.3")]
    public void Spec_4_3_WsAuthenticate_ProtocolMismatch_ShouldReturnNotSupportedAndClose()
    {
        var request = CreateWsAuthenticateRequest(new
        {
            token = "token-1",
            protocolVersion = 2,
            clientId = "client-a",
            clientSessionId = "11111111-1111-1111-1111-111111111111"
        });

        var response = DevHubTransportValidator.HandleWsAuthenticate(
            request,
            () => "token-1",
            (_, _) => true,
            out var authenticated,
            out _,
            out _,
            out var closeAfterResponse);

        Assert.False(authenticated);
        Assert.True(closeAfterResponse);
        AssertError(response, -32099, "not_supported", "ws-auth");
        var data = JsonSerializer.SerializeToElement(response.Error!.Data);
        Assert.Equal("mismatch", data.GetProperty("reason").GetString());
        Assert.Equal(2, data.GetProperty("received").GetInt32());
    }

    [Fact]
    [Trait("SpecRef", "4.3")]
    public void Spec_4_3_WsAuthenticate_InvalidSessionId_ShouldReturnInvalidParams()
    {
        var request = CreateWsAuthenticateRequest(new
        {
            token = "token-1",
            protocolVersion = 1,
            clientId = "client-a",
            clientSessionId = "bad-guid"
        });

        var response = DevHubTransportValidator.HandleWsAuthenticate(
            request,
            () => "token-1",
            (_, _) => true,
            out var authenticated,
            out _,
            out _,
            out var closeAfterResponse);

        Assert.False(authenticated);
        Assert.False(closeAfterResponse);
        AssertError(response, -32602, "invalid_params", "ws-auth");
    }

    [Fact]
    [Trait("SpecRef", "4.3")]
    public void Spec_4_3_WsAuthenticate_InvalidToken_ShouldReturnUnauthorizedInvalidTokenAndClose()
    {
        var request = CreateWsAuthenticateRequest(new
        {
            token = "bad-token",
            protocolVersion = 1,
            clientId = "client-a",
            clientSessionId = "11111111-1111-1111-1111-111111111111"
        });

        var response = DevHubTransportValidator.HandleWsAuthenticate(
            request,
            () => "token-1",
            (_, _) => true,
            out var authenticated,
            out _,
            out _,
            out var closeAfterResponse);

        Assert.False(authenticated);
        Assert.True(closeAfterResponse);
        AssertError(response, -32001, "unauthorized", "ws-auth");
        var data = JsonSerializer.SerializeToElement(response.Error!.Data);
        Assert.Equal("invalid_token", data.GetProperty("reason").GetString());
    }

    [Fact]
    [Trait("SpecRef", "4.3")]
    public void Spec_4_3_WsAuthenticate_TokenProviderThrows_ShouldReturnUnauthorizedAndClose()
    {
        var request = CreateWsAuthenticateRequest(new
        {
            token = "token-1",
            protocolVersion = 1,
            clientId = "client-a",
            clientSessionId = "11111111-1111-1111-1111-111111111111"
        });

        var response = DevHubTransportValidator.HandleWsAuthenticate(
            request,
            () => throw new InvalidOperationException("token provider failed"),
            (_, _) => true,
            out var authenticated,
            out _,
            out _,
            out var closeAfterResponse);

        Assert.False(authenticated);
        Assert.True(closeAfterResponse);
        AssertError(response, -32001, "unauthorized", "ws-auth");
    }

    [Fact]
    [Trait("SpecRef", "4.3")]
    public void Spec_4_3_WsAuthenticate_MarkAuthenticatedFailed_ShouldReturnInternalErrorAndClose()
    {
        var request = CreateWsAuthenticateRequest(new
        {
            token = "token-1",
            protocolVersion = 1,
            clientId = "client-a",
            clientSessionId = "11111111-1111-1111-1111-111111111111"
        });

        var response = DevHubTransportValidator.HandleWsAuthenticate(
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
    [Trait("SpecRef", "4.3")]
    public void Spec_4_3_WsAuthenticate_Success_ShouldReturnOkAndAuthenticatedContext()
    {
        var request = CreateWsAuthenticateRequest(new
        {
            token = "token-1",
            protocolVersion = 1,
            clientId = "client-a",
            clientSessionId = "11111111-1111-1111-1111-111111111111"
        });

        var markCalled = false;
        var response = DevHubTransportValidator.HandleWsAuthenticate(
            request,
            () => "token-1",
            (clientId, sessionId) =>
            {
                markCalled = true;
                return clientId == "client-a" && sessionId == "11111111-1111-1111-1111-111111111111";
            },
            out var authenticated,
            out var clientId,
            out var clientSessionId,
            out var closeAfterResponse);

        Assert.True(markCalled);
        Assert.True(authenticated);
        Assert.False(closeAfterResponse);
        Assert.Equal("client-a", clientId);
        Assert.Equal("11111111-1111-1111-1111-111111111111", clientSessionId);
        Assert.Null(response.Error);

        var result = JsonSerializer.SerializeToElement(response.Result);
        Assert.True(result.GetProperty("ok").GetBoolean());
        Assert.Equal(1, result.GetProperty("protocolVersion").GetInt32());
    }

    [Fact]
    [Trait("SpecRef", "6.1")]
    public void Spec_6_1_TryBuildRpcRequest_InvalidEnvelope_ShouldReturnInvalidRequest()
    {
        var root = ParseJsonElement("""
        {
          "jsonrpc": "1.0",
          "method": "hub.ping",
          "id": "req-envelope"
        }
        """);

        var ok = DevHubTransportValidator.TryBuildRpcRequest(root, out _, out var errorResponse);

        Assert.False(ok);
        AssertError(errorResponse, -32600, "invalid_request", "req-envelope");
    }

    [Fact]
    [Trait("SpecRef", "6.1")]
    public void Spec_6_1_TryBuildRpcRequest_InvalidParamsType_ShouldReturnInvalidRequest()
    {
        var root = ParseJsonElement("""
        {
          "jsonrpc": "2.0",
          "method": "hub.ping",
          "id": "req-params",
          "params": "bad"
        }
        """);

        var ok = DevHubTransportValidator.TryBuildRpcRequest(root, out _, out var errorResponse);

        Assert.False(ok);
        AssertError(errorResponse, -32600, "invalid_request", "req-params");
    }

    [Fact]
    [Trait("SpecRef", "6.1")]
    public void Spec_6_1_TryBuildRpcRequest_MissingMethod_ShouldReturnInvalidRequest()
    {
        var root = ParseJsonElement("""
        {
          "jsonrpc": "2.0",
          "id": "req-missing-method"
        }
        """);

        var ok = DevHubTransportValidator.TryBuildRpcRequest(root, out _, out var errorResponse);

        Assert.False(ok);
        AssertError(errorResponse, -32600, "invalid_request", "req-missing-method");
    }

    [Fact]
    [Trait("SpecRef", "6.1")]
    public void Spec_6_1_TryBuildRpcRequest_InvalidIdType_ShouldReturnInvalidRequestWithNullId()
    {
        var root = ParseJsonElement("""
        {
          "jsonrpc": "2.0",
          "id": true,
          "method": "hub.ping"
        }
        """);

        var ok = DevHubTransportValidator.TryBuildRpcRequest(root, out _, out var errorResponse);

        Assert.False(ok);
        AssertError(errorResponse, -32600, "invalid_request", null);
    }

    [Fact]
    [Trait("SpecRef", "6.1")]
    public void Spec_6_1_TryBuildRpcRequest_FloatId_ShouldParseAsDouble()
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
    [Trait("SpecRef", "6.1")]
    public void Spec_6_1_TryBuildRpcRequest_ValidEnvelope_ShouldReturnRpcRequest()
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
    [Trait("SpecRef", "6.1")]
    public void Spec_6_1_HubParamsArray_ShouldRejectHubMethodOnly()
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
    [Trait("SpecRef", "6.3.14")]
    public void Spec_6_3_14_Subscribe_UnsupportedEventType_ShouldReturnInvalidParamsWithReason()
    {
        var request = new JsonRpcRequest
        {
            Id = "req-subscribe",
            Method = "hub.events.subscribe",
            Params = ParseJsonElement("""{ "types": ["unknown.type"] }""")
        };

        var ok = DevHubTransportValidator.TryReadSubscriptionTypes(request, out _, out var errorResponse);

        Assert.False(ok);
        AssertError(errorResponse, -32602, "invalid_params", "req-subscribe");
        var data = JsonSerializer.SerializeToElement(errorResponse.Error!.Data);
        Assert.Equal("unsupported_event_type", data.GetProperty("reason").GetString());
        Assert.Equal("unknown.type", data.GetProperty("type").GetString());
    }

    [Fact]
    [Trait("SpecRef", "6.3.14")]
    public void Spec_6_3_14_Subscribe_WhenParamsNull_ShouldAllowSubscribeAll()
    {
        var request = new JsonRpcRequest
        {
            Id = "req-subscribe-null",
            Method = "hub.events.subscribe",
            Params = null
        };

        var ok = DevHubTransportValidator.TryReadSubscriptionTypes(request, out var types, out var errorResponse);

        Assert.True(ok);
        Assert.Null(types);
        Assert.Null(errorResponse);
    }

    [Fact]
    [Trait("SpecRef", "6.3.14")]
    public void Spec_6_3_14_Subscribe_WhenParamsIsRawObject_ShouldReturnInvalidParams()
    {
        var request = new JsonRpcRequest
        {
            Id = "req-subscribe-raw-object",
            Method = "hub.events.subscribe",
            Params = new object()
        };

        var ok = DevHubTransportValidator.TryReadSubscriptionTypes(request, out _, out var errorResponse);

        Assert.False(ok);
        AssertError(errorResponse, -32602, "invalid_params", "req-subscribe-raw-object");
    }

    [Fact]
    [Trait("SpecRef", "6.3.14")]
    public void Spec_6_3_14_Subscribe_WhenParamsJsonNull_ShouldAllowSubscribeAll()
    {
        var request = new JsonRpcRequest
        {
            Id = "req-subscribe-json-null",
            Method = "hub.events.subscribe",
            Params = ParseJsonElement("null")
        };

        var ok = DevHubTransportValidator.TryReadSubscriptionTypes(request, out var types, out var errorResponse);

        Assert.True(ok);
        Assert.Null(types);
        Assert.Null(errorResponse);
    }

    [Fact]
    [Trait("SpecRef", "6.3.14")]
    public void Spec_6_3_14_Subscribe_TypesNotArray_ShouldReturnInvalidParams()
    {
        var request = new JsonRpcRequest
        {
            Id = "req-subscribe-types-not-array",
            Method = "hub.events.subscribe",
            Params = ParseJsonElement("""{ "types": "invocation.completed" }""")
        };

        var ok = DevHubTransportValidator.TryReadSubscriptionTypes(request, out _, out var errorResponse);

        Assert.False(ok);
        AssertError(errorResponse, -32602, "invalid_params", "req-subscribe-types-not-array");
    }

    [Fact]
    [Trait("SpecRef", "6.3.14")]
    public void Spec_6_3_14_Subscribe_TypesContainEmptyString_ShouldReturnInvalidParams()
    {
        var request = new JsonRpcRequest
        {
            Id = "req-subscribe-empty-type",
            Method = "hub.events.subscribe",
            Params = ParseJsonElement("""{ "types": [""] }""")
        };

        var ok = DevHubTransportValidator.TryReadSubscriptionTypes(request, out _, out var errorResponse);

        Assert.False(ok);
        AssertError(errorResponse, -32602, "invalid_params", "req-subscribe-empty-type");
    }

    [Fact]
    [Trait("SpecRef", "6.3.15")]
    public void Spec_6_3_15_Unsubscribe_MissingSubscriptionId_ShouldReturnInvalidParams()
    {
        var request = new JsonRpcRequest
        {
            Id = "req-unsubscribe",
            Method = "hub.events.unsubscribe",
            Params = ParseJsonElement("{}")
        };

        var ok = DevHubTransportValidator.TryReadUnsubscribeParam(request, out _, out var errorResponse);

        Assert.False(ok);
        AssertError(errorResponse, -32602, "invalid_params", "req-unsubscribe");
    }

    [Theory]
    [InlineData(-32700, "parse_error")]
    [InlineData(-32600, "invalid_request")]
    [InlineData(-32601, "method_not_found")]
    [InlineData(-32602, "invalid_params")]
    [InlineData(-32603, "internal_error")]
    [InlineData(-32001, "unauthorized")]
    [InlineData(-32099, "not_supported")]
    [Trait("SpecRef", "8.3")]
    public void Spec_8_3_ErrorMessage_ShouldMatchCodeMapping(int code, string message)
    {
        var response = DevHubTransportValidator.CreateErrorResponse(code, message, "req-error-map");
        AssertError(response, code, message, "req-error-map");
    }

    [Fact]
    [Trait("SpecRef", "6.2")]
    public void Spec_6_2_IsHttpOnlyMethod_ShouldMatchTransportBoundary()
    {
        Assert.True(DevHubTransportValidator.IsHttpOnlyMethod("hub.invoke.request"));
        Assert.True(DevHubTransportValidator.IsHttpOnlyMethod("hub.apps.launch"));
        Assert.False(DevHubTransportValidator.IsHttpOnlyMethod("hub.events.subscribe"));
        Assert.False(DevHubTransportValidator.IsHttpOnlyMethod("hub.ws.authenticate"));
    }

    [Fact]
    [Trait("SpecRef", "6.2")]
    public void Spec_6_2_IsWebSocketOnlyMethod_ShouldMatchTransportBoundary()
    {
        Assert.True(DevHubTransportValidator.IsWebSocketOnlyMethod("hub.ws.authenticate"));
        Assert.True(DevHubTransportValidator.IsWebSocketOnlyMethod("hub.events.subscribe"));
        Assert.True(DevHubTransportValidator.IsWebSocketOnlyMethod("hub.events.unsubscribe"));
        Assert.False(DevHubTransportValidator.IsWebSocketOnlyMethod("hub.ping"));
        Assert.False(DevHubTransportValidator.IsWebSocketOnlyMethod("hub.invoke.request"));
    }

    [Fact]
    [Trait("SpecRef", "4.2")]
    public void Spec_4_2_HttpHeaders_WithCaseSensitiveDictionary_ShouldSupportCaseInsensitiveLookup()
    {
        var headers = new Dictionary<string, string>
        {
            ["x-devhub-protocol"] = "1",
            ["x-devhub-clientid"] = "client-a",
            ["x-devhub-clientsessionid"] = "11111111-1111-1111-1111-111111111111",
            ["authorization"] = "Bearer token-1"
        };

        var ok = DevHubTransportValidator.TryValidateHttpHeaders(
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



