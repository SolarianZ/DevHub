using System.Net;
using System.Text;
using System.Text.Json;
using DevHub.Sdk.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace DevHub.Sdk.DependencyInjection.UnitTests;

/// <summary>
/// 依赖注入注册测试。
/// </summary>
public sealed class ServiceCollectionExtensionsTests
{
    private const string ExpectedHubVersion = "test-hub-version";

    [Fact]
    public async Task AddDevHubSdk_WithNamedHttpClientCustomization_ShouldCreateClientsThroughHttpClientPipeline()
    {
        var runtimeResolver = new RecordingRuntimeResolver(CreateConnectionInfo());
        var handler = new RecordingHandler();

        var services = new ServiceCollection();
        services.AddDevHubSdk(options =>
        {
            options.ClientId = "di-client";
            options.RequestTimeout = TimeSpan.FromSeconds(5);
        });
        services.Replace(ServiceDescriptor.Singleton<IDevHubRuntimeResolver>(runtimeResolver));
        services.AddHttpClient(DevHubServiceCollectionExtensions.DefaultHttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => handler);

        using var provider = services.BuildServiceProvider();
        var clientFactory = provider.GetRequiredService<IDevHubClientFactory>();
        var eventsClientFactory = provider.GetRequiredService<IDevHubEventsClientFactory>();

        await using var client = await clientFactory.CreateAsync();
        await using var eventsClient = await eventsClientFactory.CreateAsync();
        var ping = await client.PingAsync(new { channel = "di" });

        Assert.True(ping.Ok);
        Assert.Equal("di-client", client.Options.ClientId);
        Assert.Equal("di-client", eventsClient.Options.ClientId);
        Assert.Equal("http://127.0.0.1:47231", client.Runtime.HttpBaseUrl);
        Assert.Equal("ws://127.0.0.1:47231/ws", eventsClient.Runtime.WsUrl);
        Assert.Equal("Bearer sdk-di-token", handler.LastRequest!.Authorization);
        Assert.Equal("di-client", handler.LastRequest.ClientId);
        Assert.Equal("hub.ping", handler.LastRequest.Method);
        Assert.Equal(2, runtimeResolver.ResolveCallCount);
    }

    [Fact]
    public async Task AddDevHubSdk_WithoutDelegate_ShouldHonorExternalOptionsConfiguration()
    {
        var services = new ServiceCollection();
        services.AddDevHubSdk();
        services.Configure<DevHubClientOptions>(options => options.ClientId = "configured-client");
        services.Replace(ServiceDescriptor.Singleton<IDevHubRuntimeResolver>(new RecordingRuntimeResolver(CreateConnectionInfo())));

        using var provider = services.BuildServiceProvider();
        var clientFactory = provider.GetRequiredService<IDevHubClientFactory>();
        var configuredOptions = provider.GetRequiredService<IOptions<DevHubClientOptions>>().Value;

        await using var client = await clientFactory.CreateAsync();

        Assert.Equal("configured-client", configuredOptions.ClientId);
        Assert.Equal("configured-client", client.Options.ClientId);
        Assert.Equal("http://127.0.0.1:47231", client.Runtime.HttpBaseUrl);
    }

    private static DevHubRuntimeConnectionInfo CreateConnectionInfo()
    {
        var runtime = new HubRuntime
        {
            ProtocolVersion = 1,
            HubVersion = ExpectedHubVersion,
            Pid = 12345,
            HttpBaseUrl = "http://127.0.0.1:47231",
            WsUrl = "ws://127.0.0.1:47231/ws",
            TokenFile = Path.Combine(Path.GetTempPath(), "devhub-sdk-di-token.txt"),
            StartedAtUtc = DateTimeOffset.Parse("2026-03-09T00:00:00Z"),
            RuntimeTuning = new HubRuntimeTuning
            {
                LeaseSeconds = 30,
                OnlineThresholdSeconds = 30,
                LaunchDedupeWindowSeconds = 30
            }
        };

        return new DevHubRuntimeConnectionInfo(
            runtimeDirectory: Path.Combine(Path.GetTempPath(), "devhub-sdk-di", "runtime"),
            token: "sdk-di-token",
            runtime: runtime);
    }

    private sealed class RecordingRuntimeResolver : IDevHubRuntimeResolver
    {
        private readonly DevHubRuntimeConnectionInfo _connectionInfo;

        public RecordingRuntimeResolver(DevHubRuntimeConnectionInfo connectionInfo)
        {
            _connectionInfo = connectionInfo;
        }

        public int ResolveCallCount { get; private set; }

        public Task<DevHubRuntimeConnectionInfo> ResolveAsync(DevHubClientOptions options, CancellationToken cancellationToken = default)
        {
            ResolveCallCount++;
            return Task.FromResult(_connectionInfo);
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
                ClientId = request.Headers.GetValues("X-DevHub-ClientId").Single(),
                Method = method
            };

            var payload = $"{{\"jsonrpc\":\"2.0\",\"id\":\"{requestId}\",\"result\":{{\"ok\":true,\"serverTimeUtc\":\"2026-03-09T00:00:00Z\",\"echo\":{{\"channel\":\"di\"}}}}}}";

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class CapturedRequest
    {
        public string Authorization { get; init; } = string.Empty;

        public string ClientId { get; init; } = string.Empty;

        public string Method { get; init; } = string.Empty;
    }
}
