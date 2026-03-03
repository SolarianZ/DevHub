namespace DevHub.Host.Tests;

using System.Text;
using System.Text.Json;
using DevHub.Host;
using DevHub.Host.Tests.TestHelpers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;

/// <summary>
/// HTTP 通知行为规范测试。
/// </summary>
[Trait("Category", "Spec")]
public class HttpNotificationSpecTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _runtimeDirectory;
    private readonly string _definitionsDirectory;

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
    }

    [Fact]
    [Trait("SpecRef", "3.1")]
    public async Task Spec_3_1_HttpNotification_ShouldReturn200WithEmptyBody()
    {
        var hostContext = HostTestContextFactory.Create(_tempRoot, _runtimeDirectory, _definitionsDirectory);
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

    [Fact]
    [Trait("SpecRef", "6.2")]
    public async Task Spec_6_2_HttpCallWsOnlyMethod_ShouldReturnNotSupported()
    {
        var hostContext = HostTestContextFactory.Create(_tempRoot, _runtimeDirectory, _definitionsDirectory);
        var handler = new RpcHttpEndpointHandler(
            hostContext.Router,
            hostContext.FileSystemManager,
            Mock.Of<ILogger<RpcHttpEndpointHandler>>());

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Method = HttpMethods.Post;
        httpContext.Request.ContentType = "application/json";
        httpContext.Request.Headers["Authorization"] = $"Bearer {hostContext.Token}";
        httpContext.Request.Headers["X-DevHub-Protocol"] = "1";
        httpContext.Request.Headers["X-DevHub-ClientId"] = "http-ws-only-client";
        httpContext.Request.Headers["X-DevHub-ClientSessionId"] = Guid.NewGuid().ToString("D");
        httpContext.RequestServices = new ServiceCollection()
            .AddLogging()
            .AddOptions()
            .BuildServiceProvider();
        httpContext.Response.Body = new MemoryStream();

        const string requestJson = """
            {"jsonrpc":"2.0","id":"http-ws-only","method":"hub.events.subscribe","params":{}}
            """;
        httpContext.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(requestJson));

        var result = await handler.HandleAsync(httpContext.Request, currentPort: null, CancellationToken.None);
        await result.ExecuteAsync(httpContext);

        Assert.Equal(StatusCodes.Status200OK, httpContext.Response.StatusCode);
        httpContext.Response.Body.Position = 0;
        using var responseDocument = await JsonDocument.ParseAsync(httpContext.Response.Body);
        var root = responseDocument.RootElement;

        Assert.Equal("http-ws-only", root.GetProperty("id").GetString());
        var error = root.GetProperty("error");
        Assert.Equal(-32099, error.GetProperty("code").GetInt32());
        Assert.Equal("not_supported", error.GetProperty("message").GetString());
        Assert.Equal("transport_mismatch", error.GetProperty("data").GetProperty("reason").GetString());
        Assert.Equal("ws", error.GetProperty("data").GetProperty("expected").GetString());
    }

    [Fact]
    [Trait("SpecRef", "6.1")]
    public async Task Spec_6_1_HttpHubMethod_WhenParamsIsArray_ShouldReturnInvalidParams()
    {
        var hostContext = HostTestContextFactory.Create(_tempRoot, _runtimeDirectory, _definitionsDirectory);
        var handler = new RpcHttpEndpointHandler(
            hostContext.Router,
            hostContext.FileSystemManager,
            Mock.Of<ILogger<RpcHttpEndpointHandler>>());

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Method = HttpMethods.Post;
        httpContext.Request.ContentType = "application/json";
        httpContext.Request.Headers["Authorization"] = $"Bearer {hostContext.Token}";
        httpContext.Request.Headers["X-DevHub-Protocol"] = "1";
        httpContext.Request.Headers["X-DevHub-ClientId"] = "http-array-client";
        httpContext.Request.Headers["X-DevHub-ClientSessionId"] = Guid.NewGuid().ToString("D");
        httpContext.RequestServices = new ServiceCollection()
            .AddLogging()
            .AddOptions()
            .BuildServiceProvider();
        httpContext.Response.Body = new MemoryStream();

        const string requestJson = """
            {"jsonrpc":"2.0","id":"http-array-params","method":"hub.ping","params":[1,2,3]}
            """;
        httpContext.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(requestJson));

        var result = await handler.HandleAsync(httpContext.Request, currentPort: null, CancellationToken.None);
        await result.ExecuteAsync(httpContext);

        Assert.Equal(StatusCodes.Status200OK, httpContext.Response.StatusCode);
        httpContext.Response.Body.Position = 0;
        using var responseDocument = await JsonDocument.ParseAsync(httpContext.Response.Body);
        var root = responseDocument.RootElement;
        Assert.Equal("http-array-params", root.GetProperty("id").GetString());
        var error = root.GetProperty("error");
        Assert.Equal(-32602, error.GetProperty("code").GetInt32());
        Assert.Equal("invalid_params", error.GetProperty("message").GetString());
    }

    [Theory]
    [Trait("SpecRef", "6.1")]
    [InlineData("7", 7d)]
    [InlineData("1.5", 1.5d)]
    public async Task Spec_6_1_HttpRequest_WhenIdIsNumber_ShouldKeepIdCorrelation(string requestIdLiteral, double expectedId)
    {
        var hostContext = HostTestContextFactory.Create(_tempRoot, _runtimeDirectory, _definitionsDirectory);
        var handler = new RpcHttpEndpointHandler(
            hostContext.Router,
            hostContext.FileSystemManager,
            Mock.Of<ILogger<RpcHttpEndpointHandler>>());

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Method = HttpMethods.Post;
        httpContext.Request.ContentType = "application/json";
        httpContext.Request.Headers["Authorization"] = $"Bearer {hostContext.Token}";
        httpContext.Request.Headers["X-DevHub-Protocol"] = "1";
        httpContext.Request.Headers["X-DevHub-ClientId"] = "http-numeric-id-client";
        httpContext.Request.Headers["X-DevHub-ClientSessionId"] = Guid.NewGuid().ToString("D");
        httpContext.RequestServices = new ServiceCollection()
            .AddLogging()
            .AddOptions()
            .BuildServiceProvider();
        httpContext.Response.Body = new MemoryStream();

        var requestJson = $"{{\"jsonrpc\":\"2.0\",\"id\":{requestIdLiteral},\"method\":\"hub.ping\",\"params\":{{}}}}";
        httpContext.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(requestJson));

        var result = await handler.HandleAsync(httpContext.Request, currentPort: null, CancellationToken.None);
        await result.ExecuteAsync(httpContext);

        Assert.Equal(StatusCodes.Status200OK, httpContext.Response.StatusCode);
        httpContext.Response.Body.Position = 0;
        using var responseDocument = await JsonDocument.ParseAsync(httpContext.Response.Body);
        var root = responseDocument.RootElement;
        var responseId = root.GetProperty("id");
        Assert.Equal(JsonValueKind.Number, responseId.ValueKind);
        Assert.Equal(expectedId, responseId.GetDouble(), precision: 6);
        Assert.True(root.GetProperty("result").GetProperty("ok").GetBoolean());
    }

    /// <summary>
    /// 释放测试资源。
    /// </summary>
    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }
}
