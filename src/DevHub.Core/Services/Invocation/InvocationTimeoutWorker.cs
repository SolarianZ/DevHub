using Microsoft.Extensions.Logging;

namespace DevHub.Core.Services.Invocation;

/// <summary>
/// Invocation 超时扫描器（M2 内存态）。
/// </summary>
public class InvocationTimeoutWorker : IDisposable
{
    private static readonly TimeSpan DefaultInterval = TimeSpan.FromMilliseconds(200);

    private readonly InvocationStore _store;
    private readonly InvocationRequestWaiter _requestWaiter;
    private readonly ILogger<InvocationTimeoutWorker> _logger;
    private readonly Timer _timer;
    private bool _disposed;

    /// <summary>
    /// 初始化扫描器。
    /// </summary>
    public InvocationTimeoutWorker(
        InvocationStore store,
        InvocationRequestWaiter requestWaiter,
        ILogger<InvocationTimeoutWorker> logger)
    {
        _store = store;
        _requestWaiter = requestWaiter;
        _logger = logger;
        _timer = new Timer(OnTimer, null, DefaultInterval, DefaultInterval);
    }

    /// <summary>
    /// 单次扫描（用于测试或手动触发）。
    /// </summary>
    /// <param name="now">当前 UTC 时间。</param>
    public void SweepOnce(DateTime now)
    {
        var transitions = _store.Sweep(now);

        foreach (var transition in transitions)
        {
            if (transition.Kind != Models.InvocationKind.Request)
            {
                continue;
            }

            switch (transition.Outcome)
            {
                case InvocationSweepOutcome.Timeout:
                    _requestWaiter.CompleteTimeout(transition.InvocationId, transition.ElapsedMs);
                    break;
                case InvocationSweepOutcome.Expired:
                    _requestWaiter.CompleteExpired(transition.InvocationId, transition.ElapsedMs);
                    break;
                case InvocationSweepOutcome.Requeued:
                    break;
            }
        }
    }

    private void OnTimer(object? state)
    {
        try
        {
            SweepOnce(DateTime.UtcNow);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "InvocationTimeoutWorker 周期扫描失败");
        }
    }

    /// <summary>
    /// 释放资源。
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _timer.Dispose();
        _disposed = true;
    }
}
