using System.Net;
using System.Net.WebSockets;
using System.Text;
using DevHub.Sdk.Internal;

namespace DevHub.Sdk.UnitTests.Threading;

/// <summary>
/// 验证 SDK 库级 async/await 不捕获调用方同步上下文。
/// </summary>
public sealed class SynchronizationContextRegressionTests : IDisposable
{
    private readonly string _tempRoot;

    public SynchronizationContextRegressionTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "DevHubSdkSyncContextTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    [Fact]
    public async Task HttpClientApi_ShouldCompleteOnSingleThreadSynchronizationContext()
    {
        var dataDir = await CreateDataDirectoryAsync();
        var handler = new DelayedJsonResponseHandler(() =>
            """{"jsonrpc":"2.0","id":"req-ping","result":{"ok":true,"serverTimeUtc":"2026-03-09T00:00:00Z","echo":{"channel":"http"}}}""");

        await RunOnThrowingSynchronizationContextAsync(() =>
        {
            var client = DevHubClient.FromRuntimeAsync(
                new DevHubClientOptions
                {
                    ClientId = "sync-http-client",
                    DataDir = dataDir
                },
                handler,
                () => "req-ping").GetAwaiter().GetResult();

            try
            {
                var result = client.PingAsync(new { channel = "http" }, CancellationToken.None).GetAwaiter().GetResult();
                Assert.True(result.Ok);
                Assert.Equal("http", (string?)result.Echo?["channel"]);
            }
            finally
            {
                client.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        });
    }

    [Fact]
    public async Task WebSocketEventsApi_ShouldCompleteOnSingleThreadSynchronizationContext()
    {
        var dataDir = await CreateDataDirectoryAsync();
        var connection = new AsyncFakeWebSocketConnection
        {
            OnSend = sent =>
            {
                if (sent.Contains("\"id\":\"ws-auth-1\"", StringComparison.Ordinal))
                {
                    return
                    [
                        CreateTextMessage("""{"jsonrpc":"2.0","id":"ws-auth-1","result":{"ok":true,"protocolVersion":1}}""")
                    ];
                }

                if (sent.Contains("\"id\":\"ws-ping-1\"", StringComparison.Ordinal))
                {
                    return
                    [
                        CreateTextMessage("""{"jsonrpc":"2.0","id":"ws-ping-1","result":{"ok":true,"serverTimeUtc":"2026-03-09T00:00:00Z","echo":{"channel":"ws"}}}""")
                    ];
                }

                return [];
            }
        };

        await RunOnThrowingSynchronizationContextAsync(() =>
        {
            var client = DevHubEventsClient.FromRuntimeAsync(
                new DevHubClientOptions
                {
                    ClientId = "sync-ws-client",
                    DataDir = dataDir
                },
                new AsyncFakeWebSocketConnectionFactory(connection),
                new SequenceRequestIdFactory("ws-auth-1", "ws-ping-1").Create).GetAwaiter().GetResult();

            try
            {
                client.AuthenticateAsync(CancellationToken.None).GetAwaiter().GetResult();
                var result = client.PingAsync(new { channel = "ws" }, CancellationToken.None).GetAwaiter().GetResult();
                Assert.True(result.Ok);
                Assert.Equal("ws", (string?)result.Echo?["channel"]);
            }
            finally
            {
                client.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        });

        Assert.Collection(
            connection.SentTexts,
            sent => Assert.Contains("hub.ws.authenticate", sent, StringComparison.Ordinal),
            sent => Assert.Contains("hub.ping", sent, StringComparison.Ordinal));
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    private async Task<string> CreateDataDirectoryAsync()
    {
        var dataDir = Path.Combine(_tempRoot, Guid.NewGuid().ToString("N"));
        var runtimeDir = Path.Combine(dataDir, "runtime");
        Directory.CreateDirectory(runtimeDir);
        var tokenFile = Path.Combine(runtimeDir, "token.txt");
        await File.WriteAllTextAsync(tokenFile, "token-1").ConfigureAwait(false);
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
                "launchDedupeWindowSeconds": 30,
                "launchRegisterTimeoutSeconds": 30
              }
            }
            """).ConfigureAwait(false);
        return dataDir;
    }

    private static async Task RunOnThrowingSynchronizationContextAsync(Action action, int timeoutMilliseconds = 5000)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var previousContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(new ThrowingSynchronizationContext());
            try
            {
                action();
                completion.TrySetResult();
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previousContext);
            }
        })
        {
            IsBackground = true,
            Name = "DevHubSdkSyncContextRegression"
        };

        thread.Start();

        try
        {
            await completion.Task.WaitAsync(TimeSpan.FromMilliseconds(timeoutMilliseconds)).ConfigureAwait(false);
        }
        catch (TimeoutException exception)
        {
            throw new TimeoutException("SDK 调用在单线程 SynchronizationContext 下未能及时完成，疑似捕获了调用方上下文。", exception);
        }
    }

    private static WebSocketReceiveMessage CreateTextMessage(string text)
    {
        return new WebSocketReceiveMessage
        {
            MessageType = WebSocketMessageType.Text,
            Text = text
        };
    }

    private sealed class DelayedJsonResponseHandler(Func<string> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.Run(async () =>
            {
                await Task.Delay(20, cancellationToken).ConfigureAwait(false);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(responseFactory(), Encoding.UTF8, "application/json")
                };
            }, cancellationToken);
        }
    }

    private sealed class AsyncFakeWebSocketConnectionFactory(IWebSocketConnection connection) : IWebSocketConnectionFactory
    {
        public Task<IWebSocketConnection> ConnectAsync(Uri uri, CancellationToken cancellationToken)
        {
            return Task.Run(async () =>
            {
                await Task.Delay(20, cancellationToken).ConfigureAwait(false);
                return connection;
            }, cancellationToken);
        }
    }

    private sealed class AsyncFakeWebSocketConnection : IWebSocketConnection
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<WebSocketReceiveMessage> _messages = new();
        private readonly SemaphoreSlim _messageSignal = new(0);

        public List<string> SentTexts { get; } = [];

        public Func<string, IEnumerable<WebSocketReceiveMessage>>? OnSend { get; set; }

        public WebSocketState State { get; private set; } = WebSocketState.Open;

        public Task SendTextAsync(string text, CancellationToken cancellationToken)
        {
            SentTexts.Add(text);

            if (OnSend is not null)
            {
                foreach (var message in OnSend(text))
                {
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(20, CancellationToken.None).ConfigureAwait(false);
                        _messages.Enqueue(message);
                        _messageSignal.Release();
                    });
                }
            }

            return Task.Run(async () => await Task.Delay(10, cancellationToken).ConfigureAwait(false), cancellationToken);
        }

        public async Task<WebSocketReceiveMessage> ReceiveAsync(CancellationToken cancellationToken)
        {
            await _messageSignal.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (!_messages.TryDequeue(out var message))
            {
                throw new InvalidOperationException("测试消息队列状态非法。");
            }

            return message;
        }

        public Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            State = WebSocketState.Closed;
            return Task.Run(async () => await Task.Delay(10, cancellationToken).ConfigureAwait(false), cancellationToken);
        }

        public ValueTask DisposeAsync()
        {
            State = WebSocketState.Closed;
            _messageSignal.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class SequenceRequestIdFactory(params string[] requestIds)
    {
        private readonly Queue<string> _requestIds = new(requestIds);

        public string Create()
        {
            return _requestIds.Dequeue();
        }
    }

    private sealed class ThrowingSynchronizationContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state)
        {
            throw new InvalidOperationException("测试 SynchronizationContext 不允许异步续体回到调用线程。");
        }

        public override void Send(SendOrPostCallback d, object? state)
        {
            throw new InvalidOperationException("测试 SynchronizationContext 不允许同步回调切回调用线程。");
        }
    }
}
