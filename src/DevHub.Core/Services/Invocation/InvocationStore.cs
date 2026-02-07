using System.Collections.Concurrent;
using DevHub.Core.Models;
using Microsoft.Extensions.Logging;
using InvocationModel = DevHub.Core.Models.Invocation;

namespace DevHub.Core.Services.Invocation;

/// <summary>
/// Invocation 内存存储。
/// </summary>
public class InvocationStore
{
    private readonly object _syncRoot = new();
    private readonly ConcurrentDictionary<string, InvocationModel> _all = new();
    private readonly ILogger<InvocationStore> _logger;
    private readonly InvocationRoutingService _routingService;

    /// <summary>
    /// 初始化存储。
    /// </summary>
    public InvocationStore(ILogger<InvocationStore> logger, InvocationRoutingService routingService)
    {
        _logger = logger;
        _routingService = routingService;
    }

    /// <summary>
    /// 创建并存储调用。
    /// </summary>
    /// <param name="invocation">调用对象。</param>
    /// <param name="hasOnlineCandidates">是否存在在线候选。</param>
    public InvocationModel CreateInvocation(InvocationModel invocation, bool hasOnlineCandidates)
    {
        lock (_syncRoot)
        {
            invocation.State = hasOnlineCandidates ? InvocationState.Queued : InvocationState.Pending;
            _all[invocation.InvocationId] = invocation;
            return invocation;
        }
    }

    /// <summary>
    /// 获取调用。
    /// </summary>
    public bool TryGet(string invocationId, out InvocationModel? invocation)
    {
        var exists = _all.TryGetValue(invocationId, out var found);
        invocation = found;
        return exists;
    }

    /// <summary>
    /// 由实例执行 poll。
    /// </summary>
    public async Task<IReadOnlyList<InvocationModel>> PollAsync(AppInstance instance, int maxCount, int waitMs, CancellationToken cancellationToken)
    {
        var startAt = DateTime.UtcNow;

        while (true)
        {
            var now = DateTime.UtcNow;
            SweepExpiredLeases(now);
            var leased = TryLease(instance, maxCount, now);
            if (leased.Count > 0)
            {
                return leased;
            }

            if (waitMs <= 0 || (DateTime.UtcNow - startAt).TotalMilliseconds >= waitMs)
            {
                return [];
            }

            var remaining = waitMs - (int)(DateTime.UtcNow - startAt).TotalMilliseconds;
            var delayMs = Math.Clamp(remaining, 1, 100);
            await Task.Delay(delayMs, cancellationToken);
        }
    }

    /// <summary>
    /// 响应调用。
    /// </summary>
    public InvocationRespondStatus Respond(string instanceId, string invocationId, object? value, object? error)
    {
        lock (_syncRoot)
        {
            SweepExpiredLeases(DateTime.UtcNow);

            if (!_all.TryGetValue(invocationId, out var invocation))
            {
                return InvocationRespondStatus.NotFound;
            }

            var now = DateTime.UtcNow;
            if (now > invocation.CreatedAtUtc.AddMilliseconds(invocation.Options.TtlMs))
            {
                invocation.State = InvocationState.Expired;
                return InvocationRespondStatus.Expired;
            }

            if (invocation.State is InvocationState.Completed or InvocationState.Failed)
            {
                return InvocationRespondStatus.DeliveryConflict;
            }

            if (invocation.State is InvocationState.Timeout or InvocationState.Expired)
            {
                return InvocationRespondStatus.Expired;
            }

            if (invocation.State != InvocationState.Delivered)
            {
                return InvocationRespondStatus.DeliveryConflict;
            }

            if (!string.Equals(invocation.LeaseHolderInstanceId, instanceId, StringComparison.Ordinal))
            {
                return InvocationRespondStatus.DeliveryConflict;
            }

            if (invocation.LeaseExpireAtUtc.HasValue && now > invocation.LeaseExpireAtUtc.Value)
            {
                return InvocationRespondStatus.DeliveryConflict;
            }

            invocation.State = error is null ? InvocationState.Completed : InvocationState.Failed;
            invocation.CompletedAtUtc = now;
            invocation.ResponseValue = value;
            invocation.ResponseError = error;
            return InvocationRespondStatus.Success;
        }
    }

    /// <summary>
    /// 将 request 标记为等待超时。
    /// </summary>
    /// <param name="invocationId">调用 ID。</param>
    /// <param name="now">当前 UTC 时间。</param>
    public bool MarkTimeout(string invocationId, DateTime now)
    {
        lock (_syncRoot)
        {
            if (!_all.TryGetValue(invocationId, out var invocation))
            {
                return false;
            }

            if (invocation.State is InvocationState.Completed or InvocationState.Failed or InvocationState.Timeout or InvocationState.Expired)
            {
                return false;
            }

            invocation.State = InvocationState.Timeout;
            invocation.CompletedAtUtc = now;
            return true;
        }
    }

    /// <summary>
    /// 将调用标记为过期。
    /// </summary>
    /// <param name="invocationId">调用 ID。</param>
    /// <param name="now">当前 UTC 时间。</param>
    public bool MarkExpired(string invocationId, DateTime now)
    {
        lock (_syncRoot)
        {
            if (!_all.TryGetValue(invocationId, out var invocation))
            {
                return false;
            }

            if (invocation.State is InvocationState.Completed or InvocationState.Failed or InvocationState.Timeout or InvocationState.Expired)
            {
                return false;
            }

            invocation.State = InvocationState.Expired;
            invocation.CompletedAtUtc = now;
            return true;
        }
    }

    private List<InvocationModel> TryLease(AppInstance instance, int maxCount, DateTime now)
    {
        lock (_syncRoot)
        {
            var candidates = _all.Values
                .Where(i => i.State is InvocationState.Queued or InvocationState.Pending)
                .Where(i => now <= i.CreatedAtUtc.AddMilliseconds(i.Options.TtlMs))
                .Where(i => _routingService.IsEligibleForInstance(i, instance))
                .OrderBy(i => i.CreatedAtUtc)
                .Take(maxCount)
                .ToList();

            foreach (var invocation in candidates)
            {
                invocation.State = InvocationState.Delivered;
                invocation.LeaseHolderInstanceId = instance.InstanceId;
                invocation.LeaseExpireAtUtc = now.AddSeconds(invocation.Delivery.LeaseSeconds);
            }

            return candidates;
        }
    }

    private void SweepExpiredLeases(DateTime now)
    {
        lock (_syncRoot)
        {
            var expiredDelivered = _all.Values
                .Where(i => i.State == InvocationState.Delivered)
                .Where(i => i.LeaseExpireAtUtc.HasValue && now > i.LeaseExpireAtUtc.Value)
                .ToList();

            foreach (var invocation in expiredDelivered)
            {
                var ttlExpireAt = invocation.CreatedAtUtc.AddMilliseconds(invocation.Options.TtlMs);
                if (now > ttlExpireAt)
                {
                    invocation.State = InvocationState.Expired;
                    invocation.CompletedAtUtc = now;
                    continue;
                }

                invocation.LeaseHolderInstanceId = null;
                invocation.LeaseExpireAtUtc = null;
                invocation.Delivery.Attempt += 1;

                var hasOnlineCandidates = _routingService
                    .GetOnlineCandidates(invocation.AppId, invocation.Target)
                    .Count > 0;
                invocation.State = hasOnlineCandidates ? InvocationState.Queued : InvocationState.Pending;

                _logger.LogInformation(
                    "Invocation 租约到期已回收并重投递，InvocationId: {InvocationId}, Attempt: {Attempt}, NextState: {State}",
                    invocation.InvocationId,
                    invocation.Delivery.Attempt,
                    invocation.State);
            }
        }
    }
}
