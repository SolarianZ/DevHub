using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using DevHub.Core.Services;
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
    /// <param name="definitionsPath">AppDefinition 目录路径（用于 DefinitionLoader 与 FileSystemManager）。</param>
    /// <returns>原服务集合，便于链式调用。</returns>
    /// <remarks>
    /// 本方法仅负责服务装配，不承担启动流程控制或运行时状态初始化。
    /// </remarks>
    public static IServiceCollection AddDevHubCore(this IServiceCollection services, string definitionsPath)
    {
        // 注册核心服务
        services.AddSingleton<FileSystemManager>(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<FileSystemManager>>();
            return new FileSystemManager(logger, definitionsPath);
        });
        services.AddSingleton<AppRegistry>();
        services.AddSingleton<DefinitionLoader>(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<DefinitionLoader>>();
            return new DefinitionLoader(definitionsPath, logger);
        });
        services.AddSingleton<HubEventBus>();
        services.AddSingleton<InvocationRoutingService>();
        services.AddSingleton<InvocationStore>();
        services.AddSingleton<InvocationRequestWaiter>();
        services.AddSingleton<InvocationTimeoutWorker>();
        services.AddSingleton<IRuntimeHttpBaseUrlProvider, RuntimeHttpBaseUrlProvider>();
        services.AddSingleton<LaunchCoordinator>();

        // 注册RPC处理器
        services.AddSingleton<IRpcHandler, HubPingHandler>();
        services.AddSingleton<IRpcHandler, AppDefinitionsHandler>();
        services.AddSingleton<IRpcHandler, AppInstancesHandler>();
        services.AddSingleton<IRpcHandler, InvocationHandler>();
        services.AddSingleton<IRpcHandler, LaunchHandler>();

        // 注册RPC路由器
        services.AddSingleton<RpcRouter>();

        return services;
    }
}
