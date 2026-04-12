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
    /// `AddDevHubSdk()` 默认使用的命名 <see cref="HttpClient" />。
    /// </summary>
    public const string DefaultHttpClientName = "DevHub.Sdk";

    /// <summary>
    /// 注册 DevHub SDK 所需的选项、公开 seam 与工厂服务。
    /// </summary>
    /// <param name="services">服务集合。</param>
    /// <returns>原始服务集合。</returns>
    public static IServiceCollection AddDevHubSdk(this IServiceCollection services)
    {
        if (services is null)
        {
            throw new ArgumentNullException(nameof(services));
        }

        services.AddOptions<DevHubClientOptions>();
        services.AddHttpClient(DefaultHttpClientName, static client => client.Timeout = Timeout.InfiniteTimeSpan);
        services.TryAddSingleton<IDevHubRuntimeResolver, FileSystemDevHubRuntimeResolver>();
        services.TryAddSingleton<IDevHubHttpClientProvider>(static serviceProvider =>
            new NamedDevHubHttpClientProvider(
                serviceProvider.GetRequiredService<IHttpClientFactory>(),
                DefaultHttpClientName));
        services.TryAddSingleton<IDevHubHttpTransportFactory>(static serviceProvider =>
            new JsonRpcHttpTransportFactory(serviceProvider.GetRequiredService<IDevHubHttpClientProvider>()));
        services.TryAddSingleton<IDevHubWebSocketSessionFactory, JsonRpcWebSocketSessionFactory>();
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
        if (configure is null)
        {
            throw new ArgumentNullException(nameof(configure));
        }

        services.AddDevHubSdk();
        services.Configure(configure);
        return services;
    }

    private sealed class DefaultDevHubClientFactory(
        IOptionsMonitor<DevHubClientOptions> optionsMonitor,
        IDevHubRuntimeResolver runtimeResolver,
        IDevHubHttpTransportFactory transportFactory) : IDevHubClientFactory
    {
        private readonly IOptionsMonitor<DevHubClientOptions> _optionsMonitor = optionsMonitor;
        private readonly IDevHubRuntimeResolver _runtimeResolver = runtimeResolver;
        private readonly IDevHubHttpTransportFactory _transportFactory = transportFactory;

        public Task<DevHubClient> CreateAsync(CancellationToken cancellationToken = default)
        {
            return DevHubClient.FromRuntimeAsync(
                _optionsMonitor.CurrentValue,
                new DevHubClientDependencies
                {
                    RuntimeResolver = _runtimeResolver,
                    TransportFactory = _transportFactory
                },
                cancellationToken);
        }
    }

    private sealed class DefaultDevHubEventsClientFactory(
        IOptionsMonitor<DevHubClientOptions> optionsMonitor,
        IDevHubRuntimeResolver runtimeResolver,
        IDevHubWebSocketSessionFactory sessionFactory) : IDevHubEventsClientFactory
    {
        private readonly IOptionsMonitor<DevHubClientOptions> _optionsMonitor = optionsMonitor;
        private readonly IDevHubRuntimeResolver _runtimeResolver = runtimeResolver;
        private readonly IDevHubWebSocketSessionFactory _sessionFactory = sessionFactory;

        public Task<DevHubEventsClient> CreateAsync(CancellationToken cancellationToken = default)
        {
            return DevHubEventsClient.FromRuntimeAsync(
                _optionsMonitor.CurrentValue,
                new DevHubEventsClientDependencies
                {
                    RuntimeResolver = _runtimeResolver,
                    SessionFactory = _sessionFactory
                },
                cancellationToken);
        }
    }

    private sealed class NamedDevHubHttpClientProvider(IHttpClientFactory httpClientFactory, string clientName) : IDevHubHttpClientProvider
    {
        private readonly IHttpClientFactory _httpClientFactory = httpClientFactory;
        private readonly string _clientName = clientName;

        public HttpClient CreateClient(DevHubClientOptions options, DevHubRuntimeConnectionInfo connectionInfo)
        {
            _ = options ?? throw new ArgumentNullException(nameof(options));
            _ = connectionInfo ?? throw new ArgumentNullException(nameof(connectionInfo));
            return _httpClientFactory.CreateClient(_clientName);
        }
    }
}
