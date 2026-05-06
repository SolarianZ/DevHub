using System.Collections.Concurrent;
using System.Security.Cryptography;
using DevHub.Core.Models;
using DevHub.Core.Services;
using DevHub.Core.Services.Abstractions;
using DevHub.Core.Services.Events;
using Microsoft.Extensions.Logging;
using InvocationModel = DevHub.Core.Models.Invocation;

namespace DevHub.Core.Services.Invocation;

/// <summary>
/// Invocation 内存存储。
/// </summary>
public class InvocationStore
{
    private static readonly TimeSpan TerminalInvocationRetention = TimeSpan.FromMinutes(10);
    private readonly object _syncRoot = new();
    private readonly ConcurrentDictionary<string, InvocationModel> _all = new();
    private readonly ILogger<InvocationStore> _logger;
    private readonly InvocationRoutingService _routingService;
    private readonly IHubEventPublisher? _eventPublisher;
    private readonly IClock _clock;

    /// <summary>
    /// 初始化存储。
    /// </summary>
    public InvocationStore(ILogger<InvocationStore> logger, InvocationRoutingService routingService, IClock clock, IHubEventPublisher? eventPublisher = null)
    {
        _logger = logger;
        _routingService = routingService;
        _clock = clock;
        _eventPublisher = eventPublisher;
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
            return CreateInvocationCore(invocation, hasOnlineCandidates);
        }
    }

    /// <summary>
    /// 在挂起 invocation 上限约束下，原子地创建并存储调用。
    /// </summary>
    /// <param name="invocation">调用对象。</param>
    /// <param name="hasOnlineCandidates">是否存在在线候选。</param>
    /// <param name="pendingInvocationsLimit">挂起 invocation 总量上限；0 表示不启用。</param>
    /// <param name="activeInvocationCount">
    /// 当前活动 invocation 数量。
    /// 创建成功时返回创建后的数量；创建失败时返回拒绝时的数量。
    /// </param>
    /// <returns>创建成功返回 <see langword="true"/>；若因超过上限被拒绝则返回 <see langword="false"/>。</returns>
    public bool TryCreateInvocation(
        InvocationModel invocation,
        bool hasOnlineCandidates,
        int pendingInvocationsLimit,
        out int activeInvocationCount)
    {
        lock (_syncRoot)
        {
            activeInvocationCount = CountActiveInvocationsUnsafe();
            if (pendingInvocationsLimit > 0 && activeInvocationCount >= pendingInvocationsLimit)
            {
                return false;
            }

            _ = CreateInvocationCore(invocation, hasOnlineCandidates);
            activeInvocationCount += 1;
            return true;
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
    /// 获取当前仍处于活动中的 invocation 数量。
    /// </summary>
    public int GetActiveInvocationCount()
    {
        lock (_syncRoot)
        {
            return CountActiveInvocationsUnsafe();
        }
    }

    /// <summary>
    /// 由实例执行 poll。
    /// </summary>
    public async Task<IReadOnlyList<InvocationModel>> PollAsync(
        AppInstance instance,
        int maxCount,
        int waitMs,
        CancellationToken cancellationToken,
        InvocationRequestWaiter? requestWaiter = null)
    {
        var startAt = _clock.UtcNow;

        while (true)
        {
            var now = _clock.UtcNow;
            var transitions = new List<InvocationSweepTransition>();
            List<InvocationModel> leased;
            lock (_syncRoot)
            {
                SweepExpiredLeasesUnsafe(now, transitions);
                AdvanceTimeoutAndExpirationUnsafe(now, transitions);
                leased = TryLeaseUnsafe(instance, maxCount, now);
            }

            CompleteRequestWaiters(requestWaiter, transitions);

            if (leased.Count > 0)
            {
                PublishDeliveredEvents(leased, instance, now);
                return leased;
            }

            if (waitMs <= 0 || (_clock.UtcNow - startAt).TotalMilliseconds >= waitMs)
            {
                return [];
            }

            var remaining = waitMs - (int)(_clock.UtcNow - startAt).TotalMilliseconds;
            var delayMs = Math.Clamp(remaining, 1, 100);
            await Task.Delay(delayMs, cancellationToken);
        }
    }

    /// <summary>
    /// 响应调用。
    /// </summary>
    public InvocationRespondStatus Respond(string instanceId, string invocationId, string leaseToken, object? value, object? error)
    {
        ProtocolIdentifier.EnsureInstanceId(instanceId, nameof(instanceId));
        ArgumentException.ThrowIfNullOrWhiteSpace(leaseToken);

        lock (_syncRoot)
        {
            SweepExpiredLeasesUnsafe(_clock.UtcNow);

            if (!_all.TryGetValue(invocationId, out var invocation))
            {
                return InvocationRespondStatus.NotFound;
            }

            var now = _clock.UtcNow;
            if (now > invocation.CreatedAtUtc.AddMilliseconds(invocation.Options.TtlMs))
            {
                MarkTerminalUnsafe(invocation, InvocationSweepOutcome.Expired, now);
                return InvocationRespondStatus.Expired;
            }

            if (invocation.Kind == InvocationKind.Request &&
                invocation.Options.WaitTimeoutMs.HasValue &&
                now >= invocation.CreatedAtUtc.AddMilliseconds(invocation.Options.WaitTimeoutMs.Value))
            {
                MarkTerminalUnsafe(invocation, InvocationSweepOutcome.Timeout, now);
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

            if (!string.Equals(invocation.Delivery.LeaseToken, leaseToken, StringComparison.Ordinal))
            {
                return InvocationRespondStatus.DeliveryConflict;
            }

            if (invocation.LeaseExpireAtUtc.HasValue && now > invocation.LeaseExpireAtUtc.Value)
            {
                return InvocationRespondStatus.DeliveryConflict;
            }

            invocation.State = error is null ? InvocationState.Completed : InvocationState.Failed;
            invocation.CompletedAtUtc = now;
            invocation.LeaseHolderInstanceId = null;
            invocation.LeaseExpireAtUtc = null;
            invocation.Delivery.LeaseToken = string.Empty;
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

            MarkTerminalUnsafe(invocation, InvocationSweepOutcome.Timeout, now);
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

            MarkTerminalUnsafe(invocation, InvocationSweepOutcome.Expired, now);
            return true;
        }
    }

    /// <summary>
    /// 扫描并推进调用状态（TTL / waitTimeout / lease 到期）。
    /// </summary>
    /// <param name="now">当前 UTC 时间。</param>
    public IReadOnlyList<InvocationSweepTransition> Sweep(DateTime now)
    {
        var transitions = new List<InvocationSweepTransition>();

        lock (_syncRoot)
        {
            SweepExpiredLeasesUnsafe(now, transitions);
            AdvanceTimeoutAndExpirationUnsafe(now, transitions);

            CleanupTerminalInvocations(now);
        }

        return transitions;
    }

    private List<InvocationModel> TryLeaseUnsafe(AppInstance instance, int maxCount, DateTime now)
    {
        var candidates = _all.Values
            .Where(i => i.State is InvocationState.Queued or InvocationState.Pending)
            .Where(i => now <= i.CreatedAtUtc.AddMilliseconds(i.Options.TtlMs))
            .Where(i => i.Kind != InvocationKind.Request || !i.Options.WaitTimeoutMs.HasValue || now <= i.CreatedAtUtc.AddMilliseconds(i.Options.WaitTimeoutMs.Value))
            .Where(i => _routingService.IsEligibleForInstance(i, instance))
            .OrderBy(i => i.CreatedAtUtc)
            .Take(maxCount)
            .ToList();

        foreach (var invocation in candidates)
        {
            invocation.State = InvocationState.Delivered;
            invocation.LeaseHolderInstanceId = instance.InstanceId;
            invocation.LeaseExpireAtUtc = now.AddSeconds(invocation.Delivery.LeaseSeconds);
            invocation.Delivery.LeaseToken = GenerateLeaseToken();
        }

        return candidates;
    }

    private void PublishDeliveredEvents(IReadOnlyList<InvocationModel> leased, AppInstance instance, DateTime deliveredAtUtc)
    {
        if (_eventPublisher is null || leased.Count == 0)
        {
            return;
        }

        foreach (var invocation in leased)
        {
            _eventPublisher.Publish(new HubEventMessage
            {
                Type = HubEventTypes.InvocationDelivered,
                TimeUtc = deliveredAtUtc,
                Payload = new
                {
                    invocationId = invocation.InvocationId,
                    appId = invocation.AppId,
                    instanceId = instance.InstanceId,
                    target = invocation.Target,
                    scope = invocation.Target.Scope,
                    method = invocation.Method,
                    kind = invocation.Kind.ToString().ToLowerInvariant(),
                    delivery = new
                    {
                        attempt = invocation.Delivery.Attempt
                    }
                }
            });
        }
    }

    private void SweepExpiredLeasesUnsafe(DateTime now, List<InvocationSweepTransition>? transitions = null)
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
                MarkTerminalUnsafe(invocation, InvocationSweepOutcome.Expired, now, transitions);
                continue;
            }

            if (invocation.Kind == InvocationKind.Request &&
                invocation.Options.WaitTimeoutMs.HasValue &&
                now > invocation.CreatedAtUtc.AddMilliseconds(invocation.Options.WaitTimeoutMs.Value))
            {
                MarkTerminalUnsafe(invocation, InvocationSweepOutcome.Timeout, now, transitions);
                continue;
            }

            invocation.LeaseHolderInstanceId = null;
            invocation.LeaseExpireAtUtc = null;
            invocation.Delivery.LeaseToken = string.Empty;
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

            transitions?.Add(new InvocationSweepTransition
            {
                InvocationId = invocation.InvocationId,
                Kind = invocation.Kind,
                Outcome = InvocationSweepOutcome.Requeued,
                ElapsedMs = GetElapsedMs(invocation, now)
            });
        }
    }

    private void AdvanceTimeoutAndExpirationUnsafe(DateTime now, List<InvocationSweepTransition> transitions)
    {
        var timeoutOrExpiredCandidates = _all.Values
            .Where(i => i.State is InvocationState.Queued or InvocationState.Pending or InvocationState.Delivered)
            .ToList();

        foreach (var invocation in timeoutOrExpiredCandidates)
        {
            var ttlElapsed = now > invocation.CreatedAtUtc.AddMilliseconds(invocation.Options.TtlMs);
            if (ttlElapsed)
            {
                MarkTerminalUnsafe(invocation, InvocationSweepOutcome.Expired, now, transitions);
                continue;
            }

            if (invocation.Kind == InvocationKind.Request &&
                invocation.Options.WaitTimeoutMs.HasValue &&
                now > invocation.CreatedAtUtc.AddMilliseconds(invocation.Options.WaitTimeoutMs.Value))
            {
                MarkTerminalUnsafe(invocation, InvocationSweepOutcome.Timeout, now, transitions);
            }
        }
    }

    private static void MarkTerminalUnsafe(
        InvocationModel invocation,
        InvocationSweepOutcome outcome,
        DateTime now,
        List<InvocationSweepTransition>? transitions = null)
    {
        invocation.State = outcome == InvocationSweepOutcome.Expired
            ? InvocationState.Expired
            : InvocationState.Timeout;
        invocation.CompletedAtUtc = now;
        invocation.LeaseHolderInstanceId = null;
        invocation.LeaseExpireAtUtc = null;
        invocation.Delivery.LeaseToken = string.Empty;

        transitions?.Add(new InvocationSweepTransition
        {
            InvocationId = invocation.InvocationId,
            Kind = invocation.Kind,
            Outcome = outcome,
            ElapsedMs = GetElapsedMs(invocation, now)
        });
    }

    private static void CompleteRequestWaiters(InvocationRequestWaiter? requestWaiter, IReadOnlyList<InvocationSweepTransition> transitions)
    {
        if (requestWaiter is null || transitions.Count == 0)
        {
            return;
        }

        foreach (var transition in transitions)
        {
            if (transition.Kind != InvocationKind.Request)
            {
                continue;
            }

            switch (transition.Outcome)
            {
                case InvocationSweepOutcome.Timeout:
                    requestWaiter.CompleteTimeout(transition.InvocationId, transition.ElapsedMs);
                    break;
                case InvocationSweepOutcome.Expired:
                    requestWaiter.CompleteExpired(transition.InvocationId, transition.ElapsedMs);
                    break;
                case InvocationSweepOutcome.Requeued:
                    break;
            }
        }
    }

    private static int GetElapsedMs(InvocationModel invocation, DateTime now)
    {
        return (int)Math.Max(0, (now - invocation.CreatedAtUtc).TotalMilliseconds);
    }

    private void CleanupTerminalInvocations(DateTime now)
    {
        var expireBefore = now - TerminalInvocationRetention;
        var toRemove = _all.Values
            .Where(i => i.State is InvocationState.Completed or InvocationState.Failed or InvocationState.Timeout or InvocationState.Expired)
            .Where(i => (i.CompletedAtUtc ?? i.CreatedAtUtc) <= expireBefore)
            .Select(i => i.InvocationId)
            .ToList();

        foreach (var invocationId in toRemove)
        {
            _all.TryRemove(invocationId, out _);
        }

        if (toRemove.Count > 0)
        {
            _logger.LogDebug("已清理终态 Invocation，Count: {Count}, RetentionMinutes: {RetentionMinutes}", toRemove.Count, TerminalInvocationRetention.TotalMinutes);
        }
    }

    private InvocationModel CreateInvocationCore(InvocationModel invocation, bool hasOnlineCandidates)
    {
        invocation.State = hasOnlineCandidates ? InvocationState.Queued : InvocationState.Pending;
        invocation.Delivery.LeaseToken = string.Empty;
        _all[invocation.InvocationId] = invocation;
        return invocation;
    }

    private static string GenerateLeaseToken()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private int CountActiveInvocationsUnsafe()
    {
        return _all.Values.Count(static invocation => invocation.State is InvocationState.Queued or InvocationState.Pending or InvocationState.Delivered);
    }
}

/// <summary>
/// 调用扫描迁移结果。
/// </summary>
public class InvocationSweepTransition
{
    /// <summary>
    /// 调用 ID。
    /// </summary>
    public required string InvocationId { get; set; }

    /// <summary>
    /// 调用类型。
    /// </summary>
    public InvocationKind Kind { get; set; }

    /// <summary>
    /// 扫描推进结果。
    /// </summary>
    public InvocationSweepOutcome Outcome { get; set; }

    /// <summary>
    /// 已耗时毫秒。
    /// </summary>
    public int ElapsedMs { get; set; }
}

/// <summary>
/// 调用扫描推进结果类型。
/// </summary>
public enum InvocationSweepOutcome
{
    /// <summary>
    /// 已过期。
    /// </summary>
    Expired,

    /// <summary>
    /// 请求等待超时。
    /// </summary>
    Timeout,

    /// <summary>
    /// 租约到期后回队。
    /// </summary>
    Requeued
}
