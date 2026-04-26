using DevHub.Core.Extensions;
using DevHub.Core.Services;
using DevHub.Core.Services.Abstractions;
using DevHub.Core.Services.Events;
using DevHub.Core.Services.Invocation;
using DevHub.Core.Services.Rpc;
using DevHub.Core.Services.Rpc.Handlers;
using DevHub.Host.BackgroundServices;
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

        var runtimeVersion = HostVersionProvider.ResolveRuntimeVersion(hubVersion)
            ?? throw new InvalidOperationException("无法解析 Host 运行时版本。");

        services.AddDevHubCore(runtimePathOptions);
        services.AddSingleton<IHubVersionSource>(new FixedHubVersionSource(runtimeVersion));
        services.AddSingleton<HostRuntimeContext>();
        services.AddSingleton<IRuntimeHttpBaseUrlProvider, HostRuntimeHttpBaseUrlProvider>();
        services.AddSingleton<HubEventBus>();
        services.AddSingleton<IHubEventPublisher>(sp => sp.GetRequiredService<HubEventBus>());
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
        services.AddSingleton<IRpcHandler, HubPingHandler>();
        services.AddSingleton<IRpcHandler, HubGetVersionHandler>();
        services.AddSingleton<IRpcHandler, AppDefinitionsHandler>();
        services.AddSingleton<IRpcHandler, AppInstancesHandler>();
        services.AddSingleton<IRpcHandler, InvocationHandler>();
        services.AddSingleton<IRpcHandler, LaunchHandler>();
        services.AddSingleton<RpcRouter>();
        services.AddSingleton<RpcHttpEndpointHandler>();
        services.AddSingleton<WebSocketSessionHandler>();

        return services;
    }

    private sealed class FixedHubVersionSource : IHubVersionSource
    {
        public FixedHubVersionSource(string currentVersion)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(currentVersion);
            CurrentVersion = currentVersion;
        }

        public string CurrentVersion { get; }
    }
}
