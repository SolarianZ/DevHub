using DevHub.Core.Extensions;
using DevHub.Core.Services;
using DevHub.Core.Services.Events;
using DevHub.Core.Services.Invocation;
using DevHub.Core.Services.Rpc;
using DevHub.Host.BackgroundServices;
using DevHub.Host.Events;
using DevHub.Host.Rpc.Handlers;
using DevHub.Host.Runtime;
using Microsoft.Extensions.DependencyInjection;

namespace DevHub.Host.Extensions;

/// <summary>
/// Host 宿主层服务装配扩展。
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// 注册 DevHub Host 所需的 Core 与宿主服务。
    /// </summary>
    /// <param name="services">依赖注入服务集合。</param>
    /// <param name="runtimePathOptions">显式传入的运行时路径选项。</param>
    /// <param name="hubVersion">写入 <c>hub.json</c> 的 Host 版本号。</param>
    /// <returns>原服务集合，便于链式调用。</returns>
    public static IServiceCollection AddDevHubHost(
        this IServiceCollection services,
        RuntimePathOptions runtimePathOptions,
        string? hubVersion = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(runtimePathOptions);

        services.AddDevHubCore(runtimePathOptions);
        services.AddSingleton<HostRuntimeContext>();
        services.AddSingleton<IRuntimeHttpBaseUrlProvider, HostRuntimeHttpBaseUrlProvider>();
        services.AddSingleton<HubEventSessionManager>();
        services.AddSingleton<IHubEventPublisher>(sp => sp.GetRequiredService<HubEventSessionManager>());
        services.AddSingleton<HostDataDirectoryInitializer>();
        services.AddSingleton<HostRuntimeArtifactManager>(sp =>
            new HostRuntimeArtifactManager(
                sp.GetRequiredService<ILogger<HostRuntimeArtifactManager>>(),
                sp.GetRequiredService<RuntimePathOptions>(),
                sp.GetRequiredService<RuntimeTuningOptions>(),
                hubVersion));
        services.AddSingleton<HostBootstrapper>();
        services.AddSingleton<AppRegistryCleanupBackgroundService>();
        services.AddHostedService(sp => sp.GetRequiredService<AppRegistryCleanupBackgroundService>());
        services.AddSingleton<InvocationTimeoutBackgroundService>();
        services.AddHostedService(sp => sp.GetRequiredService<InvocationTimeoutBackgroundService>());
        services.AddSingleton<IRpcHandler, HubPingRpcHandler>();
        services.AddSingleton<IRpcHandler, AppDefinitionsRpcHandler>();
        services.AddSingleton<IRpcHandler, AppInstancesRpcHandler>();
        services.AddSingleton<IRpcHandler, InvocationRpcHandler>();
        services.AddSingleton<IRpcHandler, LaunchRpcHandler>();
        services.AddSingleton<RpcRouter>();
        services.AddSingleton<RpcHttpEndpointHandler>();
        services.AddSingleton<WebSocketSessionHandler>();

        return services;
    }
}
