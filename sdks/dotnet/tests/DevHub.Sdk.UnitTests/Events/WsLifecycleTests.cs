using System.Net.WebSockets;
using System.Text.Json;
using DevHub.Sdk.Internal;
using DevHub.Sdk.Models;

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
            """{"jsonrpc":"2.0","method":"hub.event","params":{"subscriptionId":"sub-1","type":"app.definition.deleted","timeUtc":"2026-03-09T00:00:00Z","payload":{}}}""",
            "appId"
        },
        {
            """{"jsonrpc":"2.0","method":"hub.event","params":{"subscriptionId":"sub-1","type":"app.instance.registered","timeUtc":"2026-03-09T00:00:00Z","payload":{"appId":"test.app","instanceId":"inst-1","password":"secret-1"}}}""",
            "password"
        }
    };

    public WsLifecycleTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "DevHubSdkWsTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    [Fact]
    public async Task M5_DN_UT_005_EventsClient_BeforeAuthenticate_ShouldRejectSubscribeAndRead()
    {
        var dataDir = await CreateDataDirectoryAsync();
        var factory = new FakeWebSocketConnectionFactory(new FakeWebSocketConnection());
        await using var client = await DevHubEventsClient.FromRuntimeAsync(
            new DevHubClientOptions
            {
                ClientId = "ws-client",
                DataDir = dataDir
            },
            factory,
            () => "ws-auth-1");

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.SubscribeAsync(new[] { DevHubEventTypes.InvocationCompleted }));
        Assert.Throws<InvalidOperationException>(() => client.ReadEventsAsync());
    }

    [Fact]
    public async Task M5_DN_UT_005_EventsClient_AfterAuthenticate_ShouldSubscribeAndReadEvents()
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
        var subscriptionId = await client.SubscribeAsync(new[] { DevHubEventTypes.InvocationCompleted });
        Assert.Equal("sub-1", subscriptionId);

        var enumerator = client.ReadEventsAsync().GetAsyncEnumerator();
        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal(DevHubEventTypes.InvocationCompleted, enumerator.Current.Type);
        Assert.Equal("sub-1", enumerator.Current.SubscriptionId);

        Assert.Collection(connection.SentTexts,
            sent => Assert.Contains("hub.ws.authenticate", sent, StringComparison.Ordinal),
            sent => Assert.Contains("hub.events.subscribe", sent, StringComparison.Ordinal));
    }

    [Fact]
    public async Task M5_DN_UT_005_EventsClient_AfterAuthenticate_ShouldUnsubscribe()
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
    public async Task M5_DN_UT_005_EventsClient_AfterAuthenticate_ShouldSupportWsReadableMethods()
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

            if (sent.Contains("\"id\":\"ws-listdefs-1\"", StringComparison.Ordinal))
            {
                return
                [
                    CreateTextMessage("""{"jsonrpc":"2.0","id":"ws-listdefs-1","result":{"ok":true,"definitions":[{"appId":"ws.app","displayName":"WS App"}]}}""")
                ];
            }

            if (sent.Contains("\"id\":\"ws-getdef-1\"", StringComparison.Ordinal))
            {
                return
                [
                    CreateTextMessage("""{"jsonrpc":"2.0","id":"ws-getdef-1","result":{"ok":true,"definition":{"appId":"ws.app","displayName":"WS App"}}}""")
                ];
            }

            if (sent.Contains("\"id\":\"ws-listinst-1\"", StringComparison.Ordinal))
            {
                return
                [
                    CreateTextMessage("""{"jsonrpc":"2.0","id":"ws-listinst-1","result":{"ok":true,"instances":[{"instanceId":"inst-1","appId":"ws.app","scope":null,"pid":12345,"registeredAtUtc":"2026-03-09T00:00:00Z","lastSeenUtc":"2026-03-09T00:00:01Z","invoke":{"poll":true,"respond":true}}]}}""")
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
            new SequenceRequestIdFactory("ws-auth-1", "ws-ping-1", "ws-listdefs-1", "ws-getdef-1", "ws-listinst-1").Create);

        await client.AuthenticateAsync();

        var ping = await client.PingAsync(new { value = 1 });
        var definitions = await client.ListDefinitionsAsync();
        var definition = await client.GetDefinitionAsync("ws.app");
        var instances = await client.ListInstancesAsync();

        Assert.True(ping.Ok);
        Assert.Equal("ws.app", definitions.Single().AppId);
        Assert.Equal("ws.app", definition.AppId);
        Assert.Equal("inst-1", instances.Single().InstanceId);

        var listDefinitionsRequest = connection.SentTexts.Single(sent => sent.Contains("hub.apps.listDefinitions", StringComparison.Ordinal));
        Assert.DoesNotContain("\"params\"", listDefinitionsRequest, StringComparison.Ordinal);

        Assert.Collection(connection.SentTexts,
            sent => Assert.Contains("hub.ws.authenticate", sent, StringComparison.Ordinal),
            sent => Assert.Contains("hub.ping", sent, StringComparison.Ordinal),
            sent => Assert.Contains("hub.apps.listDefinitions", sent, StringComparison.Ordinal),
            sent => Assert.Contains("hub.apps.getDefinition", sent, StringComparison.Ordinal),
            sent => Assert.Contains("hub.apps.listInstances", sent, StringComparison.Ordinal));
    }

    [Fact]
    public async Task M5_DN_UT_005_EventsClient_WhenConnectionClosesAfterQueuedEvent_ShouldStillReadBufferedEvents()
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
        _ = await client.SubscribeAsync(new[] { DevHubEventTypes.InvocationCompleted });
        await connection.WaitForCloseObservedAsync();

        await using var enumerator = client.ReadEventsAsync().GetAsyncEnumerator();
        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal(DevHubEventTypes.InvocationCompleted, enumerator.Current.Type);
        Assert.Equal("sub-1", enumerator.Current.SubscriptionId);
        Assert.False(await enumerator.MoveNextAsync());
    }

    [Fact]
    public async Task M5_DN_UT_005_EventsClient_WhenConnectionTerminates_ShouldAllowAuthenticateAgainAndRequireResubscribe()
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
        _ = await client.SubscribeAsync(new[] { DevHubEventTypes.InvocationCompleted });
        await firstConnection.WaitForCloseObservedAsync();

        await using (var firstEnumerator = client.ReadEventsAsync().GetAsyncEnumerator())
        {
            Assert.True(await firstEnumerator.MoveNextAsync());
            Assert.Equal("invk-1", firstEnumerator.Current.Payload!.Value.GetProperty("invocationId").GetString());
            Assert.False(await firstEnumerator.MoveNextAsync());
        }

        await client.AuthenticateAsync();
        _ = await client.SubscribeAsync(new[] { DevHubEventTypes.InvocationCompleted });

        await using (var secondEnumerator = client.ReadEventsAsync().GetAsyncEnumerator())
        {
            Assert.True(await secondEnumerator.MoveNextAsync());
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
    public async Task M5_DN_UT_005_EventsClient_WhenAuthenticateFails_ShouldThrowDevHubRpcException()
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
        Assert.Equal("invalid_token", exception.Data!.Value.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task M5_DN_UT_005_EventsClient_WhenAuthenticateTimesOut_ShouldThrowOperationCanceledException()
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
    public async Task M5_DN_UT_005_EventsClient_WhenAuthenticateReturnsInvalidSuccessPayload_ShouldThrowInvalidOperationException()
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
    public async Task M5_DN_UT_005_EventsClient_WhenAuthenticateResponseJsonRpcVersionInvalid_ShouldThrowInvalidOperationException()
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

    [Fact]
    public async Task M5_DN_UT_005_EventsClient_WhenAuthenticateResponseMissingResultAndError_ShouldThrowInvalidOperationException()
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

    [Theory]
    [MemberData(nameof(InvalidEventNotifications))]
    public async Task M5_DN_UT_005_EventsClient_WhenEventPayloadViolatesSpec_ShouldFaultEventStream(
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
        _ = await client.SubscribeAsync(new[] { DevHubEventTypes.InvocationCompleted });

        var exception = await Record.ExceptionAsync(async () =>
        {
            await using var enumerator = client.ReadEventsAsync().GetAsyncEnumerator();
            await enumerator.MoveNextAsync();
        });

        Assert.NotNull(exception);
        Assert.Contains(expectedMessage, CollectExceptionMessages(exception!), StringComparison.Ordinal);
    }

    [Fact]
    public async Task M5_DN_UT_005_EventsClient_WhenEventNotificationContainsId_ShouldFaultEventStream()
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
        _ = await client.SubscribeAsync(new[] { DevHubEventTypes.InvocationCompleted });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await using var enumerator = client.ReadEventsAsync().GetAsyncEnumerator();
            await enumerator.MoveNextAsync();
        });

        Assert.Contains("hub.event", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task M5_DN_UT_005_EventsClient_WhenServerSendsBinaryFrame_ShouldFaultEventStream()
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
        _ = await client.SubscribeAsync(new[] { DevHubEventTypes.InvocationCompleted });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await using var enumerator = client.ReadEventsAsync().GetAsyncEnumerator();
            await enumerator.MoveNextAsync();
        });

        Assert.Contains("文本", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task M5_DN_UT_005_EventsClient_WhenServerSendsBlankTextFrame_ShouldFaultEventStream()
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
        _ = await client.SubscribeAsync(new[] { DevHubEventTypes.InvocationCompleted });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await using var enumerator = client.ReadEventsAsync().GetAsyncEnumerator();
            await enumerator.MoveNextAsync();
        });

        Assert.Contains("不能为空", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task M5_DN_UT_005_EventsClient_WhenServerSendsUnsupportedNotification_ShouldFaultEventStream()
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
        _ = await client.SubscribeAsync(new[] { DevHubEventTypes.InvocationCompleted });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await using var enumerator = client.ReadEventsAsync().GetAsyncEnumerator();
            await enumerator.MoveNextAsync();
        });

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
        private readonly Queue<WebSocketReceiveMessage> _messages = new();
        private readonly SemaphoreSlim _messageSignal = new(0);
        private readonly TaskCompletionSource<bool> _closeObserved = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<string> SentTexts { get; } = [];

        public Func<string, IEnumerable<WebSocketReceiveMessage>>? OnSend { get; set; }

        public WebSocketState State { get; private set; } = WebSocketState.Open;

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
            var message = _messages.Dequeue();
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
}
