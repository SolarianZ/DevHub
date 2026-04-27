namespace DevHub.Host.Tests;

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using DevHub.Core.Models;
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
    public void Impl_HubEventNotification_WhenPayloadIsNull_ShouldWritePayloadAsNull()
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
        Assert.True(parameters.TryGetProperty("payload", out var payload));
        Assert.Equal(JsonValueKind.Null, payload.ValueKind);
    }

    [Fact]
    public void Impl_HubEventNotification_WhenPayloadContainsNullField_ShouldPreserveNestedNull()
    {
        var delivery = new HubEventDelivery
        {
            ConnectionId = "conn-3",
            SubscriptionId = "sub-3",
            Type = "app.instance.registered",
            TimeUtc = DateTime.UtcNow,
            Payload = new
            {
                appId = "event.global.app",
                instanceId = "inst-event-global",
                scope = ScopeContract.Global
            }
        };

        var json = JsonSerializer.SerializeToElement(
            HubEventNotificationFactory.Create(delivery),
            new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            });

        var payload = json.GetProperty("params").GetProperty("payload");
        Assert.True(payload.TryGetProperty("scope", out var scope));
        Assert.Equal(JsonValueKind.String, scope.ValueKind);
        Assert.Equal(ScopeContract.Global, scope.GetString());
    }

    [Fact]
    public void Impl_HubEventNotification_WhenDefinitionPayloadContainsOptionalNullField_ShouldOmitNestedNull()
    {
        var delivery = new HubEventDelivery
        {
            ConnectionId = "conn-4",
            SubscriptionId = "sub-4",
            Type = "app.definition.upserted",
            TimeUtc = DateTime.UtcNow,
            Payload = new
            {
                appId = "definition.app",
                scope = ScopeContract.Global,
                definition = new
                {
                    appId = "definition.app",
                    scope = ScopeContract.Global,
                    displayName = "Definition App",
                    description = (string?)null
                }
            }
        };

        var json = JsonSerializer.SerializeToElement(
            HubEventNotificationFactory.Create(delivery),
            new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            });

        var payload = json.GetProperty("params").GetProperty("payload");
        Assert.True(payload.TryGetProperty("scope", out var scope));
        Assert.Equal(JsonValueKind.String, scope.ValueKind);
        Assert.Equal(ScopeContract.Global, scope.GetString());

        var definition = payload.GetProperty("definition");
        Assert.True(definition.TryGetProperty("scope", out var definitionScope));
        Assert.Equal(JsonValueKind.String, definitionScope.ValueKind);
        Assert.Equal(ScopeContract.Global, definitionScope.GetString());
        Assert.False(definition.TryGetProperty("description", out _));
    }

    [Fact]
    public void Impl_HubEventNotification_WhenDefinitionDeletedPayloadContainsNullScope_ShouldPreserveScope()
    {
        var delivery = new HubEventDelivery
        {
            ConnectionId = "conn-5",
            SubscriptionId = "sub-5",
            Type = "app.definition.deleted",
            TimeUtc = DateTime.UtcNow,
            Payload = new
            {
                appId = "definition.app",
                scope = ScopeContract.Global
            }
        };

        var json = JsonSerializer.SerializeToElement(
            HubEventNotificationFactory.Create(delivery),
            new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            });

        var payload = json.GetProperty("params").GetProperty("payload");
        Assert.True(payload.TryGetProperty("scope", out var scope));
        Assert.Equal(JsonValueKind.String, scope.ValueKind);
        Assert.Equal(ScopeContract.Global, scope.GetString());
    }
}

