namespace DevHub.Tests;

using System.Text.Json;
using DevHub.Core.Models.Rpc;
using DevHub.Core.Services;
using DevHub.Core.Services.Abstractions;
using DevHub.Core.Services.Invocation;
using DevHub.Core.Services.Rpc.Handlers;
using Microsoft.Extensions.Logging;
using Moq;

/// <summary>
/// InvocationHandler 边界行为测试。
/// </summary>
public sealed class InvocationHandlerBoundaryTests : IDisposable
{
    private readonly string _tempDirectory;

    /// <summary>
    /// 初始化测试上下文。
    /// </summary>
    public InvocationHandlerBoundaryTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "DevHubInvocationHandlerBoundaryTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
    }

    [Fact]
    public async Task Notify_WhenRequiredFieldsMissing_ShouldReturnInvalidParams()
    {
        WriteDefinition("invocation-required-fields", rpcEnabled: true);
        using var appRegistry = new AppRegistry(new SystemClock(), Mock.Of<ILogger<AppRegistry>>());
        var handler = CreateHandler(appRegistry);

        var response = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "notify-missing-fields",
            Method = HubRpcMethods.HubInvokeNotify,
            Params = JsonSerializer.SerializeToElement(new
            {
                appId = "invocation-required-fields"
            })
        }, CancellationToken.None);

        AssertError(response, -32602, "invalid_params");
    }

    [Fact]
    public async Task Notify_WhenParamsNotObject_ShouldReturnInvalidParams()
    {
        WriteDefinition("invocation-invalid-params-root", rpcEnabled: true);
        using var appRegistry = new AppRegistry(new SystemClock(), Mock.Of<ILogger<AppRegistry>>());
        var handler = CreateHandler(appRegistry);

        var response = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "notify-params-not-object",
            Method = HubRpcMethods.HubInvokeNotify,
            Params = JsonSerializer.SerializeToElement("bad")
        }, CancellationToken.None);

        AssertError(response, -32602, "invalid_params");
    }

    [Fact]
    public async Task Notify_WhenOptionsContainInvalidTypes_ShouldReturnInvalidParams()
    {
        WriteDefinition("invocation-notify-options", rpcEnabled: true);
        using var appRegistry = new AppRegistry(new SystemClock(), Mock.Of<ILogger<AppRegistry>>());
        var handler = CreateHandler(appRegistry);

        var invalidQueueIfOffline = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "notify-invalid-queue",
            Method = HubRpcMethods.HubInvokeNotify,
            Params = JsonSerializer.SerializeToElement(new
            {
                appId = "invocation-notify-options",
                target = new { scope = (string?)null, instanceId = (string?)null },
                method = "task.run",
                options = new
                {
                    queueIfOffline = "yes",
                    autoLaunch = false
                }
            })
        }, CancellationToken.None);
        AssertError(invalidQueueIfOffline, -32602, "invalid_params");

        var invalidAutoLaunch = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "notify-invalid-auto-launch",
            Method = HubRpcMethods.HubInvokeNotify,
            Params = JsonSerializer.SerializeToElement(new
            {
                appId = "invocation-notify-options",
                target = new { scope = (string?)null, instanceId = (string?)null },
                method = "task.run",
                options = new
                {
                    queueIfOffline = true,
                    autoLaunch = "yes"
                }
            })
        }, CancellationToken.None);
        AssertError(invalidAutoLaunch, -32602, "invalid_params");
    }

    [Fact]
    public async Task Request_WhenOptionsInvalid_ShouldReturnInvalidParams()
    {
        WriteDefinition("invocation-request-options", rpcEnabled: true);
        using var appRegistry = new AppRegistry(new SystemClock(), Mock.Of<ILogger<AppRegistry>>());
        var handler = CreateHandler(appRegistry);

        var optionsNotObject = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "request-options-not-object",
            Method = HubRpcMethods.HubInvokeRequest,
            Params = JsonSerializer.SerializeToElement(new
            {
                appId = "invocation-request-options",
                target = new { scope = (string?)null, instanceId = (string?)null },
                method = "task.run",
                options = 1
            })
        }, CancellationToken.None);
        AssertError(optionsNotObject, -32602, "invalid_params");

        var invalidWaitTimeout = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "request-invalid-wait-timeout",
            Method = HubRpcMethods.HubInvokeRequest,
            Params = JsonSerializer.SerializeToElement(new
            {
                appId = "invocation-request-options",
                target = new { scope = (string?)null, instanceId = (string?)null },
                method = "task.run",
                options = new
                {
                    ttlMs = 2000,
                    waitTimeoutMs = 0,
                    queueIfOffline = true,
                    autoLaunch = false
                }
            })
        }, CancellationToken.None);
        AssertError(invalidWaitTimeout, -32602, "invalid_params");
    }

    [Fact]
    public async Task Poll_WhenParamsInvalid_ShouldReturnInvalidParams()
    {
        using var appRegistry = new AppRegistry(new SystemClock(), Mock.Of<ILogger<AppRegistry>>());
        var handler = CreateHandler(appRegistry);

        var paramsNotObject = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "poll-params-not-object",
            Method = HubRpcMethods.HubInvokePoll,
            Params = JsonSerializer.SerializeToElement("bad")
        }, CancellationToken.None);
        AssertError(paramsNotObject, -32602, "invalid_params");

        var missingInstanceId = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "poll-missing-instance-id",
            Method = HubRpcMethods.HubInvokePoll,
            Params = JsonSerializer.SerializeToElement(new { maxCount = 1 })
        }, CancellationToken.None);
        AssertError(missingInstanceId, -32602, "invalid_params");

        var invalidWaitMs = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "poll-invalid-wait-ms",
            Method = HubRpcMethods.HubInvokePoll,
            Params = JsonSerializer.SerializeToElement(new { instanceId = "inst-1", waitMs = -1 })
        }, CancellationToken.None);
        AssertError(invalidWaitMs, -32602, "invalid_params");
    }

    [Fact]
    public async Task Respond_WhenParamsInvalid_ShouldReturnInvalidParams()
    {
        using var appRegistry = new AppRegistry(new SystemClock(), Mock.Of<ILogger<AppRegistry>>());
        var handler = CreateHandler(appRegistry);

        var paramsNotObject = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "respond-params-not-object",
            Method = HubRpcMethods.HubInvokeRespond,
            Params = JsonSerializer.SerializeToElement("bad")
        }, CancellationToken.None);
        AssertError(paramsNotObject, -32602, "invalid_params");

        var missingInvocationId = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "respond-missing-fields",
            Method = HubRpcMethods.HubInvokeRespond,
            Params = JsonSerializer.SerializeToElement(new
            {
                instanceId = "inst-1",
                value = new { ok = true }
            })
        }, CancellationToken.None);
        AssertError(missingInvocationId, -32602, "invalid_params");

        var bothValueAndError = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "respond-both-value-error",
            Method = HubRpcMethods.HubInvokeRespond,
            Params = JsonSerializer.SerializeToElement(new
            {
                instanceId = "inst-1",
                invocationId = "invk-1",
                value = new { ok = true },
                error = new { code = 1 }
            })
        }, CancellationToken.None);
        AssertError(bothValueAndError, -32602, "invalid_params");
    }

    [Fact]
    public async Task Respond_WhenInvocationMissing_ShouldReturnInvocationExpired()
    {
        using var appRegistry = new AppRegistry(new SystemClock(), Mock.Of<ILogger<AppRegistry>>());
        appRegistry.RegisterInstance(new DevHub.Core.Models.AppInstance
        {
            InstanceId = "respond-missing-inst",
            AppId = "respond-missing-app",
            Scope = null,
            Pid = 6001,
            Invoke = new DevHub.Core.Models.InvokeCapability
            {
                Poll = true,
                Respond = true
            }
        });

        var handler = CreateHandler(appRegistry);

        var response = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "respond-missing-invocation",
            Method = HubRpcMethods.HubInvokeRespond,
            Params = JsonSerializer.SerializeToElement(new
            {
                instanceId = "respond-missing-inst",
                invocationId = "invk-not-exists",
                value = new { ok = true }
            })
        }, CancellationToken.None);

        AssertError(response, -32011, "invocation_expired");
    }

    [Fact]
    public async Task Request_WhenCanceledAndTtlReached_ShouldReturnInvocationExpired()
    {
        const string appId = "invocation-cancel-ttl";
        WriteDefinition(appId, rpcEnabled: true);

        var clock = new SequenceClock(DateTime.UtcNow, TimeSpan.FromSeconds(2));
        using var appRegistry = new AppRegistry(clock, Mock.Of<ILogger<AppRegistry>>());
        var handler = CreateHandler(appRegistry, clock);

        using var canceledTokenSource = new CancellationTokenSource();
        canceledTokenSource.Cancel();

        var response = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "request-cancel-ttl",
            Method = HubRpcMethods.HubInvokeRequest,
            Params = JsonSerializer.SerializeToElement(new
            {
                appId,
                target = new { scope = (string?)null, instanceId = (string?)null },
                method = "task.run",
                options = new
                {
                    ttlMs = 1000,
                    waitTimeoutMs = 1000,
                    queueIfOffline = true,
                    autoLaunch = false
                }
            })
        }, canceledTokenSource.Token);

        AssertError(response, -32011, "invocation_expired");
    }

    [Fact]
    public async Task HandleAsync_WhenMethodUnknown_ShouldReturnMethodNotFound()
    {
        using var appRegistry = new AppRegistry(new SystemClock(), Mock.Of<ILogger<AppRegistry>>());
        var handler = CreateHandler(appRegistry);

        var response = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "unknown-method",
            Method = "hub.invoke.unknown",
            Params = JsonSerializer.SerializeToElement(new { })
        }, CancellationToken.None);

        AssertError(response, -32601, "method_not_found");
    }

    [Fact]
    public async Task Request_WhenCanceledBeforeTtlReached_ShouldReturnInvocationTimeout()
    {
        const string appId = "invocation-cancel-timeout";
        WriteDefinition(appId, rpcEnabled: true);

        var clock = new SequenceClock(DateTime.UtcNow, TimeSpan.FromMilliseconds(200));
        using var appRegistry = new AppRegistry(clock, Mock.Of<ILogger<AppRegistry>>());
        var handler = CreateHandler(appRegistry, clock);

        using var canceledTokenSource = new CancellationTokenSource();
        canceledTokenSource.Cancel();

        var response = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "request-cancel-timeout",
            Method = HubRpcMethods.HubInvokeRequest,
            Params = JsonSerializer.SerializeToElement(new
            {
                appId,
                target = new { scope = (string?)null, instanceId = (string?)null },
                method = "task.run",
                options = new
                {
                    ttlMs = 5000,
                    waitTimeoutMs = 3000,
                    queueIfOffline = true,
                    autoLaunch = false
                }
            })
        }, canceledTokenSource.Token);

        AssertError(response, -32012, "invocation_timeout");
    }

    [Fact]
    public async Task Respond_WhenLeaseHeldByAnotherInstance_ShouldReturnDeliveryConflict()
    {
        const string appId = "invocation-delivery-conflict";
        WriteDefinition(appId, rpcEnabled: true);

        using var appRegistry = new AppRegistry(new SystemClock(), Mock.Of<ILogger<AppRegistry>>());
        appRegistry.RegisterInstance(new DevHub.Core.Models.AppInstance
        {
            InstanceId = "holder-instance",
            AppId = appId,
            Scope = null,
            Pid = 6002,
            Invoke = new DevHub.Core.Models.InvokeCapability
            {
                Poll = true,
                Respond = true
            }
        });

        appRegistry.RegisterInstance(new DevHub.Core.Models.AppInstance
        {
            InstanceId = "other-instance",
            AppId = appId,
            Scope = null,
            Pid = 6003,
            Invoke = new DevHub.Core.Models.InvokeCapability
            {
                Poll = true,
                Respond = true
            }
        });

        var handler = CreateHandler(appRegistry);

        var notify = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "notify-delivery-conflict",
            Method = HubRpcMethods.HubInvokeNotify,
            Params = JsonSerializer.SerializeToElement(new
            {
                appId,
                target = new { scope = (string?)null, instanceId = "holder-instance" },
                method = "task.run",
                options = new
                {
                    ttlMs = 60000,
                    queueIfOffline = true,
                    autoLaunch = false
                }
            })
        }, CancellationToken.None);
        Assert.Null(notify.Error);
        var notifyResult = JsonSerializer.SerializeToElement(notify.Result);
        var invocationId = notifyResult.GetProperty("invocationId").GetString();
        Assert.False(string.IsNullOrWhiteSpace(invocationId));

        var poll = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "poll-delivery-conflict",
            Method = HubRpcMethods.HubInvokePoll,
            Params = JsonSerializer.SerializeToElement(new
            {
                instanceId = "holder-instance",
                maxCount = 1,
                waitMs = 0
            })
        }, CancellationToken.None);
        Assert.Null(poll.Error);

        var conflict = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "respond-delivery-conflict",
            Method = HubRpcMethods.HubInvokeRespond,
            Params = JsonSerializer.SerializeToElement(new
            {
                instanceId = "other-instance",
                invocationId,
                value = new { ok = true }
            })
        }, CancellationToken.None);

        AssertError(conflict, -32030, "delivery_conflict");
        var conflictData = JsonSerializer.SerializeToElement(conflict.Error!.Data);
        Assert.Equal(invocationId, conflictData.GetProperty("invocationId").GetString());
        Assert.Equal("holder-instance", conflictData.GetProperty("currentLeaseHolder").GetString());
    }

    /// <inheritdoc />
    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDirectory))
            {
                Directory.Delete(_tempDirectory, recursive: true);
            }
        }
        catch
        {
        }
    }

    private InvocationHandler CreateHandler(AppRegistry appRegistry, IClock? clock = null)
    {
        var effectiveClock = clock ?? new SystemClock();

        var definitionLoader = new DefinitionLoader(_tempDirectory, Mock.Of<ILogger<DefinitionLoader>>());
        var definitionProvider = new DefinitionProvider(definitionLoader);
        definitionProvider.Refresh();

        var routingService = new InvocationRoutingService(appRegistry, Mock.Of<ILogger<InvocationRoutingService>>());
        var store = new InvocationStore(Mock.Of<ILogger<InvocationStore>>(), routingService, effectiveClock);
        var waiter = new InvocationRequestWaiter(Mock.Of<ILogger<InvocationRequestWaiter>>());
        var runtimeHttpBaseUrlProvider = new RuntimeHttpBaseUrlProvider(Mock.Of<ILogger<RuntimeHttpBaseUrlProvider>>(), RuntimePathOptions.Resolve(_tempDirectory));
        var launchCoordinator = new LaunchCoordinator(
            definitionProvider,
            appRegistry,
            runtimeHttpBaseUrlProvider,
            new ProcessLauncher(),
            effectiveClock,
            Mock.Of<ILogger<LaunchCoordinator>>());

        return new InvocationHandler(
            appRegistry,
            definitionProvider,
            routingService,
            store,
            waiter,
            launchCoordinator,
            effectiveClock,
            Mock.Of<ILogger<InvocationHandler>>());
    }

    private void WriteDefinition(string appId, bool rpcEnabled)
    {
        var path = Path.Combine(_tempDirectory, $"{appId}.json");
        var payload = new
        {
            appId,
            displayName = appId,
            capabilities = new
            {
                rpc = rpcEnabled,
                events = false
            }
        };

        File.WriteAllText(path, JsonSerializer.Serialize(payload));
    }

    private static void AssertError(JsonRpcResponse response, int code, string message)
    {
        Assert.NotNull(response.Error);
        Assert.Equal(code, response.Error!.Code);
        Assert.Equal(message, response.Error.Message);
    }

    private sealed class SequenceClock : IClock
    {
        private readonly DateTime _start;
        private readonly TimeSpan _delta;
        private int _readCount;

        public SequenceClock(DateTime start, TimeSpan delta)
        {
            _start = start;
            _delta = delta;
        }

        public DateTime UtcNow
        {
            get
            {
                var current = Interlocked.Increment(ref _readCount);
                return _start.AddTicks(_delta.Ticks * current);
            }
        }
    }
}
