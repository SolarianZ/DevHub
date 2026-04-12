using DevHub.Core.Services;
using Microsoft.Extensions.Hosting;

namespace DevHub.Host.BackgroundServices;

/// <summary>
/// 应用实例清理后台服务。
/// </summary>
public sealed class AppRegistryCleanupBackgroundService : BackgroundService
{
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromSeconds(60);

    private readonly AppRegistry _appRegistry;
    private readonly ILogger<AppRegistryCleanupBackgroundService> _logger;

    /// <summary>
    /// 初始化实例清理后台服务。
    /// </summary>
    /// <param name="appRegistry">实例注册表。</param>
    /// <param name="logger">日志记录器。</param>
    public AppRegistryCleanupBackgroundService(
        AppRegistry appRegistry,
        ILogger<AppRegistryCleanupBackgroundService> logger)
    {
        _appRegistry = appRegistry;
        _logger = logger;
    }

    /// <summary>
    /// 执行一次实例清理。
    /// </summary>
    public void ExecuteOneIteration()
    {
        try
        {
            _appRegistry.CleanupExpiredInstances();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AppRegistry 周期清理失败");
        }
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(CleanupInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            ExecuteOneIteration();
        }
    }
}
