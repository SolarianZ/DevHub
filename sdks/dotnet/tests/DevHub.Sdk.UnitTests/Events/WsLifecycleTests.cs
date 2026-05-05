using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using DevHub.Sdk.Internal;
using DevHub.Sdk.Models;
using Microsoft.Extensions.Logging;

namespace DevHub.Sdk.UnitTests.Events;

/// <summary>
/// WS 生命周期白盒测试。
/// </summary>
public sealed class WsLifecycleTests : IDisposable
{
    private readonly string _tempRoot;

    public static TheoryData<string, string> InvalidEventNotifications => new()
    {
        {
            """{"jsonrpc":"2.0","method":"hub.event","params":{"subscriptionId":"","type":"invocation.completed","timeUtc":"2026-03-09T00:00:00Z","payload":{"invocationId":"invk-1"}}}""",
            "subscriptionId"
        },
        {
            """{"jsonrpc":"2.0","method":"hub.event","params":{"subscriptionId":"sub-1","type":"unknown.type","timeUtc":"2026-03-09T00:00:00Z","payload":{"invocationId":"invk-1"}}}""",
            "type"
        },
        {
            """{"jsonrpc":"2.0","method":"hub.event","params":{"subscriptionId":"sub-1","type":"invocation.completed","timeUtc":"0001-01-01T00:00:00+00:00","payload":{"invocationId":"invk-1"}}}""",
            "timeUtc"
        },
        {
            """{"jsonrpc":"2.0","method":"hub.event","params":{"subscriptionId":"sub-1","type":"app.definition.deleted","timeUtc":"2026-03-09T00:00:00Z","payload":{"appId":"test.app"}}}""",
            "scope"
        },
        {
            """{"jsonrpc":"2.0","method":"hub.event","params":{"subscriptionId":"sub-1","type":"app.definition.deleted","timeUtc":"2026-03-09T00:00:00Z","payload":{"appId":".test.app","scope":""}}}""",
            "appId"
        },
        {
            """{"jsonrpc":"2.0","method":"hub.event","params":{"subscriptionId":"sub-1","type":"app.definition.deleted","timeUtc":"2026-03-09T00:00:00Z","payload":{"appId":"test.app","scope":"workspace."}}}""",
            "scope"
        },
        {
            """{"jsonrpc":"2.0","method":"hub.event","params":{"subscriptionId":"sub-1","type":"app.instance.registered","timeUtc":"2026-03-09T00:00:00Z","payload":{"appId":"test.app","instanceId":"inst-1","scope":null}}}""",
            "scope"
        },
        {
            """{"jsonrpc":"2.0","method":"hub.event","params":{"subscriptionId":"sub-1","type":"app.instance.registered","timeUtc":"2026-03-09T00:00:00Z","payload":{"appId":"test.app","instanceId":"inst-1"}}}""",
            "scope"
        },
        {
            """{"jsonrpc":"2.0","method":"hub.event","params":{"subscriptionId":"sub-1","type":"app.instance.registered","timeUtc":"2026-03-09T00:00:00Z","payload":{"appId":"test.app","instanceId":"inst-1.","scope":""}}}""",
            "instanceId"
        },
        {
            """{"jsonrpc":"2.0","method":"hub.event","params":{"subscriptionId":"sub-1","type":"app.instance.registered","timeUtc":"2026-03-09T00:00:00Z","payload":{"appId":"test.app","instanceId":"inst-1","scope":"","password":"secret-1"}}}""",
            "password"
        }
    };

    public WsLifecycleTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "DevHubSdkWsTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    [Fact]
    public async Task EventsClient_BeforeAuthenticate_ShouldRejectSubscribeAndRead()
    {
        var dataDir = await CreateDataDirectoryAsync();
        var connection = new FakeWebSocketConnection();
        var factory = new FakeWebSocketConnectionFactory(connection);
        await using var client = await DevHubEventsClient.FromRuntimeAsync(
            new DevHubClientOptions
            {
                ClientId = "ws-client",
                DataDir = dataDir
            },
            factory,
            () => "ws-auth-1");

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SubscribeAsync(new[] { DevHubEventTypes.InvocationCompleted }));
        _ = await AssertReadEventsThrowsInvalidOperationAsync(client);
        Assert.Empty(connection.SentTexts);
    }

    [Fact]
    public async Task EventsClient_BeforeAuthenticate_ShouldRejectReadOnlyRpcMethodsWithoutSendingRequest()
    {
        var dataDir = await CreateDataDirectoryAsync();
        var connection = new FakeWebSocketConnection();
        var factory = new FakeWebSocketConnectionFactory(connection);
        await using var client = await DevHubEventsClient.FromRuntimeAsync(
            new DevHubClientOptions
            {
                ClientId = "ws-client",
                DataDir = dataDir
            },
            factory,
            new SequenceRequestIdFactory(
                "ws-ping-1",
                "ws-get-version-1",
                "ws-check-version-1",
                "ws-listdefs-1",
                "ws-getdef-1",
                "ws-getinst-1",
                "ws-listinst-1").Create);

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.PingAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetHostVersionAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.CheckVersionCompatibilityAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.ListDefinitionsAsync(new ListDefinitionsRequest()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetDefinitionAsync("ws.app", string.Empty));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetInstanceAsync("inst-1"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.ListInstancesAsync(new ListInstancesRequest()));
        Assert.Empty(connection.SentTexts);
    }

    [Fact]
    public async Task EventsClient_AfterAuthenticate_ShouldSubscribeAndReadEvents()
    {
        var dataDir = await CreateDataDirectoryAsync();
        var connection = new FakeWebSocketConnection();
        connection.OnSend = sent =>
        {
            if (sent.Contains("\"id\":\"ws-auth-1\"", StringComparison.Ordinal))
            {
                return
                [
                    CreateTextMessage("""{"jsonrpc":"2.0","id":"ws-auth-1","result":{"ok":true,"protocolVersion":1}}""")
                ];
            }

            if (sent.Contains("\"id\":\"ws-sub-1\"", StringComparison.Ordinal))
            {
                return
                [
                    CreateTextMessage("""{"jsonrpc":"2.0","id":"ws-sub-1","result":{"ok":true,"subscriptionId":"sub-1"}}"""),
                    CreateTextMessage("""{"jsonrpc":"2.0","method":"hub.event","params":{"subscriptionId":"sub-1","type":"invocation.completed","timeUtc":"2026-03-09T00:00:00Z","payload":{"invocationId":"invk-1"}}}"""),
                    CreateCloseMessage()
                ];
            }

            return [];
        };
        var factory = new FakeWebSocketConnectionFactory(connection);
        await using var client = await DevHubEventsClient.FromRuntimeAsync(
            new DevHubClientOptions
            {
                ClientId = "ws-client",
                DataDir = dataDir
            },
            factory,
            new SequenceRequestIdFactory("ws-auth-1", "ws-sub-1").Create);

        await client.AuthenticateAsync();
        await using var enumerator = client.ReadEventsAsync().GetAsyncEnumerator();
        var moveNextTask = enumerator.MoveNextAsync().AsTask();
        var subscriptionId = await client.SubscribeAsync(new[] { DevHubEventTypes.InvocationCompleted });
        Assert.Equal("sub-1", subscriptionId);

        Assert.True(await moveNextTask);
        Assert.Equal(DevHubEventTypes.InvocationCompleted, enumerator.Current.Type);
        Assert.Equal("sub-1", enumerator.Current.SubscriptionId);

        Assert.Collection(connection.SentTexts,
            sent => Assert.Contains("hub.ws.authenticate", sent, StringComparison.Ordinal),
            sent => Assert.Contains("hub.events.subscribe", sent, StringComparison.Ordinal));
    }

    [Fact]
    public async Task EventsClient_AfterAuthenticate_ShouldUnsubscribe()
    {
        var dataDir = await CreateDataDirectoryAsync();
        var connection = new FakeWebSocketConnection();
        connection.OnSend = sent =>
        {
            if (sent.Contains("\"id\":\"ws-auth-1\"", StringComparison.Ordinal))
            {
                return
                [
                    CreateTextMessage("""{"jsonrpc":"2.0","id":"ws-auth-1","result":{"ok":true,"protocolVersion":1}}""")
                ];
            }

            if (sent.Contains("\"id\":\"ws-unsub-1\"", StringComparison.Ordinal))
            {
                return
                [
                    CreateTextMessage("""{"jsonrpc":"2.0","id":"ws-unsub-1","result":{"ok":true}}""")
                ];
            }

            return [];
        };

        var factory = new FakeWebSocketConnectionFactory(connection);
        await using var client = await DevHubEventsClient.FromRuntimeAsync(
            new DevHubClientOptions
            {
                ClientId = "ws-client",
                DataDir = dataDir
            },
            factory,
            new SequenceRequestIdFactory("ws-auth-1", "ws-unsub-1").Create);

        await client.AuthenticateAsync();
        await client.UnsubscribeAsync("sub-1");

        Assert.Collection(connection.SentTexts,
            sent => Assert.Contains("hub.ws.authenticate", sent, StringComparison.Ordinal),
            sent =>
            {
                Assert.Contains("hub.events.unsubscribe", sent, StringComparison.Ordinal);
                Assert.Contains("\"subscriptionId\":\"sub-1\"", sent, StringComparison.Ordinal);
            });
    }

    [Fact]
    public async Task EventsClient_AfterAuthenticate_ShouldSupportWsReadableMethods()
    {
        var dataDir = await CreateDataDirectoryAsync();
        var connection = new FakeWebSocketConnection();
        connection.OnSend = sent =>
        {
            if (sent.Contains("\"id\":\"ws-auth-1\"", StringComparison.Ordinal))
            {
                return
                [
                    CreateTextMessage("""{"jsonrpc":"2.0","id":"ws-auth-1","result":{"ok":true,"protocolVersion":1}}""")
                ];
            }

            if (sent.Contains("\"id\":\"ws-ping-1\"", StringComparison.Ordinal))
            {
                return
                [
                    CreateTextMessage("""{"jsonrpc":"2.0","id":"ws-ping-1","result":{"ok":true,"serverTimeUtc":"2026-03-09T00:00:00Z","echo":{"value":1}}}""")
                ];
            }

            if (sent.Contains("\"id\":\"ws-get-version-1\"", StringComparison.Ordinal) ||
                sent.Contains("\"id\":\"ws-check-version-1\"", StringComparison.Ordinal))
            {
                var responseId = sent.Contains("\"id\":\"ws-get-version-1\"", StringComparison.Ordinal)
                    ? "ws-get-version-1"
                    : "ws-check-version-1";
                return
                [
                    CreateTextMessage(
                        $"{{\"jsonrpc\":\"2.0\",\"id\":\"{responseId}\",\"result\":{{\"ok\":true,\"version\":\"{CreateHostVersionWithPatchDelta(3)}\"}}}}")
                ];
            }

            if (sent.Contains("\"id\":\"ws-listdefs-1\"", StringComparison.Ordinal))
            {
                return
                [
                    CreateTextMessage("""{"jsonrpc":"2.0","id":"ws-listdefs-1","result":{"ok":true,"definitions":[{"appId":"ws.app","scope":"","displayName":"WS App"}]}}""")
                ];
            }

            if (sent.Contains("\"id\":\"ws-getdef-1\"", StringComparison.Ordinal))
            {
                return
                [
                    CreateTextMessage("""{"jsonrpc":"2.0","id":"ws-getdef-1","result":{"ok":true,"definition":{"appId":"ws.app","scope":"","displayName":"WS App"}}}""")
                ];
            }

            if (sent.Contains("\"id\":\"ws-getinst-1\"", StringComparison.Ordinal))
            {
                return
                [
                    CreateTextMessage("""{"jsonrpc":"2.0","id":"ws-getinst-1","result":{"ok":true,"instance":{"instanceId":"inst-1","appId":"ws.app","scope":"","pid":12345,"registeredAtUtc":"2026-03-09T00:00:00Z","lastSeenUtc":"2026-03-09T00:00:01Z","invoke":{"poll":true,"respond":true}}}}""")
                ];
            }

            if (sent.Contains("\"id\":\"ws-listinst-1\"", StringComparison.Ordinal))
            {
                return
                [
                    CreateTextMessage("""{"jsonrpc":"2.0","id":"ws-listinst-1","result":{"ok":true,"instances":[{"instanceId":"inst-1","appId":"ws.app","scope":"","pid":12345,"registeredAtUtc":"2026-03-09T00:00:00Z","lastSeenUtc":"2026-03-09T00:00:01Z","invoke":{"poll":true,"respond":true}}]}}""")
                ];
            }

            return [];
        };

        var factory = new FakeWebSocketConnectionFactory(connection);
        await using var client = await DevHubEventsClient.FromRuntimeAsync(
            new DevHubClientOptions
            {
                ClientId = "ws-client",
                DataDir = dataDir
            },
            factory,
            new SequenceRequestIdFactory(
                "ws-auth-1",
                "ws-ping-1",
                "ws-get-version-1",
                "ws-check-version-1",
                "ws-listdefs-1",
                "ws-getdef-1",
                "ws-getinst-1",
                "ws-listinst-1").Create);

        await client.AuthenticateAsync();

        var ping = await client.PingAsync(new { value = 1 });
        var hostVersion = await client.GetHostVersionAsync();
        var compatibility = await client.CheckVersionCompatibilityAsync();
        var definitions = await client.ListDefinitionsAsync(new ListDefinitionsRequest());
        var definition = await client.GetDefinitionAsync("ws.app", string.Empty);
        var instance = await client.GetInstanceAsync("inst-1");
        var instances = await client.ListInstancesAsync(new ListInstancesRequest());

        Assert.True(ping.Ok);
        Assert.Equal(CreateHostVersionWithPatchDelta(3), hostVersion);
        Assert.Equal(CreateHostVersionWithPatchDelta(3), compatibility.HostVersion);
        Assert.Equal(SdkVersionSource.CurrentVersion, compatibility.SdkVersion);
        Assert.Equal(VersionCompatibilityStatus.Compatible, compatibility.Status);
        Assert.Equal("ws.app", definitions.Single().AppId);
        Assert.Equal("ws.app", definition.AppId);
        Assert.Equal(string.Empty, definition.Scope);
        Assert.Equal("inst-1", instance.InstanceId);
        Assert.DoesNotContain("instanceSessionToken", JsonSerializer.Serialize(instance));
        Assert.Equal("inst-1", instances.Single().InstanceId);

        var listDefinitionsRequest = connection.SentTexts.Single(sent => sent.Contains("hub.apps.listDefinitions", StringComparison.Ordinal));
        Assert.Contains("\"scope\":null", listDefinitionsRequest, StringComparison.Ordinal);
        Assert.DoesNotContain("includeAllScopes", listDefinitionsRequest, StringComparison.Ordinal);

        Assert.Collection(connection.SentTexts,
            sent => Assert.Contains("hub.ws.authenticate", sent, StringComparison.Ordinal),
            sent => Assert.Contains("hub.ping", sent, StringComparison.Ordinal),
            sent => Assert.Contains("hub.getVersion", sent, StringComparison.Ordinal),
            sent => Assert.Contains("hub.getVersion", sent, StringComparison.Ordinal),
            sent => Assert.Contains("hub.apps.listDefinitions", sent, StringComparison.Ordinal),
            sent =>
            {
                Assert.Contains("hub.apps.getDefinition", sent, StringComparison.Ordinal);
                Assert.Contains("\"scope\":\"\"", sent, StringComparison.Ordinal);
            },
            sent =>
            {
                Assert.Contains("hub.apps.getInstance", sent, StringComparison.Ordinal);
                Assert.Contains("\"instanceId\":\"inst-1\"", sent, StringComparison.Ordinal);
            },
            sent =>
            {
                Assert.Contains("hub.apps.listInstances", sent, StringComparison.Ordinal);
                Assert.Contains("\"scope\":null", sent, StringComparison.Ordinal);
            });
    }

    [Fact]
    public async Task EventsClient_ShouldCountAndClearAbandonedRequestsByFilter()
    {
        var dataDir = await CreateDataDirectoryAsync();
        var connection = new FakeWebSocketConnection();
        connection.OnSend = sent =>
        {
            return sent.Contains("\"id\":\"ws-auth-1\"", StringComparison.Ordinal)
                ? [CreateTextMessage("""{"jsonrpc":"2.0","id":"ws-auth-1","result":{"ok":true,"protocolVersion":1}}""")]
                : [];
        };

        var factory = new FakeWebSocketConnectionFactory(connection);
        await using var client = await DevHubEventsClient.FromRuntimeAsync(
            new DevHubClientOptions
            {
                ClientId = "ws-client",
                DataDir = dataDir,
                RequestTimeout = TimeSpan.FromMilliseconds(20)
            },
            factory,
            new SequenceRequestIdFactory("ws-auth-1", "ws-listdefs-1", "ws-getdef-1", "ws-getinst-1").Create);

        await client.AuthenticateAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.ListDefinitionsAsync(new ListDefinitionsRequest
        {
            AppId = "app-a",
            Scope = null
        }));

        await Task.Delay(70);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetDefinitionAsync("app-b", string.Empty));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetInstanceAsync("inst-1"));

        Assert.Equal(3, client.GetAbandonedRequestCount());
        Assert.Equal(1, client.GetAbandonedRequestCount(new AbandonedRequestFilter { AppId = "app-a" }));
        Assert.Equal(1, client.GetAbandonedRequestCount(new AbandonedRequestFilter { AppId = "app-b" }));
        Assert.Equal(1, client.GetAbandonedRequestCount(new AbandonedRequestFilter { Method = "hub.apps.getInstance" }));

        Assert.Equal(1, client.ClearAbandonedRequests(new AbandonedRequestFilter
        {
            OlderThan = TimeSpan.FromMilliseconds(60)
        }));
        Assert.Equal(2, client.GetAbandonedRequestCount());
        Assert.Equal(1, client.ClearAbandonedRequests(new AbandonedRequestFilter { AppId = "app-b" }));
        Assert.Equal(1, client.ClearAbandonedRequests(new AbandonedRequestFilter { Method = "hub.apps.getInstance" }));
        Assert.Equal(0, client.GetAbandonedRequestCount());
    }

    [Fact]
    public async Task JsonRpcWebSocketSession_WhenLateResponseMatchesTrackedAbandonedRequest_ShouldIgnoreItAndKeepSessionUsable()
    {
        var connection = new FakeWebSocketConnection();
        var termination = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var session = new JsonRpcWebSocketSession(
            new DevHubWebSocketSessionOptions
            {
                WebSocketEndpoint = new Uri("ws://127.0.0.1:47231/ws"),
                RequestTimeout = TimeSpan.FromMilliseconds(20),
                OnEvent = _ => { },
                OnTerminated = error => termination.TrySetResult(error)
            },
            new FakeWebSocketConnectionFactory(connection),
            new SequenceRequestIdFactory("ws-timeout-1", "ws-ping-2").Create);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.SendRequestAsync(
            "hub.apps.listDefinitions",
            new Dictionary<string, object?>
            {
                ["appId"] = "app-a",
                ["scope"] = null
            }));

        Assert.Equal(1, session.GetAbandonedRequestCount());

        connection.Enqueue(CreateTextMessage("""{"jsonrpc":"2.0","id":"ws-timeout-1","result":{"ok":true,"definitions":[]}}"""));
        await Task.Delay(50);

        Assert.Equal(1, session.GetAbandonedRequestCount());
        Assert.False(termination.Task.IsCompleted);

        var secondRequest = session.SendRequestAsync("hub.ping", null);
        await WaitUntilAsync(() => connection.SentTexts.Count >= 2);
        connection.Enqueue(CreateTextMessage("""{"jsonrpc":"2.0","id":"ws-ping-2","result":{"ok":true,"serverTimeUtc":"2026-03-09T00:00:00Z"}}"""));

        var result = await secondRequest;
        Assert.True(result.GetProperty("ok").GetBoolean());
        Assert.False(termination.Task.IsCompleted);
    }

    [Fact]
    public async Task JsonRpcWebSocketSession_WhenLateResponseMatchesClearedAbandonedRequest_ShouldTerminate()
    {
        var connection = new FakeWebSocketConnection();
        var termination = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var session = new JsonRpcWebSocketSession(
            new DevHubWebSocketSessionOptions
            {
                WebSocketEndpoint = new Uri("ws://127.0.0.1:47231/ws"),
                RequestTimeout = TimeSpan.FromMilliseconds(20),
                OnEvent = _ => { },
                OnTerminated = error => termination.TrySetResult(error)
            },
            new FakeWebSocketConnectionFactory(connection),
            new SequenceRequestIdFactory("ws-timeout-1").Create);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.SendRequestAsync(
            "hub.apps.getDefinition",
            new Dictionary<string, object?>
            {
                ["appId"] = "app-a",
                ["scope"] = string.Empty
            }));

        Assert.Equal(1, session.ClearAbandonedRequests());

        connection.Enqueue(CreateTextMessage("""{"jsonrpc":"2.0","id":"ws-timeout-1","result":{"ok":true,"definition":{"appId":"app-a","scope":"","displayName":"App A"}}}"""));

        var terminalException = await termination.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.NotNull(terminalException);
        Assert.Contains("响应 id 未匹配任何挂起请求", terminalException!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EventsClient_AfterAuthenticate_WhenGetInstanceInstanceIdInvalid_ShouldThrowArgumentExceptionWithoutSendingRequest()
    {
        var dataDir = await CreateDataDirectoryAsync();
        var connection = new FakeWebSocketConnection();
        connection.OnSend = sent =>
        {
            return sent.Contains("\"id\":\"ws-auth-1\"", StringComparison.Ordinal)
                ? [CreateTextMessage("""{"jsonrpc":"2.0","id":"ws-auth-1","result":{"ok":true,"protocolVersion":1}}""")]
                : [];
        };

        var factory = new FakeWebSocketConnectionFactory(connection);
        await using var client = await DevHubEventsClient.FromRuntimeAsync(
            new DevHubClientOptions
            {
                ClientId = "ws-client",
                DataDir = dataDir
            },
            factory,
            new SequenceRequestIdFactory("ws-auth-1", "ws-getinst-1").Create);

        await client.AuthenticateAsync();
        var sentCountAfterAuthenticate = connection.SentTexts.Count;

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => client.GetInstanceAsync("inst/1"));

        Assert.Equal("instanceId", exception.ParamName);
        Assert.Equal(sentCountAfterAuthenticate, connection.SentTexts.Count);
    }

    [Fact]
    public async Task EventsClient_WhenSecondReaderStartsWhileFirstActive_ShouldRejectConcurrentRead()
    {
        var dataDir = await CreateDataDirectoryAsync();
        var connection = new FakeWebSocketConnection();
        connection.OnSend = sent =>
        {
            return sent.Contains("\"id\":\"ws-auth-1\"", StringComparison.Ordinal)
                ? [CreateTextMessage("""{"jsonrpc":"2.0","id":"ws-auth-1","result":{"ok":true,"protocolVersion":1}}""")]
                : [];
        };

        var factory = new FakeWebSocketConnectionFactory(connection);
        await using var client = await DevHubEventsClient.FromRuntimeAsync(
            new DevHubClientOptions
            {
                ClientId = "ws-client",
                DataDir = dataDir
            },
            factory,
            () => "ws-auth-1");

        await client.AuthenticateAsync();

        using var firstReaderCts = new CancellationTokenSource();
        await using var firstEnumerator = client.ReadEventsAsync(firstReaderCts.Token).GetAsyncEnumerator();
        var firstMoveNextTask = firstEnumerator.MoveNextAsync().AsTask();
        await Task.Yield();

        await using var secondEnumerator = client.ReadEventsAsync().GetAsyncEnumerator();
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => secondEnumerator.MoveNextAsync().AsTask());
        Assert.Contains("ReadEventsAsync", exception.Message, StringComparison.Ordinal);

        firstReaderCts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => firstMoveNextTask);
    }

    [Fact]
    public async Task EventsClient_WhenConnectionClosesAfterQueuedEvent_ShouldStillReadBufferedEvents()
    {
        var dataDir = await CreateDataDirectoryAsync();
        var connection = new FakeWebSocketConnection();
        connection.OnSend = sent =>
        {
            if (sent.Contains("\"id\":\"ws-auth-1\"", StringComparison.Ordinal))
            {
                return
                [
                    CreateTextMessage("""{"jsonrpc":"2.0","id":"ws-auth-1","result":{"ok":true,"protocolVersion":1}}""")
                ];
            }

            if (sent.Contains("\"id\":\"ws-sub-1\"", StringComparison.Ordinal))
            {
                return
                [
                    CreateTextMessage("""{"jsonrpc":"2.0","id":"ws-sub-1","result":{"ok":true,"subscriptionId":"sub-1"}}"""),
                    CreateTextMessage("""{"jsonrpc":"2.0","method":"hub.event","params":{"subscriptionId":"sub-1","type":"invocation.completed","timeUtc":"2026-03-09T00:00:00Z","payload":{"invocationId":"invk-1"}}}"""),
                    CreateCloseMessage()
                ];
            }

            return [];
        };

        var factory = new FakeWebSocketConnectionFactory(connection);
        await using var client = await DevHubEventsClient.FromRuntimeAsync(
            new DevHubClientOptions
            {
                ClientId = "ws-client",
                DataDir = dataDir
            },
            factory,
            new SequenceRequestIdFactory("ws-auth-1", "ws-sub-1").Create);

        await client.AuthenticateAsync();
        await using var enumerator = client.ReadEventsAsync().GetAsyncEnumerator();
        var moveNextTask = enumerator.MoveNextAsync().AsTask();
        _ = await client.SubscribeAsync(new[] { DevHubEventTypes.InvocationCompleted });
        await connection.WaitForCloseObservedAsync();

        Assert.True(await moveNextTask);
        Assert.Equal(DevHubEventTypes.InvocationCompleted, enumerator.Current.Type);
        Assert.Equal("sub-1", enumerator.Current.SubscriptionId);
        Assert.False(await enumerator.MoveNextAsync());
    }

    [Fact]
    public async Task EventsClient_WhenConnectionTerminates_ShouldAllowAuthenticateAgainAndRequireResubscribe()
    {
        var dataDir = await CreateDataDirectoryAsync();

        var firstConnection = new FakeWebSocketConnection();
        firstConnection.OnSend = sent =>
        {
            if (sent.Contains("\"id\":\"ws-auth-1\"", StringComparison.Ordinal))
            {
                return
                [
                    CreateTextMessage("""{"jsonrpc":"2.0","id":"ws-auth-1","result":{"ok":true,"protocolVersion":1}}""")
                ];
            }

            if (sent.Contains("\"id\":\"ws-sub-1\"", StringComparison.Ordinal))
            {
                return
                [
                    CreateTextMessage("""{"jsonrpc":"2.0","id":"ws-sub-1","result":{"ok":true,"subscriptionId":"sub-1"}}"""),
                    CreateTextMessage("""{"jsonrpc":"2.0","method":"hub.event","params":{"subscriptionId":"sub-1","type":"invocation.completed","timeUtc":"2026-03-09T00:00:00Z","payload":{"invocationId":"invk-1"}}}"""),
                    CreateCloseMessage()
                ];
            }

            return [];
        };

        var secondConnection = new FakeWebSocketConnection();
        secondConnection.OnSend = sent =>
        {
            if (sent.Contains("\"id\":\"ws-auth-2\"", StringComparison.Ordinal))
            {
                return
                [
                    CreateTextMessage("""{"jsonrpc":"2.0","id":"ws-auth-2","result":{"ok":true,"protocolVersion":1}}""")
                ];
            }

            if (sent.Contains("\"id\":\"ws-sub-2\"", StringComparison.Ordinal))
            {
                return
                [
                    CreateTextMessage("""{"jsonrpc":"2.0","id":"ws-sub-2","result":{"ok":true,"subscriptionId":"sub-2"}}"""),
                    CreateTextMessage("""{"jsonrpc":"2.0","method":"hub.event","params":{"subscriptionId":"sub-2","type":"invocation.completed","timeUtc":"2026-03-09T00:00:01Z","payload":{"invocationId":"invk-2"}}}"""),
                    CreateCloseMessage()
                ];
            }

            return [];
        };

        var factory = new SequenceWebSocketConnectionFactory(firstConnection, secondConnection);
        await using var client = await DevHubEventsClient.FromRuntimeAsync(
            new DevHubClientOptions
            {
                ClientId = "ws-client",
                DataDir = dataDir
            },
            factory,
            new SequenceRequestIdFactory("ws-auth-1", "ws-sub-1", "ws-auth-2", "ws-sub-2").Create);

        await client.AuthenticateAsync();
        await using (var firstEnumerator = client.ReadEventsAsync().GetAsyncEnumerator())
        {
            var firstMoveNextTask = firstEnumerator.MoveNextAsync().AsTask();
            _ = await client.SubscribeAsync(new[] { DevHubEventTypes.InvocationCompleted });
            await firstConnection.WaitForCloseObservedAsync();

            Assert.True(await firstMoveNextTask);
            Assert.Equal("invk-1", firstEnumerator.Current.Payload!.Value.GetProperty("invocationId").GetString());
            Assert.False(await firstEnumerator.MoveNextAsync());
        }

        _ = await AssertReadEventsThrowsInvalidOperationAsync(client);

        await client.AuthenticateAsync();
        await using (var secondEnumerator = client.ReadEventsAsync().GetAsyncEnumerator())
        {
            var secondMoveNextTask = secondEnumerator.MoveNextAsync().AsTask();
            _ = await client.SubscribeAsync(new[] { DevHubEventTypes.InvocationCompleted });

            Assert.True(await secondMoveNextTask);
            Assert.Equal("invk-2", secondEnumerator.Current.Payload!.Value.GetProperty("invocationId").GetString());
            Assert.Equal(DevHubEventTypes.InvocationCompleted, secondEnumerator.Current.Type);
        }

        Assert.Collection(firstConnection.SentTexts,
            sent => Assert.Contains("hub.ws.authenticate", sent, StringComparison.Ordinal),
            sent => Assert.Contains("hub.events.subscribe", sent, StringComparison.Ordinal));

        Assert.Collection(secondConnection.SentTexts,
            sent => Assert.Contains("hub.ws.authenticate", sent, StringComparison.Ordinal),
            sent => Assert.Contains("hub.events.subscribe", sent, StringComparison.Ordinal));
    }

    [Fact]
    public async Task EventsClient_WhenAuthenticateFails_ShouldThrowDevHubRpcException()
    {
        var dataDir = await CreateDataDirectoryAsync();
        var connection = new FakeWebSocketConnection();
        connection.OnSend = sent =>
        {
            return sent.Contains("\"id\":\"ws-auth-1\"", StringComparison.Ordinal)
                ? [CreateTextMessage("""{"jsonrpc":"2.0","id":"ws-auth-1","error":{"code":-32001,"message":"unauthorized","data":{"reason":"invalid_token"}}}""")]
                : [];
        };
        var factory = new FakeWebSocketConnectionFactory(connection);
        await using var client = await DevHubEventsClient.FromRuntimeAsync(
            new DevHubClientOptions
            {
                ClientId = "ws-client",
                DataDir = dataDir
            },
            factory,
            () => "ws-auth-1");

        var exception = await Assert.ThrowsAsync<DevHubRpcException>(() => client.AuthenticateAsync());
        Assert.Equal(-32001, exception.Code);
        Assert.Equal("unauthorized", exception.Message);
        Assert.Equal("invalid_token", exception.ErrorData!.Value.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task EventsClient_WhenInstanceLifecycleEventIncludesScope_ShouldAcceptPayload()
    {
        var dataDir = await CreateDataDirectoryAsync();
        var connection = new FakeWebSocketConnection();
        connection.OnSend = sent =>
        {
            if (sent.Contains("\"id\":\"ws-auth-1\"", StringComparison.Ordinal))
            {
                return
                [
                    CreateTextMessage("""{"jsonrpc":"2.0","id":"ws-auth-1","result":{"ok":true,"protocolVersion":1}}""")
                ];
            }

            if (sent.Contains("\"id\":\"ws-sub-1\"", StringComparison.Ordinal))
            {
                return
                [
                    CreateTextMessage("""{"jsonrpc":"2.0","id":"ws-sub-1","result":{"ok":true,"subscriptionId":"sub-1"}}"""),
                    CreateTextMessage("""{"jsonrpc":"2.0","method":"hub.event","params":{"subscriptionId":"sub-1","type":"app.instance.registered","timeUtc":"2026-03-09T00:00:00Z","payload":{"appId":"test.app","instanceId":"inst-1","scope":""}}}"""),
                    CreateCloseMessage()
                ];
            }

            return [];
        };

        var factory = new FakeWebSocketConnectionFactory(connection);
        await using var client = await DevHubEventsClient.FromRuntimeAsync(
            new DevHubClientOptions
            {
                ClientId = "ws-client",
                DataDir = dataDir
            },
            factory,
            new SequenceRequestIdFactory("ws-auth-1", "ws-sub-1").Create);

        await client.AuthenticateAsync();
        await using var enumerator = client.ReadEventsAsync().GetAsyncEnumerator();
        var moveNextTask = enumerator.MoveNextAsync().AsTask();
        _ = await client.SubscribeAsync(new[] { DevHubEventTypes.AppInstanceRegistered });

        Assert.True(await moveNextTask);
        Assert.Equal(DevHubEventTypes.AppInstanceRegistered, enumerator.Current.Type);
        Assert.Equal(string.Empty, enumerator.Current.Payload!.Value.GetProperty("scope").GetString());
    }

    [Fact]
    public async Task EventsClient_WhenAuthenticateTimesOut_ShouldThrowOperationCanceledException()
    {
        var dataDir = await CreateDataDirectoryAsync();
        var factory = new FakeWebSocketConnectionFactory(new FakeWebSocketConnection());
        await using var client = await DevHubEventsClient.FromRuntimeAsync(
            new DevHubClientOptions
            {
                ClientId = "ws-client",
                DataDir = dataDir,
                RequestTimeout = TimeSpan.FromMilliseconds(50)
            },
            factory,
            () => "ws-auth-1");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.AuthenticateAsync());
    }

    [Fact]
    public async Task EventsClient_WhenAuthenticateCalledConcurrently_ShouldSerializeAuthenticateRequestAndLogLifecycle()
    {
        var dataDir = await CreateDataDirectoryAsync();
        var loggerFactory = new RecordingLoggerFactory();
        var connection = new FakeWebSocketConnection();
        connection.OnSend = sent =>
        {
            return sent.Contains("\"id\":\"ws-auth-1\"", StringComparison.Ordinal)
                ? []
                : [];
        };

        var factory = new FakeWebSocketConnectionFactory(connection);
        await using var client = await DevHubEventsClient.FromRuntimeAsync(
            new DevHubClientOptions
            {
                ClientId = "ws-client",
                DataDir = dataDir
            },
            new DevHubEventsClientDependencies
            {
                LoggerFactory = loggerFactory
            },
            factory,
            new SequenceRequestIdFactory("ws-auth-1", "ws-auth-2").Create,
            CancellationToken.None);

        var firstAuthenticateTask = client.AuthenticateAsync();
        await WaitUntilAsync(() => connection.SentTexts.Count == 1);

        var secondAuthenticateTask = client.AuthenticateAsync();
        await Task.Delay(50);
        Assert.False(secondAuthenticateTask.IsCompleted);

        connection.Enqueue(CreateTextMessage("""{"jsonrpc":"2.0","id":"ws-auth-1","result":{"ok":true,"protocolVersion":1}}"""));

        await firstAuthenticateTask;
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => secondAuthenticateTask);

        Assert.Contains("已完成认证", exception.Message, StringComparison.Ordinal);
        Assert.Single(connection.SentTexts, sent => sent.Contains("hub.ws.authenticate", StringComparison.Ordinal));
        Assert.Contains(loggerFactory.Entries, entry => entry.Message.Contains("Authenticating DevHub WebSocket session", StringComparison.Ordinal));
        Assert.Contains(loggerFactory.Entries, entry => entry.Message.Contains("Authenticated DevHub WebSocket session", StringComparison.Ordinal));
        Assert.DoesNotContain(loggerFactory.Entries, entry => entry.Message.Contains("token-1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EventsClient_WhenAuthenticateReturnsInvalidSuccessPayload_ShouldThrowInvalidOperationException()
    {
        var dataDir = await CreateDataDirectoryAsync();
        var connection = new FakeWebSocketConnection();
        connection.OnSend = sent =>
        {
            return sent.Contains("\"id\":\"ws-auth-1\"", StringComparison.Ordinal)
                ? [CreateTextMessage("""{"jsonrpc":"2.0","id":"ws-auth-1","result":{"ok":false,"protocolVersion":1}}""")]
                : [];
        };

        var factory = new FakeWebSocketConnectionFactory(connection);
        await using var client = await DevHubEventsClient.FromRuntimeAsync(
            new DevHubClientOptions
            {
                ClientId = "ws-client",
                DataDir = dataDir
            },
            factory,
            () => "ws-auth-1");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => client.AuthenticateAsync());
        Assert.Contains("hub.ws.authenticate", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EventsClient_WhenAuthenticateResponseJsonRpcVersionInvalid_ShouldThrowInvalidOperationException()
    {
        var dataDir = await CreateDataDirectoryAsync();
        var connection = new FakeWebSocketConnection();
        connection.OnSend = sent =>
        {
            return sent.Contains("\"id\":\"ws-auth-1\"", StringComparison.Ordinal)
                ? [CreateTextMessage("""{"jsonrpc":"1.0","id":"ws-auth-1","result":{"ok":true,"protocolVersion":1}}""")]
                : [];
        };

        var factory = new FakeWebSocketConnectionFactory(connection);
        await using var client = await DevHubEventsClient.FromRuntimeAsync(
            new DevHubClientOptions
            {
                ClientId = "ws-client",
                DataDir = dataDir
            },
            factory,
            () => "ws-auth-1");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => client.AuthenticateAsync());
        Assert.Contains("jsonrpc", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("1.5")]
    [InlineData("9223372036854775808")]
    public async Task EventsClient_WhenAuthenticateResponseIdUsesUnsupportedNumericShape_ShouldThrowInvalidOperationException(string requestIdLiteral)
    {
        var dataDir = await CreateDataDirectoryAsync();
        var connection = new FakeWebSocketConnection();
        connection.OnSend = sent =>
        {
            return sent.Contains($"\"id\":\"{requestIdLiteral}\"", StringComparison.Ordinal)
                ? [CreateTextMessage($"{{\"jsonrpc\":\"2.0\",\"id\":{requestIdLiteral},\"result\":{{\"ok\":true,\"protocolVersion\":1}}}}")]
                : [];
        };

        var factory = new FakeWebSocketConnectionFactory(connection);
        await using var client = await DevHubEventsClient.FromRuntimeAsync(
            new DevHubClientOptions
            {
                ClientId = "ws-client",
                DataDir = dataDir
            },
            factory,
            () => requestIdLiteral);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => client.AuthenticateAsync());
        Assert.Contains("id", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Int64", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EventsClient_WhenAuthenticateResponseMissingResultAndError_ShouldThrowInvalidOperationException()
    {
        var dataDir = await CreateDataDirectoryAsync();
        var connection = new FakeWebSocketConnection();
        connection.OnSend = sent =>
        {
            return sent.Contains("\"id\":\"ws-auth-1\"", StringComparison.Ordinal)
                ? [CreateTextMessage("""{"jsonrpc":"2.0","id":"ws-auth-1"}""")]
                : [];
        };

        var factory = new FakeWebSocketConnectionFactory(connection);
        await using var client = await DevHubEventsClient.FromRuntimeAsync(
            new DevHubClientOptions
            {
                ClientId = "ws-client",
                DataDir = dataDir
            },
            factory,
            () => "ws-auth-1");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => client.AuthenticateAsync());
        Assert.Contains("响应", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EventsClient_WhenSubscribeTimesOut_ShouldDisconnectAndRequireReauthentication()
    {
        var dataDir = await CreateDataDirectoryAsync();
        var loggerFactory = new RecordingLoggerFactory();

        var firstConnection = new FakeWebSocketConnection();
        firstConnection.OnSend = sent =>
        {
            if (sent.Contains("\"id\":\"ws-auth-1\"", StringComparison.Ordinal))
            {
                return [CreateTextMessage("""{"jsonrpc":"2.0","id":"ws-auth-1","result":{"ok":true,"protocolVersion":1}}""")];
            }

            return [];
        };

        var secondConnection = new FakeWebSocketConnection();
        secondConnection.OnSend = sent =>
        {
            if (sent.Contains("\"id\":\"ws-auth-2\"", StringComparison.Ordinal))
            {
                return [CreateTextMessage("""{"jsonrpc":"2.0","id":"ws-auth-2","result":{"ok":true,"protocolVersion":1}}""")];
            }

            if (sent.Contains("\"id\":\"ws-sub-2\"", StringComparison.Ordinal))
            {
                return [CreateTextMessage("""{"jsonrpc":"2.0","id":"ws-sub-2","result":{"ok":true,"subscriptionId":"sub-2"}}""")];
            }

            return [];
        };

        var factory = new SequenceWebSocketConnectionFactory(firstConnection, secondConnection);
        await using var client = await DevHubEventsClient.FromRuntimeAsync(
            new DevHubClientOptions
            {
                ClientId = "ws-client",
                DataDir = dataDir,
                RequestTimeout = TimeSpan.FromMilliseconds(50)
            },
            new DevHubEventsClientDependencies
            {
                LoggerFactory = loggerFactory
            },
            factory,
            new SequenceRequestIdFactory("ws-auth-1", "ws-sub-timeout-1", "ws-auth-2", "ws-sub-2").Create,
            CancellationToken.None);

        await client.AuthenticateAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.SubscribeAsync(new[] { DevHubEventTypes.InvocationCompleted }));
        Assert.True(firstConnection.CloseCallCount >= 1);
        Assert.Contains("subscription_state_ambiguous", firstConnection.CloseStatusDescriptions);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SubscribeAsync(new[] { DevHubEventTypes.InvocationCompleted }));
        _ = await AssertReadEventsThrowsInvalidOperationAsync(client);

        await client.AuthenticateAsync();
        var subscriptionId = await client.SubscribeAsync(new[] { DevHubEventTypes.InvocationCompleted });

        Assert.Equal("sub-2", subscriptionId);
        Assert.Contains(loggerFactory.Entries, entry => entry.Message.Contains("subscription lifecycle request became ambiguous", StringComparison.Ordinal));
        Assert.DoesNotContain(loggerFactory.Entries, entry => entry.Message.Contains("token-1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EventsClient_WhenUnsubscribeTimesOut_ShouldDisconnectAndRequireReauthentication()
    {
        var dataDir = await CreateDataDirectoryAsync();

        var firstConnection = new FakeWebSocketConnection();
        firstConnection.OnSend = sent =>
        {
            if (sent.Contains("\"id\":\"ws-auth-1\"", StringComparison.Ordinal))
            {
                return [CreateTextMessage("""{"jsonrpc":"2.0","id":"ws-auth-1","result":{"ok":true,"protocolVersion":1}}""")];
            }

            if (sent.Contains("\"id\":\"ws-sub-1\"", StringComparison.Ordinal))
            {
                return [CreateTextMessage("""{"jsonrpc":"2.0","id":"ws-sub-1","result":{"ok":true,"subscriptionId":"sub-1"}}""")];
            }

            return [];
        };

        var secondConnection = new FakeWebSocketConnection();
        secondConnection.OnSend = sent =>
        {
            if (sent.Contains("\"id\":\"ws-auth-2\"", StringComparison.Ordinal))
            {
                return [CreateTextMessage("""{"jsonrpc":"2.0","id":"ws-auth-2","result":{"ok":true,"protocolVersion":1}}""")];
            }

            if (sent.Contains("\"id\":\"ws-sub-2\"", StringComparison.Ordinal))
            {
                return [CreateTextMessage("""{"jsonrpc":"2.0","id":"ws-sub-2","result":{"ok":true,"subscriptionId":"sub-2"}}""")];
            }

            return [];
        };

        var factory = new SequenceWebSocketConnectionFactory(firstConnection, secondConnection);
        await using var client = await DevHubEventsClient.FromRuntimeAsync(
            new DevHubClientOptions
            {
                ClientId = "ws-client",
                DataDir = dataDir,
                RequestTimeout = TimeSpan.FromMilliseconds(50)
            },
            new DevHubEventsClientDependencies(),
            factory,
            new SequenceRequestIdFactory("ws-auth-1", "ws-sub-1", "ws-unsub-timeout-1", "ws-auth-2", "ws-sub-2").Create,
            CancellationToken.None);

        await client.AuthenticateAsync();
        _ = await client.SubscribeAsync(new[] { DevHubEventTypes.InvocationCompleted });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.UnsubscribeAsync("sub-1"));
        Assert.True(firstConnection.CloseCallCount >= 1);
        Assert.Contains("subscription_state_ambiguous", firstConnection.CloseStatusDescriptions);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.UnsubscribeAsync("sub-1"));

        await client.AuthenticateAsync();
        var subscriptionId = await client.SubscribeAsync(new[] { DevHubEventTypes.InvocationCompleted });
        Assert.Equal("sub-2", subscriptionId);
    }

    [Fact]
    public async Task EventsClient_WhenEventBufferOverflows_ShouldTerminateCurrentStreamAndLogFailure()
    {
        var dataDir = await CreateDataDirectoryAsync();
        var loggerFactory = new RecordingLoggerFactory();
        var overflowLogTask = loggerFactory.WaitForEntryAsync(
            entry => entry.Message.Contains("event buffer overflowed", StringComparison.Ordinal));
        var connection = new FakeWebSocketConnection();
        connection.OnSend = sent =>
        {
            if (sent.Contains("\"id\":\"ws-auth-1\"", StringComparison.Ordinal))
            {
                return [CreateTextMessage("""{"jsonrpc":"2.0","id":"ws-auth-1","result":{"ok":true,"protocolVersion":1}}""")];
            }

            if (sent.Contains("\"id\":\"ws-sub-1\"", StringComparison.Ordinal))
            {
                return [CreateTextMessage("""{"jsonrpc":"2.0","id":"ws-sub-1","result":{"ok":true,"subscriptionId":"sub-1"}}""")];
            }

            return [];
        };

        var factory = new FakeWebSocketConnectionFactory(connection);
        await using var client = await DevHubEventsClient.FromRuntimeAsync(
            new DevHubClientOptions
            {
                ClientId = "ws-client",
                DataDir = dataDir
            },
            new DevHubEventsClientDependencies
            {
                LoggerFactory = loggerFactory
            },
            factory,
            new SequenceRequestIdFactory("ws-auth-1", "ws-sub-1").Create,
            CancellationToken.None);

        await client.AuthenticateAsync();
        _ = await client.SubscribeAsync(new[] { DevHubEventTypes.InvocationCompleted });

        for (var index = 0; index <= DevHubEventsClient.DefaultEventBufferCapacity; index++)
        {
            connection.Enqueue(CreateTextMessage(
                "{\"jsonrpc\":\"2.0\",\"method\":\"hub.event\",\"params\":{\"subscriptionId\":\"sub-1\",\"type\":\"invocation.completed\",\"timeUtc\":\"2026-03-09T00:00:00Z\",\"payload\":{\"invocationId\":\"invk-"
                + index
                + "\"}}}"));
        }

        await WaitUntilAsync(() => connection.CloseCallCount == 1);
        await overflowLogTask;

        var exception = await AssertReadEventsThrowsInvalidOperationAsync(client);
        Assert.Contains("事件流不可用", exception.Message, StringComparison.Ordinal);
        Assert.Contains(loggerFactory.Entries, entry => entry.Message.Contains("event buffer overflowed", StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(InvalidEventNotifications))]
    public async Task EventsClient_WhenEventPayloadViolatesSpec_ShouldFaultEventStream(
        string notificationJson,
        string expectedMessage)
    {
        var dataDir = await CreateDataDirectoryAsync();
        var connection = new FakeWebSocketConnection();
        connection.OnSend = sent =>
        {
            if (sent.Contains("\"id\":\"ws-auth-1\"", StringComparison.Ordinal))
            {
                return
                [
                    CreateTextMessage("""{"jsonrpc":"2.0","id":"ws-auth-1","result":{"ok":true,"protocolVersion":1}}""")
                ];
            }

            if (sent.Contains("\"id\":\"ws-sub-1\"", StringComparison.Ordinal))
            {
                return
                [
                    CreateTextMessage("""{"jsonrpc":"2.0","id":"ws-sub-1","result":{"ok":true,"subscriptionId":"sub-1"}}"""),
                    CreateTextMessage(notificationJson),
                    CreateCloseMessage()
                ];
            }

            return [];
        };

        var factory = new FakeWebSocketConnectionFactory(connection);
        await using var client = await DevHubEventsClient.FromRuntimeAsync(
            new DevHubClientOptions
            {
                ClientId = "ws-client",
                DataDir = dataDir
            },
            factory,
            new SequenceRequestIdFactory("ws-auth-1", "ws-sub-1").Create);

        await client.AuthenticateAsync();
        await using var enumerator = client.ReadEventsAsync().GetAsyncEnumerator();
        var moveNextTask = enumerator.MoveNextAsync().AsTask();
        _ = await client.SubscribeAsync(new[] { DevHubEventTypes.InvocationCompleted });

        var exception = await Record.ExceptionAsync(async () => await moveNextTask);

        Assert.NotNull(exception);
        Assert.Contains(expectedMessage, CollectExceptionMessages(exception!), StringComparison.Ordinal);
    }

    [Fact]
    public async Task EventsClient_WhenEventNotificationContainsId_ShouldFaultEventStream()
    {
        var dataDir = await CreateDataDirectoryAsync();
        var connection = new FakeWebSocketConnection();
        connection.OnSend = sent =>
        {
            if (sent.Contains("\"id\":\"ws-auth-1\"", StringComparison.Ordinal))
            {
                return
                [
                    CreateTextMessage("""{"jsonrpc":"2.0","id":"ws-auth-1","result":{"ok":true,"protocolVersion":1}}""")
                ];
            }

            if (sent.Contains("\"id\":\"ws-sub-1\"", StringComparison.Ordinal))
            {
                return
                [
                    CreateTextMessage("""{"jsonrpc":"2.0","id":"ws-sub-1","result":{"ok":true,"subscriptionId":"sub-1"}}"""),
                    CreateTextMessage("""{"jsonrpc":"2.0","id":"evt-1","method":"hub.event","params":{"subscriptionId":"sub-1","type":"invocation.completed","timeUtc":"2026-03-09T00:00:00Z","payload":{"invocationId":"invk-1"}}}"""),
                    CreateCloseMessage()
                ];
            }

            return [];
        };

        var factory = new FakeWebSocketConnectionFactory(connection);
        await using var client = await DevHubEventsClient.FromRuntimeAsync(
            new DevHubClientOptions
            {
                ClientId = "ws-client",
                DataDir = dataDir
            },
            factory,
            new SequenceRequestIdFactory("ws-auth-1", "ws-sub-1").Create);

        await client.AuthenticateAsync();
        await using var enumerator = client.ReadEventsAsync().GetAsyncEnumerator();
        var moveNextTask = enumerator.MoveNextAsync().AsTask();
        _ = await client.SubscribeAsync(new[] { DevHubEventTypes.InvocationCompleted });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () => await moveNextTask);

        Assert.Contains("hub.event", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EventsClient_WhenServerSendsBinaryFrame_ShouldFaultEventStream()
    {
        var dataDir = await CreateDataDirectoryAsync();
        var connection = new FakeWebSocketConnection();
        connection.OnSend = sent =>
        {
            if (sent.Contains("\"id\":\"ws-auth-1\"", StringComparison.Ordinal))
            {
                return
                [
                    CreateTextMessage("""{"jsonrpc":"2.0","id":"ws-auth-1","result":{"ok":true,"protocolVersion":1}}""")
                ];
            }

            if (sent.Contains("\"id\":\"ws-sub-1\"", StringComparison.Ordinal))
            {
                return
                [
                    CreateTextMessage("""{"jsonrpc":"2.0","id":"ws-sub-1","result":{"ok":true,"subscriptionId":"sub-1"}}"""),
                    CreateBinaryMessage(),
                    CreateCloseMessage()
                ];
            }

            return [];
        };

        var factory = new FakeWebSocketConnectionFactory(connection);
        await using var client = await DevHubEventsClient.FromRuntimeAsync(
            new DevHubClientOptions
            {
                ClientId = "ws-client",
                DataDir = dataDir
            },
            factory,
            new SequenceRequestIdFactory("ws-auth-1", "ws-sub-1").Create);

        await client.AuthenticateAsync();
        await using var enumerator = client.ReadEventsAsync().GetAsyncEnumerator();
        var moveNextTask = enumerator.MoveNextAsync().AsTask();
        _ = await client.SubscribeAsync(new[] { DevHubEventTypes.InvocationCompleted });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () => await moveNextTask);

        Assert.Contains("文本", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EventsClient_WhenServerSendsBlankTextFrame_ShouldFaultEventStream()
    {
        var dataDir = await CreateDataDirectoryAsync();
        var connection = new FakeWebSocketConnection();
        connection.OnSend = sent =>
        {
            if (sent.Contains("\"id\":\"ws-auth-1\"", StringComparison.Ordinal))
            {
                return
                [
                    CreateTextMessage("""{"jsonrpc":"2.0","id":"ws-auth-1","result":{"ok":true,"protocolVersion":1}}""")
                ];
            }

            if (sent.Contains("\"id\":\"ws-sub-1\"", StringComparison.Ordinal))
            {
                return
                [
                    CreateTextMessage("""{"jsonrpc":"2.0","id":"ws-sub-1","result":{"ok":true,"subscriptionId":"sub-1"}}"""),
                    CreateTextMessage("   "),
                    CreateCloseMessage()
                ];
            }

            return [];
        };

        var factory = new FakeWebSocketConnectionFactory(connection);
        await using var client = await DevHubEventsClient.FromRuntimeAsync(
            new DevHubClientOptions
            {
                ClientId = "ws-client",
                DataDir = dataDir
            },
            factory,
            new SequenceRequestIdFactory("ws-auth-1", "ws-sub-1").Create);

        await client.AuthenticateAsync();
        await using var enumerator = client.ReadEventsAsync().GetAsyncEnumerator();
        var moveNextTask = enumerator.MoveNextAsync().AsTask();
        _ = await client.SubscribeAsync(new[] { DevHubEventTypes.InvocationCompleted });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () => await moveNextTask);

        Assert.Contains("不能为空", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EventsClient_WhenServerSendsUnsupportedNotification_ShouldFaultEventStream()
    {
        var dataDir = await CreateDataDirectoryAsync();
        var connection = new FakeWebSocketConnection();
        connection.OnSend = sent =>
        {
            if (sent.Contains("\"id\":\"ws-auth-1\"", StringComparison.Ordinal))
            {
                return
                [
                    CreateTextMessage("""{"jsonrpc":"2.0","id":"ws-auth-1","result":{"ok":true,"protocolVersion":1}}""")
                ];
            }

            if (sent.Contains("\"id\":\"ws-sub-1\"", StringComparison.Ordinal))
            {
                return
                [
                    CreateTextMessage("""{"jsonrpc":"2.0","id":"ws-sub-1","result":{"ok":true,"subscriptionId":"sub-1"}}"""),
                    CreateTextMessage("""{"jsonrpc":"2.0","method":"hub.unknown","params":{"value":1}}"""),
                    CreateCloseMessage()
                ];
            }

            return [];
        };

        var factory = new FakeWebSocketConnectionFactory(connection);
        await using var client = await DevHubEventsClient.FromRuntimeAsync(
            new DevHubClientOptions
            {
                ClientId = "ws-client",
                DataDir = dataDir
            },
            factory,
            new SequenceRequestIdFactory("ws-auth-1", "ws-sub-1").Create);

        await client.AuthenticateAsync();
        await using var enumerator = client.ReadEventsAsync().GetAsyncEnumerator();
        var moveNextTask = enumerator.MoveNextAsync().AsTask();
        _ = await client.SubscribeAsync(new[] { DevHubEventTypes.InvocationCompleted });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () => await moveNextTask);

        Assert.Contains("hub.unknown", exception.Message, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    private async Task<string> CreateDataDirectoryAsync()
    {
        var dataDir = Path.Combine(_tempRoot, Guid.NewGuid().ToString("N"));
        var runtimeDir = Path.Combine(dataDir, "runtime");
        Directory.CreateDirectory(runtimeDir);
        var tokenFile = Path.Combine(runtimeDir, "token.txt");
        await File.WriteAllTextAsync(tokenFile, "token-1");
        await File.WriteAllTextAsync(
            Path.Combine(runtimeDir, "hub.json"),
            $$"""
            {
              "protocolVersion": 1,
              "pid": 12345,
              "httpBaseUrl": "http://127.0.0.1:47231",
              "wsUrl": "ws://127.0.0.1:47231/ws",
              "tokenFile": "{{tokenFile.Replace("\\", "\\\\")}}",
              "startedAtUtc": "2026-03-09T00:00:00Z",
              "runtimeTuning": {
                "leaseSeconds": 30,
                "onlineThresholdSeconds": 30,
                "launchDedupeWindowSeconds": 30
              }
            }
            """);
        return dataDir;
    }

    private static WebSocketReceiveMessage CreateTextMessage(string text)
    {
        return new WebSocketReceiveMessage
        {
            MessageType = WebSocketMessageType.Text,
            Text = text
        };
    }

    private static WebSocketReceiveMessage CreateBinaryMessage()
    {
        return new WebSocketReceiveMessage
        {
            MessageType = WebSocketMessageType.Binary
        };
    }

    private static WebSocketReceiveMessage CreateCloseMessage()
    {
        return new WebSocketReceiveMessage
        {
            MessageType = WebSocketMessageType.Close,
            CloseStatus = WebSocketCloseStatus.NormalClosure,
            CloseStatusDescription = "done"
        };
    }

    private static async Task<InvalidOperationException> AssertReadEventsThrowsInvalidOperationAsync(DevHubEventsClient client)
    {
        return await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await using var enumerator = client.ReadEventsAsync().GetAsyncEnumerator();
            await enumerator.MoveNextAsync();
        });
    }

    private static string CreateHostVersionWithPatchDelta(int patchDelta)
    {
        Assert.True(SemanticVersionParser.TryParse(SdkVersionSource.CurrentVersion, out var sdkVersion));
        return $"{sdkVersion.Major}.{sdkVersion.Minor}.{sdkVersion.Patch + patchDelta}";
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, int timeoutMilliseconds = 1000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMilliseconds);
        while (!predicate())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("等待条件成立超时。");
            }

            await Task.Delay(10);
        }
    }

    private sealed class FakeWebSocketConnectionFactory : IWebSocketConnectionFactory
    {
        private readonly IWebSocketConnection _connection;

        public FakeWebSocketConnectionFactory(IWebSocketConnection connection)
        {
            _connection = connection;
        }

        public Task<IWebSocketConnection> ConnectAsync(Uri uri, CancellationToken cancellationToken)
        {
            return Task.FromResult(_connection);
        }
    }

    private sealed class SequenceWebSocketConnectionFactory : IWebSocketConnectionFactory
    {
        private readonly Queue<IWebSocketConnection> _connections;

        public SequenceWebSocketConnectionFactory(params IWebSocketConnection[] connections)
        {
            _connections = new Queue<IWebSocketConnection>(connections);
        }

        public Task<IWebSocketConnection> ConnectAsync(Uri uri, CancellationToken cancellationToken)
        {
            if (_connections.Count == 0)
            {
                throw new InvalidOperationException("没有可用的测试连接。");
            }

            return Task.FromResult(_connections.Dequeue());
        }
    }

    private sealed class FakeWebSocketConnection : IWebSocketConnection
    {
        private readonly ConcurrentQueue<WebSocketReceiveMessage> _messages = new();
        private readonly SemaphoreSlim _messageSignal = new(0);
        private readonly TaskCompletionSource<bool> _closeObserved = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _closeCallCount;

        public List<string> SentTexts { get; } = [];

        public Func<string, IEnumerable<WebSocketReceiveMessage>>? OnSend { get; set; }

        public WebSocketState State { get; private set; } = WebSocketState.Open;

        public int CloseCallCount => Volatile.Read(ref _closeCallCount);

        public ConcurrentQueue<string?> CloseStatusDescriptions { get; } = [];

        public void Enqueue(WebSocketReceiveMessage message)
        {
            _messages.Enqueue(message);
            _messageSignal.Release();
        }

        public Task SendTextAsync(string text, CancellationToken cancellationToken)
        {
            SentTexts.Add(text);

            if (OnSend is not null)
            {
                foreach (var message in OnSend(text))
                {
                    Enqueue(message);
                }
            }

            return Task.CompletedTask;
        }

        public async Task<WebSocketReceiveMessage> ReceiveAsync(CancellationToken cancellationToken)
        {
            await _messageSignal.WaitAsync(cancellationToken);
            if (!_messages.TryDequeue(out var message))
            {
                throw new InvalidOperationException("测试消息队列状态非法。");
            }

            if (message.MessageType == WebSocketMessageType.Close)
            {
                _closeObserved.TrySetResult(true);
            }

            return message;
        }

        public Task WaitForCloseObservedAsync()
        {
            return _closeObserved.Task;
        }

        public Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            State = WebSocketState.Closed;
            Interlocked.Increment(ref _closeCallCount);
            CloseStatusDescriptions.Enqueue(statusDescription);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            State = WebSocketState.Closed;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class SequenceRequestIdFactory
    {
        private readonly Queue<string> _requestIds;

        public SequenceRequestIdFactory(params string[] requestIds)
        {
            _requestIds = new Queue<string>(requestIds);
        }

        public string Create()
        {
            return _requestIds.Dequeue();
        }
    }

    private static string CollectExceptionMessages(Exception exception)
    {
        var messages = new List<string>();
        Exception? current = exception;
        while (current is not null)
        {
            messages.Add(current.Message);
            current = current.InnerException;
        }

        return string.Join(" | ", messages);
    }

    private sealed class RecordingLoggerFactory : ILoggerFactory
    {
        private readonly object _waitersLock = new();
        private readonly List<LogWaiter> _waiters = [];

        public ConcurrentQueue<LogEntry> Entries { get; } = [];

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public ILogger CreateLogger(string categoryName)
        {
            return new RecordingLogger(categoryName, this);
        }

        public void Dispose()
        {
        }

        public async Task WaitForEntryAsync(Predicate<LogEntry> predicate, int timeoutMilliseconds = 1000)
        {
            if (Entries.Any(entry => predicate(entry)))
            {
                return;
            }

            Task waiterTask;
            lock (_waitersLock)
            {
                if (Entries.Any(entry => predicate(entry)))
                {
                    return;
                }

                var waiter = new LogWaiter(predicate);
                _waiters.Add(waiter);
                waiterTask = waiter.Task;
            }

            try
            {
                await waiterTask.WaitAsync(TimeSpan.FromMilliseconds(timeoutMilliseconds));
            }
            catch (TimeoutException exception)
            {
                var snapshot = string.Join(" | ", Entries.Select(static entry => entry.Message));
                throw new TimeoutException($"等待日志超时。当前日志快照：{snapshot}", exception);
            }
        }

        public void Record(LogEntry entry)
        {
            Entries.Enqueue(entry);

            lock (_waitersLock)
            {
                for (var index = _waiters.Count - 1; index >= 0; index--)
                {
                    var waiter = _waiters[index];
                    if (!waiter.Predicate(entry))
                    {
                        continue;
                    }

                    _waiters.RemoveAt(index);
                    waiter.Complete();
                }
            }
        }

        private sealed class LogWaiter(Predicate<LogEntry> predicate)
        {
            private readonly TaskCompletionSource _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public Predicate<LogEntry> Predicate { get; } = predicate;

            public Task Task => _tcs.Task;

            public void Complete()
            {
                _tcs.TrySetResult();
            }
        }
    }

    private sealed class RecordingLogger(string categoryName, RecordingLoggerFactory factory) : ILogger
    {
        private readonly string _categoryName = categoryName;
        private readonly RecordingLoggerFactory _factory = factory;

        public IDisposable BeginScope<TState>(TState state)
            where TState : notnull
        {
            return NullScope.Instance;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            _factory.Record(new LogEntry(_categoryName, logLevel, formatter(state, exception)));
        }
    }

    private sealed record LogEntry(string Category, LogLevel Level, string Message);

    private sealed class NullScope : IDisposable
    {
        public static NullScope Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
