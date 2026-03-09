using System.Net.WebSockets;
using System.Text.Json;
using DevHub.Sdk.Internal;

namespace DevHub.Sdk.UnitTests.Events;

/// <summary>
/// WS 生命周期白盒测试。
/// </summary>
public sealed class WsLifecycleTests : IDisposable
{
    private readonly string _tempRoot;

    public WsLifecycleTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "DevHubSdkWsTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    [Fact]
    public async Task M5_DN_UT_005_EventsClient_BeforeAuthenticate_ShouldRejectSubscribeAndRead()
    {
        var runtimeDir = await CreateRuntimeAsync();
        var factory = new FakeWebSocketConnectionFactory(new FakeWebSocketConnection());
        await using var client = await DevHubEventsClient.FromRuntimeAsync(
            new DevHubClientOptions
            {
                ClientId = "ws-client",
                RuntimeDir = runtimeDir
            },
            factory,
            () => "ws-auth-1");

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SubscribeAsync(new[] { "invocation.completed" }));
        Assert.Throws<InvalidOperationException>(() => client.ReadEventsAsync());
    }

    [Fact]
    public async Task M5_DN_UT_005_EventsClient_AfterAuthenticate_ShouldSubscribeAndReadEvents()
    {
        var runtimeDir = await CreateRuntimeAsync();
        var connection = new FakeWebSocketConnection();
        connection.OnSend = sent =>
        {
            if (sent.Contains("\"id\":\"ws-auth-1\"", StringComparison.Ordinal))
            {
                return
                [
                    CreateTextMessage("""{"jsonrpc":"2.0","id":"ws-auth-1","result":{"ok":true,"protocolVersion":1}}""")
                ];
            }

            if (sent.Contains("\"id\":\"ws-sub-1\"", StringComparison.Ordinal))
            {
                return
                [
                    CreateTextMessage("""{"jsonrpc":"2.0","id":"ws-sub-1","result":{"ok":true,"subscriptionId":"sub-1"}}"""),
                    CreateTextMessage("""{"jsonrpc":"2.0","method":"hub.event","params":{"subscriptionId":"sub-1","type":"invocation.completed","timeUtc":"2026-03-09T00:00:00Z","payload":{"invocationId":"invk-1"}}}"""),
                    CreateCloseMessage()
                ];
            }

            return [];
        };
        var factory = new FakeWebSocketConnectionFactory(connection);
        await using var client = await DevHubEventsClient.FromRuntimeAsync(
            new DevHubClientOptions
            {
                ClientId = "ws-client",
                RuntimeDir = runtimeDir
            },
            factory,
            new SequenceRequestIdFactory("ws-auth-1", "ws-sub-1").Create);

        await client.AuthenticateAsync();
        var subscriptionId = await client.SubscribeAsync(new[] { "invocation.completed" });
        Assert.Equal("sub-1", subscriptionId);

        var enumerator = client.ReadEventsAsync().GetAsyncEnumerator();
        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal("invocation.completed", enumerator.Current.Type);
        Assert.Equal("sub-1", enumerator.Current.SubscriptionId);

        Assert.Collection(connection.SentTexts,
            sent => Assert.Contains("hub.ws.authenticate", sent, StringComparison.Ordinal),
            sent => Assert.Contains("hub.events.subscribe", sent, StringComparison.Ordinal));
    }

    [Fact]
    public async Task M5_DN_UT_005_EventsClient_WhenConnectionClosesAfterQueuedEvent_ShouldStillReadBufferedEvents()
    {
        var runtimeDir = await CreateRuntimeAsync();
        var connection = new FakeWebSocketConnection();
        connection.OnSend = sent =>
        {
            if (sent.Contains("\"id\":\"ws-auth-1\"", StringComparison.Ordinal))
            {
                return
                [
                    CreateTextMessage("""{"jsonrpc":"2.0","id":"ws-auth-1","result":{"ok":true,"protocolVersion":1}}""")
                ];
            }

            if (sent.Contains("\"id\":\"ws-sub-1\"", StringComparison.Ordinal))
            {
                return
                [
                    CreateTextMessage("""{"jsonrpc":"2.0","id":"ws-sub-1","result":{"ok":true,"subscriptionId":"sub-1"}}"""),
                    CreateTextMessage("""{"jsonrpc":"2.0","method":"hub.event","params":{"subscriptionId":"sub-1","type":"invocation.completed","timeUtc":"2026-03-09T00:00:00Z","payload":{"invocationId":"invk-1"}}}"""),
                    CreateCloseMessage()
                ];
            }

            return [];
        };

        var factory = new FakeWebSocketConnectionFactory(connection);
        await using var client = await DevHubEventsClient.FromRuntimeAsync(
            new DevHubClientOptions
            {
                ClientId = "ws-client",
                RuntimeDir = runtimeDir
            },
            factory,
            new SequenceRequestIdFactory("ws-auth-1", "ws-sub-1").Create);

        await client.AuthenticateAsync();
        _ = await client.SubscribeAsync(new[] { "invocation.completed" });
        await connection.WaitForCloseObservedAsync();

        await using var enumerator = client.ReadEventsAsync().GetAsyncEnumerator();
        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal("invocation.completed", enumerator.Current.Type);
        Assert.Equal("sub-1", enumerator.Current.SubscriptionId);
        Assert.False(await enumerator.MoveNextAsync());
    }

    [Fact]
    public async Task M5_DN_UT_005_EventsClient_WhenAuthenticateFails_ShouldThrowDevHubRpcException()
    {
        var runtimeDir = await CreateRuntimeAsync();
        var connection = new FakeWebSocketConnection();
        connection.OnSend = sent =>
        {
            return sent.Contains("\"id\":\"ws-auth-1\"", StringComparison.Ordinal)
                ? [CreateTextMessage("""{"jsonrpc":"2.0","id":"ws-auth-1","error":{"code":-32001,"message":"unauthorized","data":{"reason":"invalid_token"}}}""")]
                : [];
        };
        var factory = new FakeWebSocketConnectionFactory(connection);
        await using var client = await DevHubEventsClient.FromRuntimeAsync(
            new DevHubClientOptions
            {
                ClientId = "ws-client",
                RuntimeDir = runtimeDir
            },
            factory,
            () => "ws-auth-1");

        var exception = await Assert.ThrowsAsync<DevHubRpcException>(() => client.AuthenticateAsync());
        Assert.Equal(-32001, exception.Code);
        Assert.Equal("unauthorized", exception.Message);
        Assert.Equal("invalid_token", exception.Data!.Value.GetProperty("reason").GetString());
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    private async Task<string> CreateRuntimeAsync()
    {
        var runtimeDir = Path.Combine(_tempRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(runtimeDir);
        var tokenFile = Path.Combine(runtimeDir, "token.txt");
        await File.WriteAllTextAsync(tokenFile, "token-1");
        await File.WriteAllTextAsync(
            Path.Combine(runtimeDir, "hub.json"),
            $$"""
            {
              "protocolVersion": 1,
              "pid": 12345,
              "httpBaseUrl": "http://127.0.0.1:47231",
              "wsUrl": "ws://127.0.0.1:47231/ws",
              "tokenFile": "{{tokenFile.Replace("\\", "\\\\")}}",
              "startedAtUtc": "2026-03-09T00:00:00Z",
              "runtimeTuning": {
                "leaseSeconds": 30,
                "onlineThresholdSeconds": 30,
                "launchDedupeWindowSeconds": 30
              }
            }
            """);
        return runtimeDir;
    }

    private static WebSocketReceiveMessage CreateTextMessage(string text)
    {
        return new WebSocketReceiveMessage
        {
            MessageType = WebSocketMessageType.Text,
            Text = text
        };
    }

    private static WebSocketReceiveMessage CreateCloseMessage()
    {
        return new WebSocketReceiveMessage
        {
            MessageType = WebSocketMessageType.Close,
            CloseStatus = WebSocketCloseStatus.NormalClosure,
            CloseStatusDescription = "done"
        };
    }

    private sealed class FakeWebSocketConnectionFactory : IWebSocketConnectionFactory
    {
        private readonly IWebSocketConnection _connection;

        public FakeWebSocketConnectionFactory(IWebSocketConnection connection)
        {
            _connection = connection;
        }

        public Task<IWebSocketConnection> ConnectAsync(Uri uri, CancellationToken cancellationToken)
        {
            return Task.FromResult(_connection);
        }
    }

    private sealed class FakeWebSocketConnection : IWebSocketConnection
    {
        private readonly Queue<WebSocketReceiveMessage> _messages = new();
        private readonly SemaphoreSlim _messageSignal = new(0);
        private readonly TaskCompletionSource<bool> _closeObserved = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<string> SentTexts { get; } = [];

        public Func<string, IEnumerable<WebSocketReceiveMessage>>? OnSend { get; set; }

        public WebSocketState State { get; private set; } = WebSocketState.Open;

        public void Enqueue(WebSocketReceiveMessage message)
        {
            _messages.Enqueue(message);
            _messageSignal.Release();
        }

        public Task SendTextAsync(string text, CancellationToken cancellationToken)
        {
            SentTexts.Add(text);

            if (OnSend is not null)
            {
                foreach (var message in OnSend(text))
                {
                    Enqueue(message);
                }
            }

            return Task.CompletedTask;
        }

        public async Task<WebSocketReceiveMessage> ReceiveAsync(CancellationToken cancellationToken)
        {
            await _messageSignal.WaitAsync(cancellationToken);
            var message = _messages.Dequeue();
            if (message.MessageType == WebSocketMessageType.Close)
            {
                _closeObserved.TrySetResult(true);
            }

            return message;
        }

        public Task WaitForCloseObservedAsync()
        {
            return _closeObserved.Task;
        }

        public Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            State = WebSocketState.Closed;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            State = WebSocketState.Closed;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class SequenceRequestIdFactory
    {
        private readonly Queue<string> _requestIds;

        public SequenceRequestIdFactory(params string[] requestIds)
        {
            _requestIds = new Queue<string>(requestIds);
        }

        public string Create()
        {
            return _requestIds.Dequeue();
        }
    }
}
