using System.Net.WebSockets;
using System.Text;
using DevHub.Sdk.IntegrationTests.TestHost;
using DevHub.Sdk.Internal;
using DevHub.Sdk.Models;

namespace DevHub.Sdk.IntegrationTests.Events;

/// <summary>
/// Events 黑盒测试。
/// </summary>
public sealed class EventsFlowTests
{
    private const string InstancePassword = "sdk-events-flow-password";

    [Fact]
    public async Task WsAuthenticateSubscribeUnsubscribe_ShouldControlDelivery()
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

        await using var client = await host.CreateClientAsync("events-http-client");
        await client.RegisterInstanceAsync(CreateInstance("events.flow.app", "events-inst-1"), InstancePassword);

        var registeredEvent = await ReadSingleEventAsync(eventsClient, TimeSpan.FromSeconds(2));
        Assert.Equal(subscriptionId, registeredEvent.SubscriptionId);
        Assert.Equal(DevHubEventTypes.AppInstanceRegistered, registeredEvent.Type);
        Assert.Equal("events-inst-1", registeredEvent.Payload!.Value.GetProperty("instanceId").GetString());
        Assert.False(registeredEvent.Payload!.Value.TryGetProperty("password", out _));

        await client.UnregisterInstanceAsync("events-inst-1", InstancePassword);
        await AssertNoEventWithinAsync(eventsClient, TimeSpan.FromMilliseconds(600));

        await eventsClient.UnsubscribeAsync(subscriptionId);
        await client.RegisterInstanceAsync(CreateInstance("events.flow.app", "events-inst-2"), InstancePassword);

        await AssertNoEventWithinAsync(eventsClient, TimeSpan.FromMilliseconds(600));
    }

    [Fact]
    public async Task Impl_DefinitionLifecycleEvents_ShouldPublishUpsertedAndDeleted()
    {
        await using var host = await DevHubHostFixture.StartAsync();
        await using var eventsClient = await host.CreateEventsClientAsync("definition-events-client");
        await using var httpClient = await host.CreateClientAsync("definition-events-http-client");

        await eventsClient.AuthenticateAsync();
        var subscriptionId = await eventsClient.SubscribeAsync(new[]
        {
            DevHubEventTypes.AppDefinitionUpserted,
            DevHubEventTypes.AppDefinitionDeleted
        });

        var definition = new AppDefinition
        {
            AppId = "events.definition.app",
            DisplayName = "Events Definition App"
        };

        _ = await httpClient.UpsertDefinitionAsync(definition);
        var upsertedEvent = await ReadSingleEventAsync(eventsClient, TimeSpan.FromSeconds(2));
        Assert.Equal(subscriptionId, upsertedEvent.SubscriptionId);
        Assert.Equal(DevHubEventTypes.AppDefinitionUpserted, upsertedEvent.Type);
        Assert.Equal(definition.AppId, upsertedEvent.Payload!.Value.GetProperty("appId").GetString());
        Assert.Equal(definition.AppId, upsertedEvent.Payload!.Value.GetProperty("definition").GetProperty("appId").GetString());

        await httpClient.DeleteDefinitionAsync(definition.AppId);
        var deletedEvent = await ReadSingleEventAsync(eventsClient, TimeSpan.FromSeconds(2));
        Assert.Equal(subscriptionId, deletedEvent.SubscriptionId);
        Assert.Equal(DevHubEventTypes.AppDefinitionDeleted, deletedEvent.Type);
        Assert.Equal(definition.AppId, deletedEvent.Payload!.Value.GetProperty("appId").GetString());
    }

    [Fact]
    public async Task DisconnectCleanup_ShouldRequireResubscribeAfterReconnect()
    {
        await using var host = await DevHubHostFixture.StartAsync();
        await host.WriteDefinitionAsync(new AppDefinition
        {
            AppId = "events.reconnect.app",
            DisplayName = "events.reconnect.app"
        });

        var connectionFactory = new DisconnectableWebSocketConnectionFactory();
        await using var eventsClient = await DevHubEventsClient.FromRuntimeAsync(
            new DevHubClientOptions
            {
                ClientId = "events-client",
                DataDir = host.DataDirectory
            },
            connectionFactory);
        await using var httpClient = await host.CreateClientAsync("events-http-client");

        await eventsClient.AuthenticateAsync();
        var initialSubscriptionId = await eventsClient.SubscribeAsync(new[] { DevHubEventTypes.AppInstanceRegistered });

        await using (var initialEnumerator = eventsClient.ReadEventsAsync().GetAsyncEnumerator())
        {
            await httpClient.RegisterInstanceAsync(CreateInstance("events.reconnect.app", "events-reconnect-inst-1"), InstancePassword);

            Assert.True(await initialEnumerator.MoveNextAsync());
            Assert.Equal(initialSubscriptionId, initialEnumerator.Current.SubscriptionId);
            Assert.Equal(DevHubEventTypes.AppInstanceRegistered, initialEnumerator.Current.Type);
            Assert.Equal("events-reconnect-inst-1", initialEnumerator.Current.Payload!.Value.GetProperty("instanceId").GetString());

            Assert.NotNull(connectionFactory.CurrentConnection);
            await connectionFactory.CurrentConnection!.InitiateCloseAsync();
            await AssertEventStreamCompletedAsync(initialEnumerator, TimeSpan.FromSeconds(2));
        }

        await eventsClient.AuthenticateAsync();

        await httpClient.RegisterInstanceAsync(CreateInstance("events.reconnect.app", "events-reconnect-inst-2"), InstancePassword);
        await AssertNoEventWithinAsync(eventsClient, TimeSpan.FromMilliseconds(600));

        var resubscribedId = await eventsClient.SubscribeAsync(new[] { DevHubEventTypes.AppInstanceRegistered });
        await httpClient.RegisterInstanceAsync(CreateInstance("events.reconnect.app", "events-reconnect-inst-3"), InstancePassword);

        var reconnectedEvent = await ReadSingleEventAsync(eventsClient, TimeSpan.FromSeconds(2));
        Assert.Equal(resubscribedId, reconnectedEvent.SubscriptionId);
        Assert.Equal(DevHubEventTypes.AppInstanceRegistered, reconnectedEvent.Type);
        Assert.Equal("events-reconnect-inst-3", reconnectedEvent.Payload!.Value.GetProperty("instanceId").GetString());
    }

    [Fact]
    public async Task WsReadableMethods_ShouldMatchPublishedSurface()
    {
        await using var host = await DevHubHostFixture.StartAsync();
        await host.WriteDefinitionAsync(new AppDefinition
        {
            AppId = "events.ws.read.app",
            DisplayName = "events.ws.read.app"
        });

        await using var httpClient = await host.CreateClientAsync("events-http-client");
        await httpClient.RegisterInstanceAsync(CreateInstance("events.ws.read.app", "events-ws-read-inst-1"), InstancePassword);

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

    private static async Task<DevHubEvent> ReadSingleEventAsync(DevHubEventsClient client, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        await using var enumerator = client.ReadEventsAsync(cts.Token).GetAsyncEnumerator();
        Assert.True(await enumerator.MoveNextAsync());
        return enumerator.Current;
    }

    private static async Task AssertNoEventWithinAsync(DevHubEventsClient client, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        await using var enumerator = client.ReadEventsAsync(cts.Token).GetAsyncEnumerator();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await enumerator.MoveNextAsync().AsTask());
    }

    private static async Task AssertEventStreamCompletedAsync(IAsyncEnumerator<DevHubEvent> enumerator, TimeSpan timeout)
    {
        var moveNextTask = enumerator.MoveNextAsync().AsTask();
        var completedTask = await Task.WhenAny(moveNextTask, Task.Delay(timeout));
        Assert.Same(moveNextTask, completedTask);
        Assert.False(await moveNextTask);
    }

    private sealed class DisconnectableWebSocketConnectionFactory : IWebSocketConnectionFactory
    {
        public DisconnectableWebSocketConnection? CurrentConnection { get; private set; }

        public async Task<IWebSocketConnection> ConnectAsync(Uri uri, CancellationToken cancellationToken)
        {
            var socket = new ClientWebSocket();
            await socket.ConnectAsync(uri, cancellationToken);

            var connection = new DisconnectableWebSocketConnection(socket);
            CurrentConnection = connection;
            return connection;
        }
    }

    private sealed class DisconnectableWebSocketConnection : IWebSocketConnection
    {
        private readonly ClientWebSocket _clientWebSocket;

        public DisconnectableWebSocketConnection(ClientWebSocket clientWebSocket)
        {
            _clientWebSocket = clientWebSocket;
        }

        public WebSocketState State => _clientWebSocket.State;

        public async Task SendTextAsync(string text, CancellationToken cancellationToken)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            await _clientWebSocket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
        }

        public async Task<WebSocketReceiveMessage> ReceiveAsync(CancellationToken cancellationToken)
        {
            var buffer = new byte[4096];
            using var stream = new MemoryStream();

            while (true)
            {
                var result = await _clientWebSocket.ReceiveAsync(buffer, cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return new WebSocketReceiveMessage
                    {
                        MessageType = result.MessageType,
                        CloseStatus = result.CloseStatus,
                        CloseStatusDescription = result.CloseStatusDescription
                    };
                }

                if (result.Count > 0)
                {
                    await stream.WriteAsync(buffer.AsMemory(0, result.Count), cancellationToken);
                }

                if (result.EndOfMessage)
                {
                    return new WebSocketReceiveMessage
                    {
                        MessageType = result.MessageType,
                        Text = Encoding.UTF8.GetString(stream.ToArray())
                    };
                }
            }
        }

        public async Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            if (_clientWebSocket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                await _clientWebSocket.CloseAsync(closeStatus, statusDescription, cancellationToken);
            }
        }

        public async Task InitiateCloseAsync()
        {
            if (_clientWebSocket.State == WebSocketState.Open)
            {
                await _clientWebSocket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "test_disconnect", CancellationToken.None);
            }
        }

        public ValueTask DisposeAsync()
        {
            _clientWebSocket.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
