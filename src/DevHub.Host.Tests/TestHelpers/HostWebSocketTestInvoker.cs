namespace DevHub.Host.Tests.TestHelpers;

using System.Reflection;
using DevHub.Core.Services;
using DevHub.Core.Services.Events;
using DevHub.Core.Services.Rpc;
using DevHub.Host;
using Microsoft.Extensions.Logging;
using Moq;

/// <summary>
/// Host WebSocket 入口反射调用辅助工具。
/// </summary>
internal static class HostWebSocketTestInvoker
{
    /// <summary>
    /// 调用 Program 内部 WebSocket 处理入口。
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
        var method = typeof(Program).GetMethod("HandleWebSocketConnectionAsync", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var task = method!.Invoke(
            null,
            [
                socket,
                router,
                fileSystemManager,
                eventBus,
                Mock.Of<ILogger<Program>>(),
                CancellationToken.None
            ]) as Task;

        Assert.NotNull(task);
        await task!;
    }
}
