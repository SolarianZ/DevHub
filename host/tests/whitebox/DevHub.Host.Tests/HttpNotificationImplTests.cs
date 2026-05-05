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
    public async Task Impl_RpcHttpEndpointHandler_WhenInvokePollCanceled_ShouldPropagateOperationCanceledException()
    {
        using var harness = new HostTransportTestHarness(_tempRoot);

        var instanceId = "http-poll-cancel-inst";
        var instanceSessionToken = harness.RegisterInstance(new AppInstance
        {
            InstanceId = instanceId,
            AppId = "http.poll.cancel.app",
            Scope = string.Empty,
            Pid = 9201,
            RegisteredAtUtc = DateTime.UtcNow,
            LastSeenUtc = DateTime.UtcNow,
            Invoke = new InvokeCapability
            {
                Poll = true,
                Respond = true
            }
        }, "http-poll-cancel-password");

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Method = HttpMethods.Post;
        httpContext.Request.ContentType = "application/json";
        httpContext.Request.Headers["Authorization"] = $"Bearer {harness.Token}";
        httpContext.Request.Headers["X-DevHub-Protocol"] = "1";
        httpContext.Request.Headers["X-DevHub-ClientId"] = "http-poll-cancel-client";
        httpContext.Request.Headers["X-DevHub-ClientSessionId"] = Guid.NewGuid().ToString("D");
        httpContext.RequestServices = new ServiceCollection()
            .AddLogging()
            .AddOptions()
            .BuildServiceProvider();
        httpContext.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(
            $$"""
            {
              "jsonrpc": "2.0",
              "id": "http-poll-cancel",
              "method": "hub.invoke.poll",
              "params": {
                "instanceId": "{{instanceId}}",
                "instanceSessionToken": "{{instanceSessionToken}}",
                "maxCount": 1,
                "waitMs": 25000
              }
            }
            """));
        httpContext.Response.Body = new MemoryStream();

        using var cancellationTokenSource = new CancellationTokenSource();
        var handleTask = harness.HttpHandler.HandleAsync(httpContext.Request, cancellationTokenSource.Token);

        await Task.Delay(100);
        cancellationTokenSource.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await handleTask);
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
