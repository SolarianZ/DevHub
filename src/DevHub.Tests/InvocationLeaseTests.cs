namespace DevHub.Tests;

using DevHub.Core.Models;
using DevHub.Core.Services;
using DevHub.Core.Services.Invocation;
using Microsoft.Extensions.Logging;
using Moq;

/// <summary>
/// Invocation 租约冲突测试。
/// </summary>
public class InvocationLeaseTests
{
    private readonly Mock<ILogger<AppRegistry>> _registryLogger = new();
    private readonly Mock<ILogger<InvocationRoutingService>> _routingLogger = new();
    private readonly Mock<ILogger<InvocationStore>> _storeLogger = new();

    [Fact]
    public async Task Respond_ByNonLeaseHolder_ShouldReturnConflict()
    {
        var appRegistry = new AppRegistry(_registryLogger.Object);
        var instanceA = appRegistry.RegisterInstance(new AppInstance
        {
            InstanceId = "lease-a",
            AppId = "lease.app",
            Scope = null,
            Pid = 5001,
            Invoke = new InvokeCapability { Poll = true, Respond = true }
        });
        var instanceB = appRegistry.RegisterInstance(new AppInstance
        {
            InstanceId = "lease-b",
            AppId = "lease.app",
            Scope = null,
            Pid = 5002,
            Invoke = new InvokeCapability { Poll = true, Respond = true }
        });

        var routingService = new InvocationRoutingService(appRegistry, _routingLogger.Object);
        var store = new InvocationStore(_storeLogger.Object, routingService);

        var created = store.CreateInvocation(CreateNotify("lease.app"), hasOnlineCandidates: true);
        var polled = await store.PollAsync(instanceA, maxCount: 10, waitMs: 0, CancellationToken.None);
        Assert.Single(polled);
        Assert.Equal(created.InvocationId, polled[0].InvocationId);

        var status = store.Respond(instanceB.InstanceId, created.InvocationId, value: new { ok = true }, error: null);
        Assert.Equal(InvocationRespondStatus.DeliveryConflict, status);
    }

    [Fact]
    public async Task Respond_DuplicateSubmission_ShouldReturnConflict()
    {
        var appRegistry = new AppRegistry(_registryLogger.Object);
        var instance = appRegistry.RegisterInstance(new AppInstance
        {
            InstanceId = "lease-owner",
            AppId = "lease.app",
            Scope = null,
            Pid = 5003,
            Invoke = new InvokeCapability { Poll = true, Respond = true }
        });

        var routingService = new InvocationRoutingService(appRegistry, _routingLogger.Object);
        var store = new InvocationStore(_storeLogger.Object, routingService);

        var created = store.CreateInvocation(CreateNotify("lease.app"), hasOnlineCandidates: true);
        var polled = await store.PollAsync(instance, maxCount: 10, waitMs: 0, CancellationToken.None);
        Assert.Single(polled);
        Assert.Equal(created.InvocationId, polled[0].InvocationId);

        var first = store.Respond(instance.InstanceId, created.InvocationId, value: new { ok = true }, error: null);
        var second = store.Respond(instance.InstanceId, created.InvocationId, value: new { ok = true }, error: null);

        Assert.Equal(InvocationRespondStatus.Success, first);
        Assert.Equal(InvocationRespondStatus.DeliveryConflict, second);
    }

    [Fact]
    public async Task LeaseExpired_ShouldBeRequeuedAndAttemptIncremented()
    {
        var appRegistry = new AppRegistry(_registryLogger.Object);
        var instanceA = appRegistry.RegisterInstance(new AppInstance
        {
            InstanceId = "lease-requeue-a",
            AppId = "lease.app",
            Scope = null,
            Pid = 5004,
            Invoke = new InvokeCapability { Poll = true, Respond = true }
        });
        var instanceB = appRegistry.RegisterInstance(new AppInstance
        {
            InstanceId = "lease-requeue-b",
            AppId = "lease.app",
            Scope = null,
            Pid = 5005,
            Invoke = new InvokeCapability { Poll = true, Respond = true }
        });

        var routingService = new InvocationRoutingService(appRegistry, _routingLogger.Object);
        var store = new InvocationStore(_storeLogger.Object, routingService);

        var created = store.CreateInvocation(CreateNotify("lease.app", leaseSeconds: 1), hasOnlineCandidates: true);
        var firstPoll = await store.PollAsync(instanceA, maxCount: 1, waitMs: 0, CancellationToken.None);

        Assert.Single(firstPoll);
        Assert.Equal(1, firstPoll[0].Delivery.Attempt);
        Assert.Equal(instanceA.InstanceId, firstPoll[0].LeaseHolderInstanceId);

        await Task.Delay(TimeSpan.FromMilliseconds(1200));

        var secondPoll = await store.PollAsync(instanceB, maxCount: 1, waitMs: 200, CancellationToken.None);
        Assert.Single(secondPoll);

        var redelivered = secondPoll.Single();
        Assert.Equal(created.InvocationId, redelivered.InvocationId);
        Assert.Equal(2, redelivered.Delivery.Attempt);
        Assert.Equal(InvocationState.Delivered, redelivered.State);
        Assert.Equal(instanceB.InstanceId, redelivered.LeaseHolderInstanceId);
    }

    [Fact]
    public async Task Respond_AfterLeaseExpired_ShouldReturnDeliveryConflict()
    {
        var appRegistry = new AppRegistry(_registryLogger.Object);
        var instance = appRegistry.RegisterInstance(new AppInstance
        {
            InstanceId = "lease-expired-owner",
            AppId = "lease.app",
            Scope = null,
            Pid = 5006,
            Invoke = new InvokeCapability { Poll = true, Respond = true }
        });

        var routingService = new InvocationRoutingService(appRegistry, _routingLogger.Object);
        var store = new InvocationStore(_storeLogger.Object, routingService);

        var created = store.CreateInvocation(CreateNotify("lease.app", leaseSeconds: 1), hasOnlineCandidates: true);
        var firstPoll = await store.PollAsync(instance, maxCount: 1, waitMs: 0, CancellationToken.None);
        Assert.Single(firstPoll);

        await Task.Delay(TimeSpan.FromMilliseconds(1200));

        var status = store.Respond(instance.InstanceId, created.InvocationId, value: new { ok = true }, error: null);
        Assert.Equal(InvocationRespondStatus.DeliveryConflict, status);

        Assert.True(store.TryGet(created.InvocationId, out var current));
        Assert.Equal(InvocationState.Queued, current!.State);
        Assert.Equal(2, current.Delivery.Attempt);
    }

    private static Invocation CreateNotify(string appId, int leaseSeconds = 30)
    {
        return new Invocation
        {
            InvocationId = $"invk-{Guid.NewGuid():N}",
            AppId = appId,
            Target = new InvocationTarget
            {
                Scope = null,
                InstanceId = null
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
                LeaseSeconds = leaseSeconds,
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
}
