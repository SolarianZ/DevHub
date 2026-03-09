using System.Net;
using System.Text;
using DevHub.Sdk.Models;

namespace DevHub.Sdk.UnitTests.Transport;

/// <summary>
/// HTTP transport 白盒测试。
/// </summary>
public sealed class HttpTransportTests : IDisposable
{
    private readonly string _tempRoot;

    public HttpTransportTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "DevHubSdkHttpTransportTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    [Fact]
    public async Task M5_DN_UT_003_HttpTransport_ShouldAssembleRequiredHeaders()
    {
        var runtimeDir = await CreateRuntimeAsync();
        var handler = new CaptureHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"jsonrpc\":\"2.0\",\"id\":\"req-ping\",\"result\":{\"ok\":true,\"serverTimeUtc\":\"2026-03-09T00:00:00Z\"}}", Encoding.UTF8, "application/json")
        });

        await using var client = await DevHubClient.FromRuntimeAsync(new DevHubClientOptions
        {
            ClientId = "client-a",
            ClientSessionId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            RuntimeDir = runtimeDir
        }, handler);

        _ = await client.PingAsync(cancellationToken: CancellationToken.None);

        Assert.NotNull(handler.LastRequest);
        Assert.Equal("Bearer token-1", handler.LastRequest!.Authorization);
        Assert.Equal("1", handler.LastRequest.Protocol);
        Assert.Equal("client-a", handler.LastRequest.ClientId);
        Assert.Equal("11111111-1111-1111-1111-111111111111", handler.LastRequest.ClientSessionId);
        Assert.EndsWith("/rpc", handler.LastRequest.RequestUri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task M5_DN_UT_003_HttpTransport_WhenClientIdMissing_ShouldThrowArgumentException()
    {
        var runtimeDir = await CreateRuntimeAsync();

        await Assert.ThrowsAsync<ArgumentException>(() => DevHubClient.FromRuntimeAsync(new DevHubClientOptions
        {
            ClientId = "",
            RuntimeDir = runtimeDir
        }));
    }

    [Fact]
    public async Task M5_DN_UT_003_HttpTransport_WhenProtocolVersionMismatch_ShouldThrowArgumentOutOfRangeException()
    {
        var runtimeDir = await CreateRuntimeAsync();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => DevHubClient.FromRuntimeAsync(new DevHubClientOptions
        {
            ClientId = "client-a",
            ProtocolVersion = 2,
            RuntimeDir = runtimeDir
        }));
    }

    [Fact]
    public async Task M5_DN_UT_003_HttpTransport_WhenClientSessionIdEmpty_ShouldThrowArgumentException()
    {
        var runtimeDir = await CreateRuntimeAsync();

        await Assert.ThrowsAsync<ArgumentException>(() => DevHubClient.FromRuntimeAsync(new DevHubClientOptions
        {
            ClientId = "client-a",
            ClientSessionId = Guid.Empty,
            RuntimeDir = runtimeDir
        }));
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

    private sealed class CaptureHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responseFactory;

        public CaptureHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        {
            _responseFactory = responseFactory;
        }

        public CapturedRequest? LastRequest { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = new CapturedRequest(
                request.RequestUri?.ToString() ?? string.Empty,
                request.Headers.Authorization?.ToString() ?? string.Empty,
                request.Headers.GetValues("X-DevHub-Protocol").Single(),
                request.Headers.GetValues("X-DevHub-ClientId").Single(),
                request.Headers.GetValues("X-DevHub-ClientSessionId").Single(),
                await request.Content!.ReadAsStringAsync(cancellationToken));

            return _responseFactory(request);
        }
    }

    private sealed record CapturedRequest(string RequestUri, string Authorization, string Protocol, string ClientId, string ClientSessionId, string Body);
}
