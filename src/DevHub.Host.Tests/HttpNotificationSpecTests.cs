namespace DevHub.Host.Tests;

using System.Text;
using DevHub.Host;
using DevHub.Host.Tests.TestHelpers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Moq;

/// <summary>
/// HTTP 通知行为规范测试。
/// </summary>
public class HttpNotificationSpecTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _runtimeDirectory;
    private readonly string _definitionsDirectory;
    private readonly EnvironmentVariableScope _runtimeScope;

    /// <summary>
    /// 初始化测试上下文。
    /// </summary>
    public HttpNotificationSpecTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "DevHubHostHttpNotificationTests", Guid.NewGuid().ToString("N"));
        _runtimeDirectory = Path.Combine(_tempRoot, "runtime");
        _definitionsDirectory = Path.Combine(_tempRoot, "definitions");

        Directory.CreateDirectory(_tempRoot);
        Directory.CreateDirectory(_runtimeDirectory);
        Directory.CreateDirectory(_definitionsDirectory);

        _runtimeScope = new EnvironmentVariableScope("DEVHUB_RUNTIME_DIR", _runtimeDirectory);
    }

    [Fact]
    public async Task Spec_3_1_HttpNotification_ShouldReturn200WithEmptyBody()
    {
        var hostContext = HostTestContextFactory.Create(_definitionsDirectory);
        var handler = new RpcHttpEndpointHandler(
            hostContext.Router,
            hostContext.FileSystemManager,
            Mock.Of<ILogger<RpcHttpEndpointHandler>>());

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Method = HttpMethods.Post;
        httpContext.Request.ContentType = "application/json";
        httpContext.Request.Headers["Authorization"] = $"Bearer {hostContext.Token}";
        httpContext.Request.Headers["X-DevHub-Protocol"] = "1";
        httpContext.Request.Headers["X-DevHub-ClientId"] = "http-notify-client";
        httpContext.Request.Headers["X-DevHub-ClientSessionId"] = Guid.NewGuid().ToString("D");

        const string notificationJson = """
            {"jsonrpc":"2.0","method":"hub.ping","params":{"echo":"notify"}}
            """;
        httpContext.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(notificationJson));
        httpContext.Response.Body = new MemoryStream();

        var result = await handler.HandleAsync(httpContext.Request, currentPort: null, CancellationToken.None);
        await result.ExecuteAsync(httpContext);

        Assert.Equal(StatusCodes.Status200OK, httpContext.Response.StatusCode);
        httpContext.Response.Body.Position = 0;
        using var reader = new StreamReader(httpContext.Response.Body, Encoding.UTF8, leaveOpen: true);
        var bodyText = await reader.ReadToEndAsync();
        Assert.True(string.IsNullOrEmpty(bodyText));
    }

    /// <summary>
    /// 释放测试资源。
    /// </summary>
    public void Dispose()
    {
        _runtimeScope.Dispose();

        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }
}
