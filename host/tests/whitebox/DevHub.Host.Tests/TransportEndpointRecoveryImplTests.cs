namespace DevHub.Host.Tests;

using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using DevHub.Host.Tests.TestHelpers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using static DevHub.Host.Tests.TestHelpers.JsonRpcTestMessageHelper;

/// <summary>
/// 传输入口运行时恢复实现测试。
/// </summary>
[Trait("Category", "Impl")]
public sealed class TransportEndpointRecoveryImplTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _runtimeDirectory;

    /// <summary>
    /// 初始化测试上下文。
    /// </summary>
    public TransportEndpointRecoveryImplTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "DevHubTransportEndpointRecoveryTests", Guid.NewGuid().ToString("N"));
        _runtimeDirectory = Path.Combine(_tempRoot, "runtime");

        Directory.CreateDirectory(_tempRoot);
        Directory.CreateDirectory(_runtimeDirectory);
    }

    [Fact]
    public async Task Impl_RpcHttpEndpointHandler_WhenHubJsonMissing_ShouldRebuildRuntimeUsingConnectionLocalPort()
    {
        using var harness = new HostTransportTestHarness(_tempRoot);

        var hubJsonPath = Path.Combine(_runtimeDirectory, "hub.json");
        if (File.Exists(hubJsonPath))
        {
            File.Delete(hubJsonPath);
        }

        Assert.False(File.Exists(hubJsonPath));

        var httpContext = CreateHttpContext(
            harness.Token,
            """
            {
              "jsonrpc": "2.0",
              "id": "recover-http",
              "method": "hub.ping",
              "params": {}
            }
            """,
            localPort: 48124);

        var result = await harness.HttpHandler.HandleAsync(httpContext.Request, CancellationToken.None);
        await result.ExecuteAsync(httpContext);

        Assert.Equal(StatusCodes.Status200OK, httpContext.Response.StatusCode);
        Assert.Equal("http://127.0.0.1:48124", ReadHubRuntimeUrl(hubJsonPath, "httpBaseUrl"));
        Assert.Equal("ws://127.0.0.1:48124/ws", ReadHubRuntimeUrl(hubJsonPath, "wsUrl"));

        httpContext.Response.Body.Position = 0;
        using var responseDocument = JsonDocument.Parse(httpContext.Response.Body);
        Assert.True(responseDocument.RootElement.GetProperty("result").GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task Impl_RpcHttpEndpointHandler_ShouldLeaveRequestBodyOpenAfterHandleAsync()
    {
        using var harness = new HostTransportTestHarness(_tempRoot);

        var requestBody = new MemoryStream(Encoding.UTF8.GetBytes(
            """
            {
              "jsonrpc": "2.0",
              "id": "leave-open-body",
              "method": "hub.ping",
              "params": {
                "echo": "leave-open"
              }
            }
            """));

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Method = HttpMethods.Post;
        httpContext.Request.ContentType = "application/json";
        httpContext.Request.Headers["Authorization"] = $"Bearer {harness.Token}";
        httpContext.Request.Headers["X-DevHub-Protocol"] = "1";
        httpContext.Request.Headers["X-DevHub-ClientId"] = "leave-open-client";
        httpContext.Request.Headers["X-DevHub-ClientSessionId"] = Guid.NewGuid().ToString("D");
        httpContext.Request.Body = requestBody;
        httpContext.RequestServices = new ServiceCollection()
            .AddLogging()
            .AddOptions()
            .BuildServiceProvider();
        httpContext.Response.Body = new MemoryStream();

        var result = await harness.HttpHandler.HandleAsync(httpContext.Request, CancellationToken.None);
        await result.ExecuteAsync(httpContext);

        requestBody.Position = 0;
        using var reader = new StreamReader(requestBody, Encoding.UTF8, leaveOpen: true);
        var bodyText = await reader.ReadToEndAsync();

        Assert.Contains("\"leave-open-body\"", bodyText, StringComparison.Ordinal);
        Assert.Equal(StatusCodes.Status200OK, httpContext.Response.StatusCode);
    }

    [Fact]
    public async Task Impl_WebSocketSessionHandler_WhenHubJsonMissing_ShouldRebuildRuntimeUsingConnectionLocalPort()
    {
        using var harness = new HostTransportTestHarness(_tempRoot);

        var hubJsonPath = Path.Combine(_runtimeDirectory, "hub.json");
        if (File.Exists(hubJsonPath))
        {
            File.Delete(hubJsonPath);
        }

        Assert.False(File.Exists(hubJsonPath));

        var socket = new ScriptedWebSocket([
            CreateJson(new
            {
                jsonrpc = "2.0",
                id = "recover-ws",
                method = "hub.ws.authenticate",
                @params = new
                {
                    token = harness.Token,
                    protocolVersion = 1,
                    clientId = "recover-client",
                    clientSessionId = "11111111-1111-1111-1111-111111111111"
                }
            })
        ]);

        var httpContext = new DefaultHttpContext();
        httpContext.Connection.LocalPort = 48125;
        httpContext.Features.Set<IHttpWebSocketFeature>(new TestWebSocketFeature(socket));

        await harness.WebSocketHandler.HandleEndpointAsync(httpContext, CancellationToken.None);

        Assert.Equal("http://127.0.0.1:48125", ReadHubRuntimeUrl(hubJsonPath, "httpBaseUrl"));
        Assert.Equal("ws://127.0.0.1:48125/ws", ReadHubRuntimeUrl(hubJsonPath, "wsUrl"));

        var response = FindResponseById(ParseSentMessages(socket), "recover-ws");
        Assert.NotEqual(JsonValueKind.Undefined, response.ValueKind);
        Assert.True(response.TryGetProperty("result", out var result));
        Assert.True(result.GetProperty("ok").GetBoolean());
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    private static DefaultHttpContext CreateHttpContext(string token, string requestJson, int localPort)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Connection.LocalPort = localPort;
        httpContext.Request.Method = HttpMethods.Post;
        httpContext.Request.ContentType = "application/json";
        httpContext.Request.Headers["Authorization"] = $"Bearer {token}";
        httpContext.Request.Headers["X-DevHub-Protocol"] = "1";
        httpContext.Request.Headers["X-DevHub-ClientId"] = "endpoint-recovery-client";
        httpContext.Request.Headers["X-DevHub-ClientSessionId"] = Guid.NewGuid().ToString("D");
        httpContext.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(requestJson));
        httpContext.RequestServices = new ServiceCollection()
            .AddLogging()
            .AddOptions()
            .BuildServiceProvider();
        httpContext.Response.Body = new MemoryStream();
        return httpContext;
    }

    private static string ReadHubRuntimeUrl(string hubJsonPath, string propertyName)
    {
        Assert.True(File.Exists(hubJsonPath));
        using var document = JsonDocument.Parse(File.ReadAllText(hubJsonPath));
        return document.RootElement.GetProperty(propertyName).GetString()!;
    }

    private sealed class TestWebSocketFeature(WebSocket socket) : IHttpWebSocketFeature
    {
        public bool IsWebSocketRequest => true;

        public Task<WebSocket> AcceptAsync(WebSocketAcceptContext context)
        {
            return Task.FromResult(socket);
        }
    }
}
