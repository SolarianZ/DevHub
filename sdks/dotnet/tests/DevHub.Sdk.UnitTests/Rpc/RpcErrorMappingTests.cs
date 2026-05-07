using System.Net;
using System.Text;

namespace DevHub.Sdk.UnitTests.Rpc;

/// <summary>
/// RPC 错误映射白盒测试。
/// </summary>
public sealed class RpcErrorMappingTests : IDisposable
{
    private readonly string _tempRoot;

    public RpcErrorMappingTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "DevHubSdkRpcErrorTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    [Fact]
    public async Task RpcErrorResponse_ShouldMapToDevHubRpcException()
    {
        var dataDir = await CreateDataDirectoryAsync();
        var handler = new StubHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "{\"jsonrpc\":\"2.0\",\"id\":\"req-fixed\",\"error\":{\"code\":-32050,\"message\":\"invocation_failed\",\"data\":{\"invocationId\":\"invk-1\",\"calleeError\":{\"code\":1001,\"message\":\"app_error\",\"data\":{\"reason\":\"boom\"}}}}}",
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
            () => "req-fixed");

        var exception = await Assert.ThrowsAsync<DevHubRpcException>(() => client.PingAsync(cancellationToken: CancellationToken.None));

        Assert.Equal(-32050, exception.Code);
        Assert.Equal("invocation_failed", exception.Message);
        Assert.Equal("req-fixed", exception.RequestId);
        Assert.Equal(DevHubRpcErrorCode.InvocationFailed, exception.KnownCode);
        Assert.True(exception.Is(DevHubRpcErrorCode.InvocationFailed));
        Assert.True(exception.ErrorData.HasValue);
        var errorData = exception.ErrorData ?? throw new InvalidOperationException("缺少 error.data。");
        Assert.Equal("invk-1", exception.InvocationId);
        Assert.Equal("invk-1", errorData.GetProperty("invocationId").GetString());
        Assert.Equal(1001, errorData.GetProperty("calleeError").GetProperty("code").GetInt32());
        Assert.True(exception.TryGetDataProperty("calleeError", out var calleeError));
        Assert.Equal("app_error", calleeError.GetProperty("message").GetString());
        Assert.True(exception.TryGetCalleeError(out var typedCalleeError));
        Assert.NotNull(typedCalleeError);
        Assert.Equal(1001, typedCalleeError!.Code);
        Assert.Equal("app_error", typedCalleeError.Message);
        Assert.Equal("boom", typedCalleeError.Data!.Value.GetProperty("reason").GetString());
        Assert.Equal(typedCalleeError.Code, exception.CalleeError?.Code);
    }

    [Fact]
    public async Task RpcErrorResponse_ShouldExposeReasonHelper()
    {
        var dataDir = await CreateDataDirectoryAsync();
        var handler = new StubHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "{\"jsonrpc\":\"2.0\",\"id\":\"req-fixed\",\"error\":{\"code\":-32001,\"message\":\"unauthorized\",\"data\":{\"reason\":\"invalid_token\"}}}",
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
            () => "req-fixed");

        var exception = await Assert.ThrowsAsync<DevHubRpcException>(() => client.PingAsync(cancellationToken: CancellationToken.None));

        Assert.Equal(DevHubRpcErrorCode.Unauthorized, exception.KnownCode);
        Assert.Equal("invalid_token", exception.Reason);
        Assert.Null(exception.InvocationId);
        Assert.Null(exception.CalleeError);
        Assert.True(exception.TryGetDataString("reason", out var reason));
        Assert.Equal("invalid_token", reason);
        Assert.False(exception.TryGetDataProperty("missing", out _));
        Assert.False(exception.TryGetCalleeError(out _));
    }

    [Fact]
    public async Task RpcErrorResponse_ShouldExposeUnknownInvocationReasonAndInvocationId()
    {
        var dataDir = await CreateDataDirectoryAsync();
        var handler = new StubHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "{\"jsonrpc\":\"2.0\",\"id\":\"req-fixed\",\"error\":{\"code\":-32011,\"message\":\"invocation_expired\",\"data\":{\"invocationId\":\"invk-unknown\",\"reason\":\"unknown_invocation\"}}}",
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
            () => "req-fixed");

        var exception = await Assert.ThrowsAsync<DevHubRpcException>(() => client.PingAsync(cancellationToken: CancellationToken.None));

        Assert.Equal(DevHubRpcErrorCode.InvocationExpired, exception.KnownCode);
        Assert.Equal("invk-unknown", exception.InvocationId);
        Assert.Equal("unknown_invocation", exception.Reason);
        Assert.True(exception.TryGetDataString("reason", out var reason));
        Assert.Equal("unknown_invocation", reason);
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
                "launchDedupeWindowSeconds": 30,
                "launchRegisterTimeoutSeconds": 30
              }
            }
            """);
        return dataDir;
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpResponseMessage _response;

        public StubHandler(HttpResponseMessage response)
        {
            _response = response;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(_response);
        }
    }
}
