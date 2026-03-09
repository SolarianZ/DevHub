using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace DevHub.Sdk;

/// <summary>
/// DevHub HTTP 客户端工厂。
/// </summary>
public interface IDevHubClientFactory
{
    /// <summary>
    /// 基于当前选项与运行时发现信息创建 HTTP 客户端。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>HTTP 客户端实例。</returns>
    Task<DevHubClient> CreateAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// DevHub WebSocket 事件客户端工厂。
/// </summary>
public interface IDevHubEventsClientFactory
{
    /// <summary>
    /// 基于当前选项与运行时发现信息创建 WebSocket 事件客户端。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>事件客户端实例。</returns>
    Task<DevHubEventsClient> CreateAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// DevHub SDK 的依赖注入注册扩展。
/// </summary>
public static class DevHubServiceCollectionExtensions
{
    /// <summary>
    /// 注册 DevHub SDK 所需的选项与工厂服务。
    /// </summary>
    /// <param name="services">服务集合。</param>
    /// <returns>原始服务集合。</returns>
    public static IServiceCollection AddDevHubSdk(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<DevHubClientOptions>();
        services.TryAddSingleton<IDevHubClientFactory, DefaultDevHubClientFactory>();
        services.TryAddSingleton<IDevHubEventsClientFactory, DefaultDevHubEventsClientFactory>();
        return services;
    }

    /// <summary>
    /// 注册 DevHub SDK 所需的选项与工厂服务，并配置客户端选项。
    /// </summary>
    /// <param name="services">服务集合。</param>
    /// <param name="configure">客户端选项配置回调。</param>
    /// <returns>原始服务集合。</returns>
    public static IServiceCollection AddDevHubSdk(this IServiceCollection services, Action<DevHubClientOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        services.AddDevHubSdk();
        services.Configure(configure);
        return services;
    }

    private sealed class DefaultDevHubClientFactory(IOptionsMonitor<DevHubClientOptions> optionsMonitor) : IDevHubClientFactory
    {
        private readonly IOptionsMonitor<DevHubClientOptions> _optionsMonitor = optionsMonitor;

        public Task<DevHubClient> CreateAsync(CancellationToken cancellationToken = default)
        {
            return DevHubClient.FromRuntimeAsync(_optionsMonitor.CurrentValue, cancellationToken);
        }
    }

    private sealed class DefaultDevHubEventsClientFactory(IOptionsMonitor<DevHubClientOptions> optionsMonitor) : IDevHubEventsClientFactory
    {
        private readonly IOptionsMonitor<DevHubClientOptions> _optionsMonitor = optionsMonitor;

        public Task<DevHubEventsClient> CreateAsync(CancellationToken cancellationToken = default)
        {
            return DevHubEventsClient.FromRuntimeAsync(_optionsMonitor.CurrentValue, cancellationToken);
        }
    }
}
