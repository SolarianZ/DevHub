using DevHub.Core.Services.Abstractions;
using DevHub.Core.Services.Invocation;
using Microsoft.Extensions.Hosting;

namespace DevHub.Host.BackgroundServices;

/// <summary>
/// Invocation 超时扫描后台服务。
/// </summary>
public sealed class InvocationTimeoutBackgroundService : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMilliseconds(200);

    private readonly InvocationTimeoutWorker _worker;
    private readonly IClock _clock;
    private readonly ILogger<InvocationTimeoutBackgroundService> _logger;

    /// <summary>
    /// 初始化 Invocation 超时扫描后台服务。
    /// </summary>
    /// <param name="worker">超时扫描器。</param>
    /// <param name="clock">系统时钟。</param>
    /// <param name="logger">日志记录器。</param>
    public InvocationTimeoutBackgroundService(
        InvocationTimeoutWorker worker,
        IClock clock,
        ILogger<InvocationTimeoutBackgroundService> logger)
    {
        _worker = worker;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>
    /// 执行一次超时扫描。
    /// </summary>
    public void ExecuteOneIteration()
    {
        try
        {
            _worker.SweepOnce(_clock.UtcNow);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "InvocationTimeoutWorker 周期扫描失败");
        }
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(SweepInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            ExecuteOneIteration();
        }
    }
}
