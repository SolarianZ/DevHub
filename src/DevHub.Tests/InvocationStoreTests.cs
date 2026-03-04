namespace DevHub.Tests;

using DevHub.Core.Models;
using DevHub.Core.Services;
using DevHub.Core.Services.Invocation;
using Microsoft.Extensions.Logging;
using Moq;

/// <summary>
/// InvocationStore 生命周期测试。
/// </summary>
[Trait("Category", "Impl")]
public class InvocationStoreTests
{
    private readonly Mock<ILogger<AppRegistry>> _registryLogger = new();
    private readonly Mock<ILogger<InvocationRoutingService>> _routingLogger = new();
    private readonly Mock<ILogger<InvocationStore>> _storeLogger = new();

    [Fact]
    public async Task Impl_NotifyLifecycle_ShouldTransitionFromQueuedToDeliveredToCompleted()
    {
        var appRegistry = new AppRegistry(new SystemClock(), _registryLogger.Object);
        var instance = appRegistry.RegisterInstance(new AppInstance
        {
            InstanceId = "inst-lifecycle",
            AppId = "lifecycle.app",
            Scope = null,
            Pid = 4001,
            Invoke = new InvokeCapability { Poll = true, Respond = true }
        });

        var routingService = new InvocationRoutingService(appRegistry, _routingLogger.Object);
        var store = new InvocationStore(_storeLogger.Object, routingService, new SystemClock());

        var created = store.CreateInvocation(CreateNotify("lifecycle.app", targetScope: null, targetInstanceId: null), hasOnlineCandidates: true);
        Assert.Equal(InvocationState.Queued, created.State);

        var polled = await store.PollAsync(instance, maxCount: 10, waitMs: 0, CancellationToken.None);
        Assert.Single(polled);
        Assert.Equal(created.InvocationId, polled[0].InvocationId);
        Assert.Equal(InvocationState.Delivered, polled[0].State);
        Assert.Equal(30, polled[0].Delivery.LeaseSeconds);
        Assert.Equal(1, polled[0].Delivery.Attempt);

        var respondStatus = store.Respond(instance.InstanceId, created.InvocationId, value: new { ok = true }, error: null);
        Assert.Equal(InvocationRespondStatus.Success, respondStatus);

        Assert.True(store.TryGet(created.InvocationId, out var current));
        Assert.Equal(InvocationState.Completed, current!.State);
    }

    [Fact]
    public async Task Impl_PendingNotify_ShouldBeDeliveredAfterInstanceComesOnline()
    {
        var appRegistry = new AppRegistry(new SystemClock(), _registryLogger.Object);
        var routingService = new InvocationRoutingService(appRegistry, _routingLogger.Object);
        var store = new InvocationStore(_storeLogger.Object, routingService, new SystemClock());

        var pending = store.CreateInvocation(CreateNotify("pending.app", targetScope: null, targetInstanceId: null), hasOnlineCandidates: false);
        Assert.Equal(InvocationState.Pending, pending.State);

        var instance = appRegistry.RegisterInstance(new AppInstance
        {
            InstanceId = "inst-pending",
            AppId = "pending.app",
            Scope = null,
            Pid = 4002,
            Invoke = new InvokeCapability { Poll = true, Respond = true }
        });

        var polled = await store.PollAsync(instance, maxCount: 10, waitMs: 0, CancellationToken.None);
        Assert.Single(polled);
        Assert.Equal(pending.InvocationId, polled[0].InvocationId);
        Assert.Equal(InvocationState.Delivered, polled[0].State);
    }

    [Fact]
    public async Task Impl_DeliveredRespondWithError_ShouldTransitionToFailed()
    {
        var appRegistry = new AppRegistry(new SystemClock(), _registryLogger.Object);
        var instance = appRegistry.RegisterInstance(new AppInstance
        {
            InstanceId = "inst-failed",
            AppId = "failed.app",
            Scope = null,
            Pid = 4003,
            Invoke = new InvokeCapability { Poll = true, Respond = true }
        });

        var routingService = new InvocationRoutingService(appRegistry, _routingLogger.Object);
        var store = new InvocationStore(_storeLogger.Object, routingService, new SystemClock());

        var created = store.CreateInvocation(CreateNotify("failed.app", targetScope: null, targetInstanceId: null), hasOnlineCandidates: true);
        var polled = await store.PollAsync(instance, maxCount: 1, waitMs: 0, CancellationToken.None);
        Assert.Single(polled);

        var status = store.Respond(instance.InstanceId, created.InvocationId, value: null, error: new { code = 1001, message = "app_error" });
        Assert.Equal(InvocationRespondStatus.Success, status);

        Assert.True(store.TryGet(created.InvocationId, out var current));
        Assert.Equal(InvocationState.Failed, current!.State);
        Assert.NotNull(current.ResponseError);
    }

    [Fact]
    public async Task Impl_DeliveredMarkedTimeout_ShouldRejectLateRespondAsExpired()
    {
        var appRegistry = new AppRegistry(new SystemClock(), _registryLogger.Object);
        var instance = appRegistry.RegisterInstance(new AppInstance
        {
            InstanceId = "inst-timeout",
            AppId = "timeout.app",
            Scope = null,
            Pid = 4004,
            Invoke = new InvokeCapability { Poll = true, Respond = true }
        });

        var routingService = new InvocationRoutingService(appRegistry, _routingLogger.Object);
        var store = new InvocationStore(_storeLogger.Object, routingService, new SystemClock());

        var created = store.CreateInvocation(CreateNotify("timeout.app", targetScope: null, targetInstanceId: null), hasOnlineCandidates: true);
        var polled = await store.PollAsync(instance, maxCount: 1, waitMs: 0, CancellationToken.None);
        Assert.Single(polled);

        var marked = store.MarkTimeout(created.InvocationId, DateTime.UtcNow);
        Assert.True(marked);

        var status = store.Respond(instance.InstanceId, created.InvocationId, value: new { ok = true }, error: null);
        Assert.Equal(InvocationRespondStatus.Expired, status);
    }

    [Fact]
    public async Task Impl_DeliveredMarkedExpired_ShouldRejectLateRespondAsExpired()
    {
        var appRegistry = new AppRegistry(new SystemClock(), _registryLogger.Object);
        var instance = appRegistry.RegisterInstance(new AppInstance
        {
            InstanceId = "inst-expired",
            AppId = "expired.app",
            Scope = null,
            Pid = 4005,
            Invoke = new InvokeCapability { Poll = true, Respond = true }
        });

        var routingService = new InvocationRoutingService(appRegistry, _routingLogger.Object);
        var store = new InvocationStore(_storeLogger.Object, routingService, new SystemClock());

        var created = store.CreateInvocation(CreateNotify("expired.app", targetScope: null, targetInstanceId: null), hasOnlineCandidates: true);
        var polled = await store.PollAsync(instance, maxCount: 1, waitMs: 0, CancellationToken.None);
        Assert.Single(polled);

        var marked = store.MarkExpired(created.InvocationId, DateTime.UtcNow);
        Assert.True(marked);

        var status = store.Respond(instance.InstanceId, created.InvocationId, value: new { ok = true }, error: null);
        Assert.Equal(InvocationRespondStatus.Expired, status);
    }

    [Fact]
    public void Impl_Sweep_ShouldMarkNotifyAsExpired_WhenTtlElapsed()
    {
        var appRegistry = new AppRegistry(new SystemClock(), _registryLogger.Object);
        var routingService = new InvocationRoutingService(appRegistry, _routingLogger.Object);
        var store = new InvocationStore(_storeLogger.Object, routingService, new SystemClock());

        var notify = CreateNotify("sweep-expired.app", targetScope: null, targetInstanceId: null);
        notify.CreatedAtUtc = DateTime.UtcNow.AddMilliseconds(-1500);
        notify.Options.TtlMs = 1000;

        var created = store.CreateInvocation(notify, hasOnlineCandidates: true);
        Assert.Equal(InvocationState.Queued, created.State);

        var transitions = store.Sweep(DateTime.UtcNow);

        Assert.Contains(
            transitions,
            t => t.InvocationId == created.InvocationId && t.Outcome == InvocationSweepOutcome.Expired);

        Assert.True(store.TryGet(created.InvocationId, out var current));
        Assert.Equal(InvocationState.Expired, current!.State);
    }

    [Fact]
    public void Impl_Sweep_ShouldMarkRequestAsTimeout_WhenWaitTimeoutElapsed()
    {
        var appRegistry = new AppRegistry(new SystemClock(), _registryLogger.Object);
        var routingService = new InvocationRoutingService(appRegistry, _routingLogger.Object);
        var store = new InvocationStore(_storeLogger.Object, routingService, new SystemClock());

        var request = CreateRequest("sweep-timeout.app", targetScope: null, targetInstanceId: null, ttlMs: 5000, waitTimeoutMs: 1000);
        request.CreatedAtUtc = DateTime.UtcNow.AddMilliseconds(-1500);

        var created = store.CreateInvocation(request, hasOnlineCandidates: true);
        Assert.Equal(InvocationState.Queued, created.State);

        var transitions = store.Sweep(DateTime.UtcNow);

        Assert.Contains(
            transitions,
            t => t.InvocationId == created.InvocationId && t.Outcome == InvocationSweepOutcome.Timeout);

        Assert.True(store.TryGet(created.InvocationId, out var current));
        Assert.Equal(InvocationState.Timeout, current!.State);
    }

    [Fact]
    public async Task Impl_Sweep_ShouldRequeueDeliveredInvocation_WhenLeaseExpired()
    {
        var clock = new MutableClock(DateTime.UtcNow);
        var appRegistry = new AppRegistry(clock, _registryLogger.Object);
        var instance = appRegistry.RegisterInstance(new AppInstance
        {
            InstanceId = "inst-sweep-lease",
            AppId = "sweep-lease.app",
            Scope = null,
            Pid = 4010,
            Invoke = new InvokeCapability { Poll = true, Respond = true }
        });

        var routingService = new InvocationRoutingService(appRegistry, _routingLogger.Object);
        var store = new InvocationStore(_storeLogger.Object, routingService, clock);

        var created = store.CreateInvocation(CreateNotify("sweep-lease.app", targetScope: null, targetInstanceId: null), hasOnlineCandidates: true);

        var polled = await store.PollAsync(instance, maxCount: 1, waitMs: 0, CancellationToken.None);
        Assert.Single(polled);

        clock.Advance(TimeSpan.FromSeconds(31));
        _ = appRegistry.Heartbeat(instance.InstanceId, out _);
        _ = store.Sweep(clock.UtcNow);

        Assert.True(store.TryGet(created.InvocationId, out var current));
        Assert.Equal(InvocationState.Queued, current!.State);
        Assert.Null(current.LeaseHolderInstanceId);
        Assert.Null(current.LeaseExpireAtUtc);
        Assert.Equal(2, current.Delivery.Attempt);
    }

    [Fact]
    public async Task Impl_Sweep_ShouldCleanupTerminalInvocation_AfterRetentionWindow()
    {
        var start = DateTime.UtcNow;
        var clock = new MutableClock(start);

        var appRegistry = new AppRegistry(clock, _registryLogger.Object);
        var instance = appRegistry.RegisterInstance(new AppInstance
        {
            InstanceId = "inst-terminal-cleanup",
            AppId = "terminal-cleanup.app",
            Scope = null,
            Pid = 4011,
            Invoke = new InvokeCapability { Poll = true, Respond = true }
        });

        var routingService = new InvocationRoutingService(appRegistry, _routingLogger.Object);
        var store = new InvocationStore(_storeLogger.Object, routingService, clock);

        var created = store.CreateInvocation(CreateNotify("terminal-cleanup.app", targetScope: null, targetInstanceId: null), hasOnlineCandidates: true);
        var polled = await store.PollAsync(instance, maxCount: 1, waitMs: 0, CancellationToken.None);
        Assert.Single(polled);

        var respondStatus = store.Respond(instance.InstanceId, created.InvocationId, value: new { ok = true }, error: null);
        Assert.Equal(InvocationRespondStatus.Success, respondStatus);
        Assert.True(store.TryGet(created.InvocationId, out var terminalInvocation));
        Assert.Equal(InvocationState.Completed, terminalInvocation!.State);

        clock.Advance(TimeSpan.FromMinutes(11));
        _ = store.Sweep(clock.UtcNow);

        Assert.False(store.TryGet(created.InvocationId, out _));
    }


    private static Invocation CreateNotify(string appId, string? targetScope, string? targetInstanceId)
    {
        return new Invocation
        {
            InvocationId = $"invk-{Guid.NewGuid():N}",
            AppId = appId,
            Target = new InvocationTarget
            {
                Scope = targetScope,
                InstanceId = targetInstanceId
            },
            Method = "demo.notify",
            Args = new Dictionary<string, object?>(),
            Kind = InvocationKind.Notify,
            CreatedAtUtc = DateTime.UtcNow,
            Options = new InvocationOptions
            {
                TtlMs = 60000,
                QueueIfOffline = true,
                AutoLaunch = false
            },
            Delivery = new InvocationDelivery
            {
                LeaseSeconds = 30,
                Attempt = 1
            },
            Caller = new InvocationCaller
            {
                ClientId = "test-client",
                ClientSessionId = Guid.NewGuid().ToString("D")
            },
            State = InvocationState.Created
        };
    }

    private static Invocation CreateRequest(string appId, string? targetScope, string? targetInstanceId, int ttlMs, int waitTimeoutMs)
    {
        return new Invocation
        {
            InvocationId = $"invk-{Guid.NewGuid():N}",
            AppId = appId,
            Target = new InvocationTarget
            {
                Scope = targetScope,
                InstanceId = targetInstanceId
            },
            Method = "demo.request",
            Args = new Dictionary<string, object?>(),
            Kind = InvocationKind.Request,
            CreatedAtUtc = DateTime.UtcNow,
            Options = new InvocationOptions
            {
                TtlMs = ttlMs,
                WaitTimeoutMs = waitTimeoutMs,
                QueueIfOffline = true,
                AutoLaunch = false
            },
            Delivery = new InvocationDelivery
            {
                LeaseSeconds = 30,
                Attempt = 1
            },
            Caller = new InvocationCaller
            {
                ClientId = "test-client",
                ClientSessionId = Guid.NewGuid().ToString("D")
            },
            State = InvocationState.Created
        };
    }

    private sealed class MutableClock : DevHub.Core.Services.Abstractions.IClock
    {
        public MutableClock(DateTime now)
        {
            UtcNow = now;
        }

        public DateTime UtcNow { get; private set; }

        public void Advance(TimeSpan delta)
        {
            UtcNow = UtcNow.Add(delta);
        }
    }
}



