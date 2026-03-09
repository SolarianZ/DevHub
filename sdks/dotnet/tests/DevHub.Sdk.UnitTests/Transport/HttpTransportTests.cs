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
        }, handler, () => "req-ping");

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

    [Fact]
    public async Task M5_DN_UT_003_HttpTransport_WhenRequestTimeoutExceeded_ShouldThrowOperationCanceledException()
    {
        var runtimeDir = await CreateRuntimeAsync();
        var handler = new BlockingHandler();

        await using var client = await DevHubClient.FromRuntimeAsync(new DevHubClientOptions
        {
            ClientId = "client-a",
            RuntimeDir = runtimeDir,
            RequestTimeout = TimeSpan.FromMilliseconds(50)
        }, handler);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.PingAsync(cancellationToken: CancellationToken.None));
    }

    [Fact]
    public async Task M5_DN_UT_003_HttpTransport_WhenCallerCancellationRequested_ShouldThrowOperationCanceledException()
    {
        var runtimeDir = await CreateRuntimeAsync();
        var handler = new BlockingHandler();

        await using var client = await DevHubClient.FromRuntimeAsync(new DevHubClientOptions
        {
            ClientId = "client-a",
            RuntimeDir = runtimeDir
        }, handler, () => "req-ping");

        using var cancellationTokenSource = new CancellationTokenSource(millisecondsDelay: 50);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.PingAsync(cancellationToken: cancellationTokenSource.Token));
    }

    [Fact]
    public async Task M5_DN_UT_004_HttpTransport_WhenSuccessPayloadOkFalse_ShouldThrowInvalidOperationException()
    {
        var runtimeDir = await CreateRuntimeAsync();
        var handler = new StaticResponseHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"jsonrpc\":\"2.0\",\"id\":\"req-ping\",\"result\":{\"ok\":false,\"serverTimeUtc\":\"2026-03-09T00:00:00Z\"}}", Encoding.UTF8, "application/json")
        });

        await using var client = await DevHubClient.FromRuntimeAsync(new DevHubClientOptions
        {
            ClientId = "client-a",
            RuntimeDir = runtimeDir
        }, handler, () => "req-ping");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => client.PingAsync(cancellationToken: CancellationToken.None));
        Assert.Contains("hub.ping.result", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task M5_DN_UT_004_HttpTransport_WhenLaunchResultMissingLaunchId_ShouldThrowInvalidOperationException()
    {
        var runtimeDir = await CreateRuntimeAsync();
        var handler = new StaticResponseHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"jsonrpc\":\"2.0\",\"id\":\"req-launch\",\"result\":{\"ok\":true,\"status\":\"started\",\"pid\":12345}}", Encoding.UTF8, "application/json")
        });

        await using var client = await DevHubClient.FromRuntimeAsync(new DevHubClientOptions
        {
            ClientId = "client-a",
            RuntimeDir = runtimeDir
        }, handler, () => "req-launch");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => client.LaunchAsync(new LaunchRequest
        {
            AppId = "sample.app"
        }, CancellationToken.None));
        Assert.Contains("launchId", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task M5_DN_UT_004_HttpTransport_WhenResponseJsonRpcVersionInvalid_ShouldThrowInvalidOperationException()
    {
        var runtimeDir = await CreateRuntimeAsync();
        var handler = new StaticResponseHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"jsonrpc\":\"1.0\",\"id\":\"req-ping\",\"result\":{\"ok\":true,\"serverTimeUtc\":\"2026-03-09T00:00:00Z\"}}", Encoding.UTF8, "application/json")
        });

        await using var client = await DevHubClient.FromRuntimeAsync(new DevHubClientOptions
        {
            ClientId = "client-a",
            RuntimeDir = runtimeDir
        }, handler, () => "req-ping");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => client.PingAsync(cancellationToken: CancellationToken.None));
        Assert.Contains("jsonrpc", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task M5_DN_UT_004_HttpTransport_WhenResponseIdMismatched_ShouldThrowInvalidOperationException()
    {
        var runtimeDir = await CreateRuntimeAsync();
        var handler = new StaticResponseHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"jsonrpc\":\"2.0\",\"id\":\"req-other\",\"result\":{\"ok\":true,\"serverTimeUtc\":\"2026-03-09T00:00:00Z\"}}", Encoding.UTF8, "application/json")
        });

        await using var client = await DevHubClient.FromRuntimeAsync(
            new DevHubClientOptions
            {
                ClientId = "client-a",
                RuntimeDir = runtimeDir
            },
            handler,
            () => "req-ping");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => client.PingAsync(cancellationToken: CancellationToken.None));
        Assert.Contains("id", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task M5_DN_UT_004_HttpTransport_WhenResponseContainsResultAndError_ShouldThrowInvalidOperationException()
    {
        var runtimeDir = await CreateRuntimeAsync();
        var handler = new StaticResponseHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"jsonrpc\":\"2.0\",\"id\":\"req-ping\",\"result\":{\"ok\":true,\"serverTimeUtc\":\"2026-03-09T00:00:00Z\"},\"error\":{\"code\":-32603,\"message\":\"internal_error\"}}", Encoding.UTF8, "application/json")
        });

        await using var client = await DevHubClient.FromRuntimeAsync(new DevHubClientOptions
        {
            ClientId = "client-a",
            RuntimeDir = runtimeDir
        }, handler, () => "req-ping");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => client.PingAsync(cancellationToken: CancellationToken.None));
        Assert.Contains("result", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task M5_DN_UT_004_HttpTransport_WhenGetDefinitionResultMissingDisplayName_ShouldThrowInvalidOperationException()
    {
        var runtimeDir = await CreateRuntimeAsync();
        var handler = new StaticResponseHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{" +
                "\"jsonrpc\":\"2.0\"," +
                "\"id\":\"req-get-definition\"," +
                "\"result\":{\"ok\":true,\"definition\":{\"appId\":\"sample.app\"}}}", Encoding.UTF8, "application/json")
        });

        await using var client = await DevHubClient.FromRuntimeAsync(new DevHubClientOptions
        {
            ClientId = "client-a",
            RuntimeDir = runtimeDir
        }, handler, () => "req-get-definition");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetDefinitionAsync("sample.app", CancellationToken.None));
        Assert.Contains("displayName", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task M5_DN_UT_004_HttpTransport_WhenRegisterInstanceResultMissingLastSeenUtc_ShouldThrowInvalidOperationException()
    {
        var runtimeDir = await CreateRuntimeAsync();
        var handler = new StaticResponseHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{" +
                "\"jsonrpc\":\"2.0\"," +
                "\"id\":\"req-register\"," +
                "\"result\":{\"ok\":true,\"instance\":{\"instanceId\":\"inst-1\",\"appId\":\"sample.app\",\"pid\":12345,\"registeredAtUtc\":\"2026-03-09T00:00:00Z\",\"invoke\":{\"poll\":true,\"respond\":true}}}}", Encoding.UTF8, "application/json")
        });

        await using var client = await DevHubClient.FromRuntimeAsync(new DevHubClientOptions
        {
            ClientId = "client-a",
            RuntimeDir = runtimeDir
        }, handler, () => "req-register");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => client.RegisterInstanceAsync(new AppInstanceRegistration
        {
            InstanceId = "inst-1",
            AppId = "sample.app",
            Pid = 12345,
            Invoke = new InvokeCapability
            {
                Poll = true,
                Respond = true
            }
        }, CancellationToken.None));

        Assert.Contains("lastSeenUtc", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task M5_DN_UT_004_HttpTransport_WhenRequestResultMissingValue_ShouldThrowInvalidOperationException()
    {
        var runtimeDir = await CreateRuntimeAsync();
        var handler = new StaticResponseHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{" +
                "\"jsonrpc\":\"2.0\"," +
                "\"id\":\"req-request\"," +
                "\"result\":{\"ok\":true,\"invocationId\":\"invk-1\"}}", Encoding.UTF8, "application/json")
        });

        await using var client = await DevHubClient.FromRuntimeAsync(new DevHubClientOptions
        {
            ClientId = "client-a",
            RuntimeDir = runtimeDir
        }, handler, () => "req-request");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => client.RequestAsync(new InvokeRequest
        {
            AppId = "sample.app",
            Method = "sample.request"
        }, CancellationToken.None));

        Assert.Contains("value", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task M5_DN_UT_004_HttpTransport_WhenPollResultItemMissingCallerSessionId_ShouldThrowInvalidOperationException()
    {
        var runtimeDir = await CreateRuntimeAsync();
        var handler = new StaticResponseHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{" +
                "\"jsonrpc\":\"2.0\"," +
                "\"id\":\"req-poll\"," +
                "\"result\":{\"ok\":true,\"serverTimeUtc\":\"2026-03-09T00:00:00Z\",\"items\":[{\"invocationId\":\"invk-1\",\"appId\":\"sample.app\",\"target\":{},\"method\":\"sample.notify\",\"kind\":\"notify\",\"createdAtUtc\":\"2026-03-09T00:00:00Z\",\"caller\":{\"clientId\":\"caller-a\"}}]}}", Encoding.UTF8, "application/json")
        });

        await using var client = await DevHubClient.FromRuntimeAsync(new DevHubClientOptions
        {
            ClientId = "client-a",
            RuntimeDir = runtimeDir
        }, handler, () => "req-poll");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => client.PollAsync(new PollRequest
        {
            InstanceId = "inst-1"
        }, CancellationToken.None));

        Assert.Contains("clientSessionId", exception.Message, StringComparison.Ordinal);
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

    private sealed class StaticResponseHandler : HttpMessageHandler
    {
        private readonly HttpResponseMessage _response;

        public StaticResponseHandler(HttpResponseMessage response)
        {
            _response = response;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(_response);
        }
    }

    private sealed class BlockingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private sealed record CapturedRequest(string RequestUri, string Authorization, string Protocol, string ClientId, string ClientSessionId, string Body);
}
