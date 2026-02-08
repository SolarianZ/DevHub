namespace DevHub.Tests;

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
    public void RoutingService_WithTargetInstanceId_ShouldNotFallbackToOtherInstances()
    {
        var appRegistry = new AppRegistry(_registryLogger.Object);
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
    public void RoutingService_WithTargetScope_ShouldNotFallbackToGlobal()
    {
        var appRegistry = new AppRegistry(_registryLogger.Object);
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
    public void RoutingService_WithNullTargetScope_ShouldOnlyRouteToGlobalInstances()
    {
        var appRegistry = new AppRegistry(_registryLogger.Object);
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
    public void RoutingService_WithCaseSensitiveScopeMatching_ShouldNotCrossRoute()
    {
        var appRegistry = new AppRegistry(_registryLogger.Object);
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
    public void RoutingService_WithWhitespaceScope_ShouldRouteToExactWhitespaceScope()
    {
        var appRegistry = new AppRegistry(_registryLogger.Object);
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
    public async Task InvocationHandler_Request_WithMissingTargetInstance_ShouldReturnTargetInstanceMissing()
    {
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
    public async Task InvocationHandler_Notify_WithMissingTargetInstance_ShouldReturnTargetInstanceMissing()
    {
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
    public async Task InvocationHandler_Notify_OfflineMatrix_ShouldBeConsistentAcrossGlobalAndScopedTargets()
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
            var appRegistry = new AppRegistry(_registryLogger.Object);
            var definitionLoader = new DefinitionLoader(_tempDirectory, _definitionLogger.Object);
            var routingService = new InvocationRoutingService(appRegistry, _routingLogger.Object);
            var store = new InvocationStore(_storeLogger.Object, routingService);
            var waiter = new InvocationRequestWaiter(Mock.Of<ILogger<InvocationRequestWaiter>>());
            var runtimeHttpBaseUrlProvider = new RuntimeHttpBaseUrlProvider(Mock.Of<ILogger<RuntimeHttpBaseUrlProvider>>());
            var launchCoordinator = new LaunchCoordinator(definitionLoader, appRegistry, runtimeHttpBaseUrlProvider, _launchLogger.Object);
            var invocationHandler = new InvocationHandler(appRegistry, definitionLoader, routingService, store, waiter, launchCoordinator, _invocationHandlerLogger.Object);
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

            var noQueueAppId = $"m3-scope-010-noqueue-{scopeTag}";
            WriteDefinition(noQueueAppId, rpcEnabled: true);
            definitionLoader.Load();

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

            var pendingAppId = $"m3-scope-010-pending-{scopeTag}";
            WriteDefinition(pendingAppId, rpcEnabled: true);
            definitionLoader.Load();

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
                InstanceId = $"m3-scope-010-pending-inst-{scopeName}",
                AppId = pendingAppId,
                Scope = targetScope,
                Pid = 6021,
                Invoke = new InvokeCapability { Poll = true, Respond = true }
            });

            var pendingPoll = await store.PollAsync(matchingInstance, maxCount: 10, waitMs: 0, CancellationToken.None);
            Assert.Contains(pendingPoll, item => item.InvocationId == pendingInvocationId);

            var autoLaunchAppId = $"m3-scope-010-autolaunch-{scopeTag}";
            WriteDefinition(
                autoLaunchAppId,
                rpcEnabled: true,
                includeLaunch: true,
                dedupeKeyTemplate: "{appId}:{scopeOrGlobal}");
            definitionLoader.Load();

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
            Assert.False(string.IsNullOrWhiteSpace(autoLaunchResult.GetProperty("invocationId").GetString()));

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

            var noDefinitionAppId = $"m3-scope-010-nodef-{scopeTag}";
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
