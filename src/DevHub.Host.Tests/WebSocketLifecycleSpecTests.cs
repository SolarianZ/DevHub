namespace DevHub.Host.Tests;

using System.Net.WebSockets;
using System.Globalization;
using System.Text.Json;
using DevHub.Core.Models;
using DevHub.Core.Models.Rpc;
using DevHub.Core.Services.Events;
using DevHub.Host.Tests.TestHelpers;
using static DevHub.Host.Tests.TestHelpers.HostWebSocketTestInvoker;
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

    /// <summary>
    /// 初始化测试上下文。
    /// </summary>
    public WebSocketLifecycleSpecTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "DevHubHostWsSpecTests", Guid.NewGuid().ToString("N"));
        _runtimeDirectory = Path.Combine(_tempRoot, "runtime");
        _definitionsDirectory = Path.Combine(_tempRoot, "definitions");

        Directory.CreateDirectory(_tempRoot);
        Directory.CreateDirectory(_runtimeDirectory);
        Directory.CreateDirectory(_definitionsDirectory);
    }

    [Theory]
    [Trait("SpecRef", "4.3")]
    [InlineData("hub.ping")]
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
        await InvokeHandleWebSocketConnectionAsync(socket, context.Router, context.FileSystemManager, context.EventBus);

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
        await InvokeHandleWebSocketConnectionAsync(socket, context.Router, context.FileSystemManager, context.EventBus);

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
        await InvokeHandleWebSocketConnectionAsync(socket, context.Router, context.FileSystemManager, context.EventBus);

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
    [Trait("SpecRef", "6.2")]
    public async Task Spec_6_2_AfterAuthenticate_ShouldAllowWsSupportedMethods()
    {
        WriteDefinition("ws-supported.app");
        var context = CreateHostContext();

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
            @params = new { }
        });

        var getDefinition = CreateJson(new
        {
            jsonrpc = "2.0",
            id = "ws-get-definition",
            method = "hub.apps.getDefinition",
            @params = new { appId = "ws-supported.app" }
        });

        var listInstances = CreateJson(new
        {
            jsonrpc = "2.0",
            id = "ws-list-instances",
            method = "hub.apps.listInstances",
            @params = new { includeAllScopes = true, includeOffline = true }
        });

        var socket = new ScriptedWebSocket([auth, ping, listDefinitions, getDefinition, listInstances]);
        await InvokeHandleWebSocketConnectionAsync(socket, context.Router, context.FileSystemManager, context.EventBus);

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

        var listInstancesResponse = FindResponseById(responses, "ws-list-instances");
        Assert.True(listInstancesResponse.TryGetProperty("result", out var listInstancesResult));
        Assert.True(listInstancesResult.GetProperty("ok").GetBoolean());
        Assert.True(listInstancesResult.TryGetProperty("instances", out _));
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
        await InvokeHandleWebSocketConnectionAsync(socket, context.Router, context.FileSystemManager, context.EventBus);

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
        await InvokeHandleWebSocketConnectionAsync(socket, context.Router, context.FileSystemManager, context.EventBus);

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
        await InvokeHandleWebSocketConnectionAsync(socket, context.Router, context.FileSystemManager, context.EventBus);

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
        await InvokeHandleWebSocketConnectionAsync(socket, context.Router, context.FileSystemManager, context.EventBus);

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
        await InvokeHandleWebSocketConnectionAsync(socket, context.Router, context.FileSystemManager, context.EventBus);

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
        var runTask = InvokeHandleWebSocketConnectionAsync(socket, context.Router, context.FileSystemManager, context.EventBus);

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

        await Task.Delay(120);
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

        var socket = new ScriptedWebSocket([auth, subscribeAll], closeFrameDelay: TimeSpan.FromMilliseconds(400));
        var runTask = InvokeHandleWebSocketConnectionAsync(socket, context.Router, context.FileSystemManager, context.EventBus);

        await Task.Delay(120);

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

        await runTask;

        var messages = ParseSentMessages(socket);
        var hubEvent = messages.FirstOrDefault(m =>
            m.TryGetProperty("method", out var method)
            && string.Equals(method.GetString(), "hub.event", StringComparison.Ordinal));

        Assert.NotEqual(JsonValueKind.Undefined, hubEvent.ValueKind);
        var parameters = hubEvent.GetProperty("params");
        Assert.Equal("app.instance.registered", parameters.GetProperty("type").GetString());
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
        await InvokeHandleWebSocketConnectionAsync(socket, context.Router, context.FileSystemManager, context.EventBus);

        var responses = ParseSentMessages(socket);
        var unsubscribeResponse = FindResponseById(responses, "unsub-unknown");
        Assert.True(unsubscribeResponse.TryGetProperty("result", out var unsubscribeResult));
        Assert.True(unsubscribeResult.GetProperty("ok").GetBoolean());
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

        var socket = new ScriptedWebSocket([auth, subscribe], closeFrameDelay: TimeSpan.FromMilliseconds(400));
        var runTask = InvokeHandleWebSocketConnectionAsync(socket, context.Router, context.FileSystemManager, context.EventBus);

        await Task.Delay(120);

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

        await runTask;

        var messages = ParseSentMessages(socket);
        var hubEvent = messages.FirstOrDefault(m =>
            m.TryGetProperty("method", out var method)
            && string.Equals(method.GetString(), "hub.event", StringComparison.Ordinal));

        Assert.NotEqual(JsonValueKind.Undefined, hubEvent.ValueKind);
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
        await InvokeHandleWebSocketConnectionAsync(socket, context.Router, context.FileSystemManager, context.EventBus);

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

        await Task.Delay(80);
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

    private HostTestContext CreateHostContext() => HostTestContextFactory.Create(_tempRoot, _runtimeDirectory, _definitionsDirectory);

    private void WriteDefinition(string appId)
    {
        var filePath = Path.Combine(_definitionsDirectory, $"{appId}.json");
        File.WriteAllText(filePath, JsonSerializer.Serialize(new
        {
            appId,
            displayName = appId,
            capabilities = new
            {
                rpc = true,
                events = false
            }
        }));
    }

}
