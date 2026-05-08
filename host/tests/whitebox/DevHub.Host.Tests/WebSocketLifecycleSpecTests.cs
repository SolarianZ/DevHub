namespace DevHub.Host.Tests;

using System.Net.WebSockets;
using System.Globalization;
using System.Text.Json;
using DevHub.Core.Models;
using DevHub.Core.Models.Rpc;
using DevHub.Core.Services.Events;
using DevHub.Host.Tests.TestHelpers;
using static DevHub.Host.Tests.TestHelpers.JsonRpcTestMessageHelper;

/// <summary>
/// Host 层 WebSocket 生命周期规范白盒测试。
/// </summary>
[Trait("Category", "Spec")]
public class WebSocketLifecycleSpecTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _runtimeDirectory;
    private readonly string _definitionsDirectory;
    private readonly List<IDisposable> _createdContexts = [];

    /// <summary>
    /// 初始化测试上下文。
    /// </summary>
    public WebSocketLifecycleSpecTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "DevHubHostWsSpecTests", Guid.NewGuid().ToString("N"));
        _runtimeDirectory = Path.Combine(_tempRoot, "runtime");
        _definitionsDirectory = Path.Combine(_tempRoot, "apps");

        Directory.CreateDirectory(_tempRoot);
        Directory.CreateDirectory(_runtimeDirectory);
        Directory.CreateDirectory(_definitionsDirectory);
    }

    [Theory]
    [Trait("SpecRef", "4.3")]
    [InlineData("hub.ping")]
    [InlineData("hub.getVersion")]
    [InlineData("hub.events.subscribe")]
    public async Task Spec_4_3_FirstMessageNotAuthenticate_ShouldReturnUnauthorizedAndClose(string method)
    {
        var context = CreateHostContext();
        var request = method == "hub.events.subscribe"
            ? CreateJson(new
            {
                jsonrpc = "2.0",
                id = "unauth-first",
                method,
                @params = new { types = new[] { "invocation.completed" } }
            })
            : CreateJson(new
            {
                jsonrpc = "2.0",
                id = "unauth-first",
                method,
                @params = new { }
            });

        var socket = new ScriptedWebSocket([request]);
        await context.InvokeWebSocketConnectionAsync(socket);

        var responses = ParseSentMessages(socket);
        var response = FindResponseById(responses, "unauth-first");
        Assert.NotEqual(JsonValueKind.Undefined, response.ValueKind);
        Assert.True(response.TryGetProperty("error", out var error));
        Assert.Equal(-32001, error.GetProperty("code").GetInt32());
        Assert.Equal("unauthorized", error.GetProperty("message").GetString());
        Assert.Equal("missing_token", error.GetProperty("data").GetProperty("reason").GetString());

        Assert.Equal(WebSocketCloseStatus.PolicyViolation, socket.CloseStatus);
    }

    [Fact]
    [Trait("SpecRef", "4.3")]
    public async Task Spec_4_3_AuthenticateWithoutId_ShouldReturnInvalidRequestAndClose()
    {
        var context = CreateHostContext();
        var authWithoutId = CreateJson(new
        {
            jsonrpc = "2.0",
            method = "hub.ws.authenticate",
            @params = new
            {
                token = context.Token,
                protocolVersion = 1,
                clientId = "ws-test-client",
                clientSessionId = "11111111-1111-1111-1111-111111111111"
            }
        });

        var socket = new ScriptedWebSocket([authWithoutId]);
        await context.InvokeWebSocketConnectionAsync(socket);

        var responses = ParseSentMessages(socket);
        Assert.Single(responses);
        var response = responses[0];
        Assert.True(response.TryGetProperty("error", out var error));
        Assert.Equal(-32600, error.GetProperty("code").GetInt32());
        Assert.Equal("invalid_request", error.GetProperty("message").GetString());

        Assert.Equal(WebSocketCloseStatus.PolicyViolation, socket.CloseStatus);
    }

    [Fact]
    [Trait("SpecRef", "4.3")]
    public async Task Spec_4_3_ReAuthenticate_ShouldRejectWithInvalidRequest()
    {
        var context = CreateHostContext();

        var auth1 = CreateJson(new
        {
            jsonrpc = "2.0",
            id = "auth-1",
            method = "hub.ws.authenticate",
            @params = new
            {
                token = context.Token,
                protocolVersion = 1,
                clientId = "ws-test-client",
                clientSessionId = "11111111-1111-1111-1111-111111111111"
            }
        });

        var auth2 = CreateJson(new
        {
            jsonrpc = "2.0",
            id = "auth-2",
            method = "hub.ws.authenticate",
            @params = new
            {
                token = context.Token,
                protocolVersion = 1,
                clientId = "ws-test-client",
                clientSessionId = "11111111-1111-1111-1111-111111111111"
            }
        });

        var socket = new ScriptedWebSocket([auth1, auth2]);
        await context.InvokeWebSocketConnectionAsync(socket);

        var responses = ParseSentMessages(socket);

        var firstResponse = FindResponseById(responses, "auth-1");
        Assert.NotEqual(JsonValueKind.Undefined, firstResponse.ValueKind);
        Assert.True(firstResponse.TryGetProperty("result", out var firstResult));
        Assert.True(firstResult.GetProperty("ok").GetBoolean());

        var secondResponse = FindResponseById(responses, "auth-2");
        Assert.NotEqual(JsonValueKind.Undefined, secondResponse.ValueKind);
        Assert.True(secondResponse.TryGetProperty("error", out var secondError));
        Assert.Equal(-32600, secondError.GetProperty("code").GetInt32());
        Assert.Equal("invalid_request", secondError.GetProperty("message").GetString());
    }

    [Fact]
    [Trait("SpecRef", "6.3.2")]
    public async Task Spec_6_3_2_Authenticate_WhenValid_ShouldReturnOkWithProtocolVersion()
    {
        var context = CreateHostContext();

        var auth = CreateJson(new
        {
            jsonrpc = "2.0",
            id = "auth-6.3.2-ok",
            method = "hub.ws.authenticate",
            @params = new
            {
                token = context.Token,
                protocolVersion = 1,
                clientId = "ws-spec-6.3.2-client",
                clientSessionId = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"
            }
        });

        var socket = new ScriptedWebSocket([auth]);
        await context.InvokeWebSocketConnectionAsync(socket);

        var response = FindResponseById(ParseSentMessages(socket), "auth-6.3.2-ok");
        Assert.NotEqual(JsonValueKind.Undefined, response.ValueKind);
        Assert.True(response.TryGetProperty("result", out var result));
        Assert.True(result.GetProperty("ok").GetBoolean());
        Assert.Equal(1, result.GetProperty("protocolVersion").GetInt32());
    }

    [Fact]
    [Trait("SpecRef", "6.3.2")]
    public async Task Spec_6_3_2_Authenticate_WhenMissingRequiredParameter_ShouldReturnInvalidParams()
    {
        var context = CreateHostContext();

        var auth = CreateJson(new
        {
            jsonrpc = "2.0",
            id = "auth-6.3.2-missing-client-id",
            method = "hub.ws.authenticate",
            @params = new
            {
                token = context.Token,
                protocolVersion = 1,
                clientSessionId = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"
            }
        });

        var socket = new ScriptedWebSocket([auth]);
        await context.InvokeWebSocketConnectionAsync(socket);

        var response = FindResponseById(ParseSentMessages(socket), "auth-6.3.2-missing-client-id");
        Assert.NotEqual(JsonValueKind.Undefined, response.ValueKind);
        Assert.True(response.TryGetProperty("error", out var error));
        Assert.Equal(-32602, error.GetProperty("code").GetInt32());
        Assert.Equal("invalid_params", error.GetProperty("message").GetString());
        Assert.Equal(WebSocketCloseStatus.PolicyViolation, socket.CloseStatus);
    }

    [Fact]
    [Trait("SpecRef", "4.3")]
    public async Task Spec_4_3_Authenticate_WhenInvalidParams_ShouldRejectRetryOnSameConnection()
    {
        var context = CreateHostContext();

        var invalidAuth = CreateJson(new
        {
            jsonrpc = "2.0",
            id = "auth-invalid-first",
            method = "hub.ws.authenticate",
            @params = new
            {
                token = context.Token,
                protocolVersion = 1,
                clientSessionId = "11111111-1111-1111-1111-111111111111"
            }
        });

        var validRetry = CreateJson(new
        {
            jsonrpc = "2.0",
            id = "auth-valid-retry",
            method = "hub.ws.authenticate",
            @params = new
            {
                token = context.Token,
                protocolVersion = 1,
                clientId = "ws-test-client",
                clientSessionId = "22222222-2222-2222-2222-222222222222"
            }
        });

        var socket = new ScriptedWebSocket([invalidAuth, validRetry]);
        await context.InvokeWebSocketConnectionAsync(socket);

        var responses = ParseSentMessages(socket);
        var invalidResponse = FindResponseById(responses, "auth-invalid-first");
        Assert.NotEqual(JsonValueKind.Undefined, invalidResponse.ValueKind);
        Assert.True(invalidResponse.TryGetProperty("error", out var invalidError));
        Assert.Equal(-32602, invalidError.GetProperty("code").GetInt32());
        Assert.Equal("invalid_params", invalidError.GetProperty("message").GetString());

        var retryResponse = FindResponseById(responses, "auth-valid-retry");
        Assert.Equal(JsonValueKind.Undefined, retryResponse.ValueKind);
        Assert.Equal(WebSocketCloseStatus.PolicyViolation, socket.CloseStatus);
    }

    [Fact]
    [Trait("SpecRef", "4.3")]
    public async Task Spec_4_3_Authenticate_WhenParamsIsArray_ShouldReturnInvalidParamsAndClose()
    {
        var context = CreateHostContext();

        const string invalidArrayAuth = """
        {"jsonrpc":"2.0","id":"auth-array-invalid","method":"hub.ws.authenticate","params":[1,2,3]}
        """;

        var validRetry = CreateJson(new
        {
            jsonrpc = "2.0",
            id = "auth-array-retry",
            method = "hub.ws.authenticate",
            @params = new
            {
                token = context.Token,
                protocolVersion = 1,
                clientId = "ws-array-client",
                clientSessionId = "33333333-3333-3333-3333-333333333333"
            }
        });

        var socket = new ScriptedWebSocket([invalidArrayAuth, validRetry]);
        await context.InvokeWebSocketConnectionAsync(socket);

        var responses = ParseSentMessages(socket);
        var invalidResponse = FindResponseById(responses, "auth-array-invalid");
        Assert.NotEqual(JsonValueKind.Undefined, invalidResponse.ValueKind);
        Assert.True(invalidResponse.TryGetProperty("error", out var error));
        Assert.Equal(-32602, error.GetProperty("code").GetInt32());
        Assert.Equal("invalid_params", error.GetProperty("message").GetString());

        var retryResponse = FindResponseById(responses, "auth-array-retry");
        Assert.Equal(JsonValueKind.Undefined, retryResponse.ValueKind);
        Assert.Equal(WebSocketCloseStatus.PolicyViolation, socket.CloseStatus);
    }

    [Fact]
    [Trait("SpecRef", "6.2")]
    public async Task Spec_6_2_AfterAuthenticate_ShouldAllowWsSupportedMethods()
    {
        WriteDefinition("ws-supported.app");
        var context = CreateHostContext();
        context.RegisterInstance(new AppInstance
        {
            InstanceId = "ws-supported.instance",
            AppId = "ws-supported.app",
            Scope = ScopeContract.Global,
            Pid = 7101,
            Invoke = new InvokeCapability
            {
                Poll = true,
                Respond = true
            }
        }, "ws-supported-password");

        var auth = CreateJson(new
        {
            jsonrpc = "2.0",
            id = "auth-ok",
            method = "hub.ws.authenticate",
            @params = new
            {
                token = context.Token,
                protocolVersion = 1,
                clientId = "ws-test-client",
                clientSessionId = "11111111-1111-1111-1111-111111111111"
            }
        });

        var ping = CreateJson(new
        {
            jsonrpc = "2.0",
            id = "ws-ping",
            method = "hub.ping",
            @params = new { }
        });

        var listDefinitions = CreateJson(new
        {
            jsonrpc = "2.0",
            id = "ws-list-definitions",
            method = "hub.apps.listDefinitions",
            @params = new { scope = (string?)null }
        });

        var getDefinition = CreateJson(new
        {
            jsonrpc = "2.0",
            id = "ws-get-definition",
            method = "hub.apps.getDefinition",
            @params = new { appId = "ws-supported.app", scope = ScopeContract.Global }
        });

        var listInstances = CreateJson(new
        {
            jsonrpc = "2.0",
            id = "ws-list-instances",
            method = "hub.apps.listInstances",
            @params = new { scope = (string?)null, includeOffline = true }
        });

        var getInstance = CreateJson(new
        {
            jsonrpc = "2.0",
            id = "ws-get-instance",
            method = "hub.apps.getInstance",
            @params = new { instanceId = "ws-supported.instance" }
        });

        var socket = new ScriptedWebSocket([auth, ping, listDefinitions, getDefinition, listInstances, getInstance]);
        await context.InvokeWebSocketConnectionAsync(socket);

        var responses = ParseSentMessages(socket);

        var authResponse = FindResponseById(responses, "auth-ok");
        Assert.True(authResponse.TryGetProperty("result", out var authResult));
        Assert.True(authResult.GetProperty("ok").GetBoolean());

        var pingResponse = FindResponseById(responses, "ws-ping");
        Assert.True(pingResponse.TryGetProperty("result", out var pingResult));
        Assert.True(pingResult.GetProperty("ok").GetBoolean());

        var listDefinitionsResponse = FindResponseById(responses, "ws-list-definitions");
        Assert.True(listDefinitionsResponse.TryGetProperty("result", out var listDefinitionsResult));
        Assert.True(listDefinitionsResult.GetProperty("ok").GetBoolean());
        Assert.True(listDefinitionsResult.TryGetProperty("definitions", out _));

        var getDefinitionResponse = FindResponseById(responses, "ws-get-definition");
        Assert.True(getDefinitionResponse.TryGetProperty("result", out var getDefinitionResult));
        Assert.True(getDefinitionResult.GetProperty("ok").GetBoolean());
        var definition = getDefinitionResult.GetProperty("definition");
        Assert.Equal("ws-supported.app", definition.GetProperty("appId").GetString());
        Assert.False(definition.TryGetProperty("description", out _));
        Assert.False(definition.TryGetProperty("launch", out _));

        var listInstancesResponse = FindResponseById(responses, "ws-list-instances");
        Assert.True(listInstancesResponse.TryGetProperty("result", out var listInstancesResult));
        Assert.True(listInstancesResult.GetProperty("ok").GetBoolean());
        Assert.True(listInstancesResult.TryGetProperty("instances", out _));

        var getInstanceResponse = FindResponseById(responses, "ws-get-instance");
        Assert.True(getInstanceResponse.TryGetProperty("result", out var getInstanceResult));
        Assert.True(getInstanceResult.GetProperty("ok").GetBoolean());
        Assert.False(getInstanceResult.TryGetProperty("instanceSessionToken", out _));
        var instance = getInstanceResult.GetProperty("instance");
        Assert.Equal("ws-supported.instance", instance.GetProperty("instanceId").GetString());
        Assert.False(instance.TryGetProperty("meta", out _));
    }

    [Fact]
    [Trait("SpecRef", "3.3")]
    public async Task Spec_3_3_UnauthenticatedNotification_ShouldCloseWithoutErrorResponse()
    {
        var context = CreateHostContext();
        var unauthNotify = CreateJson(new
        {
            jsonrpc = "2.0",
            method = "hub.events.subscribe",
            @params = new { types = new[] { "invocation.completed" } }
        });

        var socket = new ScriptedWebSocket([unauthNotify]);
        await context.InvokeWebSocketConnectionAsync(socket);

        var responses = ParseSentMessages(socket);
        Assert.Empty(responses);
        Assert.Equal(WebSocketCloseStatus.PolicyViolation, socket.CloseStatus);
    }

    [Fact]
    [Trait("SpecRef", "3.3")]
    public async Task Spec_3_3_UnauthenticatedInvalidJson_ShouldReturnParseErrorWithNullIdAndClose()
    {
        var context = CreateHostContext();
        var invalidJson = "{\"jsonrpc\":\"2.0\",\"id\":\"bad\",\"method\":\"hub.ping\",\"params\":";

        var socket = new ScriptedWebSocket([invalidJson]);
        await context.InvokeWebSocketConnectionAsync(socket);

        var responses = ParseSentMessages(socket);
        Assert.Single(responses);

        var response = responses[0];
        Assert.Equal(JsonValueKind.Null, response.GetProperty("id").ValueKind);
        Assert.True(response.TryGetProperty("error", out var error));
        Assert.Equal(-32700, error.GetProperty("code").GetInt32());
        Assert.Equal("parse_error", error.GetProperty("message").GetString());
        Assert.Equal(WebSocketCloseStatus.PolicyViolation, socket.CloseStatus);
    }

    [Fact]
    [Trait("SpecRef", "3.3")]
    public async Task Spec_3_3_UnauthenticatedInvalidUtf8_ShouldReturnParseErrorWithNullIdAndClose()
    {
        var context = CreateHostContext();
        var invalidUtf8 = new byte[]
        {
            0x7B, 0x22, 0x6A, 0x73, 0x6F, 0x6E, 0x72, 0x70, 0x63, 0x22, 0x3A, 0x22, 0x32, 0x2E, 0x30, 0x22,
            0x2C, 0x22, 0x69, 0x64, 0x22, 0x3A, 0x22, 0x62, 0x61, 0x64, 0x2D, 0x75, 0x74, 0x66, 0x38, 0x22,
            0x2C, 0x22, 0x6D, 0x65, 0x74, 0x68, 0x6F, 0x64, 0x22, 0x3A, 0x22, 0x68, 0x75, 0x62, 0x2E, 0x70,
            0x69, 0x6E, 0x67, 0x22, 0x2C, 0x22, 0x70, 0x61, 0x72, 0x61, 0x6D, 0x73, 0x22, 0x3A, 0x7B,
            0x22, 0x65, 0x63, 0x68, 0x6F, 0x22, 0x3A, 0x22, 0xC3, 0x28, 0x22, 0x7D, 0x7D
        };

        var socket = new ScriptedWebSocket(new[] { invalidUtf8 });
        await context.InvokeWebSocketConnectionAsync(socket);

        var responses = ParseSentMessages(socket);
        Assert.Single(responses);

        var response = responses[0];
        Assert.Equal(JsonValueKind.Null, response.GetProperty("id").ValueKind);
        Assert.True(response.TryGetProperty("error", out var error));
        Assert.Equal(-32700, error.GetProperty("code").GetInt32());
        Assert.Equal("parse_error", error.GetProperty("message").GetString());
        Assert.Equal(WebSocketCloseStatus.PolicyViolation, socket.CloseStatus);
    }

    [Fact]
    [Trait("SpecRef", "3.1")]
    public async Task Spec_3_1_BatchRequestOverWs_ShouldReturnInvalidRequestWithNullIdAndClose()
    {
        var context = CreateHostContext();
        var batchRequest = """
        [
          {
            "jsonrpc": "2.0",
            "id": "batch-1",
            "method": "hub.ws.authenticate",
            "params": {
              "token": "token-from-batch",
              "protocolVersion": 1,
              "clientId": "batch-client",
              "clientSessionId": "11111111-1111-1111-1111-111111111111"
            }
          }
        ]
        """;

        var socket = new ScriptedWebSocket([batchRequest]);
        await context.InvokeWebSocketConnectionAsync(socket);

        var responses = ParseSentMessages(socket);
        Assert.Single(responses);

        var response = responses[0];
        Assert.Equal(JsonValueKind.Null, response.GetProperty("id").ValueKind);
        Assert.True(response.TryGetProperty("error", out var error));
        Assert.Equal(-32600, error.GetProperty("code").GetInt32());
        Assert.Equal("invalid_request", error.GetProperty("message").GetString());
        Assert.Equal(WebSocketCloseStatus.PolicyViolation, socket.CloseStatus);
    }

    [Fact]
    [Trait("SpecRef", "6.2")]
    public async Task Spec_6_2_AfterAuthenticate_HttpOnlyMethodOverWs_ShouldReturnNotSupported()
    {
        var context = CreateHostContext();

        var auth = CreateJson(new
        {
            jsonrpc = "2.0",
            id = "auth-http-only",
            method = "hub.ws.authenticate",
            @params = new
            {
                token = context.Token,
                protocolVersion = 1,
                clientId = "ws-http-only-client",
                clientSessionId = "44444444-4444-4444-4444-444444444444"
            }
        });

        var pollOverWs = CreateJson(new
        {
            jsonrpc = "2.0",
            id = "ws-http-only-poll",
            method = "hub.invoke.poll",
            @params = new
            {
                instanceId = "inst-http-only",
                maxCount = 1,
                waitMs = 0
            }
        });

        var socket = new ScriptedWebSocket([auth, pollOverWs]);
        await context.InvokeWebSocketConnectionAsync(socket);

        var responses = ParseSentMessages(socket);

        var authResponse = FindResponseById(responses, "auth-http-only");
        Assert.True(authResponse.TryGetProperty("result", out var authResult));
        Assert.True(authResult.GetProperty("ok").GetBoolean());

        var pollResponse = FindResponseById(responses, "ws-http-only-poll");
        Assert.NotEqual(JsonValueKind.Undefined, pollResponse.ValueKind);
        Assert.True(pollResponse.TryGetProperty("error", out var pollError));
        Assert.Equal(-32099, pollError.GetProperty("code").GetInt32());
        Assert.Equal("not_supported", pollError.GetProperty("message").GetString());
    }

    [Fact]
    [Trait("SpecRef", "3.1")]
    public async Task Spec_3_1_AfterAuthenticate_MessageTooLarge_ShouldReturnInvalidRequestAndClose()
    {
        var context = CreateHostContext();
        var auth = CreateJson(new
        {
            jsonrpc = "2.0",
            id = "auth-message-too-large",
            method = "hub.ws.authenticate",
            @params = new
            {
                token = context.Token,
                protocolVersion = 1,
                clientId = "ws-message-too-large-client",
                clientSessionId = "55555555-5555-5555-5555-555555555555"
            }
        });

        var oversizedRequest = CreateJson(new
        {
            jsonrpc = "2.0",
            id = "ws-message-too-large",
            method = "hub.ping",
            @params = new
            {
                echo = new string('x', 1024 * 1024)
            }
        });

        var socket = new ScriptedWebSocket([auth, oversizedRequest]);
        await context.InvokeWebSocketConnectionAsync(socket);

        var responses = ParseSentMessages(socket);
        var authResponse = FindResponseById(responses, "auth-message-too-large");
        Assert.True(authResponse.TryGetProperty("result", out var authResult));
        Assert.True(authResult.GetProperty("ok").GetBoolean());

        var oversizeResponse = Assert.Single(responses, message =>
            message.TryGetProperty("error", out var error)
            && error.GetProperty("code").GetInt32() == -32600
            && message.TryGetProperty("id", out var id)
            && id.ValueKind == JsonValueKind.Null);
        Assert.Equal("invalid_request", oversizeResponse.GetProperty("error").GetProperty("message").GetString());
        Assert.Equal(WebSocketCloseStatus.PolicyViolation, socket.CloseStatus);
        Assert.Equal("message_too_large", socket.CloseStatusDescription);
    }

    [Fact]
    [Trait("SpecRef", "6.1")]
    public async Task Spec_6_1_AfterAuthenticate_HubMethodParamsArrayOverWs_ShouldReturnInvalidParams()
    {
        var context = CreateHostContext();

        var auth = CreateJson(new
        {
            jsonrpc = "2.0",
            id = "auth-array-params",
            method = "hub.ws.authenticate",
            @params = new
            {
                token = context.Token,
                protocolVersion = 1,
                clientId = "ws-array-client",
                clientSessionId = "88888888-8888-8888-8888-888888888888"
            }
        });

        const string arrayParamsRequest = """
        {"jsonrpc":"2.0","id":"ws-array-params","method":"hub.ping","params":[1,2,3]}
        """;

        var socket = new ScriptedWebSocket([auth, arrayParamsRequest]);
        await context.InvokeWebSocketConnectionAsync(socket);

        var responses = ParseSentMessages(socket);

        var authResponse = FindResponseById(responses, "auth-array-params");
        Assert.True(authResponse.TryGetProperty("result", out var authResult));
        Assert.True(authResult.GetProperty("ok").GetBoolean());

        var invalidParamsResponse = FindResponseById(responses, "ws-array-params");
        Assert.NotEqual(JsonValueKind.Undefined, invalidParamsResponse.ValueKind);
        Assert.True(invalidParamsResponse.TryGetProperty("error", out var error));
        Assert.Equal(-32602, error.GetProperty("code").GetInt32());
        Assert.Equal("invalid_params", error.GetProperty("message").GetString());
    }

    [Fact]
    [Trait("SpecRef", "6.3.1")]
    public async Task Spec_6_3_1_AfterAuthenticate_HubPing_WhenParamsNullOverWs_ShouldReturnOk()
    {
        var context = CreateHostContext();

        var auth = CreateJson(new
        {
            jsonrpc = "2.0",
            id = "auth-null-params",
            method = "hub.ws.authenticate",
            @params = new
            {
                token = context.Token,
                protocolVersion = 1,
                clientId = "ws-null-client",
                clientSessionId = "98989898-9898-9898-9898-989898989898"
            }
        });

        const string nullParamsRequest = """
        {"jsonrpc":"2.0","id":"ws-null-params","method":"hub.ping","params":null}
        """;

        var socket = new ScriptedWebSocket([auth, nullParamsRequest]);
        await context.InvokeWebSocketConnectionAsync(socket);

        var responses = ParseSentMessages(socket);

        var authResponse = FindResponseById(responses, "auth-null-params");
        Assert.True(authResponse.TryGetProperty("result", out var authResult));
        Assert.True(authResult.GetProperty("ok").GetBoolean());

        var pingResponse = FindResponseById(responses, "ws-null-params");
        Assert.NotEqual(JsonValueKind.Undefined, pingResponse.ValueKind);
        Assert.True(pingResponse.TryGetProperty("result", out var result));
        Assert.True(result.GetProperty("ok").GetBoolean());
    }

    [Fact]
    [Trait("SpecRef", "6.3.1")]
    public async Task Spec_6_3_1_AfterAuthenticate_HubPing_WhenParamsScalarOverWs_ShouldReturnInvalidRequest()
    {
        var context = CreateHostContext();

        var auth = CreateJson(new
        {
            jsonrpc = "2.0",
            id = "auth-scalar-params",
            method = "hub.ws.authenticate",
            @params = new
            {
                token = context.Token,
                protocolVersion = 1,
                clientId = "ws-scalar-client",
                clientSessionId = "97979797-9797-9797-9797-979797979797"
            }
        });

        const string scalarParamsRequest = """
        {"jsonrpc":"2.0","id":"ws-ping-scalar","method":"hub.ping","params":1}
        """;

        var socket = new ScriptedWebSocket([auth, scalarParamsRequest]);
        await context.InvokeWebSocketConnectionAsync(socket);

        var responses = ParseSentMessages(socket);

        var authResponse = FindResponseById(responses, "auth-scalar-params");
        Assert.True(authResponse.TryGetProperty("result", out var authResult));
        Assert.True(authResult.GetProperty("ok").GetBoolean());

        var invalidRequestResponse = FindResponseById(responses, "ws-ping-scalar");
        Assert.NotEqual(JsonValueKind.Undefined, invalidRequestResponse.ValueKind);
        Assert.True(invalidRequestResponse.TryGetProperty("error", out var error));
        Assert.Equal(-32600, error.GetProperty("code").GetInt32());
        Assert.Equal("invalid_request", error.GetProperty("message").GetString());
    }

    [Fact]
    [Trait("SpecRef", "6.3.1A")]
    public async Task Spec_6_3_1A_AfterAuthenticate_HubGetVersion_WhenParamsNull_ShouldReturnVersion()
    {
        var context = CreateHostContext();

        var auth = CreateJson(new
        {
            jsonrpc = "2.0",
            id = "auth-get-version-null",
            method = "hub.ws.authenticate",
            @params = new
            {
                token = context.Token,
                protocolVersion = 1,
                clientId = "ws-get-version-client",
                clientSessionId = "78787878-7878-7878-7878-787878787878"
            }
        });

        const string getVersionRequest = """
        {"jsonrpc":"2.0","id":"ws-get-version-null","method":"hub.getVersion","params":null}
        """;

        var socket = new ScriptedWebSocket([auth, getVersionRequest]);
        await context.InvokeWebSocketConnectionAsync(socket);

        var responses = ParseSentMessages(socket);
        var authResponse = FindResponseById(responses, "auth-get-version-null");
        Assert.True(authResponse.TryGetProperty("result", out var authResult));
        Assert.True(authResult.GetProperty("ok").GetBoolean());

        var getVersionResponse = FindResponseById(responses, "ws-get-version-null");
        Assert.NotEqual(JsonValueKind.Undefined, getVersionResponse.ValueKind);
        Assert.True(getVersionResponse.TryGetProperty("result", out var result));
        Assert.True(result.GetProperty("ok").GetBoolean());
        Assert.Matches(
            "^(0|[1-9]\\d*)\\.(0|[1-9]\\d*)\\.(0|[1-9]\\d*)(?:[-+][0-9A-Za-z.-]+)?$",
            result.GetProperty("version").GetString()!);
    }

    [Fact]
    [Trait("SpecRef", "6.3.1A")]
    public async Task Spec_6_3_1A_AfterAuthenticate_HubGetVersion_WhenParamsContainUnexpectedField_ShouldReturnInvalidParams()
    {
        var context = CreateHostContext();

        var auth = CreateJson(new
        {
            jsonrpc = "2.0",
            id = "auth-get-version-extra",
            method = "hub.ws.authenticate",
            @params = new
            {
                token = context.Token,
                protocolVersion = 1,
                clientId = "ws-get-version-client",
                clientSessionId = "67676767-6767-6767-6767-676767676767"
            }
        });

        const string getVersionRequest = """
        {"jsonrpc":"2.0","id":"ws-get-version-extra","method":"hub.getVersion","params":{"verbose":true}}
        """;

        var socket = new ScriptedWebSocket([auth, getVersionRequest]);
        await context.InvokeWebSocketConnectionAsync(socket);

        var responses = ParseSentMessages(socket);
        var getVersionResponse = FindResponseById(responses, "ws-get-version-extra");
        Assert.NotEqual(JsonValueKind.Undefined, getVersionResponse.ValueKind);
        Assert.True(getVersionResponse.TryGetProperty("error", out var error));
        Assert.Equal(-32602, error.GetProperty("code").GetInt32());
        Assert.Equal("invalid_params", error.GetProperty("message").GetString());
    }

    [Fact]
    [Trait("SpecRef", "6.3.1A")]
    public async Task Spec_6_3_1A_AfterAuthenticate_HubGetVersion_WhenParamsScalar_ShouldReturnInvalidParams()
    {
        var context = CreateHostContext();

        var auth = CreateJson(new
        {
            jsonrpc = "2.0",
            id = "auth-get-version-scalar",
            method = "hub.ws.authenticate",
            @params = new
            {
                token = context.Token,
                protocolVersion = 1,
                clientId = "ws-get-version-scalar-client",
                clientSessionId = "66666666-6666-6666-6666-666666666666"
            }
        });

        const string getVersionRequest = """
        {"jsonrpc":"2.0","id":"ws-get-version-scalar","method":"hub.getVersion","params":1}
        """;

        var socket = new ScriptedWebSocket([auth, getVersionRequest]);
        await context.InvokeWebSocketConnectionAsync(socket);

        var responses = ParseSentMessages(socket);
        var getVersionResponse = FindResponseById(responses, "ws-get-version-scalar");
        Assert.NotEqual(JsonValueKind.Undefined, getVersionResponse.ValueKind);
        Assert.True(getVersionResponse.TryGetProperty("error", out var error));
        Assert.Equal(-32602, error.GetProperty("code").GetInt32());
        Assert.Equal("invalid_params", error.GetProperty("message").GetString());
    }

    [Fact]
    [Trait("SpecRef", "6.2")]
    [Trait("SpecRef", "6.3.17")]
    public async Task Spec_6_2_And_6_3_17_SubscribeNotification_ShouldReturnInvalidRequestAndNotCreateSubscription()
    {
        var context = CreateHostContext();

        var auth = CreateJson(new
        {
            jsonrpc = "2.0",
            id = "auth-sub-notification",
            method = "hub.ws.authenticate",
            @params = new
            {
                token = context.Token,
                protocolVersion = 1,
                clientId = "ws-sub-notification-client",
                clientSessionId = "12121212-1212-1212-1212-121212121212"
            }
        });

        var subscribeNotification = CreateJson(new
        {
            jsonrpc = "2.0",
            method = "hub.events.subscribe",
            @params = new { types = new[] { "invocation.completed" } }
        });

        var socket = new ScriptedWebSocket([auth, subscribeNotification], autoCloseWhenQueueDrained: false);
        var runTask = context.InvokeWebSocketConnectionAsync(socket);

        var authResponse = await WaitForResponseByIdAsync(socket, "auth-sub-notification", TimeSpan.FromSeconds(2));
        Assert.True(authResponse.TryGetProperty("result", out var authResult));
        Assert.True(authResult.GetProperty("ok").GetBoolean());

        var invalidRequest = await WaitForErrorByCodeAsync(socket, -32600, TimeSpan.FromSeconds(2));
        Assert.Equal(JsonValueKind.Null, invalidRequest.GetProperty("id").ValueKind);
        var error = invalidRequest.GetProperty("error");
        Assert.Equal("invalid_request", error.GetProperty("message").GetString());
        Assert.Equal("request_id_required", error.GetProperty("data").GetProperty("reason").GetString());

        context.EventBus.Publish(new HubEventMessage
        {
            Type = "invocation.completed",
            TimeUtc = DateTime.UtcNow,
            Payload = new
            {
                invocationId = "invk-sub-notification-rejected",
                appId = "ws-sub-notification.app",
                instanceId = "inst-ws-sub-notification"
            }
        });

        socket.EnqueueText(CreateJson(new
        {
            jsonrpc = "2.0",
            id = "ping-after-rejected-sub-notification",
            method = "hub.ping",
            @params = new { }
        }));

        var pingResponse = await WaitForResponseByIdAsync(socket, "ping-after-rejected-sub-notification", TimeSpan.FromSeconds(2));
        Assert.True(pingResponse.TryGetProperty("result", out var pingResult));
        Assert.True(pingResult.GetProperty("ok").GetBoolean());

        socket.EnqueueClose();
        await runTask;

        Assert.DoesNotContain(
            ParseSentMessages(socket),
            message => message.TryGetProperty("method", out var method)
                       && string.Equals(method.GetString(), "hub.event", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("SpecRef", "6.3.14")]
    [Trait("SpecRef", "6.3.15")]
    public async Task Spec_6_3_14_And_6_3_15_SubscribeThenUnsubscribe_ShouldStopEventDelivery()
    {
        var context = CreateHostContext();

        var auth = CreateJson(new
        {
            jsonrpc = "2.0",
            id = "auth-sub",
            method = "hub.ws.authenticate",
            @params = new
            {
                token = context.Token,
                protocolVersion = 1,
                clientId = "ws-sub-client",
                clientSessionId = "22222222-2222-2222-2222-222222222222"
            }
        });

        var subscribe = CreateJson(new
        {
            jsonrpc = "2.0",
            id = "sub-1",
            method = "hub.events.subscribe",
            @params = new { types = new[] { "invocation.completed" } }
        });

        var socket = new ScriptedWebSocket([auth, subscribe], autoCloseWhenQueueDrained: false);
        var runTask = context.InvokeWebSocketConnectionAsync(socket);

        var authResponse = await WaitForResponseByIdAsync(socket, "auth-sub", TimeSpan.FromSeconds(2));
        Assert.True(authResponse.TryGetProperty("result", out var authResult));
        Assert.True(authResult.GetProperty("ok").GetBoolean());

        var subscribeResponse = await WaitForResponseByIdAsync(socket, "sub-1", TimeSpan.FromSeconds(2));
        Assert.True(subscribeResponse.TryGetProperty("result", out var subscribeResult));
        Assert.True(subscribeResult.GetProperty("ok").GetBoolean());
        Assert.True(subscribeResult.TryGetProperty("subscriptionId", out var subscriptionIdElement));
        var subscriptionId = subscriptionIdElement.GetString();
        Assert.False(string.IsNullOrWhiteSpace(subscriptionId));

        socket.EnqueueText(CreateJson(new
        {
            jsonrpc = "2.0",
            id = "unsub-1",
            method = "hub.events.unsubscribe",
            @params = new { subscriptionId }
        }));

        var unsubscribeResponse = await WaitForResponseByIdAsync(socket, "unsub-1", TimeSpan.FromSeconds(2));
        Assert.True(unsubscribeResponse.TryGetProperty("result", out var unsubscribeResult));
        Assert.True(unsubscribeResult.GetProperty("ok").GetBoolean());

        context.EventBus.Publish(new HubEventMessage
        {
            Type = "invocation.completed",
            TimeUtc = DateTime.UtcNow,
            Payload = new
            {
                invocationId = "invk-after-unsubscribe",
                appId = "ws-sub-client.app",
                instanceId = "inst-ws-sub-client"
            }
        });

        socket.EnqueueText(CreateJson(new
        {
            jsonrpc = "2.0",
            id = "ping-after-unsubscribe",
            method = "hub.ping",
            @params = new { }
        }));

        var pingResponse = await WaitForResponseByIdAsync(socket, "ping-after-unsubscribe", TimeSpan.FromSeconds(2));
        Assert.True(pingResponse.TryGetProperty("result", out var pingResult));
        Assert.True(pingResult.GetProperty("ok").GetBoolean());

        socket.EnqueueClose();
        await runTask;

        var responses = ParseSentMessages(socket);
        Assert.DoesNotContain(
            responses,
            message => message.TryGetProperty("method", out var method)
                       && string.Equals(method.GetString(), "hub.event", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("SpecRef", "6.3.14")]
    public async Task Spec_6_3_14_SubscribeWithEmptyTypes_ShouldSubscribeAllEvents()
    {
        var context = CreateHostContext();

        var auth = CreateJson(new
        {
            jsonrpc = "2.0",
            id = "auth-sub-all",
            method = "hub.ws.authenticate",
            @params = new
            {
                token = context.Token,
                protocolVersion = 1,
                clientId = "ws-sub-all-client",
                clientSessionId = "66666666-6666-6666-6666-666666666666"
            }
        });

        var subscribeAll = CreateJson(new
        {
            jsonrpc = "2.0",
            id = "sub-all",
            method = "hub.events.subscribe",
            @params = new { types = Array.Empty<string>() }
        });

        var socket = new ScriptedWebSocket([auth, subscribeAll], autoCloseWhenQueueDrained: false);
        var runTask = context.InvokeWebSocketConnectionAsync(socket);

        var authResponse = await WaitForResponseByIdAsync(socket, "auth-sub-all", TimeSpan.FromSeconds(2));
        Assert.True(authResponse.TryGetProperty("result", out var authResult));
        Assert.True(authResult.GetProperty("ok").GetBoolean());

        var subscribeResponse = await WaitForResponseByIdAsync(socket, "sub-all", TimeSpan.FromSeconds(2));
        Assert.True(subscribeResponse.TryGetProperty("result", out var subscribeResult));
        Assert.True(subscribeResult.GetProperty("ok").GetBoolean());

        context.EventBus.Publish(new HubEventMessage
        {
            Type = "app.instance.registered",
            TimeUtc = DateTime.UtcNow,
            Payload = new
            {
                appId = "all-events.app",
                instanceId = "inst-all-events"
            }
        });

        socket.EnqueueText(CreateJson(new
        {
            jsonrpc = "2.0",
            id = "ping-sub-all",
            method = "hub.ping",
            @params = new { }
        }));

        var pingResponse = await WaitForResponseByIdAsync(socket, "ping-sub-all", TimeSpan.FromSeconds(2));
        Assert.True(pingResponse.TryGetProperty("result", out var pingResult));
        Assert.True(pingResult.GetProperty("ok").GetBoolean());

        var hubEvent = await WaitForHubEventAsync(socket, "app.instance.registered", TimeSpan.FromSeconds(2));

        socket.EnqueueClose();
        await runTask;

        var parameters = hubEvent.GetProperty("params");
        Assert.Equal("app.instance.registered", parameters.GetProperty("type").GetString());
    }

    [Fact]
    [Trait("SpecRef", "6.3.14")]
    public async Task Spec_6_3_14_Subscribe_WhenTypesOmitted_ShouldSubscribeAllEvents()
    {
        var context = CreateHostContext();

        var auth = CreateJson(new
        {
            jsonrpc = "2.0",
            id = "auth-sub-omitted",
            method = "hub.ws.authenticate",
            @params = new
            {
                token = context.Token,
                protocolVersion = 1,
                clientId = "ws-sub-omitted-client",
                clientSessionId = "99999999-9999-9999-9999-999999999999"
            }
        });

        var subscribeAll = CreateJson(new
        {
            jsonrpc = "2.0",
            id = "sub-omitted",
            method = "hub.events.subscribe",
            @params = new { }
        });

        var socket = new ScriptedWebSocket([auth, subscribeAll], autoCloseWhenQueueDrained: false);
        var runTask = context.InvokeWebSocketConnectionAsync(socket);

        var authResponse = await WaitForResponseByIdAsync(socket, "auth-sub-omitted", TimeSpan.FromSeconds(2));
        Assert.True(authResponse.TryGetProperty("result", out var authResult));
        Assert.True(authResult.GetProperty("ok").GetBoolean());

        var subscribeResponse = await WaitForResponseByIdAsync(socket, "sub-omitted", TimeSpan.FromSeconds(2));
        Assert.True(subscribeResponse.TryGetProperty("result", out var subscribeResult));
        Assert.True(subscribeResult.GetProperty("ok").GetBoolean());

        context.EventBus.Publish(new HubEventMessage
        {
            Type = "invocation.failed",
            TimeUtc = DateTime.UtcNow,
            Payload = new
            {
                invocationId = "invk-omitted-types",
                appId = "omitted-types.app",
                instanceId = "inst-omitted-types"
            }
        });

        socket.EnqueueText(CreateJson(new
        {
            jsonrpc = "2.0",
            id = "ping-sub-omitted",
            method = "hub.ping",
            @params = new { }
        }));

        var pingResponse = await WaitForResponseByIdAsync(socket, "ping-sub-omitted", TimeSpan.FromSeconds(2));
        Assert.True(pingResponse.TryGetProperty("result", out var pingResult));
        Assert.True(pingResult.GetProperty("ok").GetBoolean());

        var hubEvent = await WaitForHubEventAsync(socket, "invocation.failed", TimeSpan.FromSeconds(2));

        socket.EnqueueClose();
        await runTask;

        var parameters = hubEvent.GetProperty("params");
        Assert.Equal("invocation.failed", parameters.GetProperty("type").GetString());
    }

    [Fact]
    [Trait("SpecRef", "6.3.14")]
    public async Task Spec_6_3_14_Subscribe_WithTypeFilter_ShouldNotReceiveUnsubscribedType()
    {
        var context = CreateHostContext();

        var auth = CreateJson(new
        {
            jsonrpc = "2.0",
            id = "auth-sub-filter",
            method = "hub.ws.authenticate",
            @params = new
            {
                token = context.Token,
                protocolVersion = 1,
                clientId = "ws-sub-filter-client",
                clientSessionId = "12121212-1212-1212-1212-121212121212"
            }
        });

        var subscribe = CreateJson(new
        {
            jsonrpc = "2.0",
            id = "sub-filter",
            method = "hub.events.subscribe",
            @params = new { types = new[] { "invocation.completed" } }
        });

        var socket = new ScriptedWebSocket([auth, subscribe], autoCloseWhenQueueDrained: false);
        var runTask = context.InvokeWebSocketConnectionAsync(socket);

        var authResponse = await WaitForResponseByIdAsync(socket, "auth-sub-filter", TimeSpan.FromSeconds(2));
        Assert.True(authResponse.TryGetProperty("result", out var authResult));
        Assert.True(authResult.GetProperty("ok").GetBoolean());

        var subscribeResponse = await WaitForResponseByIdAsync(socket, "sub-filter", TimeSpan.FromSeconds(2));
        Assert.True(subscribeResponse.TryGetProperty("result", out var subscribeResult));
        Assert.True(subscribeResult.GetProperty("ok").GetBoolean());

        context.EventBus.Publish(new HubEventMessage
        {
            Type = "app.instance.registered",
            TimeUtc = DateTime.UtcNow,
            Payload = new
            {
                appId = "sub-filter.app",
                instanceId = "inst-filter-ignored"
            }
        });

        context.EventBus.Publish(new HubEventMessage
        {
            Type = "invocation.completed",
            TimeUtc = DateTime.UtcNow,
            Payload = new
            {
                invocationId = "invk-filter-hit",
                appId = "sub-filter.app",
                instanceId = "inst-filter-hit"
            }
        });

        socket.EnqueueText(CreateJson(new
        {
            jsonrpc = "2.0",
            id = "ping-sub-filter",
            method = "hub.ping",
            @params = new { }
        }));

        var pingResponse = await WaitForResponseByIdAsync(socket, "ping-sub-filter", TimeSpan.FromSeconds(2));
        Assert.True(pingResponse.TryGetProperty("result", out var pingResult));
        Assert.True(pingResult.GetProperty("ok").GetBoolean());

        _ = await WaitForHubEventAsync(socket, "invocation.completed", TimeSpan.FromSeconds(2));

        socket.EnqueueClose();
        await runTask;

        var eventMessages = ParseSentMessages(socket)
            .Where(message =>
                message.TryGetProperty("method", out var method)
                && string.Equals(method.GetString(), "hub.event", StringComparison.Ordinal))
            .ToList();

        Assert.Single(eventMessages);
        Assert.Equal("invocation.completed", eventMessages[0].GetProperty("params").GetProperty("type").GetString());
        Assert.DoesNotContain(
            eventMessages,
            message => string.Equals(
                message.GetProperty("params").GetProperty("type").GetString(),
                "app.instance.registered",
                StringComparison.Ordinal));
    }

    [Fact]
    [Trait("SpecRef", "6.3.15")]
    public async Task Spec_6_3_15_UnsubscribeUnknownSubscription_ShouldReturnOk()
    {
        var context = CreateHostContext();

        var auth = CreateJson(new
        {
            jsonrpc = "2.0",
            id = "auth-unsub-unknown",
            method = "hub.ws.authenticate",
            @params = new
            {
                token = context.Token,
                protocolVersion = 1,
                clientId = "ws-unsub-unknown-client",
                clientSessionId = "77777777-7777-7777-7777-777777777777"
            }
        });

        var unsubscribeUnknown = CreateJson(new
        {
            jsonrpc = "2.0",
            id = "unsub-unknown",
            method = "hub.events.unsubscribe",
            @params = new { subscriptionId = "sub-not-exists" }
        });

        var socket = new ScriptedWebSocket([auth, unsubscribeUnknown]);
        await context.InvokeWebSocketConnectionAsync(socket);

        var responses = ParseSentMessages(socket);
        var unsubscribeResponse = FindResponseById(responses, "unsub-unknown");
        Assert.True(unsubscribeResponse.TryGetProperty("result", out var unsubscribeResult));
        Assert.True(unsubscribeResult.GetProperty("ok").GetBoolean());
    }

    [Fact]
    [Trait("SpecRef", "6.2")]
    [Trait("SpecRef", "6.3.18")]
    public async Task Spec_6_2_And_6_3_18_UnsubscribeNotification_ShouldBeAcceptedWithoutResponse()
    {
        var context = CreateHostContext();

        var auth = CreateJson(new
        {
            jsonrpc = "2.0",
            id = "auth-unsub-notification",
            method = "hub.ws.authenticate",
            @params = new
            {
                token = context.Token,
                protocolVersion = 1,
                clientId = "ws-unsub-notification-client",
                clientSessionId = "13131313-1313-1313-1313-131313131313"
            }
        });

        var unsubscribeNotification = CreateJson(new
        {
            jsonrpc = "2.0",
            method = "hub.events.unsubscribe",
            @params = new { subscriptionId = "sub-not-exists" }
        });

        var ping = CreateJson(new
        {
            jsonrpc = "2.0",
            id = "ping-after-unsub-notification",
            method = "hub.ping",
            @params = new { }
        });

        var socket = new ScriptedWebSocket([auth, unsubscribeNotification, ping]);
        await context.InvokeWebSocketConnectionAsync(socket);

        var responses = ParseSentMessages(socket);
        var authResponse = FindResponseById(responses, "auth-unsub-notification");
        Assert.True(authResponse.TryGetProperty("result", out var authResult));
        Assert.True(authResult.GetProperty("ok").GetBoolean());

        var pingResponse = FindResponseById(responses, "ping-after-unsub-notification");
        Assert.True(pingResponse.TryGetProperty("result", out var pingResult));
        Assert.True(pingResult.GetProperty("ok").GetBoolean());

        Assert.Equal(2, responses.Count);
        Assert.DoesNotContain(
            responses,
            message => message.TryGetProperty("error", out var error)
                       && error.GetProperty("code").GetInt32() == -32600);
    }

    [Fact]
    [Trait("SpecRef", "6.3.16")]
    public async Task Spec_6_3_16_AfterSubscribe_ShouldReceiveHubEventNotification()
    {
        var context = CreateHostContext();

        var auth = CreateJson(new
        {
            jsonrpc = "2.0",
            id = "auth-event",
            method = "hub.ws.authenticate",
            @params = new
            {
                token = context.Token,
                protocolVersion = 1,
                clientId = "ws-event-client",
                clientSessionId = "33333333-3333-3333-3333-333333333333"
            }
        });

        var subscribe = CreateJson(new
        {
            jsonrpc = "2.0",
            id = "sub-event",
            method = "hub.events.subscribe",
            @params = new { types = new[] { "invocation.completed" } }
        });

        var socket = new ScriptedWebSocket([auth, subscribe], autoCloseWhenQueueDrained: false);
        var runTask = context.InvokeWebSocketConnectionAsync(socket);

        var authResponse = await WaitForResponseByIdAsync(socket, "auth-event", TimeSpan.FromSeconds(2));
        Assert.True(authResponse.TryGetProperty("result", out var authResult));
        Assert.True(authResult.GetProperty("ok").GetBoolean());

        var subscribeResponse = await WaitForResponseByIdAsync(socket, "sub-event", TimeSpan.FromSeconds(2));
        Assert.True(subscribeResponse.TryGetProperty("result", out var subscribeResult));
        Assert.True(subscribeResult.GetProperty("ok").GetBoolean());

        context.EventBus.Publish(new HubEventMessage
        {
            Type = "invocation.completed",
            TimeUtc = DateTime.UtcNow,
            Payload = new
            {
                invocationId = "invk-ws-event-1",
                appId = "ws-event.app",
                instanceId = "inst-ws-event-1"
            }
        });

        socket.EnqueueText(CreateJson(new
        {
            jsonrpc = "2.0",
            id = "ping-event",
            method = "hub.ping",
            @params = new { }
        }));

        var pingResponse = await WaitForResponseByIdAsync(socket, "ping-event", TimeSpan.FromSeconds(2));
        Assert.True(pingResponse.TryGetProperty("result", out var pingResult));
        Assert.True(pingResult.GetProperty("ok").GetBoolean());

        var hubEvent = await WaitForHubEventAsync(socket, "invocation.completed", TimeSpan.FromSeconds(2));

        socket.EnqueueClose();
        await runTask;

        Assert.False(hubEvent.TryGetProperty("id", out _));

        var parameters = hubEvent.GetProperty("params");
        var subscriptionId = parameters.GetProperty("subscriptionId").GetString();
        Assert.False(string.IsNullOrWhiteSpace(subscriptionId));
        Assert.StartsWith("sub-", subscriptionId!);

        var timeUtc = parameters.GetProperty("timeUtc").GetString();
        Assert.NotNull(timeUtc);
        Assert.True(DateTime.TryParse(timeUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _));

        Assert.Equal("invocation.completed", parameters.GetProperty("type").GetString());

        var payload = parameters.GetProperty("payload");
        Assert.Equal("invk-ws-event-1", payload.GetProperty("invocationId").GetString());
        Assert.Equal("ws-event.app", payload.GetProperty("appId").GetString());
        Assert.Equal("inst-ws-event-1", payload.GetProperty("instanceId").GetString());
    }

    [Fact]
    [Trait("SpecRef", "6.3.15")]
    public async Task Spec_6_3_15_AfterConnectionClosed_SubscriptionsShouldBeCleanedAndNoFurtherDelivery()
    {
        var context = CreateHostContext();

        var auth = CreateJson(new
        {
            jsonrpc = "2.0",
            id = "auth-cleanup",
            method = "hub.ws.authenticate",
            @params = new
            {
                token = context.Token,
                protocolVersion = 1,
                clientId = "ws-cleanup-client",
                clientSessionId = "55555555-5555-5555-5555-555555555555"
            }
        });

        var subscribe = CreateJson(new
        {
            jsonrpc = "2.0",
            id = "sub-cleanup",
            method = "hub.events.subscribe",
            @params = new { types = new[] { "invocation.completed" } }
        });

        var socket = new ScriptedWebSocket([auth, subscribe], closeFrameDelay: TimeSpan.FromMilliseconds(400));
        await context.InvokeWebSocketConnectionAsync(socket);

        var responses = ParseSentMessages(socket);
        var subscribeResponse = FindResponseById(responses, "sub-cleanup");
        Assert.True(subscribeResponse.TryGetProperty("result", out var subscribeResult));
        Assert.True(subscribeResult.GetProperty("ok").GetBoolean());
        var subscriptionId = subscribeResult.GetProperty("subscriptionId").GetString();
        Assert.False(string.IsNullOrWhiteSpace(subscriptionId));

        context.EventBus.Publish(new HubEventMessage
        {
            Type = "invocation.completed",
            TimeUtc = DateTime.UtcNow,
            Payload = new
            {
                invocationId = "invk-after-close",
                appId = "after-close.app",
                instanceId = "inst-after-close"
            }
        });

        var responsesAfterClose = ParseSentMessages(socket);
        Assert.DoesNotContain(
            responsesAfterClose,
            m => m.TryGetProperty("method", out var method)
                 && string.Equals(method.GetString(), "hub.event", StringComparison.Ordinal));
    }

    /// <summary>
    /// 释放测试资源。
    /// </summary>
    public void Dispose()
    {
        foreach (var context in _createdContexts)
        {
            context.Dispose();
        }

        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    private static async Task<JsonElement> WaitForResponseByIdAsync(ScriptedWebSocket socket, string id, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var response = FindResponseById(ParseSentMessages(socket), id);
            if (response.ValueKind != JsonValueKind.Undefined)
            {
                return response;
            }

            await Task.Delay(20);
        }

        throw new TimeoutException($"在 {timeout.TotalMilliseconds}ms 内未收到 id={id} 的响应。");
    }

    private static async Task<JsonElement> WaitForErrorByCodeAsync(ScriptedWebSocket socket, int code, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var response = ParseSentMessages(socket).FirstOrDefault(message =>
                message.TryGetProperty("error", out var error)
                && error.GetProperty("code").GetInt32() == code);

            if (response.ValueKind != JsonValueKind.Undefined)
            {
                return response;
            }

            await Task.Delay(20);
        }

        throw new TimeoutException($"在 {timeout.TotalMilliseconds}ms 内未收到 code={code} 的错误响应。");
    }

    private static async Task<JsonElement> WaitForHubEventAsync(ScriptedWebSocket socket, string eventType, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var hubEvent = ParseSentMessages(socket).FirstOrDefault(message =>
                message.TryGetProperty("method", out var method)
                && string.Equals(method.GetString(), "hub.event", StringComparison.Ordinal)
                && message.TryGetProperty("params", out var parameters)
                && string.Equals(parameters.GetProperty("type").GetString(), eventType, StringComparison.Ordinal));

            if (hubEvent.ValueKind != JsonValueKind.Undefined)
            {
                return hubEvent;
            }

            await Task.Delay(20);
        }

        throw new TimeoutException($"在 {timeout.TotalMilliseconds}ms 内未收到 type={eventType} 的 hub.event 通知。");
    }

    private HostTestContext CreateHostContext()
    {
        var context = HostTestContextFactory.Create(_tempRoot);
        _createdContexts.Add(context);
        return context;
    }

    private void WriteDefinition(string appId)
    {
        DefinitionCatalogTestHelper.UpsertDefinition(
            DefinitionCatalogTestHelper.GetCatalogPath(_definitionsDirectory),
            JsonSerializer.Serialize(new
            {
                appId,
                scope = ScopeContract.Global,
                displayName = appId,
                capabilities = new
                {
                    rpc = true,
                    events = false
                }
            }));
    }

}
