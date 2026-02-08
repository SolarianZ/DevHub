namespace DevHub.Tests;

using DevHub.Core.Services.Events;
using Microsoft.Extensions.Logging;
using Moq;

/// <summary>
/// HubEventBus 行为测试。
/// </summary>
public class HubEventBusTests
{
    private readonly Mock<ILogger<HubEventBus>> _logger = new();

    [Fact]
    public void GetSupportedEventTypes_ShouldMatchSpecDefinitions()
    {
        var eventTypes = HubEventBus.GetSupportedEventTypes();

        Assert.Equal(6, eventTypes.Count);
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
    public void TrySubscribe_ShouldRequireAuthenticatedConnection()
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
    public void Publish_ShouldDeliverOnlyMatchingSubscriptions()
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
    public void Unsubscribe_ShouldBeIdempotentAndStopFutureDelivery()
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
}
