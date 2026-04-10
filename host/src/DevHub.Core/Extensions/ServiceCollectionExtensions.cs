using System.ComponentModel;
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
    /// 注册 DevHub Core 核心服务，并自动解析运行时路径选项。
    /// </summary>
    /// <param name="services">依赖注入服务集合。</param>
    /// <returns>原服务集合，便于链式调用。</returns>
    /// <remarks>
    /// 该重载仅保留为兼容入口；仓库内部应统一使用显式传入 <see cref="RuntimePathOptions"/> 的重载，
    /// 避免再次引入隐式环境解析耦合。
    /// </remarks>
    [Obsolete("请改用 AddDevHubCore(IServiceCollection, RuntimePathOptions)。该重载仅保留为兼容入口。", error: false)]
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static IServiceCollection AddDevHubCore(this IServiceCollection services)
    {
        return services.AddDevHubCore(RuntimePathOptions.Resolve());
    }

    /// <summary>
    /// 注册 DevHub Core 核心服务。
    /// </summary>
    /// <param name="services">依赖注入服务集合。</param>
    /// <param name="runtimePathOptions">显式传入的运行时路径选项。</param>
    /// <returns>原服务集合，便于链式调用。</returns>
    /// <remarks>
    /// 本方法仅负责 Core 服务装配，不承担 Host 启动流程控制或运行时文件初始化。
    /// </remarks>
    public static IServiceCollection AddDevHubCore(this IServiceCollection services, RuntimePathOptions runtimePathOptions)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(runtimePathOptions);

        services.AddSingleton(runtimePathOptions);
        services.AddSingleton<RuntimeTuningOptions>(sp =>
            RuntimeTuningOptions.Resolve(sp.GetRequiredService<ILogger<RuntimeTuningOptions>>()));
        services.AddSingleton<RpcTestFaultInjectionPolicy>(sp =>
            RpcTestFaultInjectionPolicy.Resolve(sp.GetRequiredService<ILogger<RpcTestFaultInjectionPolicy>>()));
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<IProcessLauncher, ProcessLauncher>();
        services.AddSingleton<AppDefinitionValidator>();

        // 注册核心服务
        services.AddSingleton<AppRegistry>(sp =>
            new AppRegistry(
                sp.GetRequiredService<IClock>(),
                sp.GetRequiredService<ILogger<AppRegistry>>(),
                sp.GetRequiredService<RuntimeTuningOptions>()));
        services.AddSingleton<DefinitionLoader>(sp =>
            new DefinitionLoader(
                sp.GetRequiredService<RuntimePathOptions>().DefinitionsPath,
                sp.GetRequiredService<ILogger<DefinitionLoader>>(),
                sp.GetRequiredService<AppDefinitionValidator>()));
        services.AddSingleton<IDefinitionProvider, DefinitionProvider>();
        services.AddSingleton<IDefinitionManager>(sp =>
            new DefinitionManager(
                sp.GetRequiredService<RuntimePathOptions>(),
                sp.GetRequiredService<IDefinitionProvider>(),
                sp.GetRequiredService<AppDefinitionValidator>(),
                sp.GetRequiredService<IClock>(),
                sp.GetRequiredService<ILogger<DefinitionManager>>(),
                sp.GetService<HubEventBus>()));
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
                sp.GetRequiredService<RuntimeTuningOptions>(),
                sp.GetRequiredService<ILogger<LaunchCoordinator>>()));

        // 注册 RPC 处理器
        services.AddSingleton<IRpcHandler, HubPingHandler>();
        services.AddSingleton<IRpcHandler>(sp =>
            new AppDefinitionsHandler(
                sp.GetRequiredService<IDefinitionProvider>(),
                sp.GetRequiredService<IDefinitionManager>(),
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
                sp.GetRequiredService<RuntimeTuningOptions>(),
                sp.GetService<HubEventBus>()));
        services.AddSingleton<IRpcHandler, LaunchHandler>();

        // 注册 RPC 路由器
        services.AddSingleton<RpcRouter>();

        return services;
    }
}
