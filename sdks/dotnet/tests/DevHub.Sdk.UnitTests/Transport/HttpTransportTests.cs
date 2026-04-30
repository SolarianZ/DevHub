using System.Net;
using System.Text;
using System.Text.Json;
using DevHub.Sdk.Internal;
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
    public async Task HttpTransport_ShouldAssembleRequiredHeaders()
    {
        var dataDir = await CreateDataDirectoryAsync();
        var handler = new CaptureHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"jsonrpc\":\"2.0\",\"id\":\"req-ping\",\"result\":{\"ok\":true,\"serverTimeUtc\":\"2026-03-09T00:00:00Z\"}}", Encoding.UTF8, "application/json")
        });

        await using var client = await DevHubClient.FromRuntimeAsync(new DevHubClientOptions
        {
            ClientId = "client-a",
            ClientSessionId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            DataDir = dataDir
        }, handler, () => "req-ping");

        _ = await client.PingAsync(cancellationToken: CancellationToken.None);

        Assert.NotNull(handler.LastRequest);
        Assert.Equal("Bearer token-1", handler.LastRequest!.Authorization);
        Assert.Equal("1", handler.LastRequest.Protocol);
        Assert.Equal("client-a", handler.LastRequest.ClientId);
        Assert.Equal("11111111-1111-1111-1111-111111111111", handler.LastRequest.ClientSessionId);
        Assert.EndsWith("/rpc", handler.LastRequest.RequestUri, StringComparison.Ordinal);
        Assert.DoesNotContain("\"params\"", handler.LastRequest.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HttpTransport_WhenGetHostVersionSucceeds_ShouldReturnVersionAndOmitParams()
    {
        var dataDir = await CreateDataDirectoryAsync();
        var handler = new CaptureHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "{\"jsonrpc\":\"2.0\",\"id\":\"req-get-version\",\"result\":{\"ok\":true,\"version\":\"1.2.3-beta.1+build.4\"}}",
                Encoding.UTF8,
                "application/json")
        });

        await using var client = await DevHubClient.FromRuntimeAsync(
            new DevHubClientOptions
            {
                ClientId = "client-a",
                DataDir = dataDir
            },
            handler,
            () => "req-get-version");

        var version = await client.GetHostVersionAsync(CancellationToken.None);

        Assert.Equal("1.2.3-beta.1+build.4", version);
        Assert.NotNull(handler.LastRequest);
        Assert.Contains("\"method\":\"hub.getVersion\"", handler.LastRequest!.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("\"params\"", handler.LastRequest.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HttpTransport_WhenGetHostVersionMethodNotFound_ShouldPropagateDevHubRpcException()
    {
        var dataDir = await CreateDataDirectoryAsync();
        var handler = new StaticResponseHandler(CreateMethodNotFoundResponse("req-get-version"));

        await using var client = await DevHubClient.FromRuntimeAsync(
            new DevHubClientOptions
            {
                ClientId = "client-a",
                DataDir = dataDir
            },
            handler,
            () => "req-get-version");

        var exception = await Assert.ThrowsAsync<DevHubRpcException>(() => client.GetHostVersionAsync(CancellationToken.None));

        Assert.Equal((int)DevHubRpcErrorCode.MethodNotFound, exception.Code);
        Assert.Equal("method_not_found", exception.Message);
    }

    [Fact]
    public async Task HttpTransport_WhenCheckVersionCompatibilityMethodNotFoundAndRuntimeHubVersionValid_ShouldUseFallback()
    {
        var fallbackVersion = CreateHostVersionWithPatchDelta(5);
        var dataDir = await CreateDataDirectoryAsync(fallbackVersion);
        var handler = new StaticResponseHandler(CreateMethodNotFoundResponse("req-check-version"));

        await using var client = await DevHubClient.FromRuntimeAsync(
            new DevHubClientOptions
            {
                ClientId = "client-a",
                DataDir = dataDir
            },
            handler,
            () => "req-check-version");

        var result = await client.CheckVersionCompatibilityAsync(CancellationToken.None);

        Assert.Equal(SdkVersionSource.CurrentVersion, result.SdkVersion);
        Assert.Equal(fallbackVersion, result.HostVersion);
        Assert.Equal(VersionCompatibilityStatus.Compatible, result.Status);
    }

    [Fact]
    public async Task HttpTransport_WhenCheckVersionCompatibilityMethodNotFoundAndRuntimeHubVersionInvalid_ShouldReturnUnknown()
    {
        const string invalidFallbackVersion = "not-semver";
        var dataDir = await CreateDataDirectoryAsync(invalidFallbackVersion);
        var handler = new StaticResponseHandler(CreateMethodNotFoundResponse("req-check-version"));

        await using var client = await DevHubClient.FromRuntimeAsync(
            new DevHubClientOptions
            {
                ClientId = "client-a",
                DataDir = dataDir
            },
            handler,
            () => "req-check-version");

        var result = await client.CheckVersionCompatibilityAsync(CancellationToken.None);

        Assert.Equal(SdkVersionSource.CurrentVersion, result.SdkVersion);
        Assert.Equal(invalidFallbackVersion, result.HostVersion);
        Assert.Equal(VersionCompatibilityStatus.Unknown, result.Status);
    }

    [Fact]
    public async Task HttpTransport_WhenClientIdMissing_ShouldThrowArgumentException()
    {
        var dataDir = await CreateDataDirectoryAsync();

        await Assert.ThrowsAsync<ArgumentException>(() => DevHubClient.FromRuntimeAsync(new DevHubClientOptions
        {
            ClientId = "",
            DataDir = dataDir
        }));
    }

    [Fact]
    public async Task HttpTransport_WhenProtocolVersionMismatch_ShouldThrowArgumentOutOfRangeException()
    {
        var dataDir = await CreateDataDirectoryAsync();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => DevHubClient.FromRuntimeAsync(new DevHubClientOptions
        {
            ClientId = "client-a",
            ProtocolVersion = 2,
            DataDir = dataDir
        }));
    }

    [Fact]
    public async Task HttpTransport_WhenClientSessionIdEmpty_ShouldThrowArgumentException()
    {
        var dataDir = await CreateDataDirectoryAsync();

        await Assert.ThrowsAsync<ArgumentException>(() => DevHubClient.FromRuntimeAsync(new DevHubClientOptions
        {
            ClientId = "client-a",
            ClientSessionId = Guid.Empty,
            DataDir = dataDir
        }));
    }

    [Fact]
    public async Task HttpTransport_WhenRequestTimeoutExceeded_ShouldThrowOperationCanceledException()
    {
        var dataDir = await CreateDataDirectoryAsync();
        var handler = new BlockingHandler();

        await using var client = await DevHubClient.FromRuntimeAsync(new DevHubClientOptions
        {
            ClientId = "client-a",
            DataDir = dataDir,
            RequestTimeout = TimeSpan.FromMilliseconds(50)
        }, handler);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.PingAsync(cancellationToken: CancellationToken.None));
    }

    [Fact]
    public async Task HttpTransport_WhenCallerCancellationRequested_ShouldThrowOperationCanceledException()
    {
        var dataDir = await CreateDataDirectoryAsync();
        var handler = new BlockingHandler();

        await using var client = await DevHubClient.FromRuntimeAsync(new DevHubClientOptions
        {
            ClientId = "client-a",
            DataDir = dataDir
        }, handler, () => "req-ping");

        using var cancellationTokenSource = new CancellationTokenSource(millisecondsDelay: 50);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.PingAsync(cancellationToken: cancellationTokenSource.Token));
    }

    [Fact]
    public async Task HttpTransport_WhenSuccessPayloadOkFalse_ShouldThrowInvalidOperationException()
    {
        var dataDir = await CreateDataDirectoryAsync();
        var handler = new StaticResponseHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"jsonrpc\":\"2.0\",\"id\":\"req-ping\",\"result\":{\"ok\":false,\"serverTimeUtc\":\"2026-03-09T00:00:00Z\"}}", Encoding.UTF8, "application/json")
        });

        await using var client = await DevHubClient.FromRuntimeAsync(new DevHubClientOptions
        {
            ClientId = "client-a",
            DataDir = dataDir
        }, handler, () => "req-ping");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => client.PingAsync(cancellationToken: CancellationToken.None));
        Assert.Contains("hub.ping.result", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HttpTransport_WhenLaunchResultMissingLaunchId_ShouldThrowInvalidOperationException()
    {
        var dataDir = await CreateDataDirectoryAsync();
        var handler = new StaticResponseHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"jsonrpc\":\"2.0\",\"id\":\"req-launch\",\"result\":{\"ok\":true,\"status\":\"started\",\"pid\":12345}}", Encoding.UTF8, "application/json")
        });

        await using var client = await DevHubClient.FromRuntimeAsync(new DevHubClientOptions
        {
            ClientId = "client-a",
            DataDir = dataDir
        }, handler, () => "req-launch");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => client.LaunchAsync(new LaunchRequest
        {
            AppId = "sample.app",
            Scope = string.Empty
        }, CancellationToken.None));
        Assert.Contains("launchId", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HttpTransport_WhenResponseJsonRpcVersionInvalid_ShouldThrowInvalidOperationException()
    {
        var dataDir = await CreateDataDirectoryAsync();
        var handler = new StaticResponseHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"jsonrpc\":\"1.0\",\"id\":\"req-ping\",\"result\":{\"ok\":true,\"serverTimeUtc\":\"2026-03-09T00:00:00Z\"}}", Encoding.UTF8, "application/json")
        });

        await using var client = await DevHubClient.FromRuntimeAsync(new DevHubClientOptions
        {
            ClientId = "client-a",
            DataDir = dataDir
        }, handler, () => "req-ping");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => client.PingAsync(cancellationToken: CancellationToken.None));
        Assert.Contains("jsonrpc", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HttpTransport_WhenResponseIdMismatched_ShouldThrowInvalidOperationException()
    {
        var dataDir = await CreateDataDirectoryAsync();
        var handler = new StaticResponseHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"jsonrpc\":\"2.0\",\"id\":\"req-other\",\"result\":{\"ok\":true,\"serverTimeUtc\":\"2026-03-09T00:00:00Z\"}}", Encoding.UTF8, "application/json")
        });

        await using var client = await DevHubClient.FromRuntimeAsync(
            new DevHubClientOptions
            {
                ClientId = "client-a",
                DataDir = dataDir
            },
            handler,
            () => "req-ping");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => client.PingAsync(cancellationToken: CancellationToken.None));
        Assert.Contains("id", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("1.5")]
    [InlineData("9223372036854775808")]
    public async Task HttpTransport_WhenResponseIdUsesUnsupportedNumericShape_ShouldThrowInvalidOperationException(string requestIdLiteral)
    {
        var dataDir = await CreateDataDirectoryAsync();
        var handler = new StaticResponseHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                $"{{\"jsonrpc\":\"2.0\",\"id\":{requestIdLiteral},\"result\":{{\"ok\":true,\"serverTimeUtc\":\"2026-03-09T00:00:00Z\"}}}}",
                Encoding.UTF8,
                "application/json")
        });

        await using var client = await DevHubClient.FromRuntimeAsync(
            new DevHubClientOptions
            {
                ClientId = "client-a",
                DataDir = dataDir
            },
            handler,
            () => requestIdLiteral);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => client.PingAsync(cancellationToken: CancellationToken.None));
        Assert.Contains("id", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Int64", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HttpTransport_WhenResponseContainsResultAndError_ShouldThrowInvalidOperationException()
    {
        var dataDir = await CreateDataDirectoryAsync();
        var handler = new StaticResponseHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"jsonrpc\":\"2.0\",\"id\":\"req-ping\",\"result\":{\"ok\":true,\"serverTimeUtc\":\"2026-03-09T00:00:00Z\"},\"error\":{\"code\":-32603,\"message\":\"internal_error\"}}", Encoding.UTF8, "application/json")
        });

        await using var client = await DevHubClient.FromRuntimeAsync(new DevHubClientOptions
        {
            ClientId = "client-a",
            DataDir = dataDir
        }, handler, () => "req-ping");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => client.PingAsync(cancellationToken: CancellationToken.None));
        Assert.Contains("result", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\"bad_data\"")]
    [InlineData("null")]
    [InlineData("[1,2,3]")]
    public async Task HttpTransport_WhenErrorDataIsNotObject_ShouldThrowInvalidOperationException(string errorDataLiteral)
    {
        var dataDir = await CreateDataDirectoryAsync();
        var handler = new StaticResponseHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                $"{{\"jsonrpc\":\"2.0\",\"id\":\"req-ping\",\"error\":{{\"code\":-32001,\"message\":\"unauthorized\",\"data\":{errorDataLiteral}}}}}",
                Encoding.UTF8,
                "application/json")
        });

        await using var client = await DevHubClient.FromRuntimeAsync(new DevHubClientOptions
        {
            ClientId = "client-a",
            DataDir = dataDir
        }, handler, () => "req-ping");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => client.PingAsync(cancellationToken: CancellationToken.None));
        Assert.Contains("error.data", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HttpTransport_WhenGetDefinitionResultMissingScope_ShouldThrowInvalidOperationException()
    {
        var dataDir = await CreateDataDirectoryAsync();
        var handler = new StaticResponseHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{" +
                "\"jsonrpc\":\"2.0\"," +
                "\"id\":\"req-get-definition\"," +
                "\"result\":{\"ok\":true,\"definition\":{\"appId\":\"sample.app\",\"displayName\":\"Sample App\"}}}", Encoding.UTF8, "application/json")
        });

        await using var client = await DevHubClient.FromRuntimeAsync(new DevHubClientOptions
        {
            ClientId = "client-a",
            DataDir = dataDir
        }, handler, () => "req-get-definition");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetDefinitionAsync("sample.app", string.Empty, CancellationToken.None));
        Assert.Contains("scope", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HttpTransport_WhenGetDefinitionResultScopeNull_ShouldThrowInvalidOperationException()
    {
        var dataDir = await CreateDataDirectoryAsync();
        var handler = new StaticResponseHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{" +
                "\"jsonrpc\":\"2.0\"," +
                "\"id\":\"req-get-definition\"," +
                "\"result\":{\"ok\":true,\"definition\":{\"appId\":\"sample.app\",\"scope\":null,\"displayName\":\"Sample App\"}}}", Encoding.UTF8, "application/json")
        });

        await using var client = await DevHubClient.FromRuntimeAsync(new DevHubClientOptions
        {
            ClientId = "client-a",
            DataDir = dataDir
        }, handler, () => "req-get-definition");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetDefinitionAsync("sample.app", string.Empty, CancellationToken.None));
        Assert.Contains("scope", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HttpTransport_WhenGetDefinitionResultMissingDisplayName_ShouldThrowInvalidOperationException()
    {
        var dataDir = await CreateDataDirectoryAsync();
        var handler = new StaticResponseHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{" +
                "\"jsonrpc\":\"2.0\"," +
                "\"id\":\"req-get-definition\"," +
                "\"result\":{\"ok\":true,\"definition\":{\"appId\":\"sample.app\",\"scope\":\"\"}}}", Encoding.UTF8, "application/json")
        });

        await using var client = await DevHubClient.FromRuntimeAsync(new DevHubClientOptions
        {
            ClientId = "client-a",
            DataDir = dataDir
        }, handler, () => "req-get-definition");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetDefinitionAsync("sample.app", string.Empty, CancellationToken.None));
        Assert.Contains("displayName", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HttpTransport_WhenGetDefinitionResultAppIdViolatesCanonicalGrammar_ShouldThrowInvalidOperationException()
    {
        var dataDir = await CreateDataDirectoryAsync();
        var handler = new StaticResponseHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "{\"jsonrpc\":\"2.0\",\"id\":\"req-get-definition\",\"result\":{\"ok\":true,\"definition\":{\"appId\":\".sample.app\",\"scope\":\"\",\"displayName\":\"Sample App\"}}}",
                Encoding.UTF8,
                "application/json")
        });

        await using var client = await DevHubClient.FromRuntimeAsync(new DevHubClientOptions
        {
            ClientId = "client-a",
            DataDir = dataDir
        }, handler, () => "req-get-definition");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetDefinitionAsync("sample.app", string.Empty, CancellationToken.None));
        Assert.Contains("appId", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HttpTransport_WhenGetDefinitionResultScopeViolatesCanonicalGrammar_ShouldThrowInvalidOperationException()
    {
        var dataDir = await CreateDataDirectoryAsync();
        var handler = new StaticResponseHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "{\"jsonrpc\":\"2.0\",\"id\":\"req-get-definition\",\"result\":{\"ok\":true,\"definition\":{\"appId\":\"sample.app\",\"scope\":\"workspace.\",\"displayName\":\"Sample App\"}}}",
                Encoding.UTF8,
                "application/json")
        });

        await using var client = await DevHubClient.FromRuntimeAsync(new DevHubClientOptions
        {
            ClientId = "client-a",
            DataDir = dataDir
        }, handler, () => "req-get-definition");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetDefinitionAsync("sample.app", string.Empty, CancellationToken.None));
        Assert.Contains("scope", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HttpTransport_WhenGetInstanceInstanceIdInvalid_ShouldThrowArgumentExceptionBeforeSending()
    {
        var dataDir = await CreateDataDirectoryAsync();
        var handler = new CaptureHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"jsonrpc\":\"2.0\",\"id\":\"req-get-instance\",\"result\":{\"ok\":true,\"instance\":{\"instanceId\":\"inst-1\",\"appId\":\"sample.app\",\"scope\":\"\",\"pid\":12345,\"registeredAtUtc\":\"2026-03-09T00:00:00Z\",\"lastSeenUtc\":\"2026-03-09T00:00:01Z\",\"invoke\":{\"poll\":true,\"respond\":true}}}}", Encoding.UTF8, "application/json")
        });

        await using var client = await DevHubClient.FromRuntimeAsync(new DevHubClientOptions
        {
            ClientId = "client-a",
            DataDir = dataDir
        }, handler, () => "req-get-instance");

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => client.GetInstanceAsync("inst/1", CancellationToken.None));

        Assert.Equal("instanceId", exception.ParamName);
        Assert.Null(handler.LastRequest);
    }

    [Fact]
    public async Task HttpTransport_WhenGetInstanceRemoteReturnsInstanceNotFound_ShouldPropagateDevHubRpcException()
    {
        var dataDir = await CreateDataDirectoryAsync();
        var handler = new StaticResponseHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "{\"jsonrpc\":\"2.0\",\"id\":\"req-get-instance\",\"error\":{\"code\":-32010,\"message\":\"instance_not_found\",\"data\":{\"reason\":\"unknown_instance\",\"instanceId\":\"missing-inst-1\"}}}",
                Encoding.UTF8,
                "application/json")
        });

        await using var client = await DevHubClient.FromRuntimeAsync(new DevHubClientOptions
        {
            ClientId = "client-a",
            DataDir = dataDir
        }, handler, () => "req-get-instance");

        var exception = await Assert.ThrowsAsync<DevHubRpcException>(() => client.GetInstanceAsync("missing-inst-1", CancellationToken.None));

        Assert.Equal(-32010, exception.Code);
        Assert.Equal("instance_not_found", exception.Message);
        Assert.Equal("unknown_instance", exception.Reason);
        Assert.Equal("missing-inst-1", exception.ErrorData!.Value.GetProperty("instanceId").GetString());
    }

    [Fact]
    public async Task HttpTransport_WhenGetInstanceResultIdentifiersViolateCanonicalGrammar_ShouldThrowInvalidOperationException()
    {
        var dataDir = await CreateDataDirectoryAsync();
        var handler = new StaticResponseHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "{\"jsonrpc\":\"2.0\",\"id\":\"req-get-instance\",\"result\":{\"ok\":true,\"instance\":{\"instanceId\":\"inst-1.\",\"appId\":\"sample.app\",\"scope\":\"\",\"pid\":12345,\"registeredAtUtc\":\"2026-03-09T00:00:00Z\",\"lastSeenUtc\":\"2026-03-09T00:00:01Z\",\"invoke\":{\"poll\":true,\"respond\":true}}}}",
                Encoding.UTF8,
                "application/json")
        });

        await using var client = await DevHubClient.FromRuntimeAsync(new DevHubClientOptions
        {
            ClientId = "client-a",
            DataDir = dataDir
        }, handler, () => "req-get-instance");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetInstanceAsync("inst-1", CancellationToken.None));
        Assert.Contains("instanceId", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HttpTransport_WhenGetInstanceResultLeaksPassword_ShouldThrowInvalidOperationException()
    {
        var dataDir = await CreateDataDirectoryAsync();
        var handler = new StaticResponseHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "{\"jsonrpc\":\"2.0\",\"id\":\"req-get-instance\",\"result\":{\"ok\":true,\"instance\":{\"instanceId\":\"inst-1\",\"appId\":\"sample.app\",\"scope\":\"\",\"pid\":12345,\"registeredAtUtc\":\"2026-03-09T00:00:00Z\",\"lastSeenUtc\":\"2026-03-09T00:00:01Z\",\"invoke\":{\"poll\":true,\"respond\":true},\"password\":\"secret-1\"}}}",
                Encoding.UTF8,
                "application/json")
        });

        await using var client = await DevHubClient.FromRuntimeAsync(new DevHubClientOptions
        {
            ClientId = "client-a",
            DataDir = dataDir
        }, handler, () => "req-get-instance");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetInstanceAsync("inst-1", CancellationToken.None));
        Assert.Contains("password", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HttpTransport_WhenGetInstanceResultLeaksInstanceSessionToken_ShouldThrowInvalidOperationException()
    {
        var dataDir = await CreateDataDirectoryAsync();
        var handler = new StaticResponseHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "{\"jsonrpc\":\"2.0\",\"id\":\"req-get-instance\",\"result\":{\"ok\":true,\"instance\":{\"instanceId\":\"inst-1\",\"appId\":\"sample.app\",\"scope\":\"\",\"pid\":12345,\"registeredAtUtc\":\"2026-03-09T00:00:00Z\",\"lastSeenUtc\":\"2026-03-09T00:00:01Z\",\"invoke\":{\"poll\":true,\"respond\":true},\"instanceSessionToken\":\"session-1\"}}}",
                Encoding.UTF8,
                "application/json")
        });

        await using var client = await DevHubClient.FromRuntimeAsync(new DevHubClientOptions
        {
            ClientId = "client-a",
            DataDir = dataDir
        }, handler, () => "req-get-instance");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetInstanceAsync("inst-1", CancellationToken.None));
        Assert.Contains("instanceSessionToken", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HttpTransport_WhenRegisterInstanceSucceeds_ShouldReturnSeparatedSnapshotAndToken()
    {
        var dataDir = await CreateDataDirectoryAsync();
        var handler = new StaticResponseHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{" +
                "\"jsonrpc\":\"2.0\"," +
                "\"id\":\"req-register\"," +
                "\"result\":{\"ok\":true,\"instance\":{\"instanceId\":\"inst-1\",\"appId\":\"sample.app\",\"scope\":\"\",\"pid\":12345,\"registeredAtUtc\":\"2026-03-09T00:00:00Z\",\"lastSeenUtc\":\"2026-03-09T00:00:01Z\",\"invoke\":{\"poll\":true,\"respond\":true}},\"instanceSessionToken\":\"session-1\"}}", Encoding.UTF8, "application/json")
        });

        await using var client = await DevHubClient.FromRuntimeAsync(new DevHubClientOptions
        {
            ClientId = "client-a",
            DataDir = dataDir
        }, handler, () => "req-register");

        var registered = await client.RegisterInstanceAsync(new AppInstanceRegistration
        {
            InstanceId = "inst-1",
            AppId = "sample.app",
            Scope = string.Empty,
            Pid = 12345,
            Invoke = new InvokeCapability
            {
                Poll = true,
                Respond = true
            }
        }, "secret-1", CancellationToken.None);

        Assert.Equal("inst-1", registered.Instance.InstanceId);
        Assert.Equal("sample.app", registered.Instance.AppId);
        Assert.Equal("session-1", registered.InstanceSessionToken);
        Assert.DoesNotContain("instanceSessionToken", JsonSerializer.Serialize(registered.Instance));

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(registered));
        Assert.Equal("session-1", document.RootElement.GetProperty("instanceSessionToken").GetString());
        Assert.False(document.RootElement.GetProperty("instance").TryGetProperty("instanceSessionToken", out _));
    }

    [Fact]
    public async Task HttpTransport_WhenRegisterInstanceResultMissingLastSeenUtc_ShouldThrowInvalidOperationException()
    {
        var dataDir = await CreateDataDirectoryAsync();
        var handler = new StaticResponseHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{" +
                "\"jsonrpc\":\"2.0\"," +
                "\"id\":\"req-register\"," +
                "\"result\":{\"ok\":true,\"instance\":{\"instanceId\":\"inst-1\",\"appId\":\"sample.app\",\"scope\":\"\",\"pid\":12345,\"registeredAtUtc\":\"2026-03-09T00:00:00Z\",\"invoke\":{\"poll\":true,\"respond\":true}},\"instanceSessionToken\":\"session-1\"}}", Encoding.UTF8, "application/json")
        });

        await using var client = await DevHubClient.FromRuntimeAsync(new DevHubClientOptions
        {
            ClientId = "client-a",
            DataDir = dataDir
        }, handler, () => "req-register");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => client.RegisterInstanceAsync(new AppInstanceRegistration
        {
            InstanceId = "inst-1",
            AppId = "sample.app",
            Scope = string.Empty,
            Pid = 12345,
            Invoke = new InvokeCapability
            {
                Poll = true,
                Respond = true
            }
        }, "secret-1", CancellationToken.None));

        Assert.Contains("lastSeenUtc", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Impl_HttpTransport_WhenRegisterInstanceResultLeaksPassword_ShouldThrowInvalidOperationException()
    {
        var dataDir = await CreateDataDirectoryAsync();
        var handler = new StaticResponseHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{" +
                "\"jsonrpc\":\"2.0\"," +
                "\"id\":\"req-register\"," +
                "\"result\":{\"ok\":true,\"instance\":{\"instanceId\":\"inst-1\",\"appId\":\"sample.app\",\"scope\":\"\",\"pid\":12345,\"registeredAtUtc\":\"2026-03-09T00:00:00Z\",\"lastSeenUtc\":\"2026-03-09T00:00:01Z\",\"invoke\":{\"poll\":true,\"respond\":true},\"password\":\"secret-1\"},\"instanceSessionToken\":\"session-1\"}}", Encoding.UTF8, "application/json")
        });

        await using var client = await DevHubClient.FromRuntimeAsync(new DevHubClientOptions
        {
            ClientId = "client-a",
            DataDir = dataDir
        }, handler, () => "req-register");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => client.RegisterInstanceAsync(new AppInstanceRegistration
        {
            InstanceId = "inst-1",
            AppId = "sample.app",
            Scope = string.Empty,
            Pid = 12345,
            Invoke = new InvokeCapability
            {
                Poll = true,
                Respond = true
            }
        }, "secret-1", CancellationToken.None));

        Assert.Contains("password", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HttpTransport_WhenRegisterInstanceResultMissingInstanceSessionToken_ShouldThrowInvalidOperationException()
    {
        var dataDir = await CreateDataDirectoryAsync();
        var handler = new StaticResponseHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{" +
                "\"jsonrpc\":\"2.0\"," +
                "\"id\":\"req-register\"," +
                "\"result\":{\"ok\":true,\"instance\":{\"instanceId\":\"inst-1\",\"appId\":\"sample.app\",\"scope\":\"\",\"pid\":12345,\"registeredAtUtc\":\"2026-03-09T00:00:00Z\",\"lastSeenUtc\":\"2026-03-09T00:00:01Z\",\"invoke\":{\"poll\":true,\"respond\":true}}}}", Encoding.UTF8, "application/json")
        });

        await using var client = await DevHubClient.FromRuntimeAsync(new DevHubClientOptions
        {
            ClientId = "client-a",
            DataDir = dataDir
        }, handler, () => "req-register");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => client.RegisterInstanceAsync(new AppInstanceRegistration
        {
            InstanceId = "inst-1",
            AppId = "sample.app",
            Scope = string.Empty,
            Pid = 12345,
            Invoke = new InvokeCapability
            {
                Poll = true,
                Respond = true
            }
        }, "secret-1", CancellationToken.None));

        Assert.Contains("instanceSessionToken", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HttpTransport_WhenRequestResultMissingValue_ShouldThrowInvalidOperationException()
    {
        var dataDir = await CreateDataDirectoryAsync();
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
            DataDir = dataDir
        }, handler, () => "req-request");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => client.RequestAsync(new InvokeRequest
        {
            AppId = "sample.app",
            Method = "sample.request",
            Target = new InvocationTarget
            {
                Scope = string.Empty
            }
        }, CancellationToken.None));

        Assert.Contains("value", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HttpTransport_WhenPollResultItemMissingCallerSessionId_ShouldThrowInvalidOperationException()
    {
        var dataDir = await CreateDataDirectoryAsync();
        var handler = new StaticResponseHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{" +
                "\"jsonrpc\":\"2.0\"," +
                "\"id\":\"req-poll\"," +
                "\"result\":{\"ok\":true,\"serverTimeUtc\":\"2026-03-09T00:00:00Z\",\"items\":[{\"invocationId\":\"invk-1\",\"appId\":\"sample.app\",\"target\":{\"scope\":\"\",\"instanceId\":null},\"method\":\"sample.notify\",\"kind\":\"notify\",\"createdAtUtc\":\"2026-03-09T00:00:00Z\",\"caller\":{\"clientId\":\"caller-a\"}}]}}", Encoding.UTF8, "application/json")
        });

        await using var client = await DevHubClient.FromRuntimeAsync(new DevHubClientOptions
        {
            ClientId = "client-a",
            DataDir = dataDir
        }, handler, () => "req-poll");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => client.PollAsync(new PollRequest
        {
            InstanceId = "inst-1",
            InstanceSessionToken = "session-1"
        }, CancellationToken.None));

        Assert.Contains("clientSessionId", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HttpTransport_WhenGetDefinitionCapabilitiesTypeInvalid_ShouldThrowInvalidOperationException()
    {
        var dataDir = await CreateDataDirectoryAsync();
        var handler = new StaticResponseHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{" +
                "\"jsonrpc\":\"2.0\"," +
                "\"id\":\"req-get-definition\"," +
                "\"result\":{\"ok\":true,\"definition\":{\"appId\":\"sample.app\",\"scope\":\"\",\"displayName\":\"Sample App\",\"capabilities\":{\"rpc\":\"true\"}}}}", Encoding.UTF8, "application/json")
        });

        await using var client = await DevHubClient.FromRuntimeAsync(new DevHubClientOptions
        {
            ClientId = "client-a",
            DataDir = dataDir
        }, handler, () => "req-get-definition");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetDefinitionAsync("sample.app", string.Empty, CancellationToken.None));
        Assert.Contains("definition.capabilities", exception.Message, StringComparison.Ordinal);
        Assert.Contains("rpc", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HttpTransport_WhenListInstancesResultMetaTypeInvalid_ShouldThrowInvalidOperationException()
    {
        var dataDir = await CreateDataDirectoryAsync();
        var handler = new StaticResponseHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{" +
                "\"jsonrpc\":\"2.0\"," +
                "\"id\":\"req-list-instances\"," +
                "\"result\":{\"ok\":true,\"instances\":[{\"instanceId\":\"inst-1\",\"appId\":\"sample.app\",\"scope\":\"\",\"pid\":12345,\"registeredAtUtc\":\"2026-03-09T00:00:00Z\",\"lastSeenUtc\":\"2026-03-09T00:00:00Z\",\"invoke\":{\"poll\":true,\"respond\":true},\"meta\":[1]}]}}", Encoding.UTF8, "application/json")
        });

        await using var client = await DevHubClient.FromRuntimeAsync(new DevHubClientOptions
        {
            ClientId = "client-a",
            DataDir = dataDir
        }, handler, () => "req-list-instances");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => client.ListInstancesAsync(new ListInstancesRequest(), CancellationToken.None));
        Assert.Contains("meta", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HttpTransport_WhenPollResultOptionsTypeInvalid_ShouldThrowInvalidOperationException()
    {
        var dataDir = await CreateDataDirectoryAsync();
        var handler = new StaticResponseHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{" +
                "\"jsonrpc\":\"2.0\"," +
                "\"id\":\"req-poll\"," +
                "\"result\":{\"ok\":true,\"serverTimeUtc\":\"2026-03-09T00:00:00Z\",\"items\":[{\"invocationId\":\"invk-1\",\"appId\":\"sample.app\",\"target\":{\"scope\":\"\",\"instanceId\":null},\"method\":\"sample.notify\",\"kind\":\"notify\",\"createdAtUtc\":\"2026-03-09T00:00:00Z\",\"caller\":{\"clientId\":\"caller-a\",\"clientSessionId\":\"11111111-1111-1111-1111-111111111111\"},\"options\":{\"queueIfOffline\":\"true\"}}]}}", Encoding.UTF8, "application/json")
        });

        await using var client = await DevHubClient.FromRuntimeAsync(new DevHubClientOptions
        {
            ClientId = "client-a",
            DataDir = dataDir
        }, handler, () => "req-poll");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => client.PollAsync(new PollRequest
        {
            InstanceId = "inst-1",
            InstanceSessionToken = "session-1"
        }, CancellationToken.None));

        Assert.Contains("items[0].options", exception.Message, StringComparison.Ordinal);
        Assert.Contains("queueIfOffline", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HttpTransport_WhenPollResultTargetInstanceIdViolatesCanonicalGrammar_ShouldThrowInvalidOperationException()
    {
        var dataDir = await CreateDataDirectoryAsync();
        var handler = new StaticResponseHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "{\"jsonrpc\":\"2.0\",\"id\":\"req-poll\",\"result\":{\"ok\":true,\"serverTimeUtc\":\"2026-03-09T00:00:00Z\",\"items\":[{\"invocationId\":\"invk-1\",\"appId\":\"sample.app\",\"target\":{\"scope\":\"\",\"instanceId\":\".inst-1\"},\"method\":\"sample.notify\",\"kind\":\"notify\",\"createdAtUtc\":\"2026-03-09T00:00:00Z\",\"caller\":{\"clientId\":\"caller-a\",\"clientSessionId\":\"11111111-1111-1111-1111-111111111111\"}}]}}",
                Encoding.UTF8,
                "application/json")
        });

        await using var client = await DevHubClient.FromRuntimeAsync(new DevHubClientOptions
        {
            ClientId = "client-a",
            DataDir = dataDir
        }, handler, () => "req-poll");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => client.PollAsync(new PollRequest
        {
            InstanceId = "inst-1",
            InstanceSessionToken = "session-1"
        }, CancellationToken.None));

        Assert.Contains("instanceId", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HttpTransport_WhenPollResultCallerClientSessionIdIsNotUuid_ShouldThrowInvalidOperationException()
    {
        var dataDir = await CreateDataDirectoryAsync();
        var handler = new StaticResponseHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "{\"jsonrpc\":\"2.0\",\"id\":\"req-poll\",\"result\":{\"ok\":true,\"serverTimeUtc\":\"2026-03-09T00:00:00Z\",\"items\":[{\"invocationId\":\"invk-1\",\"appId\":\"sample.app\",\"target\":{\"scope\":\"\",\"instanceId\":null},\"method\":\"sample.notify\",\"kind\":\"notify\",\"createdAtUtc\":\"2026-03-09T00:00:00Z\",\"caller\":{\"clientId\":\"caller-a\",\"clientSessionId\":\"bad-client-session-id\"}}]}}",
                Encoding.UTF8,
                "application/json")
        });

        await using var client = await DevHubClient.FromRuntimeAsync(new DevHubClientOptions
        {
            ClientId = "client-a",
            DataDir = dataDir
        }, handler, () => "req-poll");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => client.PollAsync(new PollRequest
        {
            InstanceId = "inst-1",
            InstanceSessionToken = "session-1"
        }, CancellationToken.None));

        Assert.Contains("clientSessionId", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Impl_HttpTransport_WhenValidateDefinitionResultInvalidWithoutErrors_ShouldThrowInvalidOperationException()
    {
        var dataDir = await CreateDataDirectoryAsync();
        var handler = new StaticResponseHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "{\"jsonrpc\":\"2.0\",\"id\":\"req-validate-definition\",\"result\":{\"ok\":true,\"valid\":false,\"errors\":[]}}",
                Encoding.UTF8,
                "application/json")
        });

        await using var client = await DevHubClient.FromRuntimeAsync(new DevHubClientOptions
        {
            ClientId = "client-a",
            DataDir = dataDir
        }, handler, () => "req-validate-definition");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => client.ValidateDefinitionAsync(new AppDefinition
        {
            AppId = "sample.app",
            Scope = string.Empty,
            DisplayName = "Sample App"
        }, CancellationToken.None));
        Assert.Contains("valid=false", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Impl_HttpTransport_WhenValidateDefinitionIssueMissingMessage_ShouldThrowInvalidOperationException()
    {
        var dataDir = await CreateDataDirectoryAsync();
        var handler = new StaticResponseHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "{\"jsonrpc\":\"2.0\",\"id\":\"req-validate-definition\",\"result\":{\"ok\":true,\"valid\":false,\"errors\":[{\"path\":\"definition.appId\",\"code\":\"invalid_app_id\"}]}}",
                Encoding.UTF8,
                "application/json")
        });

        await using var client = await DevHubClient.FromRuntimeAsync(new DevHubClientOptions
        {
            ClientId = "client-a",
            DataDir = dataDir
        }, handler, () => "req-validate-definition");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => client.ValidateDefinitionAsync(new AppDefinition
        {
            AppId = "sample.app",
            Scope = string.Empty,
            DisplayName = "Sample App"
        }, CancellationToken.None));
        Assert.Contains("errors[0]", exception.Message, StringComparison.Ordinal);
        Assert.Contains("message", exception.Message, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    private async Task<string> CreateDataDirectoryAsync(string? hubVersion = null)
    {
        var dataDir = Path.Combine(_tempRoot, Guid.NewGuid().ToString("N"));
        var runtimeDir = Path.Combine(dataDir, "runtime");
        Directory.CreateDirectory(runtimeDir);
        var tokenFile = Path.Combine(runtimeDir, "token.txt");
        await File.WriteAllTextAsync(tokenFile, "token-1");
        var hubVersionProperty = hubVersion is null
            ? string.Empty
            : $$"""
              "hubVersion": "{{hubVersion}}",
            """;
        await File.WriteAllTextAsync(
            Path.Combine(runtimeDir, "hub.json"),
            $$"""
            {
              "protocolVersion": 1,
              {{hubVersionProperty}}
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
        return dataDir;
    }

    private static HttpResponseMessage CreateMethodNotFoundResponse(string requestId)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                $"{{\"jsonrpc\":\"2.0\",\"id\":\"{requestId}\",\"error\":{{\"code\":-32601,\"message\":\"method_not_found\",\"data\":{{\"reason\":\"unsupported_method\",\"method\":\"hub.getVersion\"}}}}}}",
                Encoding.UTF8,
                "application/json")
        };
    }

    private static string CreateHostVersionWithPatchDelta(int patchDelta)
    {
        Assert.True(SemanticVersionParser.TryParse(SdkVersionSource.CurrentVersion, out var sdkVersion));
        return $"{sdkVersion.Major}.{sdkVersion.Minor}.{sdkVersion.Patch + patchDelta}";
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
