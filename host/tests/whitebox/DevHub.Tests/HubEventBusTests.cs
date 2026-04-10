namespace DevHub.Tests;

using DevHub.Core.Services.Events;
using Microsoft.Extensions.Logging;
using Moq;

/// <summary>
/// HubEventBus 行为测试。
/// </summary>
[Trait("Category", "Impl")]
public class HubEventBusTests
{
    private readonly Mock<ILogger<HubEventBus>> _logger = new();

    [Fact]
    public void Impl_GetSupportedEventTypes_ShouldMatchSpecDefinitions()
    {
        var eventTypes = HubEventBus.GetSupportedEventTypes();

        Assert.Equal(8, eventTypes.Count);
        Assert.Contains("app.definition.upserted", eventTypes);
        Assert.Contains("app.definition.deleted", eventTypes);
        Assert.Contains("app.instance.registered", eventTypes);
        Assert.Contains("app.instance.unregistered", eventTypes);
        Assert.Contains("invocation.queued", eventTypes);
        Assert.Contains("invocation.delivered", eventTypes);
        Assert.Contains("invocation.completed", eventTypes);
        Assert.Contains("invocation.failed", eventTypes);

        Assert.True(HubEventBus.IsSupportedEventType("invocation.completed"));
        Assert.False(HubEventBus.IsSupportedEventType("invocation.timeout"));
    }

    [Fact]
    public void Impl_TrySubscribe_ShouldRequireAuthenticatedConnection()
    {
        var bus = new HubEventBus(_logger.Object);
        bus.RegisterConnection("conn-auth");

        var subscribeBeforeAuth = bus.TrySubscribe("conn-auth", null, out _);
        Assert.False(subscribeBeforeAuth);

        var marked = bus.TryMarkAuthenticated("conn-auth", "test-client", Guid.NewGuid().ToString("D"));
        Assert.True(marked);

        var subscribeAfterAuth = bus.TrySubscribe("conn-auth", null, out var subscriptionId);
        Assert.True(subscribeAfterAuth);
        Assert.False(string.IsNullOrWhiteSpace(subscriptionId));
    }

    [Fact]
    public void Impl_Publish_ShouldDeliverOnlyMatchingSubscriptions()
    {
        var bus = new HubEventBus(_logger.Object);

        bus.RegisterConnection("conn-all");
        bus.RegisterConnection("conn-filtered");

        Assert.True(bus.TryMarkAuthenticated("conn-all", "client-all", Guid.NewGuid().ToString("D")));
        Assert.True(bus.TryMarkAuthenticated("conn-filtered", "client-filtered", Guid.NewGuid().ToString("D")));

        Assert.True(bus.TrySubscribe("conn-all", null, out var subAll));
        Assert.True(bus.TrySubscribe("conn-filtered", ["invocation.completed"], out var subFiltered));

        var queuedAt = DateTime.UtcNow;
        bus.Publish(new HubEventMessage
        {
            Type = "invocation.queued",
            TimeUtc = queuedAt,
            Payload = new { invocationId = "invk-queued" }
        });

        var completedAt = DateTime.UtcNow;
        bus.Publish(new HubEventMessage
        {
            Type = "invocation.completed",
            TimeUtc = completedAt,
            Payload = new { invocationId = "invk-completed" }
        });

        var allDeliveries = bus.DrainDeliveries("conn-all", maxCount: 10);
        Assert.Equal(2, allDeliveries.Count);
        Assert.Contains(allDeliveries, d => d.SubscriptionId == subAll && d.Type == "invocation.queued");
        Assert.Contains(allDeliveries, d => d.SubscriptionId == subAll && d.Type == "invocation.completed");

        var filteredDeliveries = bus.DrainDeliveries("conn-filtered", maxCount: 10);
        Assert.Single(filteredDeliveries);
        Assert.Equal(subFiltered, filteredDeliveries[0].SubscriptionId);
        Assert.Equal("invocation.completed", filteredDeliveries[0].Type);
    }

    [Fact]
    public void Impl_Unsubscribe_ShouldBeIdempotentAndStopFutureDelivery()
    {
        var bus = new HubEventBus(_logger.Object);
        bus.RegisterConnection("conn-unsub");
        Assert.True(bus.TryMarkAuthenticated("conn-unsub", "client", Guid.NewGuid().ToString("D")));
        Assert.True(bus.TrySubscribe("conn-unsub", null, out var subscriptionId));

        bus.Publish(new HubEventMessage
        {
            Type = "app.instance.registered",
            TimeUtc = DateTime.UtcNow,
            Payload = new { appId = "demo.app" }
        });

        Assert.NotEmpty(bus.DrainDeliveries("conn-unsub", maxCount: 10));

        bus.Unsubscribe("conn-unsub", subscriptionId);
        bus.Unsubscribe("conn-unsub", subscriptionId);

        bus.Publish(new HubEventMessage
        {
            Type = "app.instance.unregistered",
            TimeUtc = DateTime.UtcNow,
            Payload = new { appId = "demo.app" }
        });

        var deliveriesAfterUnsubscribe = bus.DrainDeliveries("conn-unsub", maxCount: 10);
        Assert.Empty(deliveriesAfterUnsubscribe);
    }

    [Fact]
    public void Impl_RemoveConnection_ShouldClearSubscriptionsAndPendingDeliveries()
    {
        var bus = new HubEventBus(_logger.Object);
        bus.RegisterConnection("conn-remove");
        Assert.True(bus.TryMarkAuthenticated("conn-remove", "client", Guid.NewGuid().ToString("D")));
        Assert.True(bus.TrySubscribe("conn-remove", null, out _));

        bus.Publish(new HubEventMessage
        {
            Type = "app.instance.registered",
            TimeUtc = DateTime.UtcNow,
            Payload = new { appId = "demo.app", instanceId = "inst-1" }
        });

        Assert.NotEmpty(bus.DrainDeliveries("conn-remove", maxCount: 10));

        Assert.True(bus.TrySubscribe("conn-remove", null, out _));
        bus.Publish(new HubEventMessage
        {
            Type = "invocation.queued",
            TimeUtc = DateTime.UtcNow,
            Payload = new { invocationId = "invk-1" }
        });

        bus.RemoveConnection("conn-remove");

        var deliveriesAfterRemove = bus.DrainDeliveries("conn-remove", maxCount: 10);
        Assert.Empty(deliveriesAfterRemove);

        bus.Publish(new HubEventMessage
        {
            Type = "invocation.completed",
            TimeUtc = DateTime.UtcNow,
            Payload = new { invocationId = "invk-2" }
        });

        var deliveriesAfterNewPublish = bus.DrainDeliveries("conn-remove", maxCount: 10);
        Assert.Empty(deliveriesAfterNewPublish);
    }

    [Fact]
    public void Impl_Publish_ShouldDropWhenConnectionQueueExceedsLimit()
    {
        var bus = new HubEventBus(_logger.Object);
        bus.RegisterConnection("conn-bounded");
        Assert.True(bus.TryMarkAuthenticated("conn-bounded", "client", Guid.NewGuid().ToString("D")));
        Assert.True(bus.TrySubscribe("conn-bounded", null, out _));

        for (var i = 0; i < HubEventBus.MaxPendingDeliveriesPerConnection + 128; i++)
        {
            bus.Publish(new HubEventMessage
            {
                Type = "invocation.queued",
                TimeUtc = DateTime.UtcNow,
                Payload = new { invocationId = $"invk-{i}" }
            });
        }

        var deliveries = bus.DrainDeliveries("conn-bounded", maxCount: HubEventBus.MaxPendingDeliveriesPerConnection + 256);
        Assert.Equal(HubEventBus.MaxPendingDeliveriesPerConnection, deliveries.Count);
    }

    [Fact]
    public async Task Impl_WaitForDeliveryAsync_WhenMultipleWaitersExist_ShouldWakeCurrentConnectionWithoutLosingSignal()
    {
        var bus = new HubEventBus(_logger.Object);
        bus.RegisterConnection("conn-wait");
        Assert.True(bus.TryMarkAuthenticated("conn-wait", "client", Guid.NewGuid().ToString("D")));
        Assert.True(bus.TrySubscribe("conn-wait", ["app.instance.registered"], out _));

        var staleWaiter = bus.WaitForDeliveryAsync("conn-wait", CancellationToken.None).AsTask();
        var currentWaiter = bus.WaitForDeliveryAsync("conn-wait", CancellationToken.None).AsTask();

        bus.Publish(new HubEventMessage
        {
            Type = "app.instance.registered",
            TimeUtc = DateTime.UtcNow,
            Payload = new { appId = "demo.app", instanceId = "inst-1" }
        });

        await Task.WhenAll(staleWaiter, currentWaiter);
        var deliveries = bus.DrainDeliveries("conn-wait", maxCount: 10);
        Assert.Single(deliveries);
        Assert.Equal("app.instance.registered", deliveries[0].Type);
    }
}

