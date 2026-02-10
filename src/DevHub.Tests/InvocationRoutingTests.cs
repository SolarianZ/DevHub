namespace DevHub.Tests;

using System.Diagnostics;
using System.Text.Json;
using DevHub.Core.Models;
using DevHub.Core.Models.Rpc;
using DevHub.Core.Services;
using DevHub.Core.Services.Invocation;
using DevHub.Core.Services.Rpc;
using DevHub.Core.Services.Rpc.Handlers;
using Microsoft.Extensions.Logging;
using Moq;

/// <summary>
/// Invocation 路由与门禁相关测试。
/// </summary>
public class InvocationRoutingTests : IDisposable
{
    private readonly string _tempDirectory;
    private readonly Mock<ILogger<AppRegistry>> _registryLogger = new();
    private readonly Mock<ILogger<DefinitionLoader>> _definitionLogger = new();
    private readonly Mock<ILogger<InvocationStore>> _storeLogger = new();
    private readonly Mock<ILogger<InvocationRoutingService>> _routingLogger = new();
    private readonly Mock<ILogger<LaunchCoordinator>> _launchLogger = new();
    private readonly Mock<ILogger<InvocationHandler>> _invocationHandlerLogger = new();
    private readonly Mock<ILogger<LaunchHandler>> _launchHandlerLogger = new();

    /// <summary>
    /// 初始化测试上下文。
    /// </summary>
    public InvocationRoutingTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "DevHubInvocationRoutingTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
    }

    [Fact]
    public async Task InvocationHandler_Notify_WithRpcDisabledDefinition_ShouldReturnForbidden()
    {
        WriteDefinition("disabled-app", rpcEnabled: false);

        var appRegistry = new AppRegistry(_registryLogger.Object);
        var definitionLoader = new DefinitionLoader(_tempDirectory, _definitionLogger.Object);
        definitionLoader.Load();
        var routingService = new InvocationRoutingService(appRegistry, _routingLogger.Object);
        var store = new InvocationStore(_storeLogger.Object, routingService);
        var waiter = new InvocationRequestWaiter(Mock.Of<ILogger<InvocationRequestWaiter>>());
        var runtimeHttpBaseUrlProvider = new RuntimeHttpBaseUrlProvider(Mock.Of<ILogger<RuntimeHttpBaseUrlProvider>>());
        var launchCoordinator = new LaunchCoordinator(definitionLoader, appRegistry, runtimeHttpBaseUrlProvider, _launchLogger.Object);
        var handler = new InvocationHandler(appRegistry, definitionLoader, routingService, store, waiter, launchCoordinator, _invocationHandlerLogger.Object);

        var request = new JsonRpcRequest
        {
            Id = "notify-rpc-disabled",
            Method = "hub.invoke.notify",
            Params = JsonSerializer.SerializeToElement(new
            {
                appId = "disabled-app",
                target = new { scope = (string?)null, instanceId = (string?)null },
                method = "disabled.call",
                args = new { },
                options = new { queueIfOffline = true, autoLaunch = false, ttlMs = 60000 }
            })
        };

        var response = await handler.HandleAsync(request, CancellationToken.None);

        Assert.NotNull(response.Error);
        Assert.Equal(-32002, response.Error.Code);
        Assert.Equal("forbidden", response.Error.Message);
        var data = JsonSerializer.SerializeToElement(response.Error.Data);
        Assert.Equal("rpc_disabled", data.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task InvocationHandler_Poll_WithPollDisabledInstance_ShouldReturnForbidden()
    {
        var appRegistry = new AppRegistry(_registryLogger.Object);
        appRegistry.RegisterInstance(new AppInstance
        {
            InstanceId = "poll-disabled",
            AppId = "poll.app",
            Scope = null,
            Pid = 3011,
            Invoke = new InvokeCapability { Poll = false, Respond = true }
        });

        var definitionLoader = new DefinitionLoader(_tempDirectory, _definitionLogger.Object);
        var routingService = new InvocationRoutingService(appRegistry, _routingLogger.Object);
        var store = new InvocationStore(_storeLogger.Object, routingService);
        var waiter = new InvocationRequestWaiter(Mock.Of<ILogger<InvocationRequestWaiter>>());
        var runtimeHttpBaseUrlProvider = new RuntimeHttpBaseUrlProvider(Mock.Of<ILogger<RuntimeHttpBaseUrlProvider>>());
        var launchCoordinator = new LaunchCoordinator(definitionLoader, appRegistry, runtimeHttpBaseUrlProvider, _launchLogger.Object);
        var handler = new InvocationHandler(appRegistry, definitionLoader, routingService, store, waiter, launchCoordinator, _invocationHandlerLogger.Object);

        var request = new JsonRpcRequest
        {
            Id = "poll-disabled",
            Method = "hub.invoke.poll",
            Params = JsonSerializer.SerializeToElement(new { instanceId = "poll-disabled", maxCount = 10, waitMs = 0 })
        };

        var response = await handler.HandleAsync(request, CancellationToken.None);

        Assert.NotNull(response.Error);
        Assert.Equal(-32002, response.Error.Code);
        Assert.Equal("forbidden", response.Error.Message);
        var data = JsonSerializer.SerializeToElement(response.Error.Data);
        Assert.Equal("poll_not_enabled", data.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task InvocationHandler_Poll_WithUnknownInstance_ShouldReturnInstanceNotFoundWithUnknownReason()
    {
        var handler = CreateInvocationHandler(new AppRegistry(_registryLogger.Object));

        var response = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "poll-unknown-instance",
            Method = "hub.invoke.poll",
            Params = JsonSerializer.SerializeToElement(new { instanceId = "missing-instance", maxCount = 1, waitMs = 0 })
        }, CancellationToken.None);

        Assert.NotNull(response.Error);
        Assert.Equal(-32010, response.Error.Code);
        Assert.Equal("instance_not_found", response.Error.Message);
        var data = JsonSerializer.SerializeToElement(response.Error.Data);
        Assert.Equal("unknown_instance", data.GetProperty("reason").GetString());
        Assert.Equal("missing-instance", data.GetProperty("instanceId").GetString());
    }

    [Fact]
    public async Task InvocationHandler_Respond_WithRespondDisabledInstance_ShouldReturnForbidden()
    {
        var appRegistry = new AppRegistry(_registryLogger.Object);
        appRegistry.RegisterInstance(new AppInstance
        {
            InstanceId = "respond-disabled",
            AppId = "respond.app",
            Scope = null,
            Pid = 3012,
            Invoke = new InvokeCapability { Poll = true, Respond = false }
        });

        var definitionLoader = new DefinitionLoader(_tempDirectory, _definitionLogger.Object);
        var routingService = new InvocationRoutingService(appRegistry, _routingLogger.Object);
        var store = new InvocationStore(_storeLogger.Object, routingService);
        var waiter = new InvocationRequestWaiter(Mock.Of<ILogger<InvocationRequestWaiter>>());
        var runtimeHttpBaseUrlProvider = new RuntimeHttpBaseUrlProvider(Mock.Of<ILogger<RuntimeHttpBaseUrlProvider>>());
        var launchCoordinator = new LaunchCoordinator(definitionLoader, appRegistry, runtimeHttpBaseUrlProvider, _launchLogger.Object);
        var handler = new InvocationHandler(appRegistry, definitionLoader, routingService, store, waiter, launchCoordinator, _invocationHandlerLogger.Object);

        var request = new JsonRpcRequest
        {
            Id = "respond-disabled",
            Method = "hub.invoke.respond",
            Params = JsonSerializer.SerializeToElement(new
            {
                instanceId = "respond-disabled",
                invocationId = "invk-non-existent",
                value = new { ok = true }
            })
        };

        var response = await handler.HandleAsync(request, CancellationToken.None);

        Assert.NotNull(response.Error);
        Assert.Equal(-32002, response.Error.Code);
        Assert.Equal("forbidden", response.Error.Message);
        var data = JsonSerializer.SerializeToElement(response.Error.Data);
        Assert.Equal("respond_not_enabled", data.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task InvocationHandler_Respond_WithUnknownInstance_ShouldReturnInstanceNotFoundWithUnknownReason()
    {
        var handler = CreateInvocationHandler(new AppRegistry(_registryLogger.Object));

        var response = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "respond-unknown-instance",
            Method = "hub.invoke.respond",
            Params = JsonSerializer.SerializeToElement(new
            {
                instanceId = "missing-instance",
                invocationId = "invk-missing",
                value = new { ok = true }
            })
        }, CancellationToken.None);

        Assert.NotNull(response.Error);
        Assert.Equal(-32010, response.Error.Code);
        Assert.Equal("instance_not_found", response.Error.Message);
        var data = JsonSerializer.SerializeToElement(response.Error.Data);
        Assert.Equal("unknown_instance", data.GetProperty("reason").GetString());
        Assert.Equal("missing-instance", data.GetProperty("instanceId").GetString());
    }

    [Fact]
    public async Task InvocationAndLaunchHandlers_ShouldReturnExpectedLaunchErrors()
    {
        var appRegistry = new AppRegistry(_registryLogger.Object);
        var definitionLoader = new DefinitionLoader(_tempDirectory, _definitionLogger.Object);
        definitionLoader.Load();
        var routingService = new InvocationRoutingService(appRegistry, _routingLogger.Object);
        var store = new InvocationStore(_storeLogger.Object, routingService);
        var waiter = new InvocationRequestWaiter(Mock.Of<ILogger<InvocationRequestWaiter>>());
        var runtimeHttpBaseUrlProvider = new RuntimeHttpBaseUrlProvider(Mock.Of<ILogger<RuntimeHttpBaseUrlProvider>>());
        var launchCoordinator = new LaunchCoordinator(definitionLoader, appRegistry, runtimeHttpBaseUrlProvider, _launchLogger.Object);
        var invocationHandler = new InvocationHandler(appRegistry, definitionLoader, routingService, store, waiter, launchCoordinator, _invocationHandlerLogger.Object);
        var launchHandler = new LaunchHandler(launchCoordinator, _launchHandlerLogger.Object);

        var requestResponse = await invocationHandler.HandleAsync(new JsonRpcRequest
        {
            Id = "request-runtime-check",
            Method = "hub.invoke.request",
            Params = JsonSerializer.SerializeToElement(new
            {
                appId = "test.app",
                target = new { scope = (string?)null, instanceId = (string?)null },
                method = "test.m",
                args = new { },
                options = new
                {
                    ttlMs = 1000,
                    waitTimeoutMs = 1000,
                    queueIfOffline = true,
                    autoLaunch = true
                }
            })
        }, CancellationToken.None);

        Assert.NotNull(requestResponse.Error);
        Assert.Equal(-32010, requestResponse.Error.Code);
        Assert.Equal("instance_not_found", requestResponse.Error.Message);
        var requestData = JsonSerializer.SerializeToElement(requestResponse.Error.Data);
        Assert.Equal("offline_no_queue", requestData.GetProperty("reason").GetString());

        var launchResponse = await launchHandler.HandleAsync(new JsonRpcRequest
        {
            Id = "launch-deferred",
            Method = "hub.apps.launch",
            Params = JsonSerializer.SerializeToElement(new { appId = "launch.app" })
        }, CancellationToken.None);

        Assert.NotNull(launchResponse.Error);
        Assert.Equal(-32014, launchResponse.Error.Code);
        Assert.Equal("app_definition_not_found", launchResponse.Error.Message);
        var launchData = JsonSerializer.SerializeToElement(launchResponse.Error.Data);
        Assert.Equal("launch.app", launchData.GetProperty("appId").GetString());
    }

    [Fact]
    public async Task InvocationHandler_Notify_AutoLaunchWithoutLaunchConfig_ShouldReturnLaunchFailed()
    {
        WriteDefinition("notify-launch-missing", rpcEnabled: true);

        var appRegistry = new AppRegistry(_registryLogger.Object);
        var definitionLoader = new DefinitionLoader(_tempDirectory, _definitionLogger.Object);
        definitionLoader.Load();
        var routingService = new InvocationRoutingService(appRegistry, _routingLogger.Object);
        var store = new InvocationStore(_storeLogger.Object, routingService);
        var waiter = new InvocationRequestWaiter(Mock.Of<ILogger<InvocationRequestWaiter>>());
        var runtimeHttpBaseUrlProvider = new RuntimeHttpBaseUrlProvider(Mock.Of<ILogger<RuntimeHttpBaseUrlProvider>>());
        var launchCoordinator = new LaunchCoordinator(definitionLoader, appRegistry, runtimeHttpBaseUrlProvider, _launchLogger.Object);
        var handler = new InvocationHandler(appRegistry, definitionLoader, routingService, store, waiter, launchCoordinator, _invocationHandlerLogger.Object);

        var request = new JsonRpcRequest
        {
            Id = "notify-launch-missing",
            Method = "hub.invoke.notify",
            Params = JsonSerializer.SerializeToElement(new
            {
                appId = "notify-launch-missing",
                target = new { scope = (string?)null, instanceId = (string?)null },
                method = "task.sync",
                args = new { },
                options = new
                {
                    ttlMs = 60000,
                    queueIfOffline = true,
                    autoLaunch = true
                }
            })
        };

        var response = await handler.HandleAsync(request, CancellationToken.None);

        Assert.NotNull(response.Error);
        Assert.Equal(-32020, response.Error.Code);
        Assert.Equal("launch_failed", response.Error.Message);
        var data = JsonSerializer.SerializeToElement(response.Error.Data);
        Assert.Equal("launch_config_missing", data.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task InvocationHandler_Notify_TtlBelowMinimum_ShouldReturnInvalidParams()
    {
        var handler = CreateInvocationHandler(new AppRegistry(_registryLogger.Object));

        var response = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "notify-ttl-below-minimum",
            Method = "hub.invoke.notify",
            Params = JsonSerializer.SerializeToElement(new
            {
                appId = "notify-ttl-below-min.app",
                target = new { scope = (string?)null, instanceId = (string?)null },
                method = "task.run",
                args = new { },
                options = new
                {
                    ttlMs = 999,
                    queueIfOffline = true,
                    autoLaunch = false
                }
            })
        }, CancellationToken.None);

        Assert.NotNull(response.Error);
        Assert.Equal(-32602, response.Error.Code);
        Assert.Equal("invalid_params", response.Error.Message);
    }

    [Fact]
    public async Task InvocationHandler_Request_TtlBelowMinimum_ShouldReturnInvalidParams()
    {
        var handler = CreateInvocationHandler(new AppRegistry(_registryLogger.Object));

        var response = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "request-ttl-below-minimum",
            Method = "hub.invoke.request",
            Params = JsonSerializer.SerializeToElement(new
            {
                appId = "request-ttl-below-min.app",
                target = new { scope = (string?)null, instanceId = (string?)null },
                method = "task.run",
                args = new { },
                options = new
                {
                    ttlMs = 999,
                    waitTimeoutMs = 500,
                    queueIfOffline = true,
                    autoLaunch = false
                }
            })
        }, CancellationToken.None);

        Assert.NotNull(response.Error);
        Assert.Equal(-32602, response.Error.Code);
        Assert.Equal("invalid_params", response.Error.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public async Task InvocationHandler_Poll_MaxCountOutOfRange_ShouldReturnInvalidParams(int maxCount)
    {
        var handler = CreateInvocationHandler(new AppRegistry(_registryLogger.Object));

        var response = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = $"poll-maxCount-{maxCount}",
            Method = "hub.invoke.poll",
            Params = JsonSerializer.SerializeToElement(new
            {
                instanceId = "poll-instance",
                maxCount,
                waitMs = 0
            })
        }, CancellationToken.None);

        Assert.NotNull(response.Error);
        Assert.Equal(-32602, response.Error.Code);
        Assert.Equal("invalid_params", response.Error.Message);
    }

    [Fact]
    public async Task InvocationHandler_Respond_WithValueAndError_ShouldReturnInvalidParams()
    {
        var handler = CreateInvocationHandler(new AppRegistry(_registryLogger.Object));

        var response = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "respond-both",
            Method = "hub.invoke.respond",
            Params = JsonSerializer.SerializeToElement(new
            {
                instanceId = "respond-instance",
                invocationId = "invk-both",
                value = new { ok = true },
                error = new { code = 1001, message = "app_error" }
            })
        }, CancellationToken.None);

        Assert.NotNull(response.Error);
        Assert.Equal(-32602, response.Error.Code);
        Assert.Equal("invalid_params", response.Error.Message);
    }

    [Fact]
    public async Task InvocationHandler_Respond_WithoutValueAndError_ShouldReturnInvalidParams()
    {
        var handler = CreateInvocationHandler(new AppRegistry(_registryLogger.Object));

        var response = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "respond-none",
            Method = "hub.invoke.respond",
            Params = JsonSerializer.SerializeToElement(new
            {
                instanceId = "respond-instance",
                invocationId = "invk-none"
            })
        }, CancellationToken.None);

        Assert.NotNull(response.Error);
        Assert.Equal(-32602, response.Error.Code);
        Assert.Equal("invalid_params", response.Error.Message);
    }

    [Fact]
    public async Task InvocationHandler_Respond_ByNonLeaseHolder_ShouldReturnDeliveryConflictWithCurrentLeaseHolder()
    {
        const string appId = "respond-delivery-conflict.app";
        WriteDefinition(appId, rpcEnabled: true);

        var appRegistry = new AppRegistry(_registryLogger.Object);
        appRegistry.RegisterInstance(new AppInstance
        {
            InstanceId = "respond-holder",
            AppId = appId,
            Scope = null,
            Pid = 3303,
            Invoke = new InvokeCapability { Poll = true, Respond = true }
        });
        appRegistry.RegisterInstance(new AppInstance
        {
            InstanceId = "respond-other",
            AppId = appId,
            Scope = null,
            Pid = 3304,
            Invoke = new InvokeCapability { Poll = true, Respond = true }
        });

        var handler = CreateInvocationHandler(appRegistry);

        var notifyResponse = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "notify-delivery-conflict",
            Method = "hub.invoke.notify",
            Params = JsonSerializer.SerializeToElement(new
            {
                appId,
                target = new { scope = (string?)null, instanceId = "respond-holder" },
                method = "task.run",
                args = new { },
                options = new
                {
                    ttlMs = 60000,
                    queueIfOffline = true,
                    autoLaunch = false
                }
            })
        }, CancellationToken.None);

        Assert.Null(notifyResponse.Error);

        var pollResponse = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "poll-delivery-conflict",
            Method = "hub.invoke.poll",
            Params = JsonSerializer.SerializeToElement(new
            {
                instanceId = "respond-holder",
                maxCount = 1,
                waitMs = 0
            })
        }, CancellationToken.None);

        Assert.Null(pollResponse.Error);
        var pollResult = JsonSerializer.SerializeToElement(pollResponse.Result);
        var invocationId = pollResult.GetProperty("items").EnumerateArray().Single().GetProperty("invocationId").GetString();
        Assert.False(string.IsNullOrWhiteSpace(invocationId));

        var conflictResponse = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "respond-delivery-conflict",
            Method = "hub.invoke.respond",
            Params = JsonSerializer.SerializeToElement(new
            {
                instanceId = "respond-other",
                invocationId,
                value = new { ok = true }
            })
        }, CancellationToken.None);

        Assert.NotNull(conflictResponse.Error);
        Assert.Equal(-32030, conflictResponse.Error.Code);
        Assert.Equal("delivery_conflict", conflictResponse.Error.Message);

        var conflictData = JsonSerializer.SerializeToElement(conflictResponse.Error.Data);
        Assert.Equal(invocationId, conflictData.GetProperty("invocationId").GetString());
        Assert.Equal("respond-holder", conflictData.GetProperty("currentLeaseHolder").GetString());
    }

    [Fact]
    public async Task InvocationHandler_Notify_AutoLaunchTrueAndQueueIfOfflineFalse_ShouldReturnInvalidParams()
    {
        var handler = CreateInvocationHandler(new AppRegistry(_registryLogger.Object));

        var response = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "notify-auto-launch-no-queue",
            Method = "hub.invoke.notify",
            Params = JsonSerializer.SerializeToElement(new
            {
                appId = "notify-auto-launch.app",
                target = new { scope = (string?)null, instanceId = (string?)null },
                method = "task.run",
                args = new { },
                options = new
                {
                    ttlMs = 60000,
                    queueIfOffline = false,
                    autoLaunch = true
                }
            })
        }, CancellationToken.None);

        Assert.NotNull(response.Error);
        Assert.Equal(-32602, response.Error.Code);
        Assert.Equal("invalid_params", response.Error.Message);
    }

    [Fact]
    public async Task InvocationHandler_Request_AutoLaunchTrueAndQueueIfOfflineFalse_ShouldReturnInvalidParams()
    {
        var handler = CreateInvocationHandler(new AppRegistry(_registryLogger.Object));

        var response = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "request-auto-launch-no-queue",
            Method = "hub.invoke.request",
            Params = JsonSerializer.SerializeToElement(new
            {
                appId = "request-auto-launch.app",
                target = new { scope = (string?)null, instanceId = (string?)null },
                method = "task.run",
                args = new { },
                options = new
                {
                    ttlMs = 5000,
                    waitTimeoutMs = 1000,
                    queueIfOffline = false,
                    autoLaunch = true
                }
            })
        }, CancellationToken.None);

        Assert.NotNull(response.Error);
        Assert.Equal(-32602, response.Error.Code);
        Assert.Equal("invalid_params", response.Error.Message);
    }

    [Fact]
    public async Task InvocationHandler_Notify_WithTargetInstanceIdAndAutoLaunchTrue_ShouldReturnInvalidParams()
    {
        var handler = CreateInvocationHandler(new AppRegistry(_registryLogger.Object));

        var response = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "notify-auto-launch-target-instance",
            Method = "hub.invoke.notify",
            Params = JsonSerializer.SerializeToElement(new
            {
                appId = "notify-target-instance.app",
                target = new { scope = (string?)null, instanceId = "instance-001" },
                method = "task.run",
                args = new { },
                options = new
                {
                    ttlMs = 60000,
                    queueIfOffline = true,
                    autoLaunch = true
                }
            })
        }, CancellationToken.None);

        Assert.NotNull(response.Error);
        Assert.Equal(-32602, response.Error.Code);
        Assert.Equal("invalid_params", response.Error.Message);
    }

    [Fact]
    public async Task InvocationHandler_Request_WithTargetInstanceIdAndAutoLaunchTrue_ShouldReturnInvalidParams()
    {
        var handler = CreateInvocationHandler(new AppRegistry(_registryLogger.Object));

        var response = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "request-auto-launch-target-instance",
            Method = "hub.invoke.request",
            Params = JsonSerializer.SerializeToElement(new
            {
                appId = "request-target-instance.app",
                target = new { scope = (string?)null, instanceId = "instance-002" },
                method = "task.run",
                args = new { },
                options = new
                {
                    ttlMs = 5000,
                    waitTimeoutMs = 1000,
                    queueIfOffline = true,
                    autoLaunch = true
                }
            })
        }, CancellationToken.None);

        Assert.NotNull(response.Error);
        Assert.Equal(-32602, response.Error.Code);
        Assert.Equal("invalid_params", response.Error.Message);
    }

    [Fact]
    public async Task InvocationHandler_Request_WithTargetInstanceIdAndOptionsOmitted_ShouldUseAutoLaunchFalseByDefault()
    {
        var handler = CreateInvocationHandler(new AppRegistry(_registryLogger.Object));

        var response = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "request-target-instance-default-options",
            Method = "hub.invoke.request",
            Params = JsonSerializer.SerializeToElement(new
            {
                appId = "request-target-instance-default-options.app",
                target = new { scope = (string?)null, instanceId = "missing-instance" },
                method = "task.run",
                args = new { }
            })
        }, CancellationToken.None);

        Assert.NotNull(response.Error);
        Assert.Equal(-32010, response.Error.Code);
        Assert.Equal("instance_not_found", response.Error.Message);
        var data = JsonSerializer.SerializeToElement(response.Error.Data);
        Assert.Equal("target_instance_missing", data.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task InvocationHandler_Poll_ShouldRefreshInstanceLastSeenUtc()
    {
        var appRegistry = new AppRegistry(_registryLogger.Object);
        var instance = appRegistry.RegisterInstance(new AppInstance
        {
            InstanceId = "poll-last-seen",
            AppId = "poll-last-seen.app",
            Scope = null,
            Pid = 3301,
            Invoke = new InvokeCapability { Poll = true, Respond = true }
        });
        instance.LastSeenUtc = DateTime.UtcNow.AddMinutes(-1);

        var handler = CreateInvocationHandler(appRegistry);
        var beforePoll = appRegistry.GetInstance("poll-last-seen")!.LastSeenUtc;

        await Task.Delay(10);

        var response = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "poll-refresh-last-seen",
            Method = "hub.invoke.poll",
            Params = JsonSerializer.SerializeToElement(new
            {
                instanceId = "poll-last-seen",
                maxCount = 1,
                waitMs = 0
            })
        }, CancellationToken.None);

        Assert.Null(response.Error);
        var afterPoll = appRegistry.GetInstance("poll-last-seen")!.LastSeenUtc;
        Assert.True(afterPoll > beforePoll);
    }

    [Fact]
    public async Task InvocationHandler_Poll_WhenQueueEmpty_ShouldWaitUntilWaitMsAndReturnEmptyItems()
    {
        var appRegistry = new AppRegistry(_registryLogger.Object);
        appRegistry.RegisterInstance(new AppInstance
        {
            InstanceId = "poll-empty-wait",
            AppId = "poll-empty-wait.app",
            Scope = null,
            Pid = 3305,
            Invoke = new InvokeCapability { Poll = true, Respond = true }
        });

        var handler = CreateInvocationHandler(appRegistry);
        var stopwatch = Stopwatch.StartNew();

        var response = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "poll-empty-wait",
            Method = "hub.invoke.poll",
            Params = JsonSerializer.SerializeToElement(new
            {
                instanceId = "poll-empty-wait",
                maxCount = 1,
                waitMs = 150
            })
        }, CancellationToken.None);

        stopwatch.Stop();

        Assert.Null(response.Error);
        var result = JsonSerializer.SerializeToElement(response.Result);
        Assert.True(result.GetProperty("ok").GetBoolean());
        Assert.True(result.TryGetProperty("serverTimeUtc", out var serverTimeUtc));
        Assert.False(string.IsNullOrWhiteSpace(serverTimeUtc.GetString()));
        Assert.Empty(result.GetProperty("items").EnumerateArray());
        Assert.True(stopwatch.ElapsedMilliseconds >= 80);
    }

    [Fact]
    public async Task InvocationHandler_Respond_Success_ShouldRefreshInstanceLastSeenUtc()
    {
        var appRegistry = new AppRegistry(_registryLogger.Object);
        appRegistry.RegisterInstance(new AppInstance
        {
            InstanceId = "respond-last-seen",
            AppId = "respond-last-seen.app",
            Scope = null,
            Pid = 3302,
            Invoke = new InvokeCapability { Poll = true, Respond = true }
        });

        var handler = CreateInvocationHandler(appRegistry);

        var notifyResponse = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "notify-last-seen",
            Method = "hub.invoke.notify",
            Params = JsonSerializer.SerializeToElement(new
            {
                appId = "respond-last-seen.app",
                target = new { scope = (string?)null, instanceId = "respond-last-seen" },
                method = "task.run",
                args = new { },
                options = new
                {
                    ttlMs = 60000,
                    queueIfOffline = true,
                    autoLaunch = false
                }
            })
        }, CancellationToken.None);

        Assert.Null(notifyResponse.Error);

        var pollResponse = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "poll-last-seen",
            Method = "hub.invoke.poll",
            Params = JsonSerializer.SerializeToElement(new
            {
                instanceId = "respond-last-seen",
                maxCount = 1,
                waitMs = 0
            })
        }, CancellationToken.None);

        Assert.Null(pollResponse.Error);
        var pollResult = JsonSerializer.SerializeToElement(pollResponse.Result);
        var invocationId = pollResult.GetProperty("items").EnumerateArray().Single().GetProperty("invocationId").GetString();
        Assert.False(string.IsNullOrWhiteSpace(invocationId));

        var beforeRespond = appRegistry.GetInstance("respond-last-seen")!.LastSeenUtc;
        await Task.Delay(10);

        var respondResponse = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "respond-last-seen",
            Method = "hub.invoke.respond",
            Params = JsonSerializer.SerializeToElement(new
            {
                instanceId = "respond-last-seen",
                invocationId,
                value = new { ok = true }
            })
        }, CancellationToken.None);

        Assert.Null(respondResponse.Error);
        var afterRespond = appRegistry.GetInstance("respond-last-seen")!.LastSeenUtc;
        Assert.True(afterRespond > beforeRespond);
    }

    /// <summary>
    /// 释放测试资源。
    /// </summary>
    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }

    private InvocationHandler CreateInvocationHandler(AppRegistry appRegistry)
    {
        var definitionLoader = new DefinitionLoader(_tempDirectory, _definitionLogger.Object);
        definitionLoader.Load();
        var routingService = new InvocationRoutingService(appRegistry, _routingLogger.Object);
        var store = new InvocationStore(_storeLogger.Object, routingService);
        var waiter = new InvocationRequestWaiter(Mock.Of<ILogger<InvocationRequestWaiter>>());
        var runtimeHttpBaseUrlProvider = new RuntimeHttpBaseUrlProvider(Mock.Of<ILogger<RuntimeHttpBaseUrlProvider>>());
        var launchCoordinator = new LaunchCoordinator(definitionLoader, appRegistry, runtimeHttpBaseUrlProvider, _launchLogger.Object);
        return new InvocationHandler(appRegistry, definitionLoader, routingService, store, waiter, launchCoordinator, _invocationHandlerLogger.Object);
    }

    private void WriteDefinition(string appId, bool rpcEnabled, bool includeLaunch = false, string? dedupeKeyTemplate = null)
    {
        var filePath = Path.Combine(_tempDirectory, $"{appId}.json");
        var payload = new Dictionary<string, object?>
        {
            ["appId"] = appId,
            ["displayName"] = appId,
            ["capabilities"] = new Dictionary<string, object?>
            {
                ["rpc"] = rpcEnabled,
                ["events"] = false
            }
        };

        if (includeLaunch)
        {
            var launch = new Dictionary<string, object?>
            {
                ["exePath"] = "dotnet",
                ["argsTemplate"] = "--version"
            };

            if (!string.IsNullOrWhiteSpace(dedupeKeyTemplate))
            {
                launch["dedupeKeyTemplate"] = dedupeKeyTemplate;
            }

            payload["launch"] = launch;
        }

        File.WriteAllText(filePath, JsonSerializer.Serialize(payload));
    }
}
