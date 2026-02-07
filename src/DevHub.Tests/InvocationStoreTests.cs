namespace DevHub.Tests;

using DevHub.Core.Models;
using DevHub.Core.Services;
using DevHub.Core.Services.Invocation;
using Microsoft.Extensions.Logging;
using Moq;

/// <summary>
/// InvocationStore 生命周期测试。
/// </summary>
public class InvocationStoreTests
{
    private readonly Mock<ILogger<AppRegistry>> _registryLogger = new();
    private readonly Mock<ILogger<InvocationRoutingService>> _routingLogger = new();
    private readonly Mock<ILogger<InvocationStore>> _storeLogger = new();

    [Fact]
    public async Task NotifyLifecycle_ShouldTransitionFromQueuedToDeliveredToCompleted()
    {
        var appRegistry = new AppRegistry(_registryLogger.Object);
        var instance = appRegistry.RegisterInstance(new AppInstance
        {
            InstanceId = "inst-lifecycle",
            AppId = "lifecycle.app",
            Scope = null,
            Pid = 4001,
            Invoke = new InvokeCapability { Poll = true, Respond = true }
        });

        var routingService = new InvocationRoutingService(appRegistry, _routingLogger.Object);
        var store = new InvocationStore(_storeLogger.Object, routingService);

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
    public async Task PendingNotify_ShouldBeDeliveredAfterInstanceComesOnline()
    {
        var appRegistry = new AppRegistry(_registryLogger.Object);
        var routingService = new InvocationRoutingService(appRegistry, _routingLogger.Object);
        var store = new InvocationStore(_storeLogger.Object, routingService);

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
}
