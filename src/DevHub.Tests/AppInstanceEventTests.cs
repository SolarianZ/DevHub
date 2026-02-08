namespace DevHub.Tests;

using System.Text.Json;
using DevHub.Core.Models.Rpc;
using DevHub.Core.Services;
using DevHub.Core.Services.Events;
using DevHub.Core.Services.Rpc.Handlers;
using Microsoft.Extensions.Logging;
using Moq;

/// <summary>
/// AppInstance 事件发布测试。
/// </summary>
public class AppInstanceEventTests
{
    private readonly Mock<ILogger<AppRegistry>> _registryLogger = new();
    private readonly Mock<ILogger<AppInstancesHandler>> _handlerLogger = new();
    private readonly Mock<ILogger<HubEventBus>> _eventBusLogger = new();

    [Fact]
    public async Task RegisterAndUnregister_ShouldPublishRegisteredAndUnregisteredEvents()
    {
        var appRegistry = new AppRegistry(_registryLogger.Object);
        var eventBus = new HubEventBus(_eventBusLogger.Object);
        var handler = new AppInstancesHandler(appRegistry, _handlerLogger.Object, eventBus);

        eventBus.RegisterConnection("conn-instance-events");
        Assert.True(eventBus.TryMarkAuthenticated("conn-instance-events", "test-client", Guid.NewGuid().ToString("D")));
        Assert.True(eventBus.TrySubscribe("conn-instance-events", ["app.instance.registered", "app.instance.unregistered"], out _));

        var registerResponse = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "register-instance",
            Method = "hub.apps.registerInstance",
            Params = JsonSerializer.SerializeToElement(new
            {
                instance = new
                {
                    instanceId = "inst-event-001",
                    appId = "event.app",
                    scope = "workspace-A",
                    pid = 5011,
                    invoke = new { poll = true, respond = true }
                }
            })
        }, CancellationToken.None);

        Assert.Null(registerResponse.Error);

        var unregisterResponse = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "unregister-instance",
            Method = "hub.apps.unregisterInstance",
            Params = JsonSerializer.SerializeToElement(new
            {
                instanceId = "inst-event-001"
            })
        }, CancellationToken.None);

        Assert.Null(unregisterResponse.Error);

        var deliveries = eventBus.DrainDeliveries("conn-instance-events", maxCount: 10);
        Assert.Equal(2, deliveries.Count);
        Assert.Contains(deliveries, d => d.Type == "app.instance.registered");
        Assert.Contains(deliveries, d => d.Type == "app.instance.unregistered");

        var registerPayload = JsonSerializer.SerializeToElement(deliveries.First(d => d.Type == "app.instance.registered").Payload);
        Assert.Equal("event.app", registerPayload.GetProperty("appId").GetString());
        Assert.Equal("inst-event-001", registerPayload.GetProperty("instanceId").GetString());
        Assert.Equal("workspace-A", registerPayload.GetProperty("scope").GetString());
    }

    [Fact]
    public async Task UnregisterUnknownInstance_ShouldNotPublishEvent()
    {
        var appRegistry = new AppRegistry(_registryLogger.Object);
        var eventBus = new HubEventBus(_eventBusLogger.Object);
        var handler = new AppInstancesHandler(appRegistry, _handlerLogger.Object, eventBus);

        eventBus.RegisterConnection("conn-unregister-idempotent");
        Assert.True(eventBus.TryMarkAuthenticated("conn-unregister-idempotent", "test-client", Guid.NewGuid().ToString("D")));
        Assert.True(eventBus.TrySubscribe("conn-unregister-idempotent", ["app.instance.unregistered"], out _));

        var unregisterResponse = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "unregister-missing",
            Method = "hub.apps.unregisterInstance",
            Params = JsonSerializer.SerializeToElement(new
            {
                instanceId = "inst-not-found"
            })
        }, CancellationToken.None);

        Assert.Null(unregisterResponse.Error);

        var deliveries = eventBus.DrainDeliveries("conn-unregister-idempotent", maxCount: 10);
        Assert.Empty(deliveries);
    }
}
