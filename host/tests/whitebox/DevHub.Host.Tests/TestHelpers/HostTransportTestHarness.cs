namespace DevHub.Host.Tests.TestHelpers;

using DevHub.Core.Extensions;
using DevHub.Core.Services;
using DevHub.Core.Services.Events;
using DevHub.Host;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Host transport 白盒测试统一夹具。
/// </summary>
internal sealed class HostTransportTestHarness : IDisposable
{
    private readonly ServiceProvider _serviceProvider;

    /// <summary>
    /// 初始化统一测试夹具。
    /// </summary>
    /// <param name="rootDirectory">测试专用数据根目录。</param>
    internal HostTransportTestHarness(string rootDirectory)
    {
        _serviceProvider = BuildServiceProvider(rootDirectory);
        FileSystemManager = _serviceProvider.GetRequiredService<FileSystemManager>();
        EventBus = _serviceProvider.GetRequiredService<HubEventBus>();
        HttpHandler = _serviceProvider.GetRequiredService<RpcHttpEndpointHandler>();
        WebSocketHandler = _serviceProvider.GetRequiredService<WebSocketSessionHandler>();

        FileSystemManager.InitializeDirectories();
        Token = FileSystemManager.GetToken();
        RefreshDefinitions();
    }

    /// <summary>
    /// 当前测试环境 token。
    /// </summary>
    internal string Token { get; }

    /// <summary>
    /// 文件系统管理器。
    /// </summary>
    internal FileSystemManager FileSystemManager { get; }

    /// <summary>
    /// 事件总线。
    /// </summary>
    internal HubEventBus EventBus { get; }

    /// HTTP 处理器。
    /// </summary>
    internal RpcHttpEndpointHandler HttpHandler { get; }

    /// <summary>
    /// WebSocket 处理器。
    /// </summary>
    internal WebSocketSessionHandler WebSocketHandler { get; }

    /// <summary>
    /// 刷新定义快照。
    /// </summary>
    internal void RefreshDefinitions()
    {
        _serviceProvider.GetRequiredService<IDefinitionProvider>().Refresh();
    }

    /// <summary>
    /// 调用 WebSocket 连接生命周期入口。
    /// </summary>
    internal Task InvokeWebSocketConnectionAsync(ScriptedWebSocket socket, CancellationToken cancellationToken = default)
    {
        return WebSocketHandler.HandleWebSocketConnectionAsync(socket, cancellationToken);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _serviceProvider.Dispose();
    }

    private static ServiceProvider BuildServiceProvider(string rootDirectory)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDevHubCore();
        ReplaceRuntimePathOptions(services, rootDirectory);
        services.AddSingleton<RpcHttpEndpointHandler>();
        services.AddSingleton<WebSocketSessionHandler>();

        return services.BuildServiceProvider();
    }

    private static void ReplaceRuntimePathOptions(IServiceCollection services, string rootDirectory)
    {
        var existingDescriptor = services.LastOrDefault(descriptor => descriptor.ServiceType == typeof(RuntimePathOptions));
        if (existingDescriptor is not null)
        {
            services.Remove(existingDescriptor);
        }

        services.AddSingleton(RuntimePathOptions.Create(rootDirectory));
    }
}
