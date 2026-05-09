namespace DevHub.Tests;

using DevHub.Core.Models;
using DevHub.Core.Services;
using DevHub.Core.Services.Abstractions;
using DevHub.Core.Services.Invocation;
using Microsoft.Extensions.Logging;
using Moq;

/// <summary>
/// InvocationTimeoutWorker 扫描与 waiter 通知测试。
/// </summary>
[Trait("Category", "Impl")]
public class InvocationTimeoutWorkerTests
{
    private static readonly DateTime BaseUtc = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private readonly Mock<ILogger<AppRegistry>> _registryLogger = new();
    private readonly Mock<ILogger<InvocationRoutingService>> _routingLogger = new();
    private readonly Mock<ILogger<InvocationStore>> _storeLogger = new();
    private readonly Mock<ILogger<InvocationRequestWaiter>> _waiterLogger = new();
    private readonly Mock<ILogger<InvocationTimeoutWorker>> _workerLogger = new();

    [Fact]
    public async Task Impl_SweepOnce_ShouldNotifyWaiterTimeout_ForRequestTimeout()
    {
        var clock = new MutableClock(BaseUtc);
        var appRegistry = new AppRegistry(clock, _registryLogger.Object);
        var routingService = new InvocationRoutingService(appRegistry, _routingLogger.Object);
        var store = new InvocationStore(_storeLogger.Object, routingService, clock);
        var waiter = new InvocationRequestWaiter(_waiterLogger.Object);
        var worker = new InvocationTimeoutWorker(store, waiter, clock, _workerLogger.Object);

        var request = CreateRequest("worker-timeout.app", ttlMs: 5000, waitTimeoutMs: 1000);
        request.CreatedAtUtc = BaseUtc.AddMilliseconds(-1500);

        var created = store.CreateInvocation(request, hasOnlineCandidates: true);
        var waitTask = waiter.Register(created.InvocationId);

        worker.SweepOnce(BaseUtc);

        var completion = await waitTask;
        Assert.Equal(InvocationRequestCompletionKind.Timeout, completion.Kind);

        Assert.True(store.TryGet(created.InvocationId, out var current));
        Assert.Equal(InvocationState.Timeout, current!.State);
    }

    [Fact]
    public async Task Impl_SweepOnce_ShouldNotifyWaiterExpired_ForRequestTtlElapsed()
    {
        var clock = new MutableClock(BaseUtc);
        var appRegistry = new AppRegistry(clock, _registryLogger.Object);
        var routingService = new InvocationRoutingService(appRegistry, _routingLogger.Object);
        var store = new InvocationStore(_storeLogger.Object, routingService, clock);
        var waiter = new InvocationRequestWaiter(_waiterLogger.Object);
        var worker = new InvocationTimeoutWorker(store, waiter, clock, _workerLogger.Object);

        var request = CreateRequest("worker-expired.app", ttlMs: 1000, waitTimeoutMs: 5000);
        request.CreatedAtUtc = BaseUtc.AddMilliseconds(-1500);

        var created = store.CreateInvocation(request, hasOnlineCandidates: true);
        var waitTask = waiter.Register(created.InvocationId);

        worker.SweepOnce(BaseUtc);

        var completion = await waitTask;
        Assert.Equal(InvocationRequestCompletionKind.Expired, completion.Kind);

        Assert.True(store.TryGet(created.InvocationId, out var current));
        Assert.Equal(InvocationState.Expired, current!.State);
    }

    [Fact]
    public void Impl_SweepOnce_ShouldIgnoreNotifyTimeoutTransitions_ForWaiterCompletion()
    {
        var clock = new MutableClock(BaseUtc);
        var appRegistry = new AppRegistry(clock, _registryLogger.Object);
        var routingService = new InvocationRoutingService(appRegistry, _routingLogger.Object);
        var store = new InvocationStore(_storeLogger.Object, routingService, clock);
        var waiter = new InvocationRequestWaiter(_waiterLogger.Object);
        var worker = new InvocationTimeoutWorker(store, waiter, clock, _workerLogger.Object);

        var notify = CreateNotify("worker-notify.app", ttlMs: 1000);
        notify.CreatedAtUtc = BaseUtc.AddMilliseconds(-1500);

        var created = store.CreateInvocation(notify, hasOnlineCandidates: true);

        worker.SweepOnce(BaseUtc);

        Assert.False(waiter.CompleteSuccess(created.InvocationId, new { ok = true }));
        Assert.True(store.TryGet(created.InvocationId, out var current));
        Assert.Equal(InvocationState.Expired, current!.State);
    }

    private static Invocation CreateRequest(string appId, int ttlMs, int waitTimeoutMs)
    {
        return new Invocation
        {
            InvocationId = $"invk-{Guid.NewGuid():N}",
            AppId = appId,
            Target = new InvocationTarget { Scope = ScopeContract.Global, InstanceId = null },
            Method = "demo.request",
            Args = new Dictionary<string, object?>(),
            Kind = InvocationKind.Request,
            CreatedAtUtc = BaseUtc,
            Options = new InvocationOptions
            {
                TtlMs = ttlMs,
                WaitTimeoutMs = waitTimeoutMs,
                QueueIfOffline = true,
                AutoLaunch = false
            },
            Delivery = new InvocationDelivery { LeaseSeconds = 30, Attempt = 1 },
            Caller = new InvocationCaller
            {
                ClientId = "worker-test-client",
                ClientSessionId = Guid.NewGuid().ToString("D")
            },
            State = InvocationState.Created
        };
    }

    private static Invocation CreateNotify(string appId, int ttlMs)
    {
        return new Invocation
        {
            InvocationId = $"invk-{Guid.NewGuid():N}",
            AppId = appId,
            Target = new InvocationTarget { Scope = ScopeContract.Global, InstanceId = null },
            Method = "demo.notify",
            Args = new Dictionary<string, object?>(),
            Kind = InvocationKind.Notify,
            CreatedAtUtc = BaseUtc,
            Options = new InvocationOptions
            {
                TtlMs = ttlMs,
                QueueIfOffline = true,
                AutoLaunch = false
            },
            Delivery = new InvocationDelivery { LeaseSeconds = 30, Attempt = 1 },
            Caller = new InvocationCaller
            {
                ClientId = "worker-test-client",
                ClientSessionId = Guid.NewGuid().ToString("D")
            },
            State = InvocationState.Created
        };
    }

    private sealed class MutableClock : IClock
    {
        public MutableClock(DateTime utcNow)
        {
            UtcNow = utcNow;
        }

        public DateTime UtcNow { get; set; }
    }
}



