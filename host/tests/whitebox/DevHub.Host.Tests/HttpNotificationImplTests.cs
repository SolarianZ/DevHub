namespace DevHub.Host.Tests;

using System.Text;
using DevHub.Core.Models;
using DevHub.Host.Tests.TestHelpers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// HTTP 通知入口实现回归测试。
/// </summary>
[Trait("Category", "Impl")]
public sealed class HttpNotificationImplTests : IDisposable
{
    private readonly string _tempRoot;

    /// <summary>
    /// 初始化测试上下文。
    /// </summary>
    public HttpNotificationImplTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "DevHubHostHttpNotificationImplTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    [Fact]
    public async Task Impl_RpcHttpEndpointHandler_WhenCanceledBeforeReadingRequestBody_ShouldPropagateOperationCanceledException()
    {
        using var harness = new HostTransportTestHarness(_tempRoot);

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Method = HttpMethods.Post;
        httpContext.Request.ContentType = "application/json";
        httpContext.Request.Headers["Authorization"] = $"Bearer {harness.Token}";
        httpContext.Request.Headers["X-DevHub-Protocol"] = "1";
        httpContext.Request.Headers["X-DevHub-ClientId"] = "http-cancel-client";
        httpContext.Request.Headers["X-DevHub-ClientSessionId"] = Guid.NewGuid().ToString("D");
        httpContext.RequestServices = new ServiceCollection()
            .AddLogging()
            .AddOptions()
            .BuildServiceProvider();
        httpContext.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(
            """
            {
              "jsonrpc": "2.0",
              "id": "http-cancel",
              "method": "hub.ping",
              "params": {}
            }
            """));
        httpContext.Response.Body = new MemoryStream();

        using var cancellationTokenSource = new CancellationTokenSource();
        cancellationTokenSource.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await harness.HttpHandler.HandleAsync(httpContext.Request, cancellationTokenSource.Token));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }
}
