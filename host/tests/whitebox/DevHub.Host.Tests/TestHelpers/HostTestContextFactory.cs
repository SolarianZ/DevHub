namespace DevHub.Host.Tests.TestHelpers;

using DevHub.Core.Services;
using DevHub.Host.Events;

/// <summary>
/// Host 测试上下文工厂。
/// </summary>
internal static class HostTestContextFactory
{
    /// <summary>
    /// 创建 Host transport 规范测试所需上下文。
    /// </summary>
    /// <param name="rootDirectory">测试专用根目录。</param>
    /// <returns>可用于调用 Host transport 入口的上下文。</returns>
    internal static HostTestContext Create(string rootDirectory)
    {
        return new HostTestContext(new HostTransportTestHarness(rootDirectory));
    }
}

/// <summary>
/// Host transport 测试上下文。
/// </summary>
internal sealed class HostTestContext : IDisposable
{
    private readonly HostTransportTestHarness _harness;

    internal HostTestContext(HostTransportTestHarness harness)
    {
        _harness = harness;
    }

    /// <summary>
    /// 事件总线。
    /// </summary>
    internal HubEventSessionManager EventBus => _harness.EventBus;

    /// <summary>
    /// 当前测试环境 token。
    /// </summary>
    internal string Token => _harness.Token;

    /// <summary>
    /// 调用 WebSocket 连接生命周期入口。
    /// </summary>
    internal Task InvokeWebSocketConnectionAsync(ScriptedWebSocket socket, CancellationToken cancellationToken = default)
    {
        return _harness.InvokeWebSocketConnectionAsync(socket, cancellationToken);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _harness.Dispose();
    }
}
