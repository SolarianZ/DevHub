using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using DevHub.Core.Services;
using DevHub.Core.Services.Abstractions;
using DevHub.Core.Services.Events;
using DevHub.Core.Services.Invocation;
using DevHub.Core.Services.Rpc;
using DevHub.Core.Services.Rpc.Handlers;

namespace DevHub.Core.Extensions;

/// <summary>
/// 服务集合扩展方法
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// 注册 DevHub M1/M2 阶段核心服务。
    /// </summary>
    /// <param name="services">依赖注入服务集合。</param>
    /// <param name="definitionsPath">AppDefinition 目录路径覆盖（可选）。</param>
    /// <returns>原服务集合，便于链式调用。</returns>
    /// <remarks>
    /// 本方法仅负责服务装配，不承担启动流程控制或运行时状态初始化。
    /// </remarks>
    public static IServiceCollection AddDevHubCore(this IServiceCollection services, string? definitionsPath = null)
    {
        var runtimePathOptions = RuntimePathOptions.Resolve(definitionsPath);

        services.AddSingleton(runtimePathOptions);
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<IProcessLauncher, ProcessLauncher>();

        // 注册核心服务
        services.AddSingleton<FileSystemManager>(sp =>
            new FileSystemManager(
                sp.GetRequiredService<ILogger<FileSystemManager>>(),
                sp.GetRequiredService<RuntimePathOptions>()));
        services.AddSingleton<AppRegistry>(sp =>
            new AppRegistry(
                sp.GetRequiredService<IClock>(),
                sp.GetRequiredService<ILogger<AppRegistry>>()));
        services.AddSingleton<DefinitionLoader>(sp =>
            new DefinitionLoader(
                sp.GetRequiredService<RuntimePathOptions>().DefinitionsPath,
                sp.GetRequiredService<ILogger<DefinitionLoader>>()));
        services.AddSingleton<IDefinitionProvider, DefinitionProvider>();
        services.AddSingleton<HubEventBus>();
        services.AddSingleton<InvocationRoutingService>();
        services.AddSingleton<InvocationStore>(sp =>
            new InvocationStore(
                sp.GetRequiredService<ILogger<InvocationStore>>(),
                sp.GetRequiredService<InvocationRoutingService>(),
                sp.GetRequiredService<IClock>(),
                sp.GetService<HubEventBus>()));
        services.AddSingleton<InvocationRequestWaiter>();
        services.AddSingleton<InvocationTimeoutWorker>(sp =>
            new InvocationTimeoutWorker(
                sp.GetRequiredService<InvocationStore>(),
                sp.GetRequiredService<InvocationRequestWaiter>(),
                sp.GetRequiredService<IClock>(),
                sp.GetRequiredService<ILogger<InvocationTimeoutWorker>>()));
        services.AddSingleton<IRuntimeHttpBaseUrlProvider>(sp =>
            new RuntimeHttpBaseUrlProvider(
                sp.GetRequiredService<ILogger<RuntimeHttpBaseUrlProvider>>(),
                sp.GetRequiredService<RuntimePathOptions>()));
        services.AddSingleton<LaunchCoordinator>(sp =>
            new LaunchCoordinator(
                sp.GetRequiredService<IDefinitionProvider>(),
                sp.GetRequiredService<AppRegistry>(),
                sp.GetRequiredService<IRuntimeHttpBaseUrlProvider>(),
                sp.GetRequiredService<IProcessLauncher>(),
                sp.GetRequiredService<IClock>(),
                sp.GetRequiredService<ILogger<LaunchCoordinator>>()));

        // 注册RPC处理器
        services.AddSingleton<IRpcHandler, HubPingHandler>();
        services.AddSingleton<IRpcHandler>(sp =>
            new AppDefinitionsHandler(
                sp.GetRequiredService<IDefinitionProvider>(),
                sp.GetRequiredService<ILogger<AppDefinitionsHandler>>()));
        services.AddSingleton<IRpcHandler>(sp =>
            new AppInstancesHandler(
                sp.GetRequiredService<AppRegistry>(),
                sp.GetRequiredService<IClock>(),
                sp.GetRequiredService<ILogger<AppInstancesHandler>>(),
                sp.GetService<HubEventBus>()));
        services.AddSingleton<IRpcHandler>(sp =>
            new InvocationHandler(
                sp.GetRequiredService<AppRegistry>(),
                sp.GetRequiredService<IDefinitionProvider>(),
                sp.GetRequiredService<InvocationRoutingService>(),
                sp.GetRequiredService<InvocationStore>(),
                sp.GetRequiredService<InvocationRequestWaiter>(),
                sp.GetRequiredService<LaunchCoordinator>(),
                sp.GetRequiredService<IClock>(),
                sp.GetRequiredService<ILogger<InvocationHandler>>(),
                sp.GetService<HubEventBus>()));
        services.AddSingleton<IRpcHandler, LaunchHandler>();

        // 注册RPC路由器
        services.AddSingleton<RpcRouter>();

        return services;
    }
}
