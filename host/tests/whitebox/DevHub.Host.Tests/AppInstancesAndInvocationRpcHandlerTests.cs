namespace DevHub.Host.Tests;

using System.Diagnostics;
using System.Text.Json;
using DevHub.Core.Models;
using DevHub.Core.Models.Rpc;
using DevHub.Core.Services;
using DevHub.Core.Services.Abstractions;
using DevHub.Core.Services.Events;
using DevHub.Core.Services.Invocation;
using DevHub.Core.Services.Rpc.Handlers;
using Microsoft.Extensions.Logging;
using Moq;

/// <summary>
/// Host 使用的共享实例管理与调用链 RPC 处理器测试。
/// </summary>
[Trait("Category", "Spec")]
public sealed class AppInstancesAndInvocationRpcHandlerTests : IDisposable
{
    private readonly string _dataDirectory;
    private readonly string _definitionsDirectory;

    /// <summary>
    /// 初始化测试上下文。
    /// </summary>
    public AppInstancesAndInvocationRpcHandlerTests()
    {
        _dataDirectory = Path.Combine(Path.GetTempPath(), "DevHubHostAppInstancesAndInvocationRpcHandlerTests", Guid.NewGuid().ToString("N"));
        _definitionsDirectory = Path.Combine(_dataDirectory, "apps", "definitions");
        Directory.CreateDirectory(_definitionsDirectory);
    }

    [Fact]
    [Trait("Category", "Spec")]
    [Trait("SpecRef", "6.3.8")]
    public async Task Spec_6_3_8_AppInstancesRpcHandler_ShouldRegisterInstanceAndRejectPasswordMismatch()
    {
        var eventPublisher = new Mock<IHubEventPublisher>();
        using var appRegistry = new AppRegistry(new SystemClock(), Mock.Of<ILogger<AppRegistry>>());
        var handler = CreateAppInstancesHandler(appRegistry, eventPublisher: eventPublisher.Object);

        var registerResponse = await handler.HandleAsync(
            CreateRequest(
                HubRpcMethods.HubAppsRegisterInstance,
                "register-instance",
                new
                {
                    password = "sample-password",
                    instance = new
                    {
                        instanceId = "instance.registered",
                        appId = "instance.app",
                        scope = "",
                        pid = 7201,
                        invoke = new
                        {
                            poll = true,
                            respond = true
                        },
                        meta = new
                        {
                            region = "cn"
                        }
                    }
                }),
            CancellationToken.None);

        var registerResult = JsonSerializer.SerializeToElement(registerResponse.Result);
        Assert.True(registerResult.GetProperty("ok").GetBoolean());

        var instance = registerResult.GetProperty("instance");
        Assert.Equal("instance.registered", instance.GetProperty("instanceId").GetString());
        Assert.Equal("instance.app", instance.GetProperty("appId").GetString());
        Assert.Equal(ScopeContract.Global, instance.GetProperty("scope").GetString());
        Assert.Equal("cn", instance.GetProperty("meta").GetProperty("region").GetString());
        Assert.False(instance.TryGetProperty("password", out _));
        var currentToken = registerResult.GetProperty("instanceSessionToken").GetString();
        Assert.False(string.IsNullOrWhiteSpace(currentToken));

        eventPublisher.Verify(
            publisher => publisher.Publish(It.Is<HubEventMessage>(message => message.Type == HubEventTypes.AppInstanceRegistered)),
            Times.Once);

        var mismatchResponse = await handler.HandleAsync(
            CreateRequest(
                HubRpcMethods.HubAppsRegisterInstance,
                "register-instance-mismatch",
                new
                {
                    password = "other-password",
                    instance = new
                    {
                        instanceId = "instance.registered",
                        appId = "instance.app",
                        scope = ScopeContract.Global,
                        pid = 7202,
                        invoke = new
                        {
                            poll = true,
                            respond = true
                        }
                    }
                }),
            CancellationToken.None);

        AssertError(mismatchResponse, -32002, "forbidden", "register-instance-mismatch");
        var errorData = JsonSerializer.SerializeToElement(mismatchResponse.Error!.Data);
        Assert.Equal("instance_password_mismatch", errorData.GetProperty("reason").GetString());
        Assert.Equal("instance.registered", errorData.GetProperty("instanceId").GetString());
    }

    [Fact]
    [Trait("Category", "Spec")]
    [Trait("SpecRef", "6.3.8")]
    public async Task Spec_6_3_8_AppInstancesRpcHandler_WhenDefinitionManagedAppSelfRegistersToOtherScope_ShouldSucceedAndRemainVisible()
    {
        const string appId = "managed.scope.app";
        const string managedScope = "workspace-A";
        const string selfRegisteredScope = "workspace-B";
        const string instanceId = "managed.scope.instance";

        WriteDefinition(appId, rpcEnabled: true, definitionScope: managedScope);

        using var appRegistry = new AppRegistry(new SystemClock(), Mock.Of<ILogger<AppRegistry>>());
        var definitionProvider = CreateDefinitionProvider();
        var handler = CreateAppInstancesHandler(appRegistry, definitionProvider: definitionProvider);

        var registerResponse = await handler.HandleAsync(
            CreateRequest(
                HubRpcMethods.HubAppsRegisterInstance,
                "register-missing-managed-scope",
                new
                {
                    password = "managed-password",
                    instance = new
                    {
                        instanceId,
                        appId,
                        scope = selfRegisteredScope,
                        pid = 7309,
                        invoke = new
                        {
                            poll = true,
                            respond = true
                        }
                    }
                }),
            CancellationToken.None);

        Assert.Null(registerResponse.Error);
        var registerResult = JsonSerializer.SerializeToElement(registerResponse.Result);
        Assert.True(registerResult.GetProperty("ok").GetBoolean());
        var registeredInstance = registerResult.GetProperty("instance");
        Assert.Equal(instanceId, registeredInstance.GetProperty("instanceId").GetString());
        Assert.Equal(appId, registeredInstance.GetProperty("appId").GetString());
        Assert.Equal(selfRegisteredScope, registeredInstance.GetProperty("scope").GetString());

        var listResponse = await handler.HandleAsync(
            CreateRequest(
                HubRpcMethods.HubAppsListInstances,
                "list-managed-scopes",
                new
                {
                    appId,
                    scope = (string?)null,
                    includeOffline = true
                }),
            CancellationToken.None);

        Assert.Null(listResponse.Error);
        var listResult = JsonSerializer.SerializeToElement(listResponse.Result);
        Assert.True(listResult.GetProperty("ok").GetBoolean());
        var listedInstance = Assert.Single(listResult.GetProperty("instances").EnumerateArray());
        Assert.Equal(instanceId, listedInstance.GetProperty("instanceId").GetString());
        Assert.Equal(selfRegisteredScope, listedInstance.GetProperty("scope").GetString());

        var getResponse = await handler.HandleAsync(
            CreateRequest(
                HubRpcMethods.HubAppsGetInstance,
                "get-managed-scope-instance",
                new
                {
                    instanceId
                }),
            CancellationToken.None);

        Assert.Null(getResponse.Error);
        var getResult = JsonSerializer.SerializeToElement(getResponse.Result);
        Assert.True(getResult.GetProperty("ok").GetBoolean());
        var fetchedInstance = getResult.GetProperty("instance");
        Assert.Equal(instanceId, fetchedInstance.GetProperty("instanceId").GetString());
        Assert.Equal(appId, fetchedInstance.GetProperty("appId").GetString());
        Assert.Equal(selfRegisteredScope, fetchedInstance.GetProperty("scope").GetString());
    }

    [Fact]
    [Trait("Category", "Spec")]
    [Trait("SpecRef", "6.3.12")]
    public async Task Spec_6_3_12_AppInstancesAndLaunch_WhenUntrackedRegistrationUsesDifferentScope_ShouldKeepLaunchWaiting()
    {
        const string appId = "managed.untracked.launch";
        const string launchScope = "workspace-A";
        const string selfRegisteredScope = "workspace-B";

        WriteDefinition(appId, rpcEnabled: true, includeLaunch: true, definitionScope: launchScope);

        using var appRegistry = new AppRegistry(new SystemClock(), Mock.Of<ILogger<AppRegistry>>());
        var definitionProvider = CreateDefinitionProvider();
        var runtimeHttpBaseUrlProvider = new Mock<IRuntimeHttpBaseUrlProvider>();
        runtimeHttpBaseUrlProvider.Setup(provider => provider.GetHttpBaseUrl()).Returns("http://127.0.0.1:57231");

        var launchStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var processLauncher = new Mock<IProcessLauncher>();
        processLauncher
            .Setup(launcher => launcher.Start(It.IsAny<LaunchConfiguration>(), It.IsAny<string?>()))
            .Callback(() => launchStarted.TrySetResult())
            .Returns(Process.GetCurrentProcess());

        var launchCoordinator = new LaunchCoordinator(
            definitionProvider,
            appRegistry,
            runtimeHttpBaseUrlProvider.Object,
            processLauncher.Object,
            new SystemClock(),
            Mock.Of<ILogger<LaunchCoordinator>>());
        var launchHandler = new LaunchHandler(launchCoordinator, Mock.Of<ILogger<LaunchHandler>>());
        var appInstancesHandler = CreateAppInstancesHandler(
            appRegistry,
            definitionProvider: definitionProvider,
            launchRegistrationTracker: launchCoordinator);

        var launchTask = launchHandler.HandleAsync(
            CreateRequest(
                HubRpcMethods.HubAppsLaunch,
                "untracked-launch",
                new
                {
                    appId,
                    scope = launchScope,
                    waitForRegisterMs = 250
                }),
            CancellationToken.None);

        await launchStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var registerResponse = await appInstancesHandler.HandleAsync(
            CreateRequest(
                HubRpcMethods.HubAppsRegisterInstance,
                "untracked-register",
                new
                {
                    password = "untracked-password",
                    instance = new
                    {
                        instanceId = "untracked.scope.instance",
                        appId,
                        scope = selfRegisteredScope,
                        pid = 7311,
                        invoke = new
                        {
                            poll = true,
                            respond = true
                        }
                    }
                }),
            CancellationToken.None);

        Assert.Null(registerResponse.Error);
        var registerResult = JsonSerializer.SerializeToElement(registerResponse.Result);
        Assert.True(registerResult.GetProperty("ok").GetBoolean());
        Assert.Equal(selfRegisteredScope, registerResult.GetProperty("instance").GetProperty("scope").GetString());

        var launchResponse = await launchTask;
        Assert.Null(launchResponse.Error);
        var launchResult = JsonSerializer.SerializeToElement(launchResponse.Result);
        Assert.Equal("starting", launchResult.GetProperty("status").GetString());
        Assert.False(string.IsNullOrWhiteSpace(launchResult.GetProperty("launchId").GetString()));
    }

    [Fact]
    [Trait("Category", "Spec")]
    [Trait("SpecRef", "6.3.8")]
    public async Task Spec_6_3_8_AppInstancesAndLaunch_ShouldRejectScopeMismatchedLaunchBinding()
    {
        const string appId = "managed.bound.launch";
        const string launchScope = "workspace-A";
        const string wrongScope = "workspace-B";

        WriteDefinition(appId, rpcEnabled: true, includeLaunch: true, definitionScope: launchScope);
        WriteDefinition(appId, rpcEnabled: true, includeLaunch: true, definitionScope: wrongScope);

        using var appRegistry = new AppRegistry(new SystemClock(), Mock.Of<ILogger<AppRegistry>>());
        var definitionProvider = CreateDefinitionProvider();
        var runtimeHttpBaseUrlProvider = new Mock<IRuntimeHttpBaseUrlProvider>();
        runtimeHttpBaseUrlProvider.Setup(provider => provider.GetHttpBaseUrl()).Returns("http://127.0.0.1:57231");

        string? capturedLaunchId = null;
        var processLauncher = new Mock<IProcessLauncher>();
        processLauncher
            .Setup(launcher => launcher.Start(It.IsAny<LaunchConfiguration>(), It.IsAny<string?>()))
            .Callback<LaunchConfiguration, string?>((launchConfig, _) =>
            {
                capturedLaunchId = launchConfig.EnvironmentVariables![LaunchCoordinator.LaunchIdEnvironmentVariable];
            })
            .Returns(Process.GetCurrentProcess());

        var launchCoordinator = new LaunchCoordinator(
            definitionProvider,
            appRegistry,
            runtimeHttpBaseUrlProvider.Object,
            processLauncher.Object,
            new SystemClock(),
            Mock.Of<ILogger<LaunchCoordinator>>());
        var launchHandler = new LaunchHandler(launchCoordinator, Mock.Of<ILogger<LaunchHandler>>());
        var appInstancesHandler = CreateAppInstancesHandler(
            appRegistry,
            definitionProvider: definitionProvider,
            launchRegistrationTracker: launchCoordinator);

        var launchTask = launchHandler.HandleAsync(
            CreateRequest(
                HubRpcMethods.HubAppsLaunch,
                "bound-launch",
                new
                {
                    appId,
                    scope = launchScope,
                    waitForRegisterMs = 300
                }),
            CancellationToken.None);

        var waitDeadline = DateTime.UtcNow.AddSeconds(2);
        while (capturedLaunchId is null && DateTime.UtcNow < waitDeadline)
        {
            await Task.Delay(10);
        }

        Assert.False(string.IsNullOrWhiteSpace(capturedLaunchId));

        var registerResponse = await appInstancesHandler.HandleAsync(
            CreateRequest(
                HubRpcMethods.HubAppsRegisterInstance,
                "bound-register-wrong-scope",
                new
                {
                    password = "bound-password",
                    instance = new
                    {
                        instanceId = "bound.instance",
                        appId,
                        scope = wrongScope,
                        pid = 7310,
                        invoke = new
                        {
                            poll = true,
                            respond = true
                        },
                        meta = new
                        {
                            launchId = capturedLaunchId
                        }
                    }
                }),
            CancellationToken.None);

        AssertError(registerResponse, -32002, "forbidden", "bound-register-wrong-scope");
        var registerErrorData = JsonSerializer.SerializeToElement(registerResponse.Error!.Data);
        Assert.Equal("definition_scope_mismatch", registerErrorData.GetProperty("reason").GetString());
        Assert.Equal(launchScope, registerErrorData.GetProperty("expectedScope").GetString());
        Assert.Equal(wrongScope, registerErrorData.GetProperty("scope").GetString());

        var launchResponse = await launchTask;
        AssertError(launchResponse, -32020, "launch_failed", "bound-launch");
        var launchErrorData = JsonSerializer.SerializeToElement(launchResponse.Error!.Data);
        Assert.Equal("definition_scope_mismatch", launchErrorData.GetProperty("reason").GetString());
        Assert.Equal(capturedLaunchId, launchErrorData.GetProperty("launchId").GetString());
    }

    [Fact]
    [Trait("Category", "Spec")]
    [Trait("SpecRef", "6.3.9")]
    public async Task Spec_6_3_9_AppInstancesRpcHandler_WhenHeartbeatUnknown_ShouldReturnInstanceNotFound()
    {
        using var appRegistry = new AppRegistry(new SystemClock(), Mock.Of<ILogger<AppRegistry>>());
        var handler = CreateAppInstancesHandler(appRegistry);

        var response = await handler.HandleAsync(
            CreateRequest(
                HubRpcMethods.HubAppsHeartbeat,
                "heartbeat-unknown",
                new
                {
                    instanceId = "missing.instance",
                    instanceSessionToken = "missing-instance-token"
                }),
            CancellationToken.None);

        AssertError(response, -32010, "instance_not_found", "heartbeat-unknown");
        var errorData = JsonSerializer.SerializeToElement(response.Error!.Data);
        Assert.Equal("unknown_instance", errorData.GetProperty("reason").GetString());
        Assert.Equal("missing.instance", errorData.GetProperty("instanceId").GetString());
    }

    [Fact]
    [Trait("Category", "Spec")]
    [Trait("SpecRef", "6.3.10")]
    public async Task Spec_6_3_10_AppInstancesRpcHandler_ShouldUnregisterIdempotentlyAndGuardOwnershipToken()
    {
        var eventPublisher = new Mock<IHubEventPublisher>();
        using var appRegistry = new AppRegistry(new SystemClock(), Mock.Of<ILogger<AppRegistry>>());
        var currentToken = RegisterInstance(appRegistry, "instance.app", "instance.unregister");
        var handler = CreateAppInstancesHandler(appRegistry, eventPublisher: eventPublisher.Object);

        var forbiddenResponse = await handler.HandleAsync(
            CreateRequest(
                HubRpcMethods.HubAppsUnregisterInstance,
                "unregister-forbidden",
                new
                {
                    instanceId = "instance.unregister",
                    instanceSessionToken = "wrong-token"
                }),
            CancellationToken.None);

        AssertError(forbiddenResponse, -32002, "forbidden", "unregister-forbidden");
        Assert.Equal(
            "instance_session_token_mismatch",
            JsonSerializer.SerializeToElement(forbiddenResponse.Error!.Data).GetProperty("reason").GetString());

        var okResponse = await handler.HandleAsync(
            CreateRequest(
                HubRpcMethods.HubAppsUnregisterInstance,
                "unregister-ok",
                new
                {
                    instanceId = "instance.unregister",
                    instanceSessionToken = currentToken
                }),
            CancellationToken.None);

        Assert.True(JsonSerializer.SerializeToElement(okResponse.Result).GetProperty("ok").GetBoolean());
        eventPublisher.Verify(
            publisher => publisher.Publish(It.Is<HubEventMessage>(message => message.Type == HubEventTypes.AppInstanceUnregistered)),
            Times.Once);

        var idempotentResponse = await handler.HandleAsync(
            CreateRequest(
                HubRpcMethods.HubAppsUnregisterInstance,
                "unregister-idempotent",
                new
                {
                    instanceId = "instance.unregister",
                    instanceSessionToken = currentToken
                }),
            CancellationToken.None);

        Assert.True(JsonSerializer.SerializeToElement(idempotentResponse.Result).GetProperty("ok").GetBoolean());
    }

    [Fact]
    [Trait("Category", "Spec")]
    [Trait("SpecRef", "6.3.11")]
    public async Task Spec_6_3_11_AppInstancesRpcHandler_GetInstance_ShouldReturnRetainedSnapshotWithoutRefreshingLastSeen()
    {
        var clock = new SequenceClock(
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            TimeSpan.FromMinutes(1));
        using var appRegistry = new AppRegistry(clock, Mock.Of<ILogger<AppRegistry>>());
        RegisterInstance(appRegistry, "get.instance.app", "get.instance.target", scope: "tenant-a");
        var lastSeenBefore = appRegistry.GetInstance("get.instance.target")!.LastSeenUtc;
        var handler = CreateAppInstancesHandler(appRegistry, clock: clock);

        var response = await handler.HandleAsync(
            CreateRequest(
                HubRpcMethods.HubAppsGetInstance,
                "get-instance",
                new
                {
                    instanceId = "get.instance.target"
                }),
            CancellationToken.None);

        Assert.Null(response.Error);
        var result = JsonSerializer.SerializeToElement(response.Result);
        Assert.True(result.GetProperty("ok").GetBoolean());
        Assert.False(result.TryGetProperty("instanceSessionToken", out _));

        var instance = result.GetProperty("instance");
        Assert.Equal("get.instance.target", instance.GetProperty("instanceId").GetString());
        Assert.Equal("get.instance.app", instance.GetProperty("appId").GetString());
        Assert.Equal("tenant-a", instance.GetProperty("scope").GetString());
        Assert.False(instance.TryGetProperty("password", out _));
        Assert.False(instance.TryGetProperty("instanceSessionToken", out _));
        Assert.Equal(lastSeenBefore, instance.GetProperty("lastSeenUtc").GetDateTime());
        Assert.Equal(lastSeenBefore, appRegistry.GetInstance("get.instance.target")!.LastSeenUtc);
    }

    [Fact]
    [Trait("Category", "Spec")]
    [Trait("SpecRef", "6.3.11")]
    public async Task Spec_6_3_11_AppInstancesRpcHandler_ListInstances_ShouldValidateParamsAndHonorExplicitScopeFilters()
    {
        using var appRegistry = new AppRegistry(new SystemClock(), Mock.Of<ILogger<AppRegistry>>());
        RegisterInstance(appRegistry, "list.app", "global.instance");
        RegisterInstance(appRegistry, "list.app", "tenant.instance", scope: "tenant-a");
        var handler = CreateAppInstancesHandler(appRegistry);

        var invalidScopeResponse = await handler.HandleAsync(
            CreateRequest(
                HubRpcMethods.HubAppsListInstances,
                "list-invalid-scope",
                new
                {
                    scope = 1
                }),
            CancellationToken.None);

        AssertError(invalidScopeResponse, -32602, "invalid_params", "list-invalid-scope");
        Assert.Equal("invalid_scope", JsonSerializer.SerializeToElement(invalidScopeResponse.Error!.Data).GetProperty("reason").GetString());

        var listResponse = await handler.HandleAsync(
            CreateRequest(
                HubRpcMethods.HubAppsListInstances,
                "list-all-scopes",
                new
                {
                    appId = "list.app",
                    scope = (string?)null,
                    includeOffline = true
                }),
            CancellationToken.None);

        var instances = JsonSerializer.SerializeToElement(listResponse.Result)
            .GetProperty("instances")
            .EnumerateArray()
            .ToArray();

        Assert.Equal(2, instances.Length);
        Assert.Contains(instances, instance => instance.GetProperty("instanceId").GetString() == "global.instance");
        Assert.Contains(instances, instance => instance.GetProperty("instanceId").GetString() == "tenant.instance");
    }

    [Fact]
    [Trait("Category", "Spec")]
    [Trait("SpecRef", "6.3.13")]
    [Trait("SpecRef", "6.3.14")]
    public async Task Spec_6_3_13_And_6_3_14_InvocationRpcHandler_ShouldValidateNotifyTargetsAndMapRouteErrors()
    {
        WriteDefinition("notify.rpc-disabled", rpcEnabled: false);
        WriteDefinition("notify.route-errors", rpcEnabled: true);

        using var appRegistry = new AppRegistry(new SystemClock(), Mock.Of<ILogger<AppRegistry>>());
        using var context = CreateInvocationHandlerContext(appRegistry);

        var invalidTargetResponse = await context.Handler.HandleAsync(
            CreateRequest(
                HubRpcMethods.HubInvokeNotify,
                "notify-invalid-target",
                new
                {
                    appId = "notify.route-errors",
                    method = "task.run",
                    target = new
                    {
                        scope = 1
                    }
                }),
            CancellationToken.None);

        AssertError(invalidTargetResponse, -32602, "invalid_params", "notify-invalid-target");
        Assert.Equal("invalid_target_scope", JsonSerializer.SerializeToElement(invalidTargetResponse.Error!.Data).GetProperty("reason").GetString());

        var forbiddenResponse = await context.Handler.HandleAsync(
            CreateRequest(
                HubRpcMethods.HubInvokeNotify,
                "notify-rpc-disabled",
                new
                {
                    appId = "notify.rpc-disabled",
                    target = new
                    {
                        scope = ScopeContract.Global,
                        instanceId = (string?)null
                    },
                    method = "task.run"
                }),
            CancellationToken.None);

        AssertError(forbiddenResponse, -32002, "forbidden", "notify-rpc-disabled");
        Assert.Equal("rpc_disabled", JsonSerializer.SerializeToElement(forbiddenResponse.Error!.Data).GetProperty("reason").GetString());

        var offlineNoQueueResponse = await context.Handler.HandleAsync(
            CreateRequest(
                HubRpcMethods.HubInvokeNotify,
                "notify-offline-no-queue",
                new
                {
                    appId = "notify.route-errors",
                    target = new
                    {
                        scope = ScopeContract.Global,
                        instanceId = (string?)null
                    },
                    method = "task.run",
                    options = new
                    {
                        queueIfOffline = false,
                        autoLaunch = false
                    }
                }),
            CancellationToken.None);

        AssertError(offlineNoQueueResponse, -32010, "instance_not_found", "notify-offline-no-queue");
        Assert.Equal("offline_no_queue", JsonSerializer.SerializeToElement(offlineNoQueueResponse.Error!.Data).GetProperty("reason").GetString());

        var missingInstanceResponse = await context.Handler.HandleAsync(
            CreateRequest(
                HubRpcMethods.HubInvokeNotify,
                "notify-missing-target-instance",
                new
                {
                    appId = "notify.route-errors",
                    method = "task.run",
                    target = new
                    {
                        scope = ScopeContract.Global,
                        instanceId = "missing.instance"
                    },
                    options = new
                    {
                        queueIfOffline = false,
                        autoLaunch = false
                    }
                }),
            CancellationToken.None);

        AssertError(missingInstanceResponse, -32010, "instance_not_found", "notify-missing-target-instance");
        Assert.Equal("target_instance_missing", JsonSerializer.SerializeToElement(missingInstanceResponse.Error!.Data).GetProperty("reason").GetString());
    }

    [Fact]
    [Trait("Category", "Spec")]
    [Trait("SpecRef", "6.3.13")]
    [Trait("SpecRef", "6.3.15")]
    public async Task Spec_6_3_13_And_6_3_15_InvocationRpcHandler_NotifyAndPoll_ShouldDeliverQueuedInvocation()
    {
        WriteDefinition("notify.success", rpcEnabled: true);

        var eventPublisher = new Mock<IHubEventPublisher>();
        using var appRegistry = new AppRegistry(new SystemClock(), Mock.Of<ILogger<AppRegistry>>());
        var notifyToken = RegisterInstance(appRegistry, "notify.success", "notify.instance");
        using var context = CreateInvocationHandlerContext(appRegistry, eventPublisher: eventPublisher.Object);

        var notifyResponse = await context.Handler.HandleAsync(
            CreateRequest(
                HubRpcMethods.HubInvokeNotify,
                "notify-success",
                new
                {
                    appId = "notify.success",
                    target = new
                    {
                        scope = ScopeContract.Global,
                        instanceId = (string?)null
                    },
                    method = "task.notify",
                    args = new
                    {
                        count = 2
                    }
                },
                clientId: "caller-ui",
                clientSessionId: "11111111-1111-1111-1111-111111111111"),
            CancellationToken.None);

        var notifyResult = JsonSerializer.SerializeToElement(notifyResponse.Result);
        Assert.True(notifyResult.GetProperty("ok").GetBoolean());
        var invocationId = notifyResult.GetProperty("invocationId").GetString();
        Assert.False(string.IsNullOrWhiteSpace(invocationId));

        var pollResponse = await context.Handler.HandleAsync(
            CreateRequest(
                HubRpcMethods.HubInvokePoll,
                "poll-notify",
                new
                {
                    instanceId = "notify.instance",
                    instanceSessionToken = notifyToken,
                    maxCount = 1,
                    waitMs = 200
                }),
            CancellationToken.None);

        var pollResult = JsonSerializer.SerializeToElement(pollResponse.Result);
        Assert.True(pollResult.GetProperty("ok").GetBoolean());
        var item = Assert.Single(pollResult.GetProperty("items").EnumerateArray());
        Assert.Equal(invocationId, item.GetProperty("invocationId").GetString());
        Assert.Equal("notify", item.GetProperty("kind").GetString());
        Assert.Equal("caller-ui", item.GetProperty("caller").GetProperty("clientId").GetString());
        Assert.Equal("11111111-1111-1111-1111-111111111111", item.GetProperty("caller").GetProperty("clientSessionId").GetString());
        Assert.Equal(60000, item.GetProperty("options").GetProperty("ttlMs").GetInt32());

        eventPublisher.Verify(
            publisher => publisher.Publish(It.Is<HubEventMessage>(message => message.Type == HubEventTypes.InvocationQueued)),
            Times.Once);
        eventPublisher.Verify(
            publisher => publisher.Publish(It.Is<HubEventMessage>(message => message.Type == HubEventTypes.InvocationDelivered)),
            Times.Once);
    }

    [Fact]
    [Trait("Category", "Spec")]
    [Trait("SpecRef", "6.3.14")]
    [Trait("SpecRef", "6.3.16")]
    public async Task Spec_6_3_14_And_6_3_16_InvocationRpcHandler_Request_ShouldReturnValueAfterPollAndRespond()
    {
        WriteDefinition("request.success", rpcEnabled: true);

        var eventPublisher = new Mock<IHubEventPublisher>();
        using var appRegistry = new AppRegistry(new SystemClock(), Mock.Of<ILogger<AppRegistry>>());
        var requestToken = RegisterInstance(appRegistry, "request.success", "request.instance");
        using var context = CreateInvocationHandlerContext(appRegistry, eventPublisher: eventPublisher.Object);

        var requestTask = context.Handler.HandleAsync(
            CreateRequest(
                HubRpcMethods.HubInvokeRequest,
                "request-success",
                new
                {
                    appId = "request.success",
                    target = new
                    {
                        scope = ScopeContract.Global,
                        instanceId = (string?)null
                    },
                    method = "task.request",
                    options = new
                    {
                        ttlMs = 5000,
                        waitTimeoutMs = 3000,
                        queueIfOffline = true,
                        autoLaunch = false
                    }
                }),
            CancellationToken.None);

        var pollResponse = await context.Handler.HandleAsync(
            CreateRequest(
                HubRpcMethods.HubInvokePoll,
                "poll-request-success",
                new
                {
                    instanceId = "request.instance",
                    instanceSessionToken = requestToken,
                    maxCount = 1,
                    waitMs = 200
                }),
            CancellationToken.None);

        var item = Assert.Single(JsonSerializer.SerializeToElement(pollResponse.Result).GetProperty("items").EnumerateArray());
        var invocationId = item.GetProperty("invocationId").GetString();

        var respondResponse = await context.Handler.HandleAsync(
            CreateRequest(
                HubRpcMethods.HubInvokeRespond,
                "respond-request-success",
                new
                {
                    instanceId = "request.instance",
                    instanceSessionToken = requestToken,
                    invocationId,
                    value = new
                    {
                        ok = true,
                        count = 2
                    }
                }),
            CancellationToken.None);

        Assert.True(JsonSerializer.SerializeToElement(respondResponse.Result).GetProperty("ok").GetBoolean());

        var requestResponse = await requestTask;
        var requestResult = JsonSerializer.SerializeToElement(requestResponse.Result);
        Assert.True(requestResult.GetProperty("ok").GetBoolean());
        Assert.Equal(invocationId, requestResult.GetProperty("invocationId").GetString());
        Assert.Equal(2, requestResult.GetProperty("value").GetProperty("count").GetInt32());

        eventPublisher.Verify(
            publisher => publisher.Publish(It.Is<HubEventMessage>(message => message.Type == HubEventTypes.InvocationCompleted)),
            Times.Once);
    }

    [Fact]
    [Trait("Category", "Spec")]
    [Trait("SpecRef", "6.3.14")]
    public async Task Spec_6_3_14_InvocationRpcHandler_Request_WhenCalleeReturnsError_ShouldMapInvocationFailed()
    {
        WriteDefinition("request.failed", rpcEnabled: true);

        var eventPublisher = new Mock<IHubEventPublisher>();
        using var appRegistry = new AppRegistry(new SystemClock(), Mock.Of<ILogger<AppRegistry>>());
        var requestFailedToken = RegisterInstance(appRegistry, "request.failed", "request.failed.instance");
        using var context = CreateInvocationHandlerContext(appRegistry, eventPublisher: eventPublisher.Object);

        var requestTask = context.Handler.HandleAsync(
            CreateRequest(
                HubRpcMethods.HubInvokeRequest,
                "request-failed",
                new
                {
                    appId = "request.failed",
                    target = new
                    {
                        scope = ScopeContract.Global,
                        instanceId = (string?)null
                    },
                    method = "task.failed",
                    options = new
                    {
                        ttlMs = 5000,
                        waitTimeoutMs = 3000,
                        queueIfOffline = true,
                        autoLaunch = false
                    }
                }),
            CancellationToken.None);

        var pollResponse = await context.Handler.HandleAsync(
            CreateRequest(
                HubRpcMethods.HubInvokePoll,
                "poll-request-failed",
                new
                {
                    instanceId = "request.failed.instance",
                    instanceSessionToken = requestFailedToken,
                    maxCount = 1,
                    waitMs = 200
                }),
            CancellationToken.None);

        var invocationId = Assert.Single(JsonSerializer.SerializeToElement(pollResponse.Result).GetProperty("items").EnumerateArray())
            .GetProperty("invocationId")
            .GetString();

        await context.Handler.HandleAsync(
            CreateRequest(
                HubRpcMethods.HubInvokeRespond,
                "respond-request-failed",
                new
                {
                    instanceId = "request.failed.instance",
                    instanceSessionToken = requestFailedToken,
                    invocationId,
                    error = new
                    {
                        code = 1001,
                        message = "app_error",
                        data = new
                        {
                            detail = "boom"
                        }
                    }
                }),
            CancellationToken.None);

        var requestResponse = await requestTask;
        AssertError(requestResponse, -32050, "invocation_failed", "request-failed");

        var errorData = JsonSerializer.SerializeToElement(requestResponse.Error!.Data);
        Assert.Equal(invocationId, errorData.GetProperty("invocationId").GetString());
        Assert.Equal(1001, errorData.GetProperty("calleeError").GetProperty("code").GetInt32());
        Assert.Equal("boom", errorData.GetProperty("calleeError").GetProperty("data").GetProperty("detail").GetString());

        eventPublisher.Verify(
            publisher => publisher.Publish(It.Is<HubEventMessage>(message => message.Type == HubEventTypes.InvocationFailed)),
            Times.Once);
    }

    [Fact]
    [Trait("Category", "Spec")]
    [Trait("SpecRef", "6.3.14")]
    public async Task Spec_6_3_14_InvocationRpcHandler_Request_WhenWaitTimeoutElapses_ShouldReturnInvocationTimeout()
    {
        WriteDefinition("request.timeout", rpcEnabled: true);

        var clock = new SequenceClock(DateTime.UtcNow, TimeSpan.FromMilliseconds(600));
        using var appRegistry = new AppRegistry(clock, Mock.Of<ILogger<AppRegistry>>());
        using var context = CreateInvocationHandlerContext(appRegistry, clock: clock);

        var response = await context.Handler.HandleAsync(
            CreateRequest(
                HubRpcMethods.HubInvokeRequest,
                "request-timeout",
                new
                {
                    appId = "request.timeout",
                    target = new
                    {
                        scope = ScopeContract.Global,
                        instanceId = (string?)null
                    },
                    method = "task.timeout",
                    options = new
                    {
                        ttlMs = 2000,
                        waitTimeoutMs = 500,
                        queueIfOffline = true,
                        autoLaunch = false
                    }
                }),
            CancellationToken.None);

        AssertError(response, -32012, "invocation_timeout", "request-timeout");
        Assert.True(JsonSerializer.SerializeToElement(response.Error!.Data).GetProperty("elapsedMs").GetInt32() >= 500);
    }

    [Fact]
    [Trait("Category", "Spec")]
    [Trait("SpecRef", "6.3.14")]
    public async Task Spec_6_3_14_InvocationRpcHandler_Request_WhenWaitTimeoutEqualsTtl_ShouldPreferInvocationExpired()
    {
        WriteDefinition("request.expired", rpcEnabled: true);

        var clock = new SequenceClock(DateTime.UtcNow, TimeSpan.FromMilliseconds(1));
        using var appRegistry = new AppRegistry(clock, Mock.Of<ILogger<AppRegistry>>());
        using var context = CreateInvocationHandlerContext(appRegistry, clock: clock);

        var response = await context.Handler.HandleAsync(
            CreateRequest(
                HubRpcMethods.HubInvokeRequest,
                "request-expired",
                new
                {
                    appId = "request.expired",
                    target = new
                    {
                        scope = ScopeContract.Global,
                        instanceId = (string?)null
                    },
                    method = "task.expired",
                    options = new
                    {
                        ttlMs = 1000,
                        waitTimeoutMs = 1000,
                        queueIfOffline = true,
                        autoLaunch = false
                    }
                }),
            CancellationToken.None);

        AssertError(response, -32011, "invocation_expired", "request-expired");
    }

    [Fact]
    [Trait("Category", "Spec")]
    [Trait("SpecRef", "6.3.15")]
    public async Task Spec_6_3_15_InvocationRpcHandler_Poll_ShouldRejectUnknownOrUnauthorizedInstances()
    {
        using var appRegistry = new AppRegistry(new SystemClock(), Mock.Of<ILogger<AppRegistry>>());
        var pollDisabledToken = RegisterInstance(appRegistry, "poll.app", "poll.disabled", poll: false);
        using var context = CreateInvocationHandlerContext(appRegistry);

        var unknownResponse = await context.Handler.HandleAsync(
            CreateRequest(
                HubRpcMethods.HubInvokePoll,
                "poll-unknown",
                new
                {
                    instanceId = "missing.poll",
                    instanceSessionToken = "missing-poll-token",
                    waitMs = 0
                }),
            CancellationToken.None);

        AssertError(unknownResponse, -32010, "instance_not_found", "poll-unknown");

        var forbiddenResponse = await context.Handler.HandleAsync(
            CreateRequest(
                HubRpcMethods.HubInvokePoll,
                "poll-forbidden",
                new
                {
                    instanceId = "poll.disabled",
                    instanceSessionToken = pollDisabledToken,
                    waitMs = 0
                }),
            CancellationToken.None);

        AssertError(forbiddenResponse, -32002, "forbidden", "poll-forbidden");
        Assert.Equal("poll_not_enabled", JsonSerializer.SerializeToElement(forbiddenResponse.Error!.Data).GetProperty("reason").GetString());
    }

    [Fact]
    [Trait("Category", "Spec")]
    [Trait("SpecRef", "6.3.16")]
    public async Task Spec_6_3_16_InvocationRpcHandler_Respond_ShouldRejectInvalidPayloadsUnauthorizedInstancesAndDeliveryConflict()
    {
        WriteDefinition("respond.conflict", rpcEnabled: true);

        using var appRegistry = new AppRegistry(new SystemClock(), Mock.Of<ILogger<AppRegistry>>());
        var holderToken = RegisterInstance(appRegistry, "respond.conflict", "holder.instance");
        var otherToken = RegisterInstance(appRegistry, "respond.conflict", "other.instance");
        var respondDisabledToken = RegisterInstance(appRegistry, "respond.conflict", "respond.disabled", respond: false);
        using var context = CreateInvocationHandlerContext(appRegistry);

        var invalidPayloadResponse = await context.Handler.HandleAsync(
            CreateRequest(
                HubRpcMethods.HubInvokeRespond,
                "respond-invalid-payload",
                new
                {
                    instanceId = "holder.instance",
                    instanceSessionToken = holderToken,
                    invocationId = "invk-invalid",
                    value = new { ok = true },
                    error = new { code = 1, message = "bad" }
                }),
            CancellationToken.None);

        AssertError(invalidPayloadResponse, -32602, "invalid_params", "respond-invalid-payload");

        var respondDisabledResponse = await context.Handler.HandleAsync(
            CreateRequest(
                HubRpcMethods.HubInvokeRespond,
                "respond-disabled",
                new
                {
                    instanceId = "respond.disabled",
                    instanceSessionToken = respondDisabledToken,
                    invocationId = "invk-any",
                    value = new { ok = true }
                }),
            CancellationToken.None);

        AssertError(respondDisabledResponse, -32002, "forbidden", "respond-disabled");
        Assert.Equal("respond_not_enabled", JsonSerializer.SerializeToElement(respondDisabledResponse.Error!.Data).GetProperty("reason").GetString());

        var unknownInvocationResponse = await context.Handler.HandleAsync(
            CreateRequest(
                HubRpcMethods.HubInvokeRespond,
                "respond-unknown-invocation",
                new
                {
                    instanceId = "holder.instance",
                    instanceSessionToken = holderToken,
                    invocationId = "invk-unknown",
                    value = new { ok = true }
                }),
            CancellationToken.None);

        AssertError(unknownInvocationResponse, -32011, "invocation_expired", "respond-unknown-invocation");
        var unknownData = JsonSerializer.SerializeToElement(unknownInvocationResponse.Error!.Data);
        Assert.Equal("invk-unknown", unknownData.GetProperty("invocationId").GetString());
        Assert.Equal("unknown_invocation", unknownData.GetProperty("reason").GetString());

        var notifyResponse = await context.Handler.HandleAsync(
            CreateRequest(
                HubRpcMethods.HubInvokeNotify,
                "notify-for-conflict",
                new
                {
                    appId = "respond.conflict",
                    method = "task.run",
                    target = new
                    {
                        scope = ScopeContract.Global,
                        instanceId = "holder.instance"
                    },
                    options = new
                    {
                        ttlMs = 60000,
                        queueIfOffline = true,
                        autoLaunch = false
                    }
                }),
            CancellationToken.None);

        var invocationId = JsonSerializer.SerializeToElement(notifyResponse.Result).GetProperty("invocationId").GetString();

        await context.Handler.HandleAsync(
            CreateRequest(
                HubRpcMethods.HubInvokePoll,
                "poll-for-conflict",
                new
                {
                    instanceId = "holder.instance",
                    instanceSessionToken = holderToken,
                    maxCount = 1,
                    waitMs = 0
                }),
            CancellationToken.None);

        var conflictResponse = await context.Handler.HandleAsync(
            CreateRequest(
                HubRpcMethods.HubInvokeRespond,
                "respond-conflict",
                new
                {
                    instanceId = "other.instance",
                    instanceSessionToken = otherToken,
                    invocationId,
                    value = new { ok = true }
                }),
            CancellationToken.None);

        AssertError(conflictResponse, -32030, "delivery_conflict", "respond-conflict");
        var errorData = JsonSerializer.SerializeToElement(conflictResponse.Error!.Data);
        Assert.Equal(invocationId, errorData.GetProperty("invocationId").GetString());
        Assert.Equal("holder.instance", errorData.GetProperty("currentLeaseHolder").GetString());
    }

    /// <inheritdoc />
    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dataDirectory))
            {
                Directory.Delete(_dataDirectory, recursive: true);
            }
        }
        catch
        {
        }
    }

    private AppInstancesHandler CreateAppInstancesHandler(
        AppRegistry appRegistry,
        IClock? clock = null,
        IHubEventPublisher? eventPublisher = null,
        IDefinitionProvider? definitionProvider = null,
        ILaunchRegistrationTracker? launchRegistrationTracker = null)
    {
        if (definitionProvider is null)
        {
            return new AppInstancesHandler(
                appRegistry,
                clock ?? new SystemClock(),
                Mock.Of<ILogger<AppInstancesHandler>>(),
                eventPublisher);
        }

        return new AppInstancesHandler(
            appRegistry,
            definitionProvider,
            launchRegistrationTracker ?? NullLaunchRegistrationTracker.Instance,
            clock ?? new SystemClock(),
            Mock.Of<ILogger<AppInstancesHandler>>(),
            eventPublisher);
    }

    private InvocationHandlerTestContext CreateInvocationHandlerContext(
        AppRegistry appRegistry,
        IClock? clock = null,
        RuntimeTuningOptions? runtimeTuningOptions = null,
        IProcessLauncher? processLauncher = null,
        IHubEventPublisher? eventPublisher = null)
    {
        var effectiveClock = clock ?? new SystemClock();
        var effectiveRuntimeTuningOptions = runtimeTuningOptions ?? RuntimeTuningOptions.Default;
        var definitionLoader = new DefinitionLoader(DefinitionCatalogTestHelper.GetCatalogPath(_definitionsDirectory), Mock.Of<ILogger<DefinitionLoader>>());
        var definitionProvider = new DefinitionProvider(definitionLoader);
        definitionProvider.Refresh();

        var routingService = new InvocationRoutingService(appRegistry, Mock.Of<ILogger<InvocationRoutingService>>());
        var store = new InvocationStore(Mock.Of<ILogger<InvocationStore>>(), routingService, effectiveClock, eventPublisher);
        var waiter = new InvocationRequestWaiter(Mock.Of<ILogger<InvocationRequestWaiter>>());
        var runtimeHttpBaseUrlProvider = new Mock<IRuntimeHttpBaseUrlProvider>();
        runtimeHttpBaseUrlProvider.Setup(provider => provider.GetHttpBaseUrl()).Returns("http://127.0.0.1:57231");

        var launchCoordinator = new LaunchCoordinator(
            definitionProvider,
            appRegistry,
            runtimeHttpBaseUrlProvider.Object,
            processLauncher ?? Mock.Of<IProcessLauncher>(),
            effectiveClock,
            effectiveRuntimeTuningOptions,
            Mock.Of<ILogger<LaunchCoordinator>>());

        return new InvocationHandlerTestContext
        {
            Handler = new InvocationHandler(
                appRegistry,
                definitionProvider,
                routingService,
                store,
                waiter,
                launchCoordinator,
                effectiveClock,
                Mock.Of<ILogger<InvocationHandler>>(),
                effectiveRuntimeTuningOptions,
                eventPublisher),
            Store = store
        };
    }

    private DefinitionProvider CreateDefinitionProvider()
    {
        var definitionLoader = new DefinitionLoader(DefinitionCatalogTestHelper.GetCatalogPath(_definitionsDirectory), Mock.Of<ILogger<DefinitionLoader>>());
        var definitionProvider = new DefinitionProvider(definitionLoader);
        definitionProvider.Refresh();
        return definitionProvider;
    }

    private void WriteDefinition(string appId, bool rpcEnabled, bool includeLaunch = false, string? definitionScope = null)
    {
        var normalizedScope = definitionScope ?? ScopeContract.Global;
        var payload = new Dictionary<string, object?>
        {
            ["appId"] = appId,
            ["scope"] = normalizedScope,
            ["displayName"] = appId,
            ["capabilities"] = new
            {
                rpc = rpcEnabled,
                events = false
            }
        };

        if (includeLaunch)
        {
            payload["launch"] = new
            {
                exePath = "dotnet",
                argsTemplate = "--info"
            };
        }

        DefinitionCatalogTestHelper.UpsertDefinition(
            DefinitionCatalogTestHelper.GetCatalogPath(_definitionsDirectory),
            JsonSerializer.Serialize(payload));
    }

    private static string RegisterInstance(
        AppRegistry appRegistry,
        string appId,
        string instanceId,
        bool poll = true,
        bool respond = true,
        string? scope = null,
        int pid = 7301)
    {
        var normalizedScope = scope ?? ScopeContract.Global;
        var instance = new AppInstance
        {
            InstanceId = instanceId,
            AppId = appId,
            Scope = normalizedScope,
            Pid = pid,
            Invoke = new InvokeCapability
            {
                Poll = poll,
                Respond = respond
            }
        };

        var registered = appRegistry.TryRegisterInstance(
            instance,
            $"{instanceId}-password",
            out _,
            out var instanceSessionToken,
            out _);
        Assert.True(registered);
        return instanceSessionToken;
    }

    private static JsonRpcRequest CreateRequest(
        string method,
        object id,
        object? parameters,
        string? clientId = null,
        string? clientSessionId = null)
    {
        return new JsonRpcRequest
        {
            Id = id,
            Method = method,
            Params = parameters is null ? null : JsonSerializer.SerializeToElement(parameters),
            ClientId = clientId,
            ClientSessionId = clientSessionId
        };
    }

    private static void AssertError(JsonRpcResponse response, int code, string message, object id)
    {
        Assert.NotNull(response.Error);
        Assert.Equal(code, response.Error!.Code);
        Assert.Equal(message, response.Error.Message);
        Assert.Equal(id, response.Id);
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

    private sealed class InvocationHandlerTestContext : IDisposable
    {
        public required InvocationHandler Handler { get; init; }

        public required InvocationStore Store { get; init; }

        public void Dispose()
        {
        }
    }
}
