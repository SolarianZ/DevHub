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
    public async Task M5_DN_UT_004_RpcErrorResponse_ShouldMapToDevHubRpcException()
    {
        var runtimeDir = await CreateRuntimeAsync();
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
                RuntimeDir = runtimeDir
            },
            handler,
            () => "req-fixed");

        var exception = await Assert.ThrowsAsync<DevHubRpcException>(() => client.PingAsync(cancellationToken: CancellationToken.None));

        Assert.Equal(-32050, exception.Code);
        Assert.Equal("invocation_failed", exception.Message);
        Assert.Equal("req-fixed", exception.RequestId);
        Assert.Equal(DevHubRpcErrorCode.InvocationFailed, exception.KnownCode);
        Assert.True(exception.Is(DevHubRpcErrorCode.InvocationFailed));
        Assert.True(exception.Data.HasValue);
        var errorData = exception.Data ?? throw new InvalidOperationException("缺少 error.data。");
        Assert.Equal(errorData.GetRawText(), exception.ErrorData?.GetRawText());
        Assert.Equal("invk-1", errorData.GetProperty("invocationId").GetString());
        Assert.Equal(1001, errorData.GetProperty("calleeError").GetProperty("code").GetInt32());
        Assert.True(exception.TryGetDataProperty("calleeError", out var calleeError));
        Assert.Equal("app_error", calleeError.GetProperty("message").GetString());
    }

    [Fact]
    public async Task M5_DN_UT_004_RpcErrorResponse_ShouldExposeReasonHelper()
    {
        var runtimeDir = await CreateRuntimeAsync();
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
                RuntimeDir = runtimeDir
            },
            handler,
            () => "req-fixed");

        var exception = await Assert.ThrowsAsync<DevHubRpcException>(() => client.PingAsync(cancellationToken: CancellationToken.None));

        Assert.Equal(DevHubRpcErrorCode.Unauthorized, exception.KnownCode);
        Assert.Equal("invalid_token", exception.Reason);
        Assert.True(exception.TryGetDataString("reason", out var reason));
        Assert.Equal("invalid_token", reason);
        Assert.False(exception.TryGetDataProperty("missing", out _));
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
