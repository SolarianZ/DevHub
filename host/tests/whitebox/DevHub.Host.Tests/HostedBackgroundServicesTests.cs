namespace DevHub.Host.Tests;

using DevHub.Core.Models;
using DevHub.Core.Services;
using DevHub.Core.Services.Abstractions;
using DevHub.Core.Services.Invocation;
using DevHub.Host.BackgroundServices;
using Microsoft.Extensions.Logging;
using Moq;

/// <summary>
/// Host 后台服务行为测试。
/// </summary>
[Trait("Category", "Impl")]
public sealed class HostedBackgroundServicesTests
{
    [Fact]
    public void Impl_AppRegistryCleanupBackgroundService_ExecuteOneIteration_ShouldCleanupExpiredInstances()
    {
        var clock = new MutableClock(DateTime.UtcNow);
        using var appRegistry = new AppRegistry(clock, Mock.Of<ILogger<AppRegistry>>());
        var service = new AppRegistryCleanupBackgroundService(appRegistry, Mock.Of<ILogger<AppRegistryCleanupBackgroundService>>());

        var instance = appRegistry.RegisterInstance(new AppInstance
        {
            InstanceId = "inst-cleanup",
            AppId = "cleanup.app",
            Scope = null,
            Pid = 1234,
            Invoke = new InvokeCapability
            {
                Poll = true,
                Respond = true
            }
        });

        Assert.NotNull(appRegistry.GetInstance(instance.InstanceId));

        clock.UtcNow = clock.UtcNow.AddHours(2);
        service.ExecuteOneIteration();

        Assert.Null(appRegistry.GetInstance(instance.InstanceId));
    }

    [Fact]
    public async Task Impl_InvocationTimeoutBackgroundService_ExecuteOneIteration_ShouldDriveTimeoutTransitions()
    {
        var clock = new MutableClock(DateTime.UtcNow);
        using var appRegistry = new AppRegistry(clock, Mock.Of<ILogger<AppRegistry>>());
        var routingService = new InvocationRoutingService(appRegistry, Mock.Of<ILogger<InvocationRoutingService>>());
        var store = new InvocationStore(Mock.Of<ILogger<InvocationStore>>(), routingService, clock);
        var waiter = new InvocationRequestWaiter(Mock.Of<ILogger<InvocationRequestWaiter>>());
        using var worker = new InvocationTimeoutWorker(store, waiter, clock, Mock.Of<ILogger<InvocationTimeoutWorker>>());
        var service = new InvocationTimeoutBackgroundService(worker, clock, Mock.Of<ILogger<InvocationTimeoutBackgroundService>>());

        var request = new Invocation
        {
            InvocationId = $"invk-{Guid.NewGuid():N}",
            AppId = "background.timeout.app",
            Target = new InvocationTarget { Scope = null, InstanceId = null },
            Method = "demo.request",
            Args = new Dictionary<string, object?>(),
            Kind = InvocationKind.Request,
            CreatedAtUtc = clock.UtcNow.AddMilliseconds(-1500),
            Options = new InvocationOptions
            {
                TtlMs = 5000,
                WaitTimeoutMs = 1000,
                QueueIfOffline = true,
                AutoLaunch = false
            },
            Delivery = new InvocationDelivery { LeaseSeconds = 30, Attempt = 1 },
            Caller = new InvocationCaller
            {
                ClientId = "background-service-test",
                ClientSessionId = Guid.NewGuid().ToString("D")
            },
            State = InvocationState.Created
        };

        var created = store.CreateInvocation(request, hasOnlineCandidates: true);
        var waitTask = waiter.Register(created.InvocationId);

        service.ExecuteOneIteration();

        var completion = await waitTask;
        Assert.Equal(InvocationRequestCompletionKind.Timeout, completion.Kind);

        Assert.True(store.TryGet(created.InvocationId, out var current));
        Assert.Equal(InvocationState.Timeout, current!.State);
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
