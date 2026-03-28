namespace DevHub.Host.Tests;

using System.Globalization;
using System.Text.Json;
using DevHub.Core.Services.Events;
using DevHub.Host.Transport;

/// <summary>
/// hub.event 通知构造器白盒测试。
/// </summary>
[Trait("Category", "Impl")]
public class HubEventNotificationFactoryTests
{
    [Fact]
    public void Impl_HubEventNotification_ShouldContainRequiredFields()
    {
        var delivery = new HubEventDelivery
        {
            ConnectionId = "conn-1",
            SubscriptionId = "sub-1",
            Type = "invocation.completed",
            TimeUtc = new DateTime(2026, 2, 8, 1, 2, 3, DateTimeKind.Utc),
            Payload = new
            {
                invocationId = "invk-1",
                appId = "app.demo"
            }
        };

        var notification = HubEventNotificationFactory.Create(delivery);
        var json = JsonSerializer.SerializeToElement(notification);

        Assert.Equal("2.0", json.GetProperty("jsonrpc").GetString());
        Assert.Equal("hub.event", json.GetProperty("method").GetString());

        var parameters = json.GetProperty("params");
        Assert.Equal("sub-1", parameters.GetProperty("subscriptionId").GetString());
        Assert.Equal("invocation.completed", parameters.GetProperty("type").GetString());

        var timeUtcText = parameters.GetProperty("timeUtc").GetString();
        Assert.NotNull(timeUtcText);
        Assert.True(DateTime.TryParse(timeUtcText, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsedTime));
        Assert.Equal(delivery.TimeUtc, parsedTime.ToUniversalTime());

        var payload = parameters.GetProperty("payload");
        Assert.Equal("invk-1", payload.GetProperty("invocationId").GetString());
        Assert.Equal("app.demo", payload.GetProperty("appId").GetString());
    }

    [Fact]
    public void Impl_HubEventNotification_WhenPayloadIsNull_ShouldOmitPayloadField()
    {
        var delivery = new HubEventDelivery
        {
            ConnectionId = "conn-2",
            SubscriptionId = "sub-2",
            Type = "app.instance.registered",
            TimeUtc = DateTime.UtcNow,
            Payload = null
        };

        var notification = HubEventNotificationFactory.Create(delivery);
        var json = JsonSerializer.SerializeToElement(notification);

        var parameters = json.GetProperty("params");
        Assert.False(parameters.TryGetProperty("payload", out _));
    }
}


