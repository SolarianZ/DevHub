namespace DevHub.Host.Tests;

using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Globalization;
using DevHub.Core.Models;
using DevHub.Core.Models.Rpc;
using DevHub.Core.Services;
using DevHub.Core.Services.Events;
using DevHub.Core.Services.Invocation;
using DevHub.Core.Services.Rpc;
using DevHub.Core.Services.Rpc.Handlers;
using DevHub.Host;
using Microsoft.Extensions.Logging;
using Moq;

/// <summary>
/// Host 层 WebSocket 生命周期规范白盒测试。
/// </summary>
public class WebSocketLifecycleSpecTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _runtimeDirectory;
    private readonly string _definitionsDirectory;
    private readonly EnvironmentVariableScope _runtimeScope;

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

        _runtimeScope = new EnvironmentVariableScope("DEVHUB_RUNTIME_DIR", _runtimeDirectory);
    }

    [Theory]
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
    public async Task Spec_4_3_ReAuthenticate_ShouldReturnAlreadyAuthenticated()
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
        Assert.Equal("already_authenticated", secondError.GetProperty("data").GetProperty("reason").GetString());
    }

    [Fact]
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

        var listInstances = CreateJson(new
        {
            jsonrpc = "2.0",
            id = "ws-list-instances",
            method = "hub.apps.listInstances",
            @params = new { includeAllScopes = true, includeOffline = true }
        });

        var socket = new ScriptedWebSocket([auth, ping, listDefinitions, listInstances]);
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

        var listInstancesResponse = FindResponseById(responses, "ws-list-instances");
        Assert.True(listInstancesResponse.TryGetProperty("result", out var listInstancesResult));
        Assert.True(listInstancesResult.GetProperty("ok").GetBoolean());
        Assert.True(listInstancesResult.TryGetProperty("instances", out _));
    }

    [Fact]
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
    public async Task Spec_6_3_14_And_6_3_15_SubscribeThenUnsubscribe_ShouldReturnOkAndSubscriptionId()
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

        var unsubscribe = CreateJson(new
        {
            jsonrpc = "2.0",
            id = "unsub-1",
            method = "hub.events.unsubscribe",
            @params = new { subscriptionId = "sub-override-by-test" }
        });

        var socket = new ScriptedWebSocket([auth, subscribe, unsubscribe]);
        await InvokeHandleWebSocketConnectionAsync(socket, context.Router, context.FileSystemManager, context.EventBus);

        var responses = ParseSentMessages(socket);

        var authResponse = FindResponseById(responses, "auth-sub");
        Assert.True(authResponse.TryGetProperty("result", out var authResult));
        Assert.True(authResult.GetProperty("ok").GetBoolean());

        var subscribeResponse = FindResponseById(responses, "sub-1");
        Assert.True(subscribeResponse.TryGetProperty("result", out var subscribeResult));
        Assert.True(subscribeResult.GetProperty("ok").GetBoolean());
        Assert.True(subscribeResult.TryGetProperty("subscriptionId", out var subscriptionIdElement));
        var subscriptionId = subscriptionIdElement.GetString();
        Assert.False(string.IsNullOrWhiteSpace(subscriptionId));

        var unsubscribeResponse = FindResponseById(responses, "unsub-1");
        Assert.True(unsubscribeResponse.TryGetProperty("result", out var unsubscribeResult));
        Assert.True(unsubscribeResult.GetProperty("ok").GetBoolean());
    }

    [Fact]
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

    /// <summary>
    /// 释放测试资源。
    /// </summary>
    public void Dispose()
    {
        _runtimeScope.Dispose();

        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    private HostContext CreateHostContext()
    {
        var fileSystemManager = new FileSystemManager(Mock.Of<ILogger<FileSystemManager>>(), _definitionsDirectory);
        fileSystemManager.InitializeDirectories();
        var token = fileSystemManager.GetToken();

        var appRegistry = new AppRegistry(Mock.Of<ILogger<AppRegistry>>());
        var definitionLoader = new DefinitionLoader(_definitionsDirectory, Mock.Of<ILogger<DefinitionLoader>>());
        definitionLoader.Load();
        var eventBus = new HubEventBus(Mock.Of<ILogger<HubEventBus>>());

        var routingService = new InvocationRoutingService(appRegistry, Mock.Of<ILogger<InvocationRoutingService>>());
        var invocationStore = new InvocationStore(Mock.Of<ILogger<InvocationStore>>(), routingService, eventBus);
        var requestWaiter = new InvocationRequestWaiter(Mock.Of<ILogger<InvocationRequestWaiter>>());
        var runtimeHttpBaseUrlProvider = new RuntimeHttpBaseUrlProvider(Mock.Of<ILogger<RuntimeHttpBaseUrlProvider>>());
        var launchCoordinator = new LaunchCoordinator(
            definitionLoader,
            appRegistry,
            runtimeHttpBaseUrlProvider,
            Mock.Of<ILogger<LaunchCoordinator>>());

        var handlers = new IRpcHandler[]
        {
            new HubPingHandler(Mock.Of<ILogger<HubPingHandler>>()),
            new AppDefinitionsHandler(definitionLoader, Mock.Of<ILogger<AppDefinitionsHandler>>()),
            new AppInstancesHandler(appRegistry, Mock.Of<ILogger<AppInstancesHandler>>(), eventBus),
            new InvocationHandler(
                appRegistry,
                definitionLoader,
                routingService,
                invocationStore,
                requestWaiter,
                launchCoordinator,
                Mock.Of<ILogger<InvocationHandler>>(),
                eventBus),
            new LaunchHandler(launchCoordinator, Mock.Of<ILogger<LaunchHandler>>())
        };

        var router = new RpcRouter(handlers, Mock.Of<ILogger<RpcRouter>>());
        return new HostContext(router, fileSystemManager, eventBus, token);
    }

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

    private static async Task InvokeHandleWebSocketConnectionAsync(
        ScriptedWebSocket socket,
        RpcRouter router,
        FileSystemManager fileSystemManager,
        HubEventBus eventBus)
    {
        var method = typeof(Program).GetMethod("HandleWebSocketConnectionAsync", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var task = method!.Invoke(
            null,
            [
                socket,
                router,
                fileSystemManager,
                eventBus,
                Mock.Of<ILogger<Program>>(),
                CancellationToken.None
            ]) as Task;

        Assert.NotNull(task);
        await task!;
    }

    private static string CreateJson(object payload)
    {
        return JsonSerializer.Serialize(payload);
    }

    private static List<JsonElement> ParseSentMessages(ScriptedWebSocket socket)
    {
        var messages = new List<JsonElement>();
        foreach (var text in socket.SentTexts)
        {
            using var document = JsonDocument.Parse(text);
            messages.Add(document.RootElement.Clone());
        }

        return messages;
    }

    private static JsonElement FindResponseById(IEnumerable<JsonElement> messages, string id)
    {
        foreach (var message in messages)
        {
            if (!message.TryGetProperty("id", out var idProperty) || idProperty.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            if (string.Equals(idProperty.GetString(), id, StringComparison.Ordinal))
            {
                return message;
            }
        }

        return default;
    }

    private sealed record HostContext(RpcRouter Router, FileSystemManager FileSystemManager, HubEventBus EventBus, string Token);

    /// <summary>
    /// 可脚本化的 WebSocket 测试替身。
    /// </summary>
    private sealed class ScriptedWebSocket : WebSocket
    {
        private readonly Queue<SocketFrame> _frames;
        private readonly TimeSpan _closeFrameDelay;
        private WebSocketState _state;
        private WebSocketCloseStatus? _closeStatus;
        private string? _closeStatusDescription;

        public ScriptedWebSocket(IEnumerable<string> textMessages, TimeSpan? closeFrameDelay = null)
        {
            _frames = new Queue<SocketFrame>(textMessages.Select(text => SocketFrame.Text(text)));
            _frames.Enqueue(SocketFrame.Close());
            _closeFrameDelay = closeFrameDelay ?? TimeSpan.Zero;
            _state = WebSocketState.Open;
        }

        public List<string> SentTexts { get; } = [];

        public override WebSocketCloseStatus? CloseStatus => _closeStatus;

        public override string? CloseStatusDescription => _closeStatusDescription;

        public override WebSocketState State => _state;

        public override string? SubProtocol => null;

        public override void Abort()
        {
            _state = WebSocketState.Aborted;
        }

        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            _closeStatus = closeStatus;
            _closeStatusDescription = statusDescription;
            _state = WebSocketState.Closed;
            return Task.CompletedTask;
        }

        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            _closeStatus = closeStatus;
            _closeStatusDescription = statusDescription;
            _state = WebSocketState.CloseSent;
            return Task.CompletedTask;
        }

        public override void Dispose()
        {
            _state = WebSocketState.Closed;
        }

        public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_state is not (WebSocketState.Open or WebSocketState.CloseReceived))
            {
                return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true);
            }

            if (_frames.Count == 0)
            {
                _state = WebSocketState.CloseReceived;
                return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true);
            }

            var frame = _frames.Dequeue();
            if (frame.MessageType == WebSocketMessageType.Close)
            {
                if (_closeFrameDelay > TimeSpan.Zero)
                {
                    await Task.Delay(_closeFrameDelay, cancellationToken);
                }

                _state = WebSocketState.CloseReceived;
                return new WebSocketReceiveResult(0, WebSocketMessageType.Close, true);
            }

            if (buffer.Array is null)
            {
                throw new InvalidOperationException("WebSocket 接收缓冲区不能为空。");
            }

            frame.Payload.CopyTo(buffer.Array, buffer.Offset);
            return new WebSocketReceiveResult(frame.Payload.Length, frame.MessageType, true);
        }

        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (messageType == WebSocketMessageType.Text && buffer.Array is not null)
            {
                SentTexts.Add(Encoding.UTF8.GetString(buffer.Array, buffer.Offset, buffer.Count));
            }

            return Task.CompletedTask;
        }

        private sealed record SocketFrame(WebSocketMessageType MessageType, byte[] Payload)
        {
            public static SocketFrame Text(string text)
            {
                return new SocketFrame(WebSocketMessageType.Text, Encoding.UTF8.GetBytes(text));
            }

            public static SocketFrame Close()
            {
                return new SocketFrame(WebSocketMessageType.Close, []);
            }
        }
    }
}
