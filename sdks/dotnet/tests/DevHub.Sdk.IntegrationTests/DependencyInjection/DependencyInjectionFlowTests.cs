using DevHub.Sdk.IntegrationTests.TestHost;
using DevHub.Sdk.Models;
using Microsoft.Extensions.DependencyInjection;

namespace DevHub.Sdk.IntegrationTests.DependencyInjection;

/// <summary>
/// 依赖注入公开入口黑盒测试。
/// </summary>
public sealed class DependencyInjectionFlowTests
{
    private const string InstancePassword = "sdk-di-flow-password";

    [Fact]
    public async Task AddDevHubSdk_Factories_ShouldCreateHttpAndEventsClientsAgainstRealHost()
    {
        await using var host = await DevHubHostFixture.StartAsync();
        await host.WriteDefinitionAsync(new AppDefinition
        {
            AppId = "di.flow.app",
            Scope = string.Empty,
            DisplayName = "DI Flow App"
        });

        var services = new ServiceCollection();
        services.AddDevHubSdk(options =>
        {
            options.ClientId = "di-flow-client";
            options.DataDir = host.DataDirectory;
        });

        await using var provider = services.BuildServiceProvider();
        var clientFactory = provider.GetRequiredService<IDevHubClientFactory>();
        var eventsClientFactory = provider.GetRequiredService<IDevHubEventsClientFactory>();

        await using var client = await clientFactory.CreateAsync();
        await using var eventsClient = await eventsClientFactory.CreateAsync();

        var ping = await client.PingAsync(new { source = "di" });
        Assert.True(ping.Ok);
        Assert.Equal("di", ping.Echo!.Value.GetProperty("source").GetString());

        await eventsClient.AuthenticateAsync();
        var subscriptionId = await eventsClient.SubscribeAsync(new[] { DevHubEventTypes.AppInstanceRegistered });
        await using var enumerator = eventsClient.ReadEventsAsync().GetAsyncEnumerator();

        var registered = await client.RegisterInstanceAsync(new AppInstanceRegistration
        {
            InstanceId = "di-flow-inst-1",
            AppId = "di.flow.app",
            Scope = string.Empty,
            Pid = Environment.ProcessId,
            Invoke = new InvokeCapability
            {
                Poll = true,
                Respond = true
            }
        }, InstancePassword);

        var registeredEvent = await ReadNextEventAsync(enumerator, TimeSpan.FromSeconds(3));
        Assert.Equal(subscriptionId, registeredEvent.SubscriptionId);
        Assert.Equal(DevHubEventTypes.AppInstanceRegistered, registeredEvent.Type);
        Assert.Equal("di-flow-inst-1", registeredEvent.Payload!.Value.GetProperty("instanceId").GetString());

        await client.UnregisterInstanceAsync(registered.Instance.InstanceId, registered.InstanceSessionToken);
    }

    private static async Task<DevHubEvent> ReadNextEventAsync(IAsyncEnumerator<DevHubEvent> enumerator, TimeSpan timeout)
    {
        var moveNextTask = enumerator.MoveNextAsync().AsTask();
        var completedTask = await Task.WhenAny(moveNextTask, Task.Delay(timeout));
        Assert.Same(moveNextTask, completedTask);
        Assert.True(await moveNextTask);
        return enumerator.Current;
    }
}
