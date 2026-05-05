namespace DevHub.Tests;

using System.Diagnostics;
using System.Text.Json;
using DevHub.Core.Models;
using DevHub.Core.Models.Rpc;
using DevHub.Core.Services;
using DevHub.Core.Services.Abstractions;
using DevHub.Core.Services.Invocation;
using DevHub.Core.Services.Rpc;
using DevHub.Core.Services.Rpc.Handlers;
using Microsoft.Extensions.Logging;
using Moq;

/// <summary>
/// Invocation 路由与门禁相关测试。
/// </summary>
[Trait("Category", "Impl")]
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
    public async Task Impl_InvocationHandler_Notify_WithRpcDisabledDefinition_ShouldReturnForbidden()
    {
        WriteDefinition("disabled-app", rpcEnabled: false);

        var appRegistry = new AppRegistry(new SystemClock(), _registryLogger.Object);
        var definitionLoader = new DefinitionLoader(DefinitionCatalogTestHelper.GetCatalogPath(_tempDirectory), _definitionLogger.Object);
        var definitionProvider = new DefinitionProvider(definitionLoader);
        definitionProvider.Refresh();
        var routingService = new InvocationRoutingService(appRegistry, _routingLogger.Object);
        var store = new InvocationStore(_storeLogger.Object, routingService, new SystemClock());
        var waiter = new InvocationRequestWaiter(Mock.Of<ILogger<InvocationRequestWaiter>>());
        var runtimeHttpBaseUrlProvider = new RuntimeHttpBaseUrlProvider(Mock.Of<ILogger<RuntimeHttpBaseUrlProvider>>(), RuntimePathOptions.Resolve());
        var launchCoordinator = new LaunchCoordinator(definitionProvider, appRegistry, runtimeHttpBaseUrlProvider, new ProcessLauncher(), new SystemClock(), _launchLogger.Object);
        var handler = new InvocationHandler(appRegistry, definitionProvider, routingService, store, waiter, launchCoordinator, new SystemClock(), _invocationHandlerLogger.Object);

        var request = new JsonRpcRequest
        {
            Id = "notify-rpc-disabled",
            Method = "hub.invoke.notify",
            Params = JsonSerializer.SerializeToElement(new
            {
                appId = "disabled-app",
                target = new { scope = ScopeContract.Global, instanceId = (string?)null },
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
    public async Task Impl_InvocationHandler_Poll_WhenLeaseSecondsOverridden_ShouldExposeConfiguredLeaseSeconds()
    {
        using var leaseScope = new EnvironmentVariableScope(RuntimeTuningOptions.LeaseSecondsEnvironmentVariable, "45");
        var tuningOptions = RuntimeTuningOptions.Resolve(Mock.Of<ILogger<RuntimeTuningOptions>>());

        const string appId = "lease-override.app";
        const string instanceId = "lease-override-inst";
        WriteDefinition(appId, rpcEnabled: true);

        var appRegistry = new AppRegistry(new SystemClock(), _registryLogger.Object, tuningOptions);
        appRegistry.RegisterInstance(new AppInstance
        {
            InstanceId = instanceId,
            AppId = appId,
            Scope = ScopeContract.Global,
            Pid = 3010,
            Invoke = new InvokeCapability { Poll = true, Respond = true }
        });

        var handler = CreateInvocationHandler(appRegistry, tuningOptions);

        var notifyResponse = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "lease-override-notify",
            Method = "hub.invoke.notify",
            Params = JsonSerializer.SerializeToElement(new
            {
                appId,
                target = new { scope = ScopeContract.Global, instanceId = (string?)null },
                method = "lease.override",
                args = new { ok = true },
                options = new { queueIfOffline = false, autoLaunch = false, ttlMs = 60000 }
            })
        }, CancellationToken.None);

        Assert.Null(notifyResponse.Error);
        var notifyResult = JsonSerializer.SerializeToElement(notifyResponse.Result);
        var invocationId = notifyResult.GetProperty("invocationId").GetString();
        Assert.False(string.IsNullOrWhiteSpace(invocationId));

        var pollResponse = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "lease-override-poll",
            Method = "hub.invoke.poll",
            Params = JsonSerializer.SerializeToElement(new
            {
                instanceId,
                instanceSessionToken = GetInstanceSessionToken(appRegistry, instanceId),
                maxCount = 1,
                waitMs = 0
            })
        }, CancellationToken.None);

        Assert.Null(pollResponse.Error);
        var pollResult = JsonSerializer.SerializeToElement(pollResponse.Result);
        var items = pollResult.GetProperty("items").EnumerateArray().ToList();
        Assert.Single(items);
        Assert.Equal(invocationId, items[0].GetProperty("invocationId").GetString());
        Assert.Equal(45, items[0].GetProperty("delivery").GetProperty("leaseSeconds").GetInt32());
    }

    [Fact]
    public async Task Impl_InvocationHandler_Poll_WithPollDisabledInstance_ShouldReturnForbidden()
    {
        var appRegistry = new AppRegistry(new SystemClock(), _registryLogger.Object);
        appRegistry.RegisterInstance(new AppInstance
        {
            InstanceId = "poll-disabled",
            AppId = "poll.app",
            Scope = ScopeContract.Global,
            Pid = 3011,
            Invoke = new InvokeCapability { Poll = false, Respond = true }
        });

        var definitionLoader = new DefinitionLoader(DefinitionCatalogTestHelper.GetCatalogPath(_tempDirectory), _definitionLogger.Object);
        var definitionProvider = new DefinitionProvider(definitionLoader);
        definitionProvider.Refresh();
        var routingService = new InvocationRoutingService(appRegistry, _routingLogger.Object);
        var store = new InvocationStore(_storeLogger.Object, routingService, new SystemClock());
        var waiter = new InvocationRequestWaiter(Mock.Of<ILogger<InvocationRequestWaiter>>());
        var runtimeHttpBaseUrlProvider = new RuntimeHttpBaseUrlProvider(Mock.Of<ILogger<RuntimeHttpBaseUrlProvider>>(), RuntimePathOptions.Resolve());
        var launchCoordinator = new LaunchCoordinator(definitionProvider, appRegistry, runtimeHttpBaseUrlProvider, new ProcessLauncher(), new SystemClock(), _launchLogger.Object);
        var handler = new InvocationHandler(appRegistry, definitionProvider, routingService, store, waiter, launchCoordinator, new SystemClock(), _invocationHandlerLogger.Object);

        var request = new JsonRpcRequest
        {
            Id = "poll-disabled",
            Method = "hub.invoke.poll",
            Params = JsonSerializer.SerializeToElement(new
            {
                instanceId = "poll-disabled",
                instanceSessionToken = GetInstanceSessionToken(appRegistry, "poll-disabled"),
                maxCount = 10,
                waitMs = 0
            })
        };

        var response = await handler.HandleAsync(request, CancellationToken.None);

        Assert.NotNull(response.Error);
        Assert.Equal(-32002, response.Error.Code);
        Assert.Equal("forbidden", response.Error.Message);
        var data = JsonSerializer.SerializeToElement(response.Error.Data);
        Assert.Equal("poll_not_enabled", data.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Impl_InvocationHandler_Poll_WithUnknownInstance_ShouldReturnInstanceNotFoundWithUnknownReason()
    {
        var handler = CreateInvocationHandler(new AppRegistry(new SystemClock(), _registryLogger.Object));

        var response = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "poll-unknown-instance",
            Method = "hub.invoke.poll",
            Params = JsonSerializer.SerializeToElement(new { instanceId = "missing-instance", instanceSessionToken = "missing-instance-token", maxCount = 1, waitMs = 0 })
        }, CancellationToken.None);

        Assert.NotNull(response.Error);
        Assert.Equal(-32010, response.Error.Code);
        Assert.Equal("instance_not_found", response.Error.Message);
        var data = JsonSerializer.SerializeToElement(response.Error.Data);
        Assert.Equal("unknown_instance", data.GetProperty("reason").GetString());
        Assert.Equal("missing-instance", data.GetProperty("instanceId").GetString());
    }

    [Fact]
    public async Task Impl_InvocationHandler_Respond_WithRespondDisabledInstance_ShouldReturnForbidden()
    {
        var appRegistry = new AppRegistry(new SystemClock(), _registryLogger.Object);
        appRegistry.RegisterInstance(new AppInstance
        {
            InstanceId = "respond-disabled",
            AppId = "respond.app",
            Scope = ScopeContract.Global,
            Pid = 3012,
            Invoke = new InvokeCapability { Poll = true, Respond = false }
        });

        var definitionLoader = new DefinitionLoader(DefinitionCatalogTestHelper.GetCatalogPath(_tempDirectory), _definitionLogger.Object);
        var definitionProvider = new DefinitionProvider(definitionLoader);
        definitionProvider.Refresh();
        var routingService = new InvocationRoutingService(appRegistry, _routingLogger.Object);
        var store = new InvocationStore(_storeLogger.Object, routingService, new SystemClock());
        var waiter = new InvocationRequestWaiter(Mock.Of<ILogger<InvocationRequestWaiter>>());
        var runtimeHttpBaseUrlProvider = new RuntimeHttpBaseUrlProvider(Mock.Of<ILogger<RuntimeHttpBaseUrlProvider>>(), RuntimePathOptions.Resolve());
        var launchCoordinator = new LaunchCoordinator(definitionProvider, appRegistry, runtimeHttpBaseUrlProvider, new ProcessLauncher(), new SystemClock(), _launchLogger.Object);
        var handler = new InvocationHandler(appRegistry, definitionProvider, routingService, store, waiter, launchCoordinator, new SystemClock(), _invocationHandlerLogger.Object);

        var request = new JsonRpcRequest
        {
            Id = "respond-disabled",
            Method = "hub.invoke.respond",
            Params = JsonSerializer.SerializeToElement(new
            {
                instanceId = "respond-disabled",
                instanceSessionToken = GetInstanceSessionToken(appRegistry, "respond-disabled"),
                invocationId = "invk-non-existent",
                leaseToken = "missing-lease-token",
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
    public async Task Impl_InvocationHandler_Respond_WithUnknownInstance_ShouldReturnInstanceNotFoundWithUnknownReason()
    {
        var handler = CreateInvocationHandler(new AppRegistry(new SystemClock(), _registryLogger.Object));

        var response = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "respond-unknown-instance",
            Method = "hub.invoke.respond",
            Params = JsonSerializer.SerializeToElement(new
            {
                instanceId = "missing-instance",
                instanceSessionToken = "missing-instance-token",
                invocationId = "invk-missing",
                leaseToken = "missing-lease-token",
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
    public async Task Impl_InvocationAndLaunchHandlers_ShouldReturnExpectedLaunchErrors()
    {
        var appRegistry = new AppRegistry(new SystemClock(), _registryLogger.Object);
        var definitionLoader = new DefinitionLoader(DefinitionCatalogTestHelper.GetCatalogPath(_tempDirectory), _definitionLogger.Object);
        var definitionProvider = new DefinitionProvider(definitionLoader);
        definitionProvider.Refresh();
        var routingService = new InvocationRoutingService(appRegistry, _routingLogger.Object);
        var store = new InvocationStore(_storeLogger.Object, routingService, new SystemClock());
        var waiter = new InvocationRequestWaiter(Mock.Of<ILogger<InvocationRequestWaiter>>());
        var runtimeHttpBaseUrlProvider = new RuntimeHttpBaseUrlProvider(Mock.Of<ILogger<RuntimeHttpBaseUrlProvider>>(), RuntimePathOptions.Resolve());
        var launchCoordinator = new LaunchCoordinator(definitionProvider, appRegistry, runtimeHttpBaseUrlProvider, new ProcessLauncher(), new SystemClock(), _launchLogger.Object);
        var invocationHandler = new InvocationHandler(appRegistry, definitionProvider, routingService, store, waiter, launchCoordinator, new SystemClock(), _invocationHandlerLogger.Object);
        var launchHandler = new LaunchHandler(launchCoordinator, _launchHandlerLogger.Object);

        var requestResponse = await invocationHandler.HandleAsync(new JsonRpcRequest
        {
            Id = "request-runtime-check",
            Method = "hub.invoke.request",
            Params = JsonSerializer.SerializeToElement(new
            {
                appId = "test.app",
                target = new { scope = ScopeContract.Global, instanceId = (string?)null },
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
            Params = JsonSerializer.SerializeToElement(new { appId = "launch.app", scope = ScopeContract.Global })
        }, CancellationToken.None);

        Assert.NotNull(launchResponse.Error);
        Assert.Equal(-32014, launchResponse.Error.Code);
        Assert.Equal("app_definition_not_found", launchResponse.Error.Message);
        var launchData = JsonSerializer.SerializeToElement(launchResponse.Error.Data);
        Assert.Equal("launch.app", launchData.GetProperty("appId").GetString());
    }

    [Fact]
    public async Task Impl_InvocationHandler_Notify_AutoLaunchWithoutLaunchConfig_ShouldReturnLaunchFailed()
    {
        WriteDefinition("notify-launch-missing", rpcEnabled: true);

        var appRegistry = new AppRegistry(new SystemClock(), _registryLogger.Object);
        var definitionLoader = new DefinitionLoader(DefinitionCatalogTestHelper.GetCatalogPath(_tempDirectory), _definitionLogger.Object);
        var definitionProvider = new DefinitionProvider(definitionLoader);
        definitionProvider.Refresh();
        var routingService = new InvocationRoutingService(appRegistry, _routingLogger.Object);
        var store = new InvocationStore(_storeLogger.Object, routingService, new SystemClock());
        var waiter = new InvocationRequestWaiter(Mock.Of<ILogger<InvocationRequestWaiter>>());
        var runtimeHttpBaseUrlProvider = new RuntimeHttpBaseUrlProvider(Mock.Of<ILogger<RuntimeHttpBaseUrlProvider>>(), RuntimePathOptions.Resolve());
        var launchCoordinator = new LaunchCoordinator(definitionProvider, appRegistry, runtimeHttpBaseUrlProvider, new ProcessLauncher(), new SystemClock(), _launchLogger.Object);
        var handler = new InvocationHandler(appRegistry, definitionProvider, routingService, store, waiter, launchCoordinator, new SystemClock(), _invocationHandlerLogger.Object);

        var request = new JsonRpcRequest
        {
            Id = "notify-launch-missing",
            Method = "hub.invoke.notify",
            Params = JsonSerializer.SerializeToElement(new
            {
                appId = "notify-launch-missing",
                target = new { scope = ScopeContract.Global, instanceId = (string?)null },
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
    public async Task Impl_InvocationHandler_Notify_TtlBelowMinimum_ShouldReturnInvalidParams()
    {
        var handler = CreateInvocationHandler(new AppRegistry(new SystemClock(), _registryLogger.Object));

        var response = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "notify-ttl-below-minimum",
            Method = "hub.invoke.notify",
            Params = JsonSerializer.SerializeToElement(new
            {
                appId = "notify-ttl-below-min.app",
                target = new { scope = ScopeContract.Global, instanceId = (string?)null },
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
    public async Task Impl_InvocationHandler_Request_TtlBelowMinimum_ShouldReturnInvalidParams()
    {
        var handler = CreateInvocationHandler(new AppRegistry(new SystemClock(), _registryLogger.Object));

        var response = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "request-ttl-below-minimum",
            Method = "hub.invoke.request",
            Params = JsonSerializer.SerializeToElement(new
            {
                appId = "request-ttl-below-min.app",
                target = new { scope = ScopeContract.Global, instanceId = (string?)null },
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
    public async Task Impl_InvocationHandler_Poll_MaxCountOutOfRange_ShouldReturnInvalidParams(int maxCount)
    {
        var handler = CreateInvocationHandler(new AppRegistry(new SystemClock(), _registryLogger.Object));

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
    public async Task Impl_InvocationHandler_Respond_WithValueAndError_ShouldReturnInvalidParams()
    {
        var handler = CreateInvocationHandler(new AppRegistry(new SystemClock(), _registryLogger.Object));

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
    public async Task Impl_InvocationHandler_Respond_WithoutValueAndError_ShouldReturnInvalidParams()
    {
        var handler = CreateInvocationHandler(new AppRegistry(new SystemClock(), _registryLogger.Object));

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
    public async Task Impl_InvocationHandler_Respond_ByNonLeaseHolder_ShouldReturnDeliveryConflictWithCurrentLeaseHolder()
    {
        const string appId = "respond-delivery-conflict.app";
        WriteDefinition(appId, rpcEnabled: true);

        var appRegistry = new AppRegistry(new SystemClock(), _registryLogger.Object);
        appRegistry.RegisterInstance(new AppInstance
        {
            InstanceId = "respond-holder",
            AppId = appId,
            Scope = ScopeContract.Global,
            Pid = 3303,
            Invoke = new InvokeCapability { Poll = true, Respond = true }
        });
        appRegistry.RegisterInstance(new AppInstance
        {
            InstanceId = "respond-other",
            AppId = appId,
            Scope = ScopeContract.Global,
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
                target = new { scope = ScopeContract.Global, instanceId = "respond-holder" },
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
                instanceSessionToken = GetInstanceSessionToken(appRegistry, "respond-holder"),
                maxCount = 1,
                waitMs = 0
            })
        }, CancellationToken.None);

        Assert.Null(pollResponse.Error);
        var pollResult = JsonSerializer.SerializeToElement(pollResponse.Result);
        var pollItem = pollResult.GetProperty("items").EnumerateArray().Single();
        var invocationId = pollItem.GetProperty("invocationId").GetString();
        var leaseToken = pollItem.GetProperty("delivery").GetProperty("leaseToken").GetString();
        Assert.False(string.IsNullOrWhiteSpace(invocationId));

        var conflictResponse = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "respond-delivery-conflict",
            Method = "hub.invoke.respond",
            Params = JsonSerializer.SerializeToElement(new
            {
                instanceId = "respond-other",
                instanceSessionToken = GetInstanceSessionToken(appRegistry, "respond-other"),
                invocationId,
                leaseToken,
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
    public async Task Impl_InvocationHandler_Notify_AutoLaunchTrueAndQueueIfOfflineFalse_ShouldReturnInvalidParams()
    {
        var handler = CreateInvocationHandler(new AppRegistry(new SystemClock(), _registryLogger.Object));

        var response = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "notify-auto-launch-no-queue",
            Method = "hub.invoke.notify",
            Params = JsonSerializer.SerializeToElement(new
            {
                appId = "notify-auto-launch.app",
                target = new { scope = ScopeContract.Global, instanceId = (string?)null },
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
    public async Task Impl_InvocationHandler_Request_AutoLaunchTrueAndQueueIfOfflineFalse_ShouldReturnInvalidParams()
    {
        var handler = CreateInvocationHandler(new AppRegistry(new SystemClock(), _registryLogger.Object));

        var response = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "request-auto-launch-no-queue",
            Method = "hub.invoke.request",
            Params = JsonSerializer.SerializeToElement(new
            {
                appId = "request-auto-launch.app",
                target = new { scope = ScopeContract.Global, instanceId = (string?)null },
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
    public async Task Impl_InvocationHandler_Notify_WithTargetInstanceIdAndAutoLaunchTrue_ShouldReturnInvalidParams()
    {
        var handler = CreateInvocationHandler(new AppRegistry(new SystemClock(), _registryLogger.Object));

        var response = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "notify-auto-launch-target-instance",
            Method = "hub.invoke.notify",
            Params = JsonSerializer.SerializeToElement(new
            {
                appId = "notify-target-instance.app",
                target = new { scope = ScopeContract.Global, instanceId = "instance-001" },
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
    public async Task Impl_InvocationHandler_Request_WithTargetInstanceIdAndAutoLaunchTrue_ShouldReturnInvalidParams()
    {
        var handler = CreateInvocationHandler(new AppRegistry(new SystemClock(), _registryLogger.Object));

        var response = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "request-auto-launch-target-instance",
            Method = "hub.invoke.request",
            Params = JsonSerializer.SerializeToElement(new
            {
                appId = "request-target-instance.app",
                target = new { scope = ScopeContract.Global, instanceId = "instance-002" },
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
    public async Task Impl_InvocationHandler_Request_WithTargetInstanceIdAndOptionsOmitted_ShouldUseAutoLaunchFalseByDefault()
    {
        var handler = CreateInvocationHandler(new AppRegistry(new SystemClock(), _registryLogger.Object));

        var response = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "request-target-instance-default-options",
            Method = "hub.invoke.request",
            Params = JsonSerializer.SerializeToElement(new
            {
                appId = "request-target-instance-default-options.app",
                target = new { scope = ScopeContract.Global, instanceId = "missing-instance" },
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
    public async Task Impl_InvocationHandler_Poll_ShouldRefreshInstanceLastSeenUtc()
    {
        var clock = new MutableClock(DateTime.UtcNow);
        var appRegistry = new AppRegistry(clock, _registryLogger.Object);
        appRegistry.RegisterInstance(new AppInstance
        {
            InstanceId = "poll-last-seen",
            AppId = "poll-last-seen.app",
            Scope = ScopeContract.Global,
            Pid = 3301,
            Invoke = new InvokeCapability { Poll = true, Respond = true }
        });

        var handler = CreateInvocationHandler(appRegistry, clock: clock);
        var beforePoll = appRegistry.GetInstance("poll-last-seen")!.LastSeenUtc;

        clock.Advance(TimeSpan.FromSeconds(1));

        var response = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "poll-refresh-last-seen",
            Method = "hub.invoke.poll",
            Params = JsonSerializer.SerializeToElement(new
            {
                instanceId = "poll-last-seen",
                instanceSessionToken = GetInstanceSessionToken(appRegistry, "poll-last-seen"),
                maxCount = 1,
                waitMs = 0
            })
        }, CancellationToken.None);

        Assert.Null(response.Error);
        var afterPoll = appRegistry.GetInstance("poll-last-seen")!.LastSeenUtc;
        Assert.True(afterPoll > beforePoll);
    }

    [Fact]
    public async Task Impl_InvocationHandler_Poll_WhenQueueEmpty_ShouldWaitUntilWaitMsAndReturnEmptyItems()
    {
        var appRegistry = new AppRegistry(new SystemClock(), _registryLogger.Object);
        appRegistry.RegisterInstance(new AppInstance
        {
            InstanceId = "poll-empty-wait",
            AppId = "poll-empty-wait.app",
            Scope = ScopeContract.Global,
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
                instanceSessionToken = GetInstanceSessionToken(appRegistry, "poll-empty-wait"),
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
    public async Task Impl_InvocationHandler_Respond_Success_ShouldRefreshInstanceLastSeenUtc()
    {
        var clock = new MutableClock(DateTime.UtcNow);
        var appRegistry = new AppRegistry(clock, _registryLogger.Object);
        appRegistry.RegisterInstance(new AppInstance
        {
            InstanceId = "respond-last-seen",
            AppId = "respond-last-seen.app",
            Scope = ScopeContract.Global,
            Pid = 3302,
            Invoke = new InvokeCapability { Poll = true, Respond = true }
        });

        var handler = CreateInvocationHandler(appRegistry, clock: clock);

        var notifyResponse = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "notify-last-seen",
            Method = "hub.invoke.notify",
            Params = JsonSerializer.SerializeToElement(new
            {
                appId = "respond-last-seen.app",
                target = new { scope = ScopeContract.Global, instanceId = "respond-last-seen" },
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
                instanceSessionToken = GetInstanceSessionToken(appRegistry, "respond-last-seen"),
                maxCount = 1,
                waitMs = 0
            })
        }, CancellationToken.None);

        Assert.Null(pollResponse.Error);
        var pollResult = JsonSerializer.SerializeToElement(pollResponse.Result);
        var pollItem = pollResult.GetProperty("items").EnumerateArray().Single();
        var invocationId = pollItem.GetProperty("invocationId").GetString();
        var leaseToken = pollItem.GetProperty("delivery").GetProperty("leaseToken").GetString();
        Assert.False(string.IsNullOrWhiteSpace(invocationId));

        var beforeRespond = appRegistry.GetInstance("respond-last-seen")!.LastSeenUtc;
        clock.Advance(TimeSpan.FromSeconds(1));

        var respondResponse = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "respond-last-seen",
            Method = "hub.invoke.respond",
            Params = JsonSerializer.SerializeToElement(new
            {
                instanceId = "respond-last-seen",
                instanceSessionToken = GetInstanceSessionToken(appRegistry, "respond-last-seen"),
                invocationId,
                leaseToken,
                value = new { ok = true }
            })
        }, CancellationToken.None);

        Assert.Null(respondResponse.Error);
        var afterRespond = appRegistry.GetInstance("respond-last-seen")!.LastSeenUtc;
        Assert.True(afterRespond > beforeRespond);
    }

    [Fact]
    public async Task Impl_InvocationHandler_Notify_WithScalarArgs_ShouldAllowAndPreserveValue()
    {
        var appRegistry = new AppRegistry(new SystemClock(), _registryLogger.Object);
        appRegistry.RegisterInstance(new AppInstance
        {
            InstanceId = "scalar-args-inst",
            AppId = "scalar-args.app",
            Scope = ScopeContract.Global,
            Pid = 3621,
            Invoke = new InvokeCapability { Poll = true, Respond = true }
        });

        var handler = CreateInvocationHandler(appRegistry);

        var notifyResponse = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "notify-scalar-args",
            Method = "hub.invoke.notify",
            Params = JsonSerializer.SerializeToElement(new
            {
                appId = "scalar-args.app",
                target = new { scope = ScopeContract.Global, instanceId = (string?)null },
                method = "scalar.echo",
                args = "hello-scalar",
                options = new { ttlMs = 60000, queueIfOffline = true, autoLaunch = false }
            })
        }, CancellationToken.None);

        Assert.Null(notifyResponse.Error);
        var notifyResult = JsonSerializer.SerializeToElement(notifyResponse.Result);
        var invocationId = notifyResult.GetProperty("invocationId").GetString();
        Assert.False(string.IsNullOrWhiteSpace(invocationId));

        var pollResponse = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "poll-scalar-args",
            Method = "hub.invoke.poll",
            Params = JsonSerializer.SerializeToElement(new
            {
                instanceId = "scalar-args-inst",
                instanceSessionToken = GetInstanceSessionToken(appRegistry, "scalar-args-inst"),
                maxCount = 1,
                waitMs = 0
            })
        }, CancellationToken.None);

        Assert.Null(pollResponse.Error);
        var pollResult = JsonSerializer.SerializeToElement(pollResponse.Result);
        var items = pollResult.GetProperty("items").EnumerateArray().ToList();
        Assert.Single(items);
        Assert.Equal(invocationId, items[0].GetProperty("invocationId").GetString());
        Assert.Equal(JsonValueKind.String, items[0].GetProperty("args").ValueKind);
        Assert.Equal("hello-scalar", items[0].GetProperty("args").GetString());
    }

    [Fact]
    public async Task Impl_InvocationHandler_Notify_WhenArgsOmitted_ShouldKeepNullInsteadOfEmptyObject()
    {
        var appRegistry = new AppRegistry(new SystemClock(), _registryLogger.Object);
        appRegistry.RegisterInstance(new AppInstance
        {
            InstanceId = "missing-args-inst",
            AppId = "missing-args.app",
            Scope = ScopeContract.Global,
            Pid = 3622,
            Invoke = new InvokeCapability { Poll = true, Respond = true }
        });

        var handler = CreateInvocationHandler(appRegistry);

        var notifyResponse = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "notify-missing-args",
            Method = "hub.invoke.notify",
            Params = JsonSerializer.SerializeToElement(new
            {
                appId = "missing-args.app",
                target = new { scope = ScopeContract.Global, instanceId = (string?)null },
                method = "missing.args",
                options = new { ttlMs = 60000, queueIfOffline = true, autoLaunch = false }
            })
        }, CancellationToken.None);

        Assert.Null(notifyResponse.Error);

        var pollResponse = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "poll-missing-args",
            Method = "hub.invoke.poll",
            Params = JsonSerializer.SerializeToElement(new
            {
                instanceId = "missing-args-inst",
                instanceSessionToken = GetInstanceSessionToken(appRegistry, "missing-args-inst"),
                maxCount = 1,
                waitMs = 0
            })
        }, CancellationToken.None);

        Assert.Null(pollResponse.Error);
        var pollResult = JsonSerializer.SerializeToElement(pollResponse.Result);
        var item = pollResult.GetProperty("items").EnumerateArray().Single();
        Assert.Equal(JsonValueKind.Null, item.GetProperty("args").ValueKind);
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

    private InvocationHandler CreateInvocationHandler(
        AppRegistry appRegistry,
        RuntimeTuningOptions? runtimeTuningOptions = null,
        IClock? clock = null)
    {
        runtimeTuningOptions ??= RuntimeTuningOptions.Default;
        var effectiveClock = clock ?? new SystemClock();
        var definitionLoader = new DefinitionLoader(DefinitionCatalogTestHelper.GetCatalogPath(_tempDirectory), _definitionLogger.Object);
        var definitionProvider = new DefinitionProvider(definitionLoader);
        definitionProvider.Refresh();
        var routingService = new InvocationRoutingService(appRegistry, _routingLogger.Object);
        var store = new InvocationStore(_storeLogger.Object, routingService, effectiveClock);
        var waiter = new InvocationRequestWaiter(Mock.Of<ILogger<InvocationRequestWaiter>>());
        var runtimeHttpBaseUrlProvider = new RuntimeHttpBaseUrlProvider(Mock.Of<ILogger<RuntimeHttpBaseUrlProvider>>(), RuntimePathOptions.Resolve());
        var launchCoordinator = new LaunchCoordinator(
            definitionProvider,
            appRegistry,
            runtimeHttpBaseUrlProvider,
            new ProcessLauncher(),
            effectiveClock,
            runtimeTuningOptions,
            _launchLogger.Object);
        return new InvocationHandler(
            appRegistry,
            definitionProvider,
            routingService,
            store,
            waiter,
            launchCoordinator,
            effectiveClock,
            _invocationHandlerLogger.Object,
            runtimeTuningOptions);
    }

    private sealed class MutableClock : IClock
    {
        public MutableClock(DateTime utcNow)
        {
            UtcNow = utcNow;
        }

        public DateTime UtcNow { get; private set; }

        public void Advance(TimeSpan duration)
        {
            UtcNow = UtcNow.Add(duration);
        }
    }

    private void WriteDefinition(string appId, bool rpcEnabled, bool includeLaunch = false, string? dedupeKeyTemplate = null)
    {
        var payload = new Dictionary<string, object?>
        {
            ["appId"] = appId,
            ["scope"] = ScopeContract.Global,
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

        DefinitionCatalogTestHelper.UpsertDefinition(
            DefinitionCatalogTestHelper.GetCatalogPath(_tempDirectory),
            JsonSerializer.Serialize(payload));
    }

    private static string GetInstanceSessionToken(AppRegistry appRegistry, string instanceId)
    {
        return appRegistry.GetCurrentInstanceSessionToken(instanceId)
            ?? throw new InvalidOperationException($"Missing instance session token for {instanceId}.");
    }
}
