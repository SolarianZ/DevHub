using System.Text.Json;
using DevHub.Sdk.Models;

namespace DevHub.Sdk.UnitTests.PublicSeams;

public sealed class PublicExtensionPointTests
{
    private const string ExpectedHubVersion = "test-hub-version";

    [Fact]
    public async Task M5_DN_UT_008_DevHubClient_FromRuntime_WithInjectedRuntimeResolverAndTransportFactory_ShouldUsePublicSeams()
    {
        var connectionInfo = CreateConnectionInfo();
        var runtimeResolver = new RecordingRuntimeResolver(connectionInfo);
        var transportFactory = new RecordingHttpTransportFactory();

        await using var client = await DevHubClient.FromRuntimeAsync(
            new DevHubClientOptions
            {
                ClientId = "public-http-client",
                DataDir = @"D:\sdk-test\data"
            },
            new DevHubClientDependencies
            {
                RuntimeResolver = runtimeResolver,
                TransportFactory = transportFactory
            });

        var ping = await client.PingAsync(new { channel = "http" });

        Assert.True(ping.Ok);
        Assert.Equal("public-http-client", runtimeResolver.LastOptions!.ClientId);
        Assert.Equal(@"D:\sdk-test\data", runtimeResolver.LastOptions.DataDir);
        Assert.Equal(connectionInfo, transportFactory.LastConnectionInfo);
        Assert.Collection(transportFactory.Transport.Methods, method => Assert.Equal("hub.ping", method));
    }

    [Fact]
    public async Task M6_DN_UT_001_DevHubClient_AfterDispose_ShouldRejectRpcWithoutInvokingTransport()
    {
        var connectionInfo = CreateConnectionInfo();
        var transportFactory = new RecordingHttpTransportFactory();

        var client = await DevHubClient.FromRuntimeAsync(
            new DevHubClientOptions
            {
                ClientId = "public-http-client",
                DataDir = @"D:\sdk-test\data"
            },
            new DevHubClientDependencies
            {
                RuntimeResolver = new RecordingRuntimeResolver(connectionInfo),
                TransportFactory = transportFactory
            });

        await client.DisposeAsync();
        await client.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => client.PingAsync());
        Assert.Empty(transportFactory.Transport.Methods);
        Assert.Equal(1, transportFactory.Transport.DisposeCallCount);
    }

    [Fact]
    public async Task M5_DN_UT_008_DevHubEventsClient_FromRuntime_WithInjectedRuntimeResolverAndSessionFactory_ShouldUsePublicSeams()
    {
        var connectionInfo = CreateConnectionInfo();
        var runtimeResolver = new RecordingRuntimeResolver(connectionInfo);
        var sessionFactory = new RecordingWebSocketSessionFactory();

        await using var client = await DevHubEventsClient.FromRuntimeAsync(
            new DevHubClientOptions
            {
                ClientId = "public-events-client",
                DataDir = @"D:\sdk-test\data"
            },
            new DevHubEventsClientDependencies
            {
                RuntimeResolver = runtimeResolver,
                SessionFactory = sessionFactory
            });

        await client.AuthenticateAsync();
        var subscriptionId = await client.SubscribeAsync(new[] { DevHubEventTypes.InvocationCompleted });
        var first = await client.ReadEventsAsync().GetAsyncEnumerator().MoveNextAsync();

        Assert.True(first);
        Assert.Equal("public-events-client", runtimeResolver.LastOptions!.ClientId);
        Assert.Equal(connectionInfo.WebSocketEndpoint, sessionFactory.LastOptions!.WebSocketEndpoint);
        Assert.Equal("sub-public", subscriptionId);
        Assert.Collection(
            sessionFactory.Session!.Methods,
            method => Assert.Equal("hub.ws.authenticate", method),
            method => Assert.Equal("hub.events.subscribe", method));
    }

    [Fact]
    public void M5_DN_UT_008_DevHubEventType_Parse_WhenUnknownValueProvided_ShouldThrowArgumentException()
    {
        Assert.Throws<ArgumentException>(() => DevHubEventType.Parse("unknown.type"));
    }

    [Fact]
    public void Impl_DevHubEventType_All_ShouldIncludeDefinitionLifecycleEvents()
    {
        Assert.Contains(DevHubEventTypes.AppDefinitionUpserted, DevHubEventTypes.All);
        Assert.Contains(DevHubEventTypes.AppDefinitionDeleted, DevHubEventTypes.All);
        Assert.Equal(DevHubEventTypes.AppDefinitionUpserted, DevHubEventType.Parse("app.definition.upserted"));
        Assert.Equal(DevHubEventTypes.AppDefinitionDeleted, DevHubEventType.Parse("app.definition.deleted"));
    }

    private static DevHubRuntimeConnectionInfo CreateConnectionInfo()
    {
        var runtime = new HubRuntime
        {
            ProtocolVersion = 1,
            HubVersion = ExpectedHubVersion,
            Pid = 12345,
            HttpBaseUrl = "http://127.0.0.1:57231",
            WsUrl = "ws://127.0.0.1:57231/ws",
            TokenFile = Path.Combine(Path.GetTempPath(), "devhub-sdk-public-seams-token.txt"),
            StartedAtUtc = DateTimeOffset.Parse("2026-03-09T00:00:00Z"),
            RuntimeTuning = new HubRuntimeTuning
            {
                LeaseSeconds = 30,
                OnlineThresholdSeconds = 30,
                LaunchDedupeWindowSeconds = 30
            }
        };

        return new DevHubRuntimeConnectionInfo(
            runtimeDirectory: Path.Combine(Path.GetTempPath(), "devhub-sdk-public-seams", "runtime"),
            token: "token-public",
            runtime: runtime);
    }

    private sealed class RecordingRuntimeResolver : IDevHubRuntimeResolver
    {
        private readonly DevHubRuntimeConnectionInfo _connectionInfo;

        public RecordingRuntimeResolver(DevHubRuntimeConnectionInfo connectionInfo)
        {
            _connectionInfo = connectionInfo;
        }

        public DevHubClientOptions? LastOptions { get; private set; }

        public Task<DevHubRuntimeConnectionInfo> ResolveAsync(DevHubClientOptions options, CancellationToken cancellationToken = default)
        {
            LastOptions = options.Clone();
            return Task.FromResult(_connectionInfo);
        }
    }

    private sealed class RecordingHttpTransportFactory : IDevHubHttpTransportFactory
    {
        public RecordingHttpTransport Transport { get; } = new();

        public DevHubRuntimeConnectionInfo? LastConnectionInfo { get; private set; }

        public IDevHubHttpTransport Create(DevHubClientOptions options, DevHubRuntimeConnectionInfo connectionInfo)
        {
            LastConnectionInfo = connectionInfo;
            return Transport;
        }
    }

    private sealed class RecordingHttpTransport : IDevHubHttpTransport
    {
        public List<string> Methods { get; } = [];

        public int DisposeCallCount { get; private set; }

        public Task<JsonElement> SendAsync(string method, object? parameters, CancellationToken cancellationToken)
        {
            Methods.Add(method);

            var payload = JsonSerializer.SerializeToElement(new
            {
                ok = true,
                serverTimeUtc = "2026-03-09T00:00:00Z",
                echo = new
                {
                    channel = "http"
                }
            });

            return Task.FromResult(payload);
        }

        public ValueTask DisposeAsync()
        {
            DisposeCallCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingWebSocketSessionFactory : IDevHubWebSocketSessionFactory
    {
        public DevHubWebSocketSessionOptions? LastOptions { get; private set; }

        public RecordingWebSocketSession? Session { get; private set; }

        public IDevHubWebSocketSession Create(DevHubWebSocketSessionOptions options)
        {
            LastOptions = options;
            Session = new RecordingWebSocketSession(options);
            return Session;
        }
    }

    private sealed class RecordingWebSocketSession : IDevHubWebSocketSession
    {
        private readonly DevHubWebSocketSessionOptions _options;

        public RecordingWebSocketSession(DevHubWebSocketSessionOptions options)
        {
            _options = options;
        }

        public List<string> Methods { get; } = [];

        public Task EnsureConnectedAsync(CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task<JsonElement> SendRequestAsync(string method, object? parameters, CancellationToken cancellationToken = default)
        {
            Methods.Add(method);

            if (string.Equals(method, "hub.ws.authenticate", StringComparison.Ordinal))
            {
                return Task.FromResult(JsonSerializer.SerializeToElement(new
                {
                    ok = true,
                    protocolVersion = 1
                }));
            }

            if (string.Equals(method, "hub.events.subscribe", StringComparison.Ordinal))
            {
                _options.OnEvent(JsonSerializer.SerializeToElement(new
                {
                    subscriptionId = "sub-public",
                    type = "invocation.completed",
                    timeUtc = "2026-03-09T00:00:00Z",
                    payload = new
                    {
                        invocationId = "invk-public-1"
                    }
                }));

                return Task.FromResult(JsonSerializer.SerializeToElement(new
                {
                    ok = true,
                    subscriptionId = "sub-public"
                }));
            }

            throw new InvalidOperationException($"unexpected method: {method}");
        }

        public Task DisconnectAsync(string reason, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }
}
