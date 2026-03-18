using DevHub.Sdk.IntegrationTests.TestHost;
using DevHub.Sdk.Models;

namespace DevHub.Sdk.IntegrationTests.Events;

/// <summary>
/// Events 黑盒测试。
/// </summary>
public sealed class EventsFlowTests
{
    [Fact]
    public async Task M5_E2E_004_WsAuthenticateSubscribeUnsubscribe_ShouldControlDelivery()
    {
        await using var host = await DevHubHostFixture.StartAsync();
        await host.WriteDefinitionAsync(new AppDefinition
        {
            AppId = "events.flow.app",
            DisplayName = "events.flow.app"
        });

        await using var eventsClient = await host.CreateEventsClientAsync("events-client");
        await eventsClient.AuthenticateAsync();
        var subscriptionId = await eventsClient.SubscribeAsync(new[] { DevHubEventTypes.AppInstanceRegistered });

        var eventEnumerator = eventsClient.ReadEventsAsync().GetAsyncEnumerator();

        await using var client = await host.CreateClientAsync("events-http-client");
        await client.RegisterInstanceAsync(CreateInstance("events.flow.app", "events-inst-1"));

        Assert.True(await eventEnumerator.MoveNextAsync());
        Assert.Equal(subscriptionId, eventEnumerator.Current.SubscriptionId);
        Assert.Equal(DevHubEventTypes.AppInstanceRegistered, eventEnumerator.Current.Type);

        await eventsClient.UnsubscribeAsync(subscriptionId);
        await client.RegisterInstanceAsync(CreateInstance("events.flow.app", "events-inst-2"));

        var nextEventTask = eventEnumerator.MoveNextAsync().AsTask();
        await Assert.ThrowsAsync<TimeoutException>(() => nextEventTask.WaitAsync(TimeSpan.FromMilliseconds(600)));
    }

    [Fact]
    public async Task M5_E2E_005_SubscribeUnknownType_ShouldReturnInvalidParams()
    {
        await using var host = await DevHubHostFixture.StartAsync();
        await using var eventsClient = await host.CreateEventsClientAsync("events-client");
        await eventsClient.AuthenticateAsync();

        var exception = await Assert.ThrowsAsync<DevHubRpcException>(() => eventsClient.SubscribeAsync(new[] { "unknown.type" }));
        Assert.Equal(-32602, exception.Code);
        Assert.Equal("invalid_params", exception.Message);
    }

    [Fact]
    public async Task M5_E2E_010_DisconnectCleanup_ShouldRequireResubscribeAfterReconnect()
    {
        await using var host = await DevHubHostFixture.StartAsync();
        await host.WriteDefinitionAsync(new AppDefinition
        {
            AppId = "events.reconnect.app",
            DisplayName = "events.reconnect.app"
        });

        await using (var firstClient = await host.CreateEventsClientAsync("events-client-1"))
        {
            await firstClient.AuthenticateAsync();
            _ = await firstClient.SubscribeAsync(new[] { DevHubEventTypes.AppInstanceRegistered });
        }

        await using var secondClient = await host.CreateEventsClientAsync("events-client-2");
        await secondClient.AuthenticateAsync();

        await using var httpClient = await host.CreateClientAsync("events-http-client");
        await httpClient.RegisterInstanceAsync(CreateInstance("events.reconnect.app", "events-reconnect-inst-1"));

        _ = await secondClient.SubscribeAsync(new[] { DevHubEventTypes.AppInstanceRegistered });
        await httpClient.RegisterInstanceAsync(CreateInstance("events.reconnect.app", "events-reconnect-inst-2"));

        await using var enumerator = secondClient.ReadEventsAsync().GetAsyncEnumerator();
        var moveNextTask = enumerator.MoveNextAsync().AsTask();
        var completedTask = await Task.WhenAny(moveNextTask, Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.Same(moveNextTask, completedTask);
        Assert.True(await moveNextTask);
        Assert.Equal(DevHubEventTypes.AppInstanceRegistered, enumerator.Current.Type);
        Assert.Equal("events-reconnect-inst-2", enumerator.Current.Payload!.Value.GetProperty("instanceId").GetString());
    }

    [Fact]
    public async Task M5_E2E_011_WsReadableMethods_ShouldMatchPublishedSurface()
    {
        await using var host = await DevHubHostFixture.StartAsync();
        await host.WriteDefinitionAsync(new AppDefinition
        {
            AppId = "events.ws.read.app",
            DisplayName = "events.ws.read.app"
        });

        await using var httpClient = await host.CreateClientAsync("events-http-client");
        await httpClient.RegisterInstanceAsync(CreateInstance("events.ws.read.app", "events-ws-read-inst-1"));

        await using var eventsClient = await host.CreateEventsClientAsync("events-client");
        await eventsClient.AuthenticateAsync();

        var ping = await eventsClient.PingAsync(new { source = "ws" });
        var definitions = await eventsClient.ListDefinitionsAsync();
        var definition = await eventsClient.GetDefinitionAsync("events.ws.read.app");
        var instances = await eventsClient.ListInstancesAsync(new ListInstancesRequest
        {
            AppId = "events.ws.read.app"
        });

        Assert.True(ping.Ok);
        Assert.Equal("events.ws.read.app", definition.AppId);
        Assert.Contains(definitions, item => item.AppId == "events.ws.read.app");
        Assert.Contains(instances, item => item.InstanceId == "events-ws-read-inst-1");
    }

    private static AppInstanceRegistration CreateInstance(string appId, string instanceId)
    {
        return new AppInstanceRegistration
        {
            InstanceId = instanceId,
            AppId = appId,
            Pid = Environment.ProcessId,
            Invoke = new InvokeCapability
            {
                Poll = true,
                Respond = true
            }
        };
    }

}
