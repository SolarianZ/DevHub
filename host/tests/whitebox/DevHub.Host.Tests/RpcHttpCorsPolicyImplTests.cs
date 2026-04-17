namespace DevHub.Host.Tests;

using System.Text;
using System.Text.Json;
using DevHub.Host.Tests.TestHelpers;
using DevHub.Host.Transport;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// `/rpc` CORS 规则实现回归测试。
/// </summary>
[Trait("Category", "Impl")]
public sealed class RpcHttpCorsPolicyImplTests : IDisposable
{
    private readonly string _tempRoot;

    /// <summary>
    /// 初始化测试上下文。
    /// </summary>
    public RpcHttpCorsPolicyImplTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "DevHubRpcHttpCorsPolicyImplTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    [Fact]
    public async Task Impl_RpcHttpCorsPolicy_CreatePreflightResponse_ShouldReturnNoContentWithCorsHeaders()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Method = HttpMethods.Options;
        httpContext.Request.Headers["Origin"] = "tauri://localhost";
        httpContext.Request.Headers["Access-Control-Request-Method"] = HttpMethods.Post;
        httpContext.RequestServices = new ServiceCollection()
            .AddLogging()
            .AddOptions()
            .BuildServiceProvider();
        httpContext.Response.Body = new MemoryStream();

        var result = RpcHttpCorsPolicy.CreatePreflightResponse(httpContext.Request);
        await result.ExecuteAsync(httpContext);

        Assert.Equal(StatusCodes.Status204NoContent, httpContext.Response.StatusCode);
        Assert.Equal("tauri://localhost", httpContext.Response.Headers["Access-Control-Allow-Origin"]);
        Assert.Contains("Origin", httpContext.Response.Headers["Vary"].ToString(), StringComparison.Ordinal);
        Assert.Equal(RpcHttpCorsPolicy.AllowMethodsValue, httpContext.Response.Headers["Access-Control-Allow-Methods"]);
        Assert.Equal(RpcHttpCorsPolicy.AllowHeadersValue, httpContext.Response.Headers["Access-Control-Allow-Headers"]);
        Assert.Equal(0L, httpContext.Response.Body.Length);
    }

    [Fact]
    public async Task Impl_RpcHttpEndpointHandler_WhenOriginPresent_ShouldAppendCorsHeadersToSuccessResponse()
    {
        using var harness = new HostTransportTestHarness(_tempRoot);

        var httpContext = CreatePostContext(
            harness.Token,
            """
            {
              "jsonrpc": "2.0",
              "id": "cors-success",
              "method": "hub.ping",
              "params": {}
            }
            """,
            includeProtocolHeader: true);
        httpContext.Request.Headers["Origin"] = "http://localhost:1420";

        var result = await harness.HttpHandler.HandleAsync(httpContext.Request, CancellationToken.None);
        await result.ExecuteAsync(httpContext);

        Assert.Equal(StatusCodes.Status200OK, httpContext.Response.StatusCode);
        Assert.Equal("http://localhost:1420", httpContext.Response.Headers["Access-Control-Allow-Origin"]);
        Assert.Contains("Origin", httpContext.Response.Headers["Vary"].ToString(), StringComparison.Ordinal);

        httpContext.Response.Body.Position = 0;
        using var document = JsonDocument.Parse(httpContext.Response.Body);
        Assert.True(document.RootElement.GetProperty("result").GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task Impl_RpcHttpEndpointHandler_WhenOriginPresent_ShouldAppendCorsHeadersToJsonRpcErrorResponse()
    {
        using var harness = new HostTransportTestHarness(_tempRoot);

        var httpContext = CreatePostContext(
            harness.Token,
            """
            {
              "jsonrpc": "2.0",
              "id": "cors-error",
              "method": "hub.ping",
              "params": {}
            }
            """,
            includeProtocolHeader: false);
        httpContext.Request.Headers["Origin"] = "tauri://localhost";

        var result = await harness.HttpHandler.HandleAsync(httpContext.Request, CancellationToken.None);
        await result.ExecuteAsync(httpContext);

        Assert.Equal(StatusCodes.Status200OK, httpContext.Response.StatusCode);
        Assert.Equal("tauri://localhost", httpContext.Response.Headers["Access-Control-Allow-Origin"]);
        Assert.Contains("Origin", httpContext.Response.Headers["Vary"].ToString(), StringComparison.Ordinal);

        httpContext.Response.Body.Position = 0;
        using var document = JsonDocument.Parse(httpContext.Response.Body);
        var error = document.RootElement.GetProperty("error");
        Assert.Equal(-32099, error.GetProperty("code").GetInt32());
        Assert.Equal("not_supported", error.GetProperty("message").GetString());
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    private static DefaultHttpContext CreatePostContext(string token, string requestJson, bool includeProtocolHeader)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Method = HttpMethods.Post;
        httpContext.Request.ContentType = "application/json";
        httpContext.Request.Headers["Authorization"] = $"Bearer {token}";
        httpContext.Request.Headers["X-DevHub-ClientId"] = "cors-policy-client";
        httpContext.Request.Headers["X-DevHub-ClientSessionId"] = Guid.NewGuid().ToString("D");
        if (includeProtocolHeader)
        {
            httpContext.Request.Headers["X-DevHub-Protocol"] = "1";
        }

        httpContext.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(requestJson));
        httpContext.RequestServices = new ServiceCollection()
            .AddLogging()
            .AddOptions()
            .BuildServiceProvider();
        httpContext.Response.Body = new MemoryStream();
        return httpContext;
    }
}
