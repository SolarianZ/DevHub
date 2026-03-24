namespace DevHub.Host.Tests.TestHelpers;

using DevHub.Core.Services;
using DevHub.Core.Services.Events;
using DevHub.Core.Services.Rpc;
using DevHub.Host;
using Microsoft.Extensions.Logging;
using Moq;

/// <summary>
/// Host WebSocket 入口调用辅助工具。
/// </summary>
internal static class HostWebSocketTestInvoker
{
    /// <summary>
    /// 调用 Host WebSocket 会话处理入口。
    /// </summary>
    /// <param name="socket">脚本化 WebSocket。</param>
    /// <param name="router">RPC 路由器。</param>
    /// <param name="fileSystemManager">文件系统管理器。</param>
    /// <param name="eventBus">事件总线。</param>
    internal static async Task InvokeHandleWebSocketConnectionAsync(
        ScriptedWebSocket socket,
        RpcRouter router,
        FileSystemManager fileSystemManager,
        HubEventBus eventBus)
    {
        var handler = new WebSocketSessionHandler(
            router,
            fileSystemManager,
            eventBus,
            Mock.Of<ILogger<WebSocketSessionHandler>>());

        await handler.HandleWebSocketConnectionAsync(socket, CancellationToken.None);
    }
}
