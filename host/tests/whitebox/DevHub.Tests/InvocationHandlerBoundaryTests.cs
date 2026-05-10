namespace DevHub.Tests;

using System.Diagnostics;
using System.Text.Json;
using DevHub.Core.Models;
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
[Trait("Category", "Impl")]
public sealed class InvocationHandlerBoundaryTests : IDisposable
{
    private readonly string _dataDirectory;
    private readonly string _definitionsDirectory;

    /// <summary>
    /// 初始化测试上下文。
    /// </summary>
    public InvocationHandlerBoundaryTests()
    {
        _dataDirectory = Path.Combine(Path.GetTempPath(), "DevHubInvocationHandlerBoundaryTests", Guid.NewGuid().ToString("N"));
        _definitionsDirectory = Path.Combine(_dataDirectory, "apps", "definitions");
        Directory.CreateDirectory(_definitionsDirectory);
    }

    [Fact]
    public async Task Impl_Notify_WhenRequiredFieldsMissing_ShouldReturnInvalidParams()
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
    public async Task Impl_Notify_WhenParamsNotObject_ShouldReturnInvalidParams()
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
    public async Task Impl_Notify_WhenOptionsContainInvalidTypes_ShouldReturnInvalidParams()
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
                target = new { scope = ScopeContract.Global, instanceId = (string?)null },
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
                target = new { scope = ScopeContract.Global, instanceId = (string?)null },
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
    public async Task Impl_Request_WhenOptionsInvalid_ShouldReturnInvalidParams()
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
                target = new { scope = ScopeContract.Global, instanceId = (string?)null },
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
                target = new { scope = ScopeContract.Global, instanceId = (string?)null },
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
    public async Task Impl_Notify_WhenTargetInstanceIdMalformed_ShouldReturnInvalidParamsBeforeRouting()
    {
        WriteDefinition("invocation-notify-invalid-target-instance", rpcEnabled: true);
        using var appRegistry = new AppRegistry(new SystemClock(), Mock.Of<ILogger<AppRegistry>>());
        var handler = CreateHandler(appRegistry);

        var response = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "notify-invalid-target-instance",
            Method = HubRpcMethods.HubInvokeNotify,
            Params = JsonSerializer.SerializeToElement(new
            {
                appId = "invocation-notify-invalid-target-instance",
                target = new { scope = ScopeContract.Global, instanceId = "node:01" },
                method = "task.run",
                options = new
                {
                    queueIfOffline = false,
                    autoLaunch = false
                }
            })
        }, CancellationToken.None);

        AssertError(response, -32602, "invalid_params");
        var data = JsonSerializer.SerializeToElement(response.Error!.Data);
        Assert.Equal("invalid_target_instance", data.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Impl_Request_WhenTargetInstanceIdOverlong_ShouldReturnInvalidParamsBeforeRouting()
    {
        WriteDefinition("invocation-request-overlong-target-instance", rpcEnabled: true);
        using var appRegistry = new AppRegistry(new SystemClock(), Mock.Of<ILogger<AppRegistry>>());
        var handler = CreateHandler(appRegistry);

        var response = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "request-overlong-target-instance",
            Method = HubRpcMethods.HubInvokeRequest,
            Params = JsonSerializer.SerializeToElement(new
            {
                appId = "invocation-request-overlong-target-instance",
                target = new
                {
                    scope = ScopeContract.Global,
                    instanceId = new string('a', ProtocolIdentifier.MaxInstanceIdLength + 1)
                },
                method = "task.run",
                options = new
                {
                    ttlMs = 300000,
                    waitTimeoutMs = 120000,
                    queueIfOffline = false,
                    autoLaunch = false
                }
            })
        }, CancellationToken.None);

        AssertError(response, -32602, "invalid_params");
        var data = JsonSerializer.SerializeToElement(response.Error!.Data);
        Assert.Equal("invalid_target_instance", data.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Impl_Poll_WhenParamsInvalid_ShouldReturnInvalidParams()
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

        var overlongInstanceId = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "poll-overlong-instance-id",
            Method = HubRpcMethods.HubInvokePoll,
            Params = JsonSerializer.SerializeToElement(new { instanceId = new string('a', ProtocolIdentifier.MaxInstanceIdLength + 1), waitMs = 0 })
        }, CancellationToken.None);
        AssertError(overlongInstanceId, -32602, "invalid_params");
    }

    [Fact]
    public async Task Impl_Respond_WhenParamsInvalid_ShouldReturnInvalidParams()
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

        var nullError = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "respond-null-error",
            Method = HubRpcMethods.HubInvokeRespond,
            Params = JsonSerializer.SerializeToElement(new
            {
                instanceId = "inst-1",
                invocationId = "invk-1",
                error = (object?)null
            })
        }, CancellationToken.None);
        AssertError(nullError, -32602, "invalid_params");

        var missingErrorCode = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "respond-missing-error-code",
            Method = HubRpcMethods.HubInvokeRespond,
            Params = JsonSerializer.SerializeToElement(new
            {
                instanceId = "inst-1",
                invocationId = "invk-1",
                error = new { message = "app_error" }
            })
        }, CancellationToken.None);
        AssertError(missingErrorCode, -32602, "invalid_params");

        var invalidErrorData = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "respond-invalid-error-data",
            Method = HubRpcMethods.HubInvokeRespond,
            Params = JsonSerializer.SerializeToElement(new
            {
                instanceId = "inst-1",
                invocationId = "invk-1",
                error = new { code = 1001, message = "app_error", data = "boom" }
            })
        }, CancellationToken.None);
        AssertError(invalidErrorData, -32602, "invalid_params");

        var overlongInstanceId = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "respond-overlong-instance-id",
            Method = HubRpcMethods.HubInvokeRespond,
            Params = JsonSerializer.SerializeToElement(new
            {
                instanceId = new string('a', ProtocolIdentifier.MaxInstanceIdLength + 1),
                invocationId = "invk-1",
                value = new { ok = true }
            })
        }, CancellationToken.None);
        AssertError(overlongInstanceId, -32602, "invalid_params");
    }

    [Fact]
    public async Task Impl_Respond_WhenInvocationMissing_ShouldReturnInvocationExpired()
    {
        using var appRegistry = new AppRegistry(new SystemClock(), Mock.Of<ILogger<AppRegistry>>());
        var instanceToken = RegisterInstance(appRegistry, "respond-missing-app", "respond-missing-inst", pid: 6001);

        var handler = CreateHandler(appRegistry);

        var response = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "respond-missing-invocation",
            Method = HubRpcMethods.HubInvokeRespond,
            Params = JsonSerializer.SerializeToElement(new
            {
                instanceId = "respond-missing-inst",
                instanceSessionToken = instanceToken,
                invocationId = "invk-not-exists",
                leaseToken = "missing-lease-token",
                value = new { ok = true }
            })
        }, CancellationToken.None);

        AssertError(response, -32011, "invocation_expired");
        var errorData = JsonSerializer.SerializeToElement(response.Error!.Data);
        Assert.Equal("invk-not-exists", errorData.GetProperty("invocationId").GetString());
        Assert.Equal("unknown_invocation", errorData.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Impl_Request_WhenCanceledAndTtlReached_ShouldReturnInvocationExpired()
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
                target = new { scope = ScopeContract.Global, instanceId = (string?)null },
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
    public async Task Impl_Request_WhenWaitTimeoutEqualsTtl_ShouldPreferInvocationExpired()
    {
        const string appId = "invocation-timeout-equals-ttl";
        WriteDefinition(appId, rpcEnabled: true);

        var clock = new SequenceClock(DateTime.UtcNow, TimeSpan.FromMilliseconds(1));
        using var appRegistry = new AppRegistry(clock, Mock.Of<ILogger<AppRegistry>>());
        var handler = CreateHandler(appRegistry, clock);

        var response = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "request-timeout-equals-ttl",
            Method = HubRpcMethods.HubInvokeRequest,
            Params = JsonSerializer.SerializeToElement(new
            {
                appId,
                target = new { scope = ScopeContract.Global, instanceId = (string?)null },
                method = "task.run",
                options = new
                {
                    ttlMs = 1000,
                    waitTimeoutMs = 1000,
                    queueIfOffline = true,
                    autoLaunch = false
                }
            })
        }, CancellationToken.None);

        AssertError(response, -32011, "invocation_expired");
    }

    [Fact]
    public async Task Impl_Request_WhenWaitBudgetAlreadyElapsed_ShouldTimeoutWithoutWaitingExtraWindow()
    {
        const string appId = "invocation-wait-budget-elapsed";
        WriteDefinition(appId, rpcEnabled: true);

        var clock = new SequenceClock(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), TimeSpan.FromMilliseconds(600));
        using var appRegistry = new AppRegistry(clock, Mock.Of<ILogger<AppRegistry>>());
        var handler = CreateHandler(appRegistry, clock);

        var response = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "request-wait-budget-elapsed",
            Method = HubRpcMethods.HubInvokeRequest,
            Params = JsonSerializer.SerializeToElement(new
            {
                appId,
                target = new { scope = ScopeContract.Global, instanceId = (string?)null },
                method = "task.run",
                options = new
                {
                    ttlMs = 2000,
                    waitTimeoutMs = 500,
                    queueIfOffline = true,
                    autoLaunch = false
                }
            })
        }, CancellationToken.None);

        AssertError(response, -32012, "invocation_timeout");

        var errorData = JsonSerializer.SerializeToElement(response.Error!.Data);
        Assert.True(errorData.GetProperty("elapsedMs").GetInt32() >= 500);
    }

    [Fact]
    public async Task Impl_Notify_WhenPendingInvocationLimitReached_ShouldReturnRateLimited()
    {
        const string appId = "invocation-rate-limited";
        WriteDefinition(appId, rpcEnabled: true);

        using var appRegistry = new AppRegistry(new SystemClock(), Mock.Of<ILogger<AppRegistry>>());
        var runtimeTuningOptions = RuntimeTuningOptions.Create(30, 30, 30, pendingInvocationsLimit: 1);
        var handler = CreateHandler(appRegistry, runtimeTuningOptions: runtimeTuningOptions);

        var firstResponse = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "notify-rate-limit-first",
            Method = HubRpcMethods.HubInvokeNotify,
            Params = JsonSerializer.SerializeToElement(new
            {
                appId,
                target = new { scope = ScopeContract.Global, instanceId = (string?)null },
                method = "task.queue.first",
                options = new
                {
                    ttlMs = 60000,
                    queueIfOffline = true,
                    autoLaunch = false
                }
            })
        }, CancellationToken.None);
        Assert.Null(firstResponse.Error);

        var secondResponse = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "notify-rate-limit-second",
            Method = HubRpcMethods.HubInvokeNotify,
            Params = JsonSerializer.SerializeToElement(new
            {
                appId,
                target = new { scope = ScopeContract.Global, instanceId = (string?)null },
                method = "task.queue.second",
                options = new
                {
                    ttlMs = 60000,
                    queueIfOffline = true,
                    autoLaunch = false
                }
            })
        }, CancellationToken.None);

        AssertError(secondResponse, -32040, "rate_limited");
        var errorData = JsonSerializer.SerializeToElement(secondResponse.Error!.Data);
        Assert.Equal("pending_invocations_limit_exceeded", errorData.GetProperty("reason").GetString());
        Assert.Equal(1, errorData.GetProperty("limit").GetInt32());
        Assert.Equal(1, errorData.GetProperty("active").GetInt32());
    }

    [Fact]
    public async Task Impl_Notify_WhenPendingLimitReachedAndQueueIfOfflineDisabled_ShouldReturnInstanceNotFound()
    {
        const string queuedAppId = "invocation-rate-limit-offline-seed";
        const string rejectedAppId = "invocation-rate-limit-offline-no-queue";
        WriteDefinition(queuedAppId, rpcEnabled: true);
        WriteDefinition(rejectedAppId, rpcEnabled: true);

        using var appRegistry = new AppRegistry(new SystemClock(), Mock.Of<ILogger<AppRegistry>>());
        var runtimeTuningOptions = RuntimeTuningOptions.Create(30, 30, 30, pendingInvocationsLimit: 1);
        var context = CreateHandlerContext(appRegistry, runtimeTuningOptions: runtimeTuningOptions);

        var firstResponse = await context.Handler.HandleAsync(new JsonRpcRequest
        {
            Id = "notify-rate-limit-no-queue-seed",
            Method = HubRpcMethods.HubInvokeNotify,
            Params = JsonSerializer.SerializeToElement(new
            {
                appId = queuedAppId,
                target = new { scope = ScopeContract.Global, instanceId = (string?)null },
                method = "task.queue.seed",
                options = new
                {
                    ttlMs = 60000,
                    queueIfOffline = true,
                    autoLaunch = false
                }
            })
        }, CancellationToken.None);
        Assert.Null(firstResponse.Error);
        Assert.Equal(1, context.Store.GetActiveInvocationCount());

        var secondResponse = await context.Handler.HandleAsync(new JsonRpcRequest
        {
            Id = "notify-rate-limit-no-queue-rejected",
            Method = HubRpcMethods.HubInvokeNotify,
            Params = JsonSerializer.SerializeToElement(new
            {
                appId = rejectedAppId,
                target = new { scope = ScopeContract.Global, instanceId = (string?)null },
                method = "task.queue.rejected",
                options = new
                {
                    ttlMs = 60000,
                    queueIfOffline = false,
                    autoLaunch = false
                }
            })
        }, CancellationToken.None);

        AssertError(secondResponse, -32010, "instance_not_found");
        var errorData = JsonSerializer.SerializeToElement(secondResponse.Error!.Data);
        Assert.Equal("offline_no_queue", errorData.GetProperty("reason").GetString());
        Assert.Equal(1, context.Store.GetActiveInvocationCount());
    }

    [Fact]
    public async Task Impl_Notify_WhenPendingLimitReachedAndDefinitionMissing_ShouldReturnDefinitionNotFound()
    {
        const string queuedAppId = "invocation-rate-limit-definition-seed";
        const string missingAppId = "invocation-rate-limit-definition-missing";
        WriteDefinition(queuedAppId, rpcEnabled: true);

        using var appRegistry = new AppRegistry(new SystemClock(), Mock.Of<ILogger<AppRegistry>>());
        var runtimeTuningOptions = RuntimeTuningOptions.Create(30, 30, 30, pendingInvocationsLimit: 1);
        var context = CreateHandlerContext(appRegistry, runtimeTuningOptions: runtimeTuningOptions);

        var firstResponse = await context.Handler.HandleAsync(new JsonRpcRequest
        {
            Id = "notify-rate-limit-missing-definition-seed",
            Method = HubRpcMethods.HubInvokeNotify,
            Params = JsonSerializer.SerializeToElement(new
            {
                appId = queuedAppId,
                target = new { scope = ScopeContract.Global, instanceId = (string?)null },
                method = "task.queue.seed",
                options = new
                {
                    ttlMs = 60000,
                    queueIfOffline = true,
                    autoLaunch = false
                }
            })
        }, CancellationToken.None);
        Assert.Null(firstResponse.Error);
        Assert.Equal(1, context.Store.GetActiveInvocationCount());

        var secondResponse = await context.Handler.HandleAsync(new JsonRpcRequest
        {
            Id = "notify-rate-limit-missing-definition-rejected",
            Method = HubRpcMethods.HubInvokeNotify,
            Params = JsonSerializer.SerializeToElement(new
            {
                appId = missingAppId,
                target = new { scope = ScopeContract.Global, instanceId = (string?)null },
                method = "task.queue.rejected",
                options = new
                {
                    ttlMs = 60000,
                    queueIfOffline = true,
                    autoLaunch = true
                }
            })
        }, CancellationToken.None);

        AssertError(secondResponse, -32010, "instance_not_found");
        var errorData = JsonSerializer.SerializeToElement(secondResponse.Error!.Data);
        Assert.Equal("definition_not_found", errorData.GetProperty("reason").GetString());
        Assert.Equal(missingAppId, errorData.GetProperty("appId").GetString());
        Assert.Equal(ScopeContract.Global, errorData.GetProperty("scope").GetString());
        Assert.Equal(1, context.Store.GetActiveInvocationCount());
    }

    [Fact]
    public async Task Impl_Notify_WhenPendingLimitReachedAndAutoLaunchRequested_ShouldReturnRateLimitedWithoutLaunching()
    {
        const string queuedAppId = "invocation-rate-limit-autolaunch-seed";
        const string launchAppId = "invocation-rate-limit-autolaunch";
        WriteDefinition(queuedAppId, rpcEnabled: true);
        WriteDefinition(launchAppId, rpcEnabled: true, includeLaunch: true);

        using var appRegistry = new AppRegistry(new SystemClock(), Mock.Of<ILogger<AppRegistry>>());
        var runtimeTuningOptions = RuntimeTuningOptions.Create(30, 30, 30, pendingInvocationsLimit: 1);
        var processLauncher = new Mock<IProcessLauncher>();
        processLauncher
            .Setup(launcher => launcher.Start(It.IsAny<LaunchConfiguration>(), It.IsAny<string?>()))
            .Returns(Process.GetCurrentProcess());
        var context = CreateHandlerContext(
            appRegistry,
            runtimeTuningOptions: runtimeTuningOptions,
            processLauncher: processLauncher.Object);

        var firstResponse = await context.Handler.HandleAsync(new JsonRpcRequest
        {
            Id = "notify-rate-limit-autolaunch-seed",
            Method = HubRpcMethods.HubInvokeNotify,
            Params = JsonSerializer.SerializeToElement(new
            {
                appId = queuedAppId,
                target = new { scope = ScopeContract.Global, instanceId = (string?)null },
                method = "task.queue.seed",
                options = new
                {
                    ttlMs = 60000,
                    queueIfOffline = true,
                    autoLaunch = false
                }
            })
        }, CancellationToken.None);
        Assert.Null(firstResponse.Error);
        Assert.Equal(1, context.Store.GetActiveInvocationCount());

        var secondResponse = await context.Handler.HandleAsync(new JsonRpcRequest
        {
            Id = "notify-rate-limit-autolaunch-rejected",
            Method = HubRpcMethods.HubInvokeNotify,
            Params = JsonSerializer.SerializeToElement(new
            {
                appId = launchAppId,
                target = new { scope = ScopeContract.Global, instanceId = (string?)null },
                method = "task.queue.rejected",
                options = new
                {
                    ttlMs = 60000,
                    queueIfOffline = true,
                    autoLaunch = true
                }
            })
        }, CancellationToken.None);

        AssertError(secondResponse, -32040, "rate_limited");
        var errorData = JsonSerializer.SerializeToElement(secondResponse.Error!.Data);
        Assert.Equal("pending_invocations_limit_exceeded", errorData.GetProperty("reason").GetString());
        Assert.Equal(1, errorData.GetProperty("limit").GetInt32());
        Assert.Equal(1, errorData.GetProperty("active").GetInt32());
        processLauncher.Verify(launcher => launcher.Start(It.IsAny<LaunchConfiguration>(), It.IsAny<string?>()), Times.Never);
        Assert.Equal(1, context.Store.GetActiveInvocationCount());
    }

    [Fact]
    public async Task Impl_Notify_WhenPendingInvocationLimitReachedConcurrently_ShouldAllowOnlySingleInvocation()
    {
        const string firstAppId = "invocation-rate-limited-concurrent-a";
        const string secondAppId = "invocation-rate-limited-concurrent-b";
        WriteDefinition(firstAppId, rpcEnabled: true);
        WriteDefinition(secondAppId, rpcEnabled: true, includeLaunch: true);

        using var appRegistry = new AppRegistry(new SystemClock(), Mock.Of<ILogger<AppRegistry>>());
        var runtimeTuningOptions = RuntimeTuningOptions.Create(30, 30, 30, pendingInvocationsLimit: 1);
        using var processLauncher = new BlockingProcessLauncher(expectedStarts: 1);
        var context = CreateHandlerContext(
            appRegistry,
            runtimeTuningOptions: runtimeTuningOptions,
            processLauncher: processLauncher);

        Task<JsonRpcResponse> SendAsync(string requestId, string appId, string methodName) => Task.Run(() =>
            context.Handler.HandleAsync(new JsonRpcRequest
            {
                Id = requestId,
                Method = HubRpcMethods.HubInvokeNotify,
                Params = JsonSerializer.SerializeToElement(new
                {
                    appId,
                    target = new { scope = ScopeContract.Global, instanceId = (string?)null },
                    method = methodName,
                    options = new
                    {
                        ttlMs = 60000,
                        queueIfOffline = true,
                        autoLaunch = appId == secondAppId
                    }
                })
            }, CancellationToken.None));

        var firstResponse = await SendAsync("notify-rate-limit-concurrent-first", firstAppId, "task.queue.concurrent.first");
        Assert.Null(firstResponse.Error);
        Assert.Equal(1, context.Store.GetActiveInvocationCount());

        var secondResponse = await SendAsync("notify-rate-limit-concurrent-second", secondAppId, "task.queue.concurrent.second");
        AssertError(secondResponse, -32040, "rate_limited");

        var errorData = JsonSerializer.SerializeToElement(secondResponse.Error!.Data);
        Assert.Equal("pending_invocations_limit_exceeded", errorData.GetProperty("reason").GetString());
        Assert.Equal(1, errorData.GetProperty("limit").GetInt32());
        Assert.Equal(1, errorData.GetProperty("active").GetInt32());
        Assert.Equal(0, processLauncher.StartCount);
        Assert.Equal(1, context.Store.GetActiveInvocationCount());
    }

    [Fact]
    public async Task Impl_HandleAsync_WhenMethodUnknown_ShouldReturnMethodNotFound()
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
    public async Task Impl_Request_WhenCanceledBeforeTtlReached_ShouldCancelWait()
    {
        const string appId = "invocation-cancel-timeout";
        WriteDefinition(appId, rpcEnabled: true);

        var clock = new SequenceClock(DateTime.UtcNow, TimeSpan.FromMilliseconds(200));
        using var appRegistry = new AppRegistry(clock, Mock.Of<ILogger<AppRegistry>>());
        var handler = CreateHandler(appRegistry, clock);

        using var canceledTokenSource = new CancellationTokenSource();
        canceledTokenSource.Cancel();

        var requestTask = handler.HandleAsync(new JsonRpcRequest
        {
            Id = "request-cancel-timeout",
            Method = HubRpcMethods.HubInvokeRequest,
            Params = JsonSerializer.SerializeToElement(new
            {
                appId,
                target = new { scope = ScopeContract.Global, instanceId = (string?)null },
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

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await requestTask);
    }

    [Fact]
    public async Task Impl_Respond_WhenLeaseHeldByAnotherInstance_ShouldReturnDeliveryConflict()
    {
        const string appId = "invocation-delivery-conflict";
        WriteDefinition(appId, rpcEnabled: true);

        using var appRegistry = new AppRegistry(new SystemClock(), Mock.Of<ILogger<AppRegistry>>());
        var holderToken = RegisterInstance(appRegistry, appId, "holder-instance", pid: 6002);
        var otherToken = RegisterInstance(appRegistry, appId, "other-instance", pid: 6003);

        var handler = CreateHandler(appRegistry);

        var notify = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "notify-delivery-conflict",
            Method = HubRpcMethods.HubInvokeNotify,
            Params = JsonSerializer.SerializeToElement(new
            {
                appId,
                target = new { scope = ScopeContract.Global, instanceId = "holder-instance" },
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
                instanceSessionToken = holderToken,
                maxCount = 1,
                waitMs = 0
            })
        }, CancellationToken.None);
        Assert.Null(poll.Error);
        var leaseToken = JsonSerializer.SerializeToElement(poll.Result)
            .GetProperty("items")
            .EnumerateArray()
            .Single()
            .GetProperty("delivery")
            .GetProperty("leaseToken")
            .GetString();

        var conflict = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "respond-delivery-conflict",
            Method = HubRpcMethods.HubInvokeRespond,
            Params = JsonSerializer.SerializeToElement(new
            {
                instanceId = "other-instance",
                instanceSessionToken = otherToken,
                invocationId,
                leaseToken,
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
            if (Directory.Exists(_dataDirectory))
            {
                Directory.Delete(_dataDirectory, recursive: true);
            }
        }
        catch
        {
        }
    }

    private InvocationHandler CreateHandler(
        AppRegistry appRegistry,
        IClock? clock = null,
        RuntimeTuningOptions? runtimeTuningOptions = null,
        IProcessLauncher? processLauncher = null)
    {
        return CreateHandlerContext(appRegistry, clock, runtimeTuningOptions, processLauncher).Handler;
    }

    private InvocationHandlerTestContext CreateHandlerContext(
        AppRegistry appRegistry,
        IClock? clock = null,
        RuntimeTuningOptions? runtimeTuningOptions = null,
        IProcessLauncher? processLauncher = null)
    {
        var effectiveClock = clock ?? new SystemClock();
        var effectiveRuntimeTuningOptions = runtimeTuningOptions ?? RuntimeTuningOptions.Default;
        var effectiveProcessLauncher = processLauncher ?? new ProcessLauncher();

        var definitionLoader = new DefinitionLoader(DefinitionCatalogTestHelper.GetCatalogPath(_definitionsDirectory), Mock.Of<ILogger<DefinitionLoader>>());
        var definitionProvider = new DefinitionProvider(definitionLoader);
        definitionProvider.Refresh();

        var routingService = new InvocationRoutingService(appRegistry, Mock.Of<ILogger<InvocationRoutingService>>());
        var store = new InvocationStore(Mock.Of<ILogger<InvocationStore>>(), routingService, effectiveClock);
        var waiter = new InvocationRequestWaiter(Mock.Of<ILogger<InvocationRequestWaiter>>());
        var runtimeHttpBaseUrlProvider = new RuntimeHttpBaseUrlProvider(Mock.Of<ILogger<RuntimeHttpBaseUrlProvider>>(), RuntimePathOptions.Create(_dataDirectory));
        var launchCoordinator = new LaunchCoordinator(
            definitionProvider,
            appRegistry,
            runtimeHttpBaseUrlProvider,
            effectiveProcessLauncher,
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
                effectiveRuntimeTuningOptions),
            Store = store
        };
    }

    private void WriteDefinition(string appId, bool rpcEnabled, bool includeLaunch = false)
    {
        var payload = new Dictionary<string, object?>
        {
            ["appId"] = appId,
            ["scope"] = ScopeContract.Global,
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
                argsTemplate = "--version"
            };
        }

        DefinitionCatalogTestHelper.UpsertDefinition(
            DefinitionCatalogTestHelper.GetCatalogPath(_definitionsDirectory),
            JsonSerializer.Serialize(payload));
    }

    private static string RegisterInstance(AppRegistry appRegistry, string appId, string instanceId, int pid)
    {
        var registered = appRegistry.TryRegisterInstance(
            new DevHub.Core.Models.AppInstance
            {
                InstanceId = instanceId,
                AppId = appId,
                Scope = ScopeContract.Global,
                Pid = pid,
                Invoke = new DevHub.Core.Models.InvokeCapability
                {
                    Poll = true,
                    Respond = true
                }
            },
            $"{instanceId}-password",
            out _,
            out var instanceToken,
            out _);
        Assert.True(registered);
        return instanceToken;
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

    private sealed class InvocationHandlerTestContext
    {
        public required InvocationHandler Handler { get; init; }

        public required InvocationStore Store { get; init; }
    }

    private sealed class BlockingProcessLauncher : IProcessLauncher, IDisposable
    {
        private readonly CountdownEvent _started;
        private readonly ManualResetEventSlim _release = new(false);
        private int _startCount;

        public BlockingProcessLauncher(int expectedStarts)
        {
            _started = new CountdownEvent(expectedStarts);
        }

        public Process? Start(DevHub.Core.Models.LaunchConfiguration launchConfig, string? arguments)
        {
            Interlocked.Increment(ref _startCount);
            _started.Signal();
            _release.Wait();
            return Process.GetCurrentProcess();
        }

        public int StartCount => Volatile.Read(ref _startCount);

        public bool WaitUntilBlocked(TimeSpan timeout)
        {
            return _started.Wait(timeout);
        }

        public void Release()
        {
            _release.Set();
        }

        public void Dispose()
        {
            _release.Set();
            _release.Dispose();
            _started.Dispose();
        }
    }
}
