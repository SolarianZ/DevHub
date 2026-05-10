using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
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
            Scope = string.Empty,
            DisplayName = "events.flow.app"
        });

        await using var eventsClient = await host.CreateEventsClientAsync("events-client");
        await eventsClient.AuthenticateAsync();
        var subscriptionId = await eventsClient.SubscribeAsync(new[] { DevHubEventTypes.AppInstanceRegistered });

        await using var client = await host.CreateClientAsync("events-http-client");
        var registered = await client.RegisterInstanceAsync(CreateInstance("events.flow.app", "events-inst-1"), InstancePassword);

        var registeredEvent = await ReadSingleEventAsync(eventsClient, TimeSpan.FromSeconds(2));
        Assert.Equal(subscriptionId, registeredEvent.SubscriptionId);
        Assert.Equal(DevHubEventTypes.AppInstanceRegistered, registeredEvent.Type);
        Assert.Equal("events-inst-1", registeredEvent.Payload!.Value.GetProperty("instanceId").GetString());
        Assert.False(registeredEvent.Payload!.Value.TryGetProperty("password", out _));

        await client.UnregisterInstanceAsync("events-inst-1", registered.InstanceSessionToken!);
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
            Scope = string.Empty,
            DisplayName = "Events Definition App"
        };

        _ = await httpClient.UpsertDefinitionAsync(definition);
        var upsertedEvent = await ReadSingleEventAsync(eventsClient, TimeSpan.FromSeconds(2));
        Assert.Equal(subscriptionId, upsertedEvent.SubscriptionId);
        Assert.Equal(DevHubEventTypes.AppDefinitionUpserted, upsertedEvent.Type);
        Assert.Equal(definition.AppId, upsertedEvent.Payload!.Value.GetProperty("appId").GetString());
        Assert.Equal(string.Empty, upsertedEvent.Payload!.Value.GetProperty("scope").GetString());
        Assert.Equal(definition.AppId, upsertedEvent.Payload!.Value.GetProperty("definition").GetProperty("appId").GetString());
        Assert.Equal(string.Empty, upsertedEvent.Payload!.Value.GetProperty("definition").GetProperty("scope").GetString());

        await httpClient.DeleteDefinitionAsync(definition.AppId, definition.Scope);
        var deletedEvent = await ReadSingleEventAsync(eventsClient, TimeSpan.FromSeconds(2));
        Assert.Equal(subscriptionId, deletedEvent.SubscriptionId);
        Assert.Equal(DevHubEventTypes.AppDefinitionDeleted, deletedEvent.Type);
        Assert.Equal(definition.AppId, deletedEvent.Payload!.Value.GetProperty("appId").GetString());
        Assert.Equal(string.Empty, deletedEvent.Payload!.Value.GetProperty("scope").GetString());
    }

    [Fact]
    public async Task PublicRpcActions_ShouldPublishSpecEventsWithoutSensitiveFields()
    {
        await using var host = await DevHubHostFixture.StartAsync();
        await using var eventsClient = await host.CreateEventsClientAsync("public-event-flow-ws-client");
        await using var httpClient = await host.CreateClientAsync("public-event-flow-http-client");

        await eventsClient.AuthenticateAsync();
        var subscriptionId = await eventsClient.SubscribeAsync(DevHubEventTypes.All);
        await using var enumerator = eventsClient.ReadEventsAsync().GetAsyncEnumerator();

        var definition = new AppDefinition
        {
            AppId = "events.public-flow.app",
            Scope = string.Empty,
            DisplayName = "Events Public Flow App"
        };

        _ = await httpClient.UpsertDefinitionAsync(definition);
        var upserted = await ReadNextEventAsync(enumerator, TimeSpan.FromSeconds(3));
        Assert.Equal(subscriptionId, upserted.SubscriptionId);
        Assert.Equal(DevHubEventTypes.AppDefinitionUpserted, upserted.Type);
        AssertPayloadHas(upserted, "appId", "scope", "definition");
        Assert.Equal(definition.AppId, upserted.Payload!.Value.GetProperty("appId").GetString());
        Assert.Equal(string.Empty, upserted.Payload!.Value.GetProperty("scope").GetString());
        Assert.Equal(string.Empty, upserted.Payload!.Value.GetProperty("definition").GetProperty("scope").GetString());

        var registered = await httpClient.RegisterInstanceAsync(CreateInstance(definition.AppId, "events-public-flow-inst-1"), InstancePassword);
        var registeredEvent = await ReadNextEventAsync(enumerator, TimeSpan.FromSeconds(3));
        Assert.Equal(DevHubEventTypes.AppInstanceRegistered, registeredEvent.Type);
        AssertPayloadHas(registeredEvent, "appId", "instanceId", "scope");
        AssertPayloadDoesNotHave(registeredEvent, "password", "instanceSessionToken");
        Assert.Equal("events-public-flow-inst-1", registeredEvent.Payload!.Value.GetProperty("instanceId").GetString());

        var requestTask = httpClient.RequestAsync(new InvokeRequest
        {
            AppId = definition.AppId,
            Method = "test.public.event.success",
            Target = new InvocationTarget
            {
                Scope = string.Empty
            },
            Options = new InvocationOptions
            {
                TtlMs = 5000,
                WaitTimeoutMs = 3000
            }
        });

        var queued = await ReadNextEventAsync(enumerator, TimeSpan.FromSeconds(3));
        Assert.Equal(DevHubEventTypes.InvocationQueued, queued.Type);
        AssertPayloadHas(queued, "invocationId", "appId", "target", "method", "kind");

        var invocation = await WaitForSingleInvocationAsync(httpClient, registered.Instance.InstanceId, registered.InstanceSessionToken!);
        var delivered = await ReadNextEventAsync(enumerator, TimeSpan.FromSeconds(3));
        Assert.Equal(DevHubEventTypes.InvocationDelivered, delivered.Type);
        AssertPayloadHas(delivered, "invocationId", "appId", "target", "instanceId", "delivery");
        AssertPayloadDoesNotHave(delivered.Payload!.Value.GetProperty("delivery"), "leaseToken");

        await httpClient.RespondAsync(new RespondRequest
        {
            InstanceId = registered.Instance.InstanceId,
            InstanceSessionToken = registered.InstanceSessionToken,
            InvocationId = invocation.InvocationId,
            LeaseToken = invocation.Delivery!.LeaseToken,
            Value = new { ok = true }
        });

        var completed = await ReadNextEventAsync(enumerator, TimeSpan.FromSeconds(3));
        Assert.Equal(DevHubEventTypes.InvocationCompleted, completed.Type);
        AssertPayloadHas(completed, "invocationId", "appId", "target", "instanceId");
        _ = await requestTask;

        var failedRequestTask = httpClient.RequestAsync(new InvokeRequest
        {
            AppId = definition.AppId,
            Method = "test.public.event.failure",
            Target = new InvocationTarget
            {
                Scope = string.Empty
            },
            Options = new InvocationOptions
            {
                TtlMs = 5000,
                WaitTimeoutMs = 3000
            }
        });

        _ = await ReadNextEventAsync(enumerator, TimeSpan.FromSeconds(3));
        var failedInvocation = await WaitForSingleInvocationAsync(httpClient, registered.Instance.InstanceId, registered.InstanceSessionToken!);
        _ = await ReadNextEventAsync(enumerator, TimeSpan.FromSeconds(3));

        await httpClient.RespondAsync(new RespondRequest
        {
            InstanceId = registered.Instance.InstanceId,
            InstanceSessionToken = registered.InstanceSessionToken,
            InvocationId = failedInvocation.InvocationId,
            LeaseToken = failedInvocation.Delivery!.LeaseToken,
            Error = DevHubCalleeError.Create(1001, "app_error", new { reason = "expected" })
        });

        var failed = await ReadNextEventAsync(enumerator, TimeSpan.FromSeconds(3));
        Assert.Equal(DevHubEventTypes.InvocationFailed, failed.Type);
        AssertPayloadHas(failed, "invocationId", "appId", "target", "instanceId", "reason");
        _ = await Assert.ThrowsAsync<DevHubRpcException>(() => failedRequestTask);

        await httpClient.UnregisterInstanceAsync(registered.Instance.InstanceId, registered.InstanceSessionToken!);
        var unregistered = await ReadNextEventAsync(enumerator, TimeSpan.FromSeconds(3));
        Assert.Equal(DevHubEventTypes.AppInstanceUnregistered, unregistered.Type);
        AssertPayloadHas(unregistered, "appId", "instanceId", "scope");
        AssertPayloadDoesNotHave(unregistered, "password", "instanceSessionToken");

        await httpClient.DeleteDefinitionAsync(definition.AppId, definition.Scope);
        var deleted = await ReadNextEventAsync(enumerator, TimeSpan.FromSeconds(3));
        Assert.Equal(DevHubEventTypes.AppDefinitionDeleted, deleted.Type);
        AssertPayloadHas(deleted, "appId", "scope");
        Assert.Equal(definition.AppId, deleted.Payload!.Value.GetProperty("appId").GetString());
        Assert.Equal(string.Empty, deleted.Payload!.Value.GetProperty("scope").GetString());
    }

    [Fact]
    public async Task SubscribeWithoutTypesAndEmptyTypes_ShouldReceiveAllEventTypes()
    {
        await using var host = await DevHubHostFixture.StartAsync();
        await using var omittedTypesClient = await host.CreateEventsClientAsync("events-all-omitted-types-client");
        await using var emptyTypesClient = await host.CreateEventsClientAsync("events-all-empty-types-client");
        await using var httpClient = await host.CreateClientAsync("events-all-http-client");

        await omittedTypesClient.AuthenticateAsync();
        await emptyTypesClient.AuthenticateAsync();
        var omittedTypesSubscriptionId = await omittedTypesClient.SubscribeAsync();
        var emptyTypesSubscriptionId = await emptyTypesClient.SubscribeAsync(Array.Empty<DevHubEventType>());
        await using var omittedTypesEnumerator = omittedTypesClient.ReadEventsAsync().GetAsyncEnumerator();
        await using var emptyTypesEnumerator = emptyTypesClient.ReadEventsAsync().GetAsyncEnumerator();

        var observedByOmittedTypes = new List<DevHubEventType>();
        var observedByEmptyTypes = new List<DevHubEventType>();
        var definition = new AppDefinition
        {
            AppId = "events.subscribe-all.app",
            Scope = string.Empty,
            DisplayName = "Events Subscribe All App"
        };

        _ = await httpClient.UpsertDefinitionAsync(definition);
        await AssertNextEventPairAsync(DevHubEventTypes.AppDefinitionUpserted);

        var registered = await httpClient.RegisterInstanceAsync(CreateInstance(definition.AppId, "events-subscribe-all-inst-1"), InstancePassword);
        await AssertNextEventPairAsync(DevHubEventTypes.AppInstanceRegistered);

        var completedRequestTask = httpClient.RequestAsync(new InvokeRequest
        {
            AppId = definition.AppId,
            Method = "test.subscribe.all.success",
            Target = new InvocationTarget
            {
                Scope = string.Empty
            },
            Options = new InvocationOptions
            {
                TtlMs = 5000,
                WaitTimeoutMs = 3000
            }
        });

        await AssertNextEventPairAsync(DevHubEventTypes.InvocationQueued);
        var completedInvocation = await WaitForSingleInvocationAsync(httpClient, registered.Instance.InstanceId, registered.InstanceSessionToken!);
        await AssertNextEventPairAsync(DevHubEventTypes.InvocationDelivered);

        await httpClient.RespondAsync(new RespondRequest
        {
            InstanceId = registered.Instance.InstanceId,
            InstanceSessionToken = registered.InstanceSessionToken,
            InvocationId = completedInvocation.InvocationId,
            LeaseToken = completedInvocation.Delivery!.LeaseToken,
            Value = new { ok = true }
        });

        await AssertNextEventPairAsync(DevHubEventTypes.InvocationCompleted);
        _ = await completedRequestTask;

        var failedRequestTask = httpClient.RequestAsync(new InvokeRequest
        {
            AppId = definition.AppId,
            Method = "test.subscribe.all.failure",
            Target = new InvocationTarget
            {
                Scope = string.Empty
            },
            Options = new InvocationOptions
            {
                TtlMs = 5000,
                WaitTimeoutMs = 3000
            }
        });

        await AssertNextEventPairAsync(DevHubEventTypes.InvocationQueued);
        var failedInvocation = await WaitForSingleInvocationAsync(httpClient, registered.Instance.InstanceId, registered.InstanceSessionToken!);
        await AssertNextEventPairAsync(DevHubEventTypes.InvocationDelivered);

        await httpClient.RespondAsync(new RespondRequest
        {
            InstanceId = registered.Instance.InstanceId,
            InstanceSessionToken = registered.InstanceSessionToken,
            InvocationId = failedInvocation.InvocationId,
            LeaseToken = failedInvocation.Delivery!.LeaseToken,
            Error = DevHubCalleeError.Create(1001, "app_error", new { reason = "expected" })
        });

        await AssertNextEventPairAsync(DevHubEventTypes.InvocationFailed);
        _ = await Assert.ThrowsAsync<DevHubRpcException>(() => failedRequestTask);

        await httpClient.UnregisterInstanceAsync(registered.Instance.InstanceId, registered.InstanceSessionToken!);
        await AssertNextEventPairAsync(DevHubEventTypes.AppInstanceUnregistered);

        await httpClient.DeleteDefinitionAsync(definition.AppId, definition.Scope);
        await AssertNextEventPairAsync(DevHubEventTypes.AppDefinitionDeleted);

        Assert.Equal(
            DevHubEventTypes.All.OrderBy(static item => item.Value),
            observedByOmittedTypes.Distinct().OrderBy(static item => item.Value));
        Assert.Equal(
            DevHubEventTypes.All.OrderBy(static item => item.Value),
            observedByEmptyTypes.Distinct().OrderBy(static item => item.Value));

        async Task AssertNextEventPairAsync(DevHubEventType expectedType)
        {
            var omittedTypesEvent = await ReadNextEventAsync(omittedTypesEnumerator, TimeSpan.FromSeconds(3));
            var emptyTypesEvent = await ReadNextEventAsync(emptyTypesEnumerator, TimeSpan.FromSeconds(3));

            Assert.Equal(omittedTypesSubscriptionId, omittedTypesEvent.SubscriptionId);
            Assert.Equal(emptyTypesSubscriptionId, emptyTypesEvent.SubscriptionId);
            Assert.Equal(expectedType, omittedTypesEvent.Type);
            Assert.Equal(expectedType, emptyTypesEvent.Type);

            observedByOmittedTypes.Add(omittedTypesEvent.Type);
            observedByEmptyTypes.Add(emptyTypesEvent.Type);
        }
    }

    [Fact]
    public async Task UnsubscribeMissingSubscriptionId_ShouldSucceedAndPreserveSubsequentDelivery()
    {
        await using var host = await DevHubHostFixture.StartAsync();
        await host.WriteDefinitionAsync(new AppDefinition
        {
            AppId = "events.unsubscribe-missing.app",
            Scope = string.Empty,
            DisplayName = "events.unsubscribe-missing.app"
        });

        await using var eventsClient = await host.CreateEventsClientAsync("events-unsubscribe-missing-client");
        await using var httpClient = await host.CreateClientAsync("events-unsubscribe-missing-http-client");

        await eventsClient.AuthenticateAsync();
        await eventsClient.UnsubscribeAsync("missing-subscription-id");

        var subscriptionId = await eventsClient.SubscribeAsync(new[] { DevHubEventTypes.AppInstanceRegistered });
        await httpClient.RegisterInstanceAsync(CreateInstance("events.unsubscribe-missing.app", "events-unsubscribe-missing-inst-1"), InstancePassword);

        var deliveredEvent = await ReadSingleEventAsync(eventsClient, TimeSpan.FromSeconds(2));
        Assert.Equal(subscriptionId, deliveredEvent.SubscriptionId);
        Assert.Equal(DevHubEventTypes.AppInstanceRegistered, deliveredEvent.Type);
        Assert.Equal("events-unsubscribe-missing-inst-1", deliveredEvent.Payload!.Value.GetProperty("instanceId").GetString());
    }

    [Fact]
    public async Task DisconnectCleanup_ShouldRequireResubscribeAfterReconnect()
    {
        await using var host = await DevHubHostFixture.StartAsync();
        await host.WriteDefinitionAsync(new AppDefinition
        {
            AppId = "events.reconnect.app",
            Scope = string.Empty,
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
            Scope = string.Empty,
            DisplayName = "events.ws.read.app"
        });

        await using var httpClient = await host.CreateClientAsync("events-http-client");
        await httpClient.RegisterInstanceAsync(CreateInstance("events.ws.read.app", "events-ws-read-inst-1"), InstancePassword);

        await using var eventsClient = await host.CreateEventsClientAsync("events-client");
        await eventsClient.AuthenticateAsync();

        var ping = await eventsClient.PingAsync(new { source = "ws" });
        var definitions = await eventsClient.ListDefinitionsAsync(new ListDefinitionsRequest());
        var definition = await eventsClient.GetDefinitionAsync("events.ws.read.app", string.Empty);
        var instance = await eventsClient.GetInstanceAsync("events-ws-read-inst-1");
        var instances = await eventsClient.ListInstancesAsync(new ListInstancesRequest
        {
            AppId = "events.ws.read.app",
            Scope = null
        });

        Assert.True(ping.Ok);
        Assert.Equal("events.ws.read.app", definition.AppId);
        Assert.Equal(string.Empty, definition.Scope);
        Assert.Equal("events-ws-read-inst-1", instance.InstanceId);
        Assert.DoesNotContain("instanceSessionToken", JsonSerializer.Serialize(instance));
        Assert.Contains(definitions, item => item.AppId == "events.ws.read.app");
        Assert.Contains(instances, item => item.InstanceId == "events-ws-read-inst-1");
    }

    [Fact]
    public async Task WsVersionCompatibility_AfterAuthenticate_ShouldUseRpcWhenAvailableOrFallbackToRuntime()
    {
        await using var host = await DevHubHostFixture.StartAsync();
        await using var eventsClient = await host.CreateEventsClientAsync("events-version-client");
        await eventsClient.AuthenticateAsync();

        var compatibility = await eventsClient.CheckVersionCompatibilityAsync();

        Assert.False(string.IsNullOrWhiteSpace(compatibility.SdkVersion));

        try
        {
            var hostVersion = await eventsClient.GetHostVersionAsync();
            Assert.Equal(hostVersion, compatibility.HostVersion);
        }
        catch (DevHubRpcException exception) when (exception.Is(DevHubRpcErrorCode.MethodNotFound))
        {
            Assert.Equal(eventsClient.Runtime.HubVersion, compatibility.HostVersion);
        }

        Assert.NotEqual(VersionCompatibilityStatus.Unknown, compatibility.Status);
    }

    [Fact]
    public async Task WsGetInstance_WhenMissing_ShouldPropagateInstanceNotFound()
    {
        await using var host = await DevHubHostFixture.StartAsync();
        await using var eventsClient = await host.CreateEventsClientAsync("events-get-instance-missing-client");
        await eventsClient.AuthenticateAsync();

        var exception = await Assert.ThrowsAsync<DevHubRpcException>(() => eventsClient.GetInstanceAsync("missing-events-inst"));

        Assert.Equal(-32010, exception.Code);
        Assert.Equal("instance_not_found", exception.Message);
        Assert.Equal("unknown_instance", exception.Reason);
        Assert.Equal("missing-events-inst", exception.ErrorData!.Value.GetProperty("instanceId").GetString());
    }

    [Fact]
    public async Task WsAuthenticate_WhenTokenInvalid_ShouldPropagateUnauthorized()
    {
        await using var host = await DevHubHostFixture.StartAsync();
        var connectionFactory = new AuthenticateTamperingConnectionFactory(static socket =>
        {
            socket.TokenOverride = "bad-token";
        });

        await using var eventsClient = await DevHubEventsClient.FromRuntimeAsync(
            new DevHubClientOptions
            {
                ClientId = "events-auth-invalid-token-client",
                DataDir = host.DataDirectory
            },
            connectionFactory);

        var exception = await Assert.ThrowsAsync<DevHubRpcException>(() => eventsClient.AuthenticateAsync());

        Assert.Equal(-32001, exception.Code);
        Assert.Equal("unauthorized", exception.Message);
        Assert.Equal("invalid_token", exception.Reason);
    }

    [Fact]
    public async Task WsAuthenticate_WhenProtocolVersionMismatch_ShouldPropagateNotSupported()
    {
        await using var host = await DevHubHostFixture.StartAsync();
        var connectionFactory = new AuthenticateTamperingConnectionFactory(static socket =>
        {
            socket.ProtocolVersionOverride = 2;
        });

        await using var eventsClient = await DevHubEventsClient.FromRuntimeAsync(
            new DevHubClientOptions
            {
                ClientId = "events-auth-protocol-client",
                DataDir = host.DataDirectory
            },
            connectionFactory);

        var exception = await Assert.ThrowsAsync<DevHubRpcException>(() => eventsClient.AuthenticateAsync());

        Assert.Equal(-32099, exception.Code);
        Assert.Equal("not_supported", exception.Message);
        Assert.Equal("mismatch", exception.Reason);
    }

    private static AppInstanceRegistration CreateInstance(string appId, string instanceId)
    {
        return new AppInstanceRegistration
        {
            InstanceId = instanceId,
            AppId = appId,
            Scope = string.Empty,
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

    private static async Task<DevHubEvent> ReadNextEventAsync(IAsyncEnumerator<DevHubEvent> enumerator, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        var moveNextTask = enumerator.MoveNextAsync().AsTask();
        var completedTask = await Task.WhenAny(moveNextTask, Task.Delay(Timeout.InfiniteTimeSpan, cts.Token));
        Assert.Same(moveNextTask, completedTask);
        Assert.True(await moveNextTask);
        return enumerator.Current;
    }

    private static async Task<Invocation> WaitForSingleInvocationAsync(
        DevHubClient client,
        string instanceId,
        string instanceSessionToken,
        int timeoutMs = 3000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            var remaining = deadline - DateTime.UtcNow;
            var poll = await client.PollAsync(new PollRequest
            {
                InstanceId = instanceId,
                InstanceSessionToken = instanceSessionToken,
                WaitMs = Math.Min(Math.Max((int)Math.Ceiling(remaining.TotalMilliseconds), 0), 250)
            });

            if (poll.Items.Count == 1)
            {
                var invocation = poll.Items[0];
                Assert.False(string.IsNullOrWhiteSpace(invocation.Delivery?.LeaseToken));
                return invocation;
            }
        }

        throw new TimeoutException($"未能在 {timeoutMs}ms 内拉取到实例 {instanceId} 的单条调用。");
    }

    private static void AssertPayloadHas(DevHubEvent hubEvent, params string[] propertyNames)
    {
        Assert.NotNull(hubEvent.Payload);
        foreach (var propertyName in propertyNames)
        {
            Assert.True(
                hubEvent.Payload!.Value.TryGetProperty(propertyName, out _),
                $"事件 {hubEvent.Type} 缺少 payload.{propertyName}。");
        }
    }

    private static void AssertPayloadDoesNotHave(DevHubEvent hubEvent, params string[] propertyNames)
    {
        Assert.NotNull(hubEvent.Payload);
        AssertPayloadDoesNotHave(hubEvent.Payload!.Value, propertyNames);
    }

    private static void AssertPayloadDoesNotHave(JsonElement payload, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            Assert.False(payload.TryGetProperty(propertyName, out _), $"payload.{propertyName} 不应出现在事件载荷中。");
        }
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

    private sealed class AuthenticateTamperingConnectionFactory : IWebSocketConnectionFactory
    {
        private readonly Action<AuthenticateTamperingWebSocketConnection> _configure;

        public AuthenticateTamperingConnectionFactory(Action<AuthenticateTamperingWebSocketConnection> configure)
        {
            _configure = configure;
        }

        public async Task<IWebSocketConnection> ConnectAsync(Uri uri, CancellationToken cancellationToken)
        {
            var socket = new ClientWebSocket();
            await socket.ConnectAsync(uri, cancellationToken);

            var connection = new AuthenticateTamperingWebSocketConnection(socket);
            _configure(connection);
            return connection;
        }
    }

    private sealed class AuthenticateTamperingWebSocketConnection : IWebSocketConnection
    {
        private readonly ClientWebSocket _clientWebSocket;
        private bool _tampered;

        public AuthenticateTamperingWebSocketConnection(ClientWebSocket clientWebSocket)
        {
            _clientWebSocket = clientWebSocket;
        }

        public string? TokenOverride { get; set; }

        public int? ProtocolVersionOverride { get; set; }

        public WebSocketState State => _clientWebSocket.State;

        public async Task SendTextAsync(string text, CancellationToken cancellationToken)
        {
            var outboundText = text;
            if (!_tampered && text.Contains("\"method\":\"hub.ws.authenticate\"", StringComparison.Ordinal))
            {
                using var document = JsonDocument.Parse(text);
                var request = document.RootElement;
                var payload = new Dictionary<string, object?>
                {
                    ["jsonrpc"] = request.GetProperty("jsonrpc").GetString(),
                    ["id"] = request.GetProperty("id").GetString(),
                    ["method"] = request.GetProperty("method").GetString()
                };

                var originalParams = request.GetProperty("params");
                payload["params"] = new Dictionary<string, object?>
                {
                    ["token"] = TokenOverride ?? originalParams.GetProperty("token").GetString(),
                    ["protocolVersion"] = ProtocolVersionOverride ?? originalParams.GetProperty("protocolVersion").GetInt32(),
                    ["clientId"] = originalParams.GetProperty("clientId").GetString(),
                    ["clientSessionId"] = originalParams.GetProperty("clientSessionId").GetString()
                };

                outboundText = JsonSerializer.Serialize(payload);
                _tampered = true;
            }

            var bytes = Encoding.UTF8.GetBytes(outboundText);
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

        public ValueTask DisposeAsync()
        {
            _clientWebSocket.Dispose();
            return ValueTask.CompletedTask;
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
