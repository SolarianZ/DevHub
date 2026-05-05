namespace DevHub.Host.Tests;

using DevHub.Host.Tests.TestHelpers;
using static DevHub.Host.Tests.TestHelpers.JsonRpcTestMessageHelper;

/// <summary>
/// Host 层 WebSocket 生命周期实现回归测试。
/// </summary>
[Trait("Category", "Impl")]
public sealed class WebSocketLifecycleImplTests : IDisposable
{
    private readonly string _tempRoot;

    /// <summary>
    /// 初始化测试上下文。
    /// </summary>
    public WebSocketLifecycleImplTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "DevHubHostWsImplTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    [Fact]
    public async Task Impl_WebSocketSessionHandler_WhenCloseHandshakeStalls_ShouldCompleteCleanupWithinBoundedTimeout()
    {
        using var context = HostTestContextFactory.Create(_tempRoot);

        var auth = CreateJson(new
        {
            jsonrpc = "2.0",
            id = "auth-stalled-close",
            method = "hub.ws.authenticate",
            @params = new
            {
                token = context.Token,
                protocolVersion = 1,
                clientId = "ws-stalled-close-client",
                clientSessionId = "66666666-6666-6666-6666-666666666666"
            }
        });

        var socket = new ScriptedWebSocket(
            [auth],
            autoCloseWhenQueueDrained: true,
            blockCloseAsyncUntilCanceled: true);

        var handlerTask = context.InvokeWebSocketConnectionAsync(socket);
        var completedTask = await Task.WhenAny(handlerTask, Task.Delay(TimeSpan.FromSeconds(3)));

        Assert.Same(handlerTask, completedTask);
        await handlerTask;
        Assert.True(socket.CloseAsyncCallCount >= 1);
        Assert.True(socket.CloseAsyncCancellationCount >= 1);
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
