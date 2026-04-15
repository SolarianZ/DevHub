namespace DevHub.Tests;

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
/// Invocation scope 路由专项测试。
/// </summary>
[Trait("Category", "Impl")]
public class InvocationScopeRoutingTests : IDisposable
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
    public InvocationScopeRoutingTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "DevHubInvocationScopeRoutingTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
    }

    [Fact]
    public void Impl_RoutingService_WithTargetInstanceId_ShouldNotFallbackToOtherInstances()
    {
        var appRegistry = new AppRegistry(new SystemClock(), _registryLogger.Object);
        appRegistry.RegisterInstance(new AppInstance
        {
            InstanceId = "inst-a",
            AppId = "route.app",
            Scope = null,
            Pid = 1001,
            Invoke = new InvokeCapability { Poll = true, Respond = true }
        });
        appRegistry.RegisterInstance(new AppInstance
        {
            InstanceId = "inst-b",
            AppId = "route.app",
            Scope = null,
            Pid = 1002,
            Invoke = new InvokeCapability { Poll = true, Respond = true }
        });

        var service = new InvocationRoutingService(appRegistry, _routingLogger.Object);

        var candidates = service.GetOnlineCandidates(
            "route.app",
            new InvocationTarget { Scope = null, InstanceId = "inst-b" });

        Assert.Single(candidates);
        Assert.Equal("inst-b", candidates[0].InstanceId);
    }

    [Fact]
    public void Impl_RoutingService_WithTargetScope_ShouldNotFallbackToGlobal()
    {
        var appRegistry = new AppRegistry(new SystemClock(), _registryLogger.Object);
        appRegistry.RegisterInstance(new AppInstance
        {
            InstanceId = "global-inst",
            AppId = "scope.app",
            Scope = null,
            Pid = 2001,
            Invoke = new InvokeCapability { Poll = true, Respond = true }
        });
        appRegistry.RegisterInstance(new AppInstance
        {
            InstanceId = "scoped-inst",
            AppId = "scope.app",
            Scope = "workspace://a",
            Pid = 2002,
            Invoke = new InvokeCapability { Poll = true, Respond = true }
        });

        var service = new InvocationRoutingService(appRegistry, _routingLogger.Object);

        var candidates = service.GetOnlineCandidates(
            "scope.app",
            new InvocationTarget { Scope = "workspace://a", InstanceId = null });

        Assert.Single(candidates);
        Assert.Equal("scoped-inst", candidates[0].InstanceId);
    }

    [Fact]
    public void Impl_RoutingService_WithNullTargetScope_ShouldOnlyRouteToGlobalInstances()
    {
        var appRegistry = new AppRegistry(new SystemClock(), _registryLogger.Object);
        appRegistry.RegisterInstance(new AppInstance
        {
            InstanceId = "global-only-inst",
            AppId = "scope-null.app",
            Scope = null,
            Pid = 2051,
            Invoke = new InvokeCapability { Poll = true, Respond = true }
        });
        appRegistry.RegisterInstance(new AppInstance
        {
            InstanceId = "scoped-only-inst",
            AppId = "scope-null.app",
            Scope = "workspace-A",
            Pid = 2052,
            Invoke = new InvokeCapability { Poll = true, Respond = true }
        });

        var service = new InvocationRoutingService(appRegistry, _routingLogger.Object);

        var candidates = service.GetOnlineCandidates(
            "scope-null.app",
            new InvocationTarget { Scope = null, InstanceId = null });

        Assert.Single(candidates);
        Assert.Equal("global-only-inst", candidates[0].InstanceId);
    }

    [Fact]
    public void Impl_RoutingService_WithCaseSensitiveScopeMatching_ShouldNotCrossRoute()
    {
        var appRegistry = new AppRegistry(new SystemClock(), _registryLogger.Object);
        appRegistry.RegisterInstance(new AppInstance
        {
            InstanceId = "scope-upper-inst",
            AppId = "scope-case.app",
            Scope = "workspace-A",
            Pid = 2061,
            Invoke = new InvokeCapability { Poll = true, Respond = true }
        });
        appRegistry.RegisterInstance(new AppInstance
        {
            InstanceId = "scope-lower-inst",
            AppId = "scope-case.app",
            Scope = "workspace-a",
            Pid = 2062,
            Invoke = new InvokeCapability { Poll = true, Respond = true }
        });

        var service = new InvocationRoutingService(appRegistry, _routingLogger.Object);

        var upperCandidates = service.GetOnlineCandidates(
            "scope-case.app",
            new InvocationTarget { Scope = "workspace-A", InstanceId = null });
        var lowerCandidates = service.GetOnlineCandidates(
            "scope-case.app",
            new InvocationTarget { Scope = "workspace-a", InstanceId = null });

        Assert.Single(upperCandidates);
        Assert.Single(lowerCandidates);
        Assert.Equal("scope-upper-inst", upperCandidates[0].InstanceId);
        Assert.Equal("scope-lower-inst", lowerCandidates[0].InstanceId);
        Assert.NotEqual(upperCandidates[0].InstanceId, lowerCandidates[0].InstanceId);
    }

    [Fact]
    public void Impl_RoutingService_WithWhitespaceScope_ShouldRouteToExactWhitespaceScope()
    {
        var appRegistry = new AppRegistry(new SystemClock(), _registryLogger.Object);
        appRegistry.RegisterInstance(new AppInstance
        {
            InstanceId = "global-inst",
            AppId = "space-scope.app",
            Scope = null,
            Pid = 2101,
            Invoke = new InvokeCapability { Poll = true, Respond = true }
        });
        appRegistry.RegisterInstance(new AppInstance
        {
            InstanceId = "space-scope-inst",
            AppId = "space-scope.app",
            Scope = "   ",
            Pid = 2102,
            Invoke = new InvokeCapability { Poll = true, Respond = true }
        });

        var service = new InvocationRoutingService(appRegistry, _routingLogger.Object);

        var candidates = service.GetOnlineCandidates(
            "space-scope.app",
            new InvocationTarget { Scope = "   ", InstanceId = null });

        Assert.Single(candidates);
        Assert.Equal("space-scope-inst", candidates[0].InstanceId);
    }

    [Fact]
    public async Task Impl_InvocationHandler_Request_WithMissingTargetInstance_ShouldReturnTargetInstanceMissing()
    {
        var appRegistry = new AppRegistry(new SystemClock(), _registryLogger.Object);
        var definitionLoader = new DefinitionLoader(_tempDirectory, _definitionLogger.Object);
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
            Id = "request-target-instance-missing",
            Method = "hub.invoke.request",
            Params = JsonSerializer.SerializeToElement(new
            {
                appId = "target-missing.app",
                target = new { scope = (string?)null, instanceId = "inst-not-exists" },
                method = "task.run",
                args = new { },
                options = new
                {
                    ttlMs = 1000,
                    waitTimeoutMs = 1000,
                    queueIfOffline = false,
                    autoLaunch = false
                }
            })
        };

        var response = await handler.HandleAsync(request, CancellationToken.None);

        Assert.NotNull(response.Error);
        Assert.Equal(-32010, response.Error.Code);
        Assert.Equal("instance_not_found", response.Error.Message);
        var data = JsonSerializer.SerializeToElement(response.Error.Data);
        Assert.Equal("target_instance_missing", data.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Impl_InvocationHandler_Notify_WithMissingTargetInstance_ShouldReturnTargetInstanceMissing()
    {
        var appRegistry = new AppRegistry(new SystemClock(), _registryLogger.Object);
        var definitionLoader = new DefinitionLoader(_tempDirectory, _definitionLogger.Object);
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
            Id = "notify-target-instance-missing",
            Method = "hub.invoke.notify",
            Params = JsonSerializer.SerializeToElement(new
            {
                appId = "target-missing.app",
                target = new { scope = (string?)null, instanceId = "inst-not-exists" },
                method = "task.run",
                args = new { },
                options = new
                {
                    ttlMs = 60000,
                    queueIfOffline = false,
                    autoLaunch = false
                }
            })
        };

        var response = await handler.HandleAsync(request, CancellationToken.None);

        Assert.NotNull(response.Error);
        Assert.Equal(-32010, response.Error.Code);
        Assert.Equal("instance_not_found", response.Error.Message);
        var data = JsonSerializer.SerializeToElement(response.Error.Data);
        Assert.Equal("target_instance_missing", data.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Impl_InvocationHandler_Notify_WithGlobalTarget_ShouldRouteToGlobalCandidate()
    {
        var appRegistry = new AppRegistry(new SystemClock(), _registryLogger.Object);
        WriteDefinition("route-log-notify.app", rpcEnabled: true);

        var definitionLoader = new DefinitionLoader(_tempDirectory, _definitionLogger.Object);
        var definitionProvider = new DefinitionProvider(definitionLoader);
        definitionProvider.Refresh();
        var routingService = new InvocationRoutingService(appRegistry, _routingLogger.Object);
        var store = new InvocationStore(_storeLogger.Object, routingService, new SystemClock());
        var waiter = new InvocationRequestWaiter(Mock.Of<ILogger<InvocationRequestWaiter>>());
        var runtimeHttpBaseUrlProvider = new RuntimeHttpBaseUrlProvider(Mock.Of<ILogger<RuntimeHttpBaseUrlProvider>>(), RuntimePathOptions.Resolve());
        var launchCoordinator = new LaunchCoordinator(definitionProvider, appRegistry, runtimeHttpBaseUrlProvider, new ProcessLauncher(), new SystemClock(), _launchLogger.Object);
        var handler = new InvocationHandler(appRegistry, definitionProvider, routingService, store, waiter, launchCoordinator, new SystemClock(), _invocationHandlerLogger.Object);

        appRegistry.RegisterInstance(new AppInstance
        {
            InstanceId = "route-log-notify-global",
            AppId = "route-log-notify.app",
            Scope = null,
            Pid = 4201,
            Invoke = new InvokeCapability { Poll = true, Respond = true }
        });

        var request = new JsonRpcRequest
        {
            Id = "route-log-notify",
            Method = "hub.invoke.notify",
            Params = JsonSerializer.SerializeToElement(new
            {
                appId = "route-log-notify.app",
                target = new { scope = (string?)null, instanceId = (string?)null },
                method = "asset.rebuild",
                args = new { sample = true },
                options = new
                {
                    ttlMs = 60000,
                    queueIfOffline = false,
                    autoLaunch = false
                }
            })
        };

        var response = await handler.HandleAsync(request, CancellationToken.None);

        Assert.Null(response.Error);
        var notifyResult = JsonSerializer.SerializeToElement(response.Result);
        var invocationId = notifyResult.GetProperty("invocationId").GetString();
        Assert.False(string.IsNullOrWhiteSpace(invocationId));

        var pollItems = await store.PollAsync(
            appRegistry.GetInstance("route-log-notify-global")!,
            maxCount: 1,
            waitMs: 0,
            CancellationToken.None);

        Assert.Single(pollItems);
        Assert.Equal(invocationId, pollItems[0].InvocationId);
        Assert.Equal("route-log-notify-global", pollItems[0].LeaseHolderInstanceId);
    }

    [Fact]
    public async Task Impl_InvocationHandler_Request_WithScopedTarget_ShouldRouteToScopedCandidate()
    {
        var appRegistry = new AppRegistry(new SystemClock(), _registryLogger.Object);
        WriteDefinition("route-log-request.app", rpcEnabled: true);

        var definitionLoader = new DefinitionLoader(_tempDirectory, _definitionLogger.Object);
        var definitionProvider = new DefinitionProvider(definitionLoader);
        definitionProvider.Refresh();
        var routingService = new InvocationRoutingService(appRegistry, _routingLogger.Object);
        var store = new InvocationStore(_storeLogger.Object, routingService, new SystemClock());
        var waiter = new InvocationRequestWaiter(Mock.Of<ILogger<InvocationRequestWaiter>>());
        var runtimeHttpBaseUrlProvider = new RuntimeHttpBaseUrlProvider(Mock.Of<ILogger<RuntimeHttpBaseUrlProvider>>(), RuntimePathOptions.Resolve());
        var launchCoordinator = new LaunchCoordinator(definitionProvider, appRegistry, runtimeHttpBaseUrlProvider, new ProcessLauncher(), new SystemClock(), _launchLogger.Object);
        var handler = new InvocationHandler(appRegistry, definitionProvider, routingService, store, waiter, launchCoordinator, new SystemClock(), _invocationHandlerLogger.Object);

        var scopedInstance = appRegistry.RegisterInstance(new AppInstance
        {
            InstanceId = "route-log-request-scoped",
            AppId = "route-log-request.app",
            Scope = "workspace-A",
            Pid = 4202,
            Invoke = new InvokeCapability { Poll = true, Respond = true }
        });

        var request = new JsonRpcRequest
        {
            Id = "route-log-request",
            Method = "hub.invoke.request",
            Params = JsonSerializer.SerializeToElement(new
            {
                appId = "route-log-request.app",
                target = new { scope = "workspace-A", instanceId = (string?)null },
                method = "asset.build",
                args = new { sample = true },
                options = new
                {
                    ttlMs = 3000,
                    waitTimeoutMs = 1000,
                    queueIfOffline = false,
                    autoLaunch = false
                }
            })
        };

        var responderTask = Task.Run(async () =>
        {
            var pollResponse = await store.PollAsync(scopedInstance, maxCount: 1, waitMs: 1500, CancellationToken.None);
            if (pollResponse.Count > 0)
            {
                _ = store.Respond("route-log-request-scoped", pollResponse[0].InvocationId, new { ok = true }, null);
                waiter.CompleteSuccess(pollResponse[0].InvocationId, new { ok = true });
            }
        });

        var response = await handler.HandleAsync(request, CancellationToken.None);
        await responderTask;

        Assert.Null(response.Error);
        var responseResult = JsonSerializer.SerializeToElement(response.Result);
        Assert.True(responseResult.GetProperty("ok").GetBoolean());
        var value = responseResult.GetProperty("value");
        Assert.True(value.TryGetProperty("ok", out var okValue) && okValue.GetBoolean());

        Assert.Equal("route-log-request-scoped", scopedInstance.InstanceId);
    }

    [Fact]
    public async Task Impl_InvocationHandler_Notify_OfflineMatrix_ShouldBeConsistentAcrossGlobalAndScopedTargets()
    {
        foreach (var scopeCase in new (string? TargetScope, string ScopeName, string ScopeTag)[]
                 {
                     (null, "global", "global"),
                     ("workspace-A", "workspace-A", "scoped")
                 })
        {
            var targetScope = scopeCase.TargetScope;
            var scopeName = scopeCase.ScopeName;
            var scopeTag = scopeCase.ScopeTag;
            var appRegistry = new AppRegistry(new SystemClock(), _registryLogger.Object);
            var definitionLoader = new DefinitionLoader(_tempDirectory, _definitionLogger.Object);
            var definitionProvider = new DefinitionProvider(definitionLoader);
            definitionProvider.Refresh();
            var routingService = new InvocationRoutingService(appRegistry, _routingLogger.Object);
            var store = new InvocationStore(_storeLogger.Object, routingService, new SystemClock());
            var waiter = new InvocationRequestWaiter(Mock.Of<ILogger<InvocationRequestWaiter>>());
            var runtimeHttpBaseUrlProvider = new RuntimeHttpBaseUrlProvider(Mock.Of<ILogger<RuntimeHttpBaseUrlProvider>>(), RuntimePathOptions.Resolve());
            string? launchArguments = null;
            var processLauncher = new Mock<IProcessLauncher>();
            processLauncher
                .Setup(launcher => launcher.Start(It.IsAny<LaunchConfiguration>(), It.IsAny<string?>()))
                .Callback<LaunchConfiguration, string?>((_, args) => launchArguments = args)
                .Returns(System.Diagnostics.Process.GetCurrentProcess());
            var launchCoordinator = new LaunchCoordinator(definitionProvider, appRegistry, runtimeHttpBaseUrlProvider, processLauncher.Object, new SystemClock(), _launchLogger.Object);
            var invocationHandler = new InvocationHandler(appRegistry, definitionProvider, routingService, store, waiter, launchCoordinator, new SystemClock(), _invocationHandlerLogger.Object);
            var launchHandler = new LaunchHandler(launchCoordinator, _launchHandlerLogger.Object);

            static JsonRpcRequest BuildNotifyRequest(
                string id,
                string appId,
                string? scope,
                bool queueIfOffline,
                bool autoLaunch,
                string caseName)
            {
                return new JsonRpcRequest
                {
                    Id = id,
                    Method = "hub.invoke.notify",
                    Params = JsonSerializer.SerializeToElement(new
                    {
                        appId,
                        target = new { scope, instanceId = (string?)null },
                        method = "asset.rebuild",
                        args = new { @case = caseName },
                        options = new
                        {
                            ttlMs = 60000,
                            queueIfOffline,
                            autoLaunch
                        }
                    })
                };
            }

            var noQueueAppId = $"scope-010-noqueue-{scopeTag}";
            WriteDefinition(noQueueAppId, rpcEnabled: true);
            definitionProvider.Refresh();

            var noQueueResponse = await invocationHandler.HandleAsync(
                BuildNotifyRequest(
                    id: $"scope010-{scopeTag}-noqueue",
                    appId: noQueueAppId,
                    scope: targetScope,
                    queueIfOffline: false,
                    autoLaunch: false,
                    caseName: "queue-false"),
                CancellationToken.None);

            Assert.NotNull(noQueueResponse.Error);
            Assert.Equal(-32010, noQueueResponse.Error.Code);
            Assert.Equal("instance_not_found", noQueueResponse.Error.Message);
            var noQueueData = JsonSerializer.SerializeToElement(noQueueResponse.Error.Data);
            Assert.Equal("offline_no_queue", noQueueData.GetProperty("reason").GetString());

            var pendingAppId = $"scope-010-pending-{scopeTag}";
            WriteDefinition(pendingAppId, rpcEnabled: true);
            definitionProvider.Refresh();

            var pendingResponse = await invocationHandler.HandleAsync(
                BuildNotifyRequest(
                    id: $"scope010-{scopeTag}-pending",
                    appId: pendingAppId,
                    scope: targetScope,
                    queueIfOffline: true,
                    autoLaunch: false,
                    caseName: "queue-true-autolaunch-false"),
                CancellationToken.None);

            Assert.Null(pendingResponse.Error);
            var pendingResult = JsonSerializer.SerializeToElement(pendingResponse.Result);
            var pendingInvocationId = pendingResult.GetProperty("invocationId").GetString();
            Assert.False(string.IsNullOrWhiteSpace(pendingInvocationId));

            var matchingInstance = appRegistry.RegisterInstance(new AppInstance
            {
                InstanceId = $"scope-010-pending-inst-{scopeName}",
                AppId = pendingAppId,
                Scope = targetScope,
                Pid = 6021,
                Invoke = new InvokeCapability { Poll = true, Respond = true }
            });

            var pendingPoll = await store.PollAsync(matchingInstance, maxCount: 10, waitMs: 0, CancellationToken.None);
            Assert.Contains(pendingPoll, item => item.InvocationId == pendingInvocationId);

            var autoLaunchAppId = $"scope-010-autolaunch-{scopeTag}";
            WriteDefinition(
                autoLaunchAppId,
                rpcEnabled: true,
                includeLaunch: true,
                dedupeKeyTemplate: "{appId}:{scopeOrGlobal}",
                argsTemplate: "{scopeOrGlobal}");
            definitionProvider.Refresh();

            var autoLaunchResponse = await invocationHandler.HandleAsync(
                BuildNotifyRequest(
                    id: $"scope010-{scopeTag}-autolaunch",
                    appId: autoLaunchAppId,
                    scope: targetScope,
                    queueIfOffline: true,
                    autoLaunch: true,
                    caseName: "queue-true-autolaunch-true"),
                CancellationToken.None);

            Assert.Null(autoLaunchResponse.Error);
            var autoLaunchResult = JsonSerializer.SerializeToElement(autoLaunchResponse.Result);
            var autoLaunchInvocationId = autoLaunchResult.GetProperty("invocationId").GetString();
            Assert.False(string.IsNullOrWhiteSpace(autoLaunchInvocationId));

            var autoLaunchReceiver = appRegistry.RegisterInstance(new AppInstance
            {
                InstanceId = $"scope-010-autolaunch-inst-{scopeName}",
                AppId = autoLaunchAppId,
                Scope = targetScope,
                Pid = 6022,
                Invoke = new InvokeCapability { Poll = true, Respond = true }
            });

            var autoLaunchPoll = await store.PollAsync(autoLaunchReceiver, maxCount: 10, waitMs: 0, CancellationToken.None);
            Assert.Contains(autoLaunchPoll, item => item.InvocationId == autoLaunchInvocationId);

            var launchAfterAutoLaunch = await launchHandler.HandleAsync(new JsonRpcRequest
            {
                Id = $"scope010-{scopeName}-launch-check",
                Method = "hub.apps.launch",
                Params = JsonSerializer.SerializeToElement(new
                {
                    appId = autoLaunchAppId,
                    scope = targetScope,
                    waitForRegisterMs = 0
                })
            }, CancellationToken.None);

            Assert.Null(launchAfterAutoLaunch.Error);
            var launchResult = JsonSerializer.SerializeToElement(launchAfterAutoLaunch.Result);
            Assert.Equal("already_running", launchResult.GetProperty("status").GetString());
            var expectedLaunchArguments = targetScope is null ? "global" : targetScope;
            Assert.Equal(expectedLaunchArguments, launchArguments);
            processLauncher.Verify(launcher => launcher.Start(It.IsAny<LaunchConfiguration>(), It.IsAny<string?>()), Times.Once);

            var noDefinitionAppId = $"scope-010-nodef-{scopeTag}";
            var noDefinitionResponse = await invocationHandler.HandleAsync(
                BuildNotifyRequest(
                    id: $"scope010-{scopeTag}-nodef",
                    appId: noDefinitionAppId,
                    scope: targetScope,
                    queueIfOffline: true,
                    autoLaunch: false,
                    caseName: "nodef"),
                CancellationToken.None);

            Assert.NotNull(noDefinitionResponse.Error);
            Assert.Equal(-32010, noDefinitionResponse.Error.Code);
            Assert.Equal("instance_not_found", noDefinitionResponse.Error.Message);
            var noDefinitionData = JsonSerializer.SerializeToElement(noDefinitionResponse.Error.Data);
            Assert.Equal("offline_no_queue", noDefinitionData.GetProperty("reason").GetString());
        }
    }

    [Fact]
    public async Task Impl_Sweep_RequeueScopedInvocation_ShouldNotLeakAcrossScopes()
    {
        var clock = new MutableClock(DateTime.UtcNow);
        var appRegistry = new AppRegistry(clock, _registryLogger.Object);
        var holderInstance = appRegistry.RegisterInstance(new AppInstance
        {
            InstanceId = "inst-scope-requeue-holder",
            AppId = "scope-requeue.app",
            Scope = "workspace-A",
            Pid = 4011,
            Invoke = new InvokeCapability { Poll = true, Respond = true }
        });

        var sameScopeReceiver = appRegistry.RegisterInstance(new AppInstance
        {
            InstanceId = "inst-scope-requeue-receiver",
            AppId = "scope-requeue.app",
            Scope = "workspace-A",
            Pid = 4012,
            Invoke = new InvokeCapability { Poll = true, Respond = true }
        });

        var globalInstance = appRegistry.RegisterInstance(new AppInstance
        {
            InstanceId = "inst-scope-requeue-global",
            AppId = "scope-requeue.app",
            Scope = null,
            Pid = 4013,
            Invoke = new InvokeCapability { Poll = true, Respond = true }
        });

        var otherScopeInstance = appRegistry.RegisterInstance(new AppInstance
        {
            InstanceId = "inst-scope-requeue-other",
            AppId = "scope-requeue.app",
            Scope = "workspace-B",
            Pid = 4014,
            Invoke = new InvokeCapability { Poll = true, Respond = true }
        });

        var routingService = new InvocationRoutingService(appRegistry, _routingLogger.Object);
        var store = new InvocationStore(_storeLogger.Object, routingService, clock);

        var scopedInvocation = store.CreateInvocation(
            CreateNotify("scope-requeue.app", targetScope: "workspace-A", targetInstanceId: null),
            hasOnlineCandidates: true);

        var firstPoll = await store.PollAsync(holderInstance, maxCount: 1, waitMs: 0, CancellationToken.None);
        Assert.Single(firstPoll);

        clock.Advance(TimeSpan.FromSeconds(31));
        _ = appRegistry.Heartbeat(sameScopeReceiver.InstanceId, out _);
        _ = store.Sweep(clock.UtcNow);

        Assert.True(store.TryGet(scopedInvocation.InvocationId, out var requeued));
        Assert.Equal(InvocationState.Queued, requeued!.State);
        Assert.Equal(2, requeued.Delivery.Attempt);

        var globalPoll = await store.PollAsync(globalInstance, maxCount: 1, waitMs: 0, CancellationToken.None);
        Assert.Empty(globalPoll);

        var otherScopePoll = await store.PollAsync(otherScopeInstance, maxCount: 1, waitMs: 0, CancellationToken.None);
        Assert.Empty(otherScopePoll);

        var sameScopePoll = await store.PollAsync(sameScopeReceiver, maxCount: 1, waitMs: 0, CancellationToken.None);
        Assert.Single(sameScopePoll);

        var redelivered = sameScopePoll[0];
        Assert.Equal(scopedInvocation.InvocationId, redelivered.InvocationId);
        Assert.Equal(2, redelivered.Delivery.Attempt);
        Assert.Equal(sameScopeReceiver.InstanceId, redelivered.LeaseHolderInstanceId);
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

    private void WriteDefinition(
        string appId,
        bool rpcEnabled,
        bool includeLaunch = false,
        string? dedupeKeyTemplate = null,
        string? argsTemplate = null)
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
                ["argsTemplate"] = argsTemplate ?? "--version"
            };

            if (!string.IsNullOrWhiteSpace(dedupeKeyTemplate))
            {
                launch["dedupeKeyTemplate"] = dedupeKeyTemplate;
            }

            payload["launch"] = launch;
        }

        File.WriteAllText(filePath, JsonSerializer.Serialize(payload));
    }

    private static Invocation CreateNotify(string appId, string? targetScope, string? targetInstanceId)
    {
        return new Invocation
        {
            InvocationId = $"invk-{Guid.NewGuid():N}",
            AppId = appId,
            Target = new InvocationTarget
            {
                Scope = targetScope,
                InstanceId = targetInstanceId
            },
            Method = "demo.notify",
            Args = new Dictionary<string, object?>(),
            Kind = InvocationKind.Notify,
            CreatedAtUtc = DateTime.UtcNow,
            Options = new InvocationOptions
            {
                TtlMs = 60000,
                QueueIfOffline = true,
                AutoLaunch = false
            },
            Delivery = new InvocationDelivery
            {
                LeaseSeconds = 30,
                Attempt = 1
            },
            Caller = new InvocationCaller
            {
                ClientId = "test-client",
                ClientSessionId = Guid.NewGuid().ToString("D")
            },
            State = InvocationState.Created
        };
    }

}






