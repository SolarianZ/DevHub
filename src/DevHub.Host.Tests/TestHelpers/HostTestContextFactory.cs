namespace DevHub.Host.Tests.TestHelpers;

using DevHub.Core.Services;
using DevHub.Core.Services.Events;
using DevHub.Core.Services.Invocation;
using DevHub.Core.Services.Rpc;
using DevHub.Core.Services.Rpc.Handlers;
using Microsoft.Extensions.Logging;
using Moq;

/// <summary>
/// Host 测试上下文工厂。
/// </summary>
internal static class HostTestContextFactory
{
    /// <summary>
    /// 创建 Host WS 生命周期测试所需上下文。
    /// </summary>
    /// <param name="definitionsDirectory">AppDefinition 目录。</param>
    /// <returns>可用于调用 Host WS 入口的上下文。</returns>
    internal static HostTestContext Create(string definitionsDirectory)
    {
        var fileSystemManager = new FileSystemManager(Mock.Of<ILogger<FileSystemManager>>(), definitionsDirectory);
        fileSystemManager.InitializeDirectories();
        var token = fileSystemManager.GetToken();

        var appRegistry = new AppRegistry(Mock.Of<ILogger<AppRegistry>>());
        var definitionLoader = new DefinitionLoader(definitionsDirectory, Mock.Of<ILogger<DefinitionLoader>>());
        definitionLoader.Load();
        var eventBus = new HubEventBus(Mock.Of<ILogger<HubEventBus>>());

        var routingService = new InvocationRoutingService(appRegistry, Mock.Of<ILogger<InvocationRoutingService>>());
        var invocationStore = new InvocationStore(Mock.Of<ILogger<InvocationStore>>(), routingService, eventBus);
        var requestWaiter = new InvocationRequestWaiter(Mock.Of<ILogger<InvocationRequestWaiter>>());
        var runtimeHttpBaseUrlProvider = new RuntimeHttpBaseUrlProvider(Mock.Of<ILogger<RuntimeHttpBaseUrlProvider>>());
        var launchCoordinator = new LaunchCoordinator(
            definitionLoader,
            appRegistry,
            runtimeHttpBaseUrlProvider,
            Mock.Of<ILogger<LaunchCoordinator>>());

        var handlers = new IRpcHandler[]
        {
            new HubPingHandler(Mock.Of<ILogger<HubPingHandler>>()),
            new AppDefinitionsHandler(definitionLoader, Mock.Of<ILogger<AppDefinitionsHandler>>()),
            new AppInstancesHandler(appRegistry, Mock.Of<ILogger<AppInstancesHandler>>(), eventBus),
            new InvocationHandler(
                appRegistry,
                definitionLoader,
                routingService,
                invocationStore,
                requestWaiter,
                launchCoordinator,
                Mock.Of<ILogger<InvocationHandler>>(),
                eventBus),
            new LaunchHandler(launchCoordinator, Mock.Of<ILogger<LaunchHandler>>())
        };

        var router = new RpcRouter(handlers, Mock.Of<ILogger<RpcRouter>>());
        return new HostTestContext(router, fileSystemManager, eventBus, token);
    }
}

/// <summary>
/// Host 测试运行上下文。
/// </summary>
/// <param name="Router">RPC 路由器。</param>
/// <param name="FileSystemManager">文件系统管理器。</param>
/// <param name="EventBus">事件总线。</param>
/// <param name="Token">当前测试环境 token。</param>
internal sealed record HostTestContext(
    RpcRouter Router,
    FileSystemManager FileSystemManager,
    HubEventBus EventBus,
    string Token);
