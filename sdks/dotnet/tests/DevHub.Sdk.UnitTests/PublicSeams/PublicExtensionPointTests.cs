using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using DevHub.Sdk.Internal;
using DevHub.Sdk.Models;

namespace DevHub.Sdk.UnitTests.PublicSeams;

public sealed class PublicExtensionPointTests
{
    private const string ExpectedHubVersion = "test-hub-version";

    [Fact]
    public async Task DevHubClient_FromRuntime_WithInjectedRuntimeResolverAndHttpClientProvider_ShouldUsePublicSeams()
    {
        var connectionInfo = CreateConnectionInfo();
        var runtimeResolver = new RecordingRuntimeResolver(connectionInfo);
        var httpClientProvider = new RecordingHttpClientProvider();

        await using var client = await DevHubClient.FromRuntimeAsync(
            new DevHubClientOptions
            {
                ClientId = "public-http-client",
                DataDir = @"D:\sdk-test\data"
            },
            new DevHubClientDependencies
            {
                RuntimeResolver = runtimeResolver,
                HttpClientProvider = httpClientProvider
            });

        var ping = await client.PingAsync(new { channel = "http" });

        Assert.True(ping.Ok);
        Assert.Equal("public-http-client", runtimeResolver.LastOptions!.ClientId);
        Assert.Equal(@"D:\sdk-test\data", runtimeResolver.LastOptions.DataDir);
        Assert.Equal("public-http-client", httpClientProvider.LastOptions!.ClientId);
        Assert.Equal(connectionInfo, httpClientProvider.LastConnectionInfo);
        Assert.Equal("Bearer token-public", httpClientProvider.LastRequest!.Authorization);
        Assert.Equal("1", httpClientProvider.LastRequest.Protocol);
        Assert.Equal("public-http-client", httpClientProvider.LastRequest.ClientId);
        Assert.Equal("hub.ping", httpClientProvider.LastRequest.Method);
    }

    [Fact]
    public async Task DevHubClient_FromRuntime_WithInjectedRuntimeResolverAndHttpClientProvider_ShouldSupportVersionCompatibilityApis()
    {
        var connectionInfo = CreateConnectionInfo();
        var runtimeResolver = new RecordingRuntimeResolver(connectionInfo);
        var httpClientProvider = new RecordingHttpClientProvider();

        await using var client = await DevHubClient.FromRuntimeAsync(
            new DevHubClientOptions
            {
                ClientId = "public-http-client",
                DataDir = @"D:\sdk-test\data"
            },
            new DevHubClientDependencies
            {
                RuntimeResolver = runtimeResolver,
                HttpClientProvider = httpClientProvider
            });

        var hostVersion = await client.GetHostVersionAsync();
        var compatibility = await client.CheckVersionCompatibilityAsync();

        Assert.Equal(SdkVersionSource.CurrentVersion, hostVersion);
        Assert.Equal(SdkVersionSource.CurrentVersion, compatibility.SdkVersion);
        Assert.Equal(SdkVersionSource.CurrentVersion, compatibility.HostVersion);
        Assert.Equal(VersionCompatibilityStatus.Compatible, compatibility.Status);
        Assert.Equal(connectionInfo, httpClientProvider.LastConnectionInfo);
        Assert.Equal("hub.getVersion", httpClientProvider.LastRequest!.Method);
    }

    [Fact]
    public async Task DevHubClient_AfterDispose_ShouldRejectRpcWithoutInvokingTransport()
    {
        var connectionInfo = CreateConnectionInfo();
        var httpClientProvider = new RecordingHttpClientProvider();

        var client = await DevHubClient.FromRuntimeAsync(
            new DevHubClientOptions
            {
                ClientId = "public-http-client",
                DataDir = @"D:\sdk-test\data"
            },
            new DevHubClientDependencies
            {
                RuntimeResolver = new RecordingRuntimeResolver(connectionInfo),
                HttpClientProvider = httpClientProvider
            });

        await client.DisposeAsync();
        await client.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => client.PingAsync());
        Assert.Null(httpClientProvider.LastRequest);
    }

    [Fact]
    public async Task DevHubEventsClient_FromRuntime_WithInjectedRuntimeResolver_ShouldUsePublicSeams()
    {
        var connectionInfo = CreateConnectionInfo();
        var runtimeResolver = new RecordingRuntimeResolver(connectionInfo);

        await using var client = await DevHubEventsClient.FromRuntimeAsync(
            new DevHubClientOptions
            {
                ClientId = "public-events-client",
                DataDir = @"D:\sdk-test\data"
            },
            new DevHubEventsClientDependencies
            {
                RuntimeResolver = runtimeResolver
            });

        Assert.Equal("public-events-client", runtimeResolver.LastOptions!.ClientId);
        Assert.Equal(connectionInfo.Runtime.WsUrl, client.Runtime.WsUrl);
    }

    [Fact]
    public void PublicSurface_ShouldHideLowLevelTransportAndSessionTypes()
    {
        var exportedTypeNames = typeof(DevHubClient).Assembly.GetExportedTypes().Select(type => type.Name).ToArray();

        Assert.Contains("VersionCompatibilityResult", exportedTypeNames);
        Assert.Contains("VersionCompatibilityStatus", exportedTypeNames);
        Assert.DoesNotContain("IDevHubHttpTransport", exportedTypeNames);
        Assert.DoesNotContain("JsonRpcHttpTransport", exportedTypeNames);
        Assert.DoesNotContain("IDevHubWebSocketSession", exportedTypeNames);
        Assert.DoesNotContain("JsonRpcWebSocketSession", exportedTypeNames);
    }

    [Fact]
    public void DevHubEventType_Parse_WhenUnknownValueProvided_ShouldThrowArgumentException()
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

    private sealed class RecordingHttpClientProvider : IDevHubHttpClientProvider
    {
        private readonly RecordingHandler _handler = new();

        public DevHubRuntimeConnectionInfo? LastConnectionInfo { get; private set; }

        public DevHubClientOptions? LastOptions { get; private set; }

        public CapturedRequest? LastRequest => _handler.LastRequest;

        public HttpClient CreateClient(DevHubClientOptions options, DevHubRuntimeConnectionInfo connectionInfo)
        {
            LastOptions = options.Clone();
            LastConnectionInfo = connectionInfo;
            return new HttpClient(_handler)
            {
                Timeout = Timeout.InfiniteTimeSpan
            };
        }
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public CapturedRequest? LastRequest { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            using var document = JsonDocument.Parse(body);
            var requestId = document.RootElement.GetProperty("id").GetString() ?? string.Empty;
            var method = document.RootElement.GetProperty("method").GetString() ?? string.Empty;

            LastRequest = new CapturedRequest
            {
                Authorization = request.Headers.Authorization?.ToString() ?? string.Empty,
                Protocol = request.Headers.GetValues("X-DevHub-Protocol").Single(),
                ClientId = request.Headers.GetValues("X-DevHub-ClientId").Single(),
                RequestUri = request.RequestUri,
                Method = method
            };

            var payload = method switch
            {
                "hub.getVersion" => $"{{\"jsonrpc\":\"2.0\",\"id\":\"{requestId}\",\"result\":{{\"ok\":true,\"version\":\"{SdkVersionSource.CurrentVersion}\"}}}}",
                _ => $"{{\"jsonrpc\":\"2.0\",\"id\":\"{requestId}\",\"result\":{{\"ok\":true,\"serverTimeUtc\":\"2026-03-09T00:00:00Z\",\"echo\":{{\"channel\":\"http\"}}}}}}"
            };

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class CapturedRequest
    {
        public string Authorization { get; init; } = string.Empty;

        public string Protocol { get; init; } = string.Empty;

        public string ClientId { get; init; } = string.Empty;

        public Uri? RequestUri { get; init; }

        public string Method { get; init; } = string.Empty;
    }
}
