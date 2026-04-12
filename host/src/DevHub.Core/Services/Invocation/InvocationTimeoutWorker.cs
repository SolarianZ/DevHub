using Microsoft.Extensions.Logging;
using DevHub.Core.Services.Abstractions;

namespace DevHub.Core.Services.Invocation;

/// <summary>
/// Invocation 超时扫描器（M2 内存态）。
/// </summary>
public class InvocationTimeoutWorker : IDisposable
{
    private readonly InvocationStore _store;
    private readonly InvocationRequestWaiter _requestWaiter;
    private readonly ILogger<InvocationTimeoutWorker> _logger;

    /// <summary>
    /// 初始化扫描器。
    /// </summary>
    public InvocationTimeoutWorker(
        InvocationStore store,
        InvocationRequestWaiter requestWaiter,
        IClock clock,
        ILogger<InvocationTimeoutWorker> logger)
    {
        _store = store;
        _requestWaiter = requestWaiter;
        _logger = logger;
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

    /// <summary>
    /// 释放资源。
    /// </summary>
    public void Dispose()
    {
    }
}
