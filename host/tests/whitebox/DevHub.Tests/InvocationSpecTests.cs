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
/// Invocation 规范白盒测试。
/// </summary>
[Trait("Category", "Spec")]
public class InvocationSpecTests : IDisposable
{
    private readonly string _tempDirectory;

    public InvocationSpecTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "DevHubInvocationSpecTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
    }

    [Fact]
    [Trait("SpecRef", "6.3.13")]
    public async Task Spec_6_3_13_Notify_WhenOfflineAndOptionsOmitted_ShouldDefaultQueueAndAutoLaunch()
    {
        const string appId = "spec-6.3.10-default-offline-options";
        WriteDefinition(appId, rpcEnabled: true);

        using var appRegistry = new AppRegistry(new SystemClock(), Mock.Of<ILogger<AppRegistry>>());
        var handler = CreateInvocationHandler(appRegistry);

        var response = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "spec-6.3.10-default-offline-options",
            Method = "hub.invoke.notify",
            Params = JsonSerializer.SerializeToElement(new
            {
                appId,
                target = new { scope = string.Empty },
                method = "asset.notify.defaults.offline",
                args = new { value = 1 }
            })
        }, CancellationToken.None);

        AssertError(response, -32020, "launch_failed");
        var data = JsonSerializer.SerializeToElement(response.Error!.Data);
        Assert.Equal("launch_config_missing", data.GetProperty("reason").GetString());
    }

    [Fact]
    [Trait("SpecRef", "6.3.13")]
    public async Task Spec_6_3_13_Notify_WhenTargetInstanceSpecifiedAndOptionsOmitted_ShouldDefaultAutoLaunchFalse()
    {
        const string appId = "spec-6.3.10-default-target-instance-options";
        WriteDefinition(appId, rpcEnabled: true);

        using var appRegistry = new AppRegistry(new SystemClock(), Mock.Of<ILogger<AppRegistry>>());
        RegisterInstance(appRegistry, "spec-6.3.10-target-instance-online", appId, scope: null, poll: true, respond: true, pid: 7100);

        var handler = CreateInvocationHandler(appRegistry);

        var notify = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "spec-6.3.10-default-target-instance-options",
            Method = "hub.invoke.notify",
            Params = JsonSerializer.SerializeToElement(new
            {
                appId,
                target = new
                {
                    scope = string.Empty,
                    instanceId = "spec-6.3.10-target-instance-missing"
                },
                method = "asset.notify.target",
                args = new { value = 1 }
            })
        }, CancellationToken.None);

        AssertSuccess(notify);
        var invocationId = JsonSerializer.SerializeToElement(notify.Result).GetProperty("invocationId").GetString();
        Assert.False(string.IsNullOrWhiteSpace(invocationId));

        var onlinePoll = await PollAsync(handler, appRegistry, "spec-6.3.10-target-instance-online", maxCount: 1, waitMs: 0);
        AssertSuccess(onlinePoll);
        var onlineItems = JsonSerializer.SerializeToElement(onlinePoll.Result).GetProperty("items").EnumerateArray().ToList();
        Assert.Empty(onlineItems);
    }

    [Fact]
    [Trait("SpecRef", "6.3.13")]
    public async Task Spec_6_3_13_Notify_WhenOptionsOmittedWithExplicitGlobalScope_ShouldUseDefaultTtlAndRouteGlobal()
    {
        const string appId = "spec-6.3.10-default-options";
        WriteDefinition(appId, rpcEnabled: true);

        using var appRegistry = new AppRegistry(new SystemClock(), Mock.Of<ILogger<AppRegistry>>());
        RegisterInstance(appRegistry, "spec-6.3.10-global", appId, scope: null, poll: true, respond: true, pid: 7101);
        RegisterInstance(appRegistry, "spec-6.3.10-scoped", appId, scope: "workspace-A", poll: true, respond: true, pid: 7102);

        var handler = CreateInvocationHandler(appRegistry);

        var notify = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "spec-6.3.10-default-options-notify",
            Method = "hub.invoke.notify",
            Params = JsonSerializer.SerializeToElement(new
            {
                appId,
                target = new { scope = string.Empty },
                method = "asset.notify.defaults",
                args = new { value = 1 }
            })
        }, CancellationToken.None);

        AssertSuccess(notify);
        var notifyResult = JsonSerializer.SerializeToElement(notify.Result);
        var invocationId = notifyResult.GetProperty("invocationId").GetString();

        var pollGlobal = await PollAsync(handler, appRegistry, "spec-6.3.10-global", maxCount: 1, waitMs: 100);
        AssertSuccess(pollGlobal);
        var globalItems = JsonSerializer.SerializeToElement(pollGlobal.Result).GetProperty("items").EnumerateArray().ToList();
        Assert.Single(globalItems);
        Assert.Equal(invocationId, globalItems[0].GetProperty("invocationId").GetString());
        Assert.Equal(60000, globalItems[0].GetProperty("options").GetProperty("ttlMs").GetInt32());

        var pollScoped = await PollAsync(handler, appRegistry, "spec-6.3.10-scoped", maxCount: 1, waitMs: 0);
        AssertSuccess(pollScoped);
        var scopedItems = JsonSerializer.SerializeToElement(pollScoped.Result).GetProperty("items").EnumerateArray().ToList();
        Assert.Empty(scopedItems);
    }

    [Fact]
    [Trait("SpecRef", "6.3.13")]
    public async Task Spec_6_3_13_Notify_WhenScopeOmittedOrNull_ShouldReturnInvalidParams_AndEmptyShouldRouteOnlyToGlobal()
    {
        const string appId = "spec-6.3.10-explicit-global-scope";
        WriteDefinition(appId, rpcEnabled: true);

        using var appRegistry = new AppRegistry(new SystemClock(), Mock.Of<ILogger<AppRegistry>>());
        RegisterInstance(appRegistry, "spec-6.3.10-scope-global", appId, scope: null, poll: true, respond: true, pid: 7103);
        RegisterInstance(appRegistry, "spec-6.3.10-scope-scoped", appId, scope: "workspace-A", poll: true, respond: true, pid: 7104);

        var handler = CreateInvocationHandler(appRegistry);

        foreach (var testCase in new[]
                 {
                     new { Name = "omitted", Target = (object)new { }, ShouldReturnInvalidParams = true },
                     new { Name = "null", Target = (object)new { scope = (string?)null }, ShouldReturnInvalidParams = true },
                     new { Name = "empty", Target = (object)new { scope = string.Empty }, ShouldReturnInvalidParams = false }
                 })
        {
            var notify = await handler.HandleAsync(new JsonRpcRequest
            {
                Id = $"spec-6.3.10-scope-{testCase.Name}",
                Method = "hub.invoke.notify",
                Params = JsonSerializer.SerializeToElement(new
                {
                    appId,
                    target = testCase.Target,
                    method = "asset.notify.scope",
                    args = new { caseName = testCase.Name },
                    options = new
                    {
                        ttlMs = 60000,
                        queueIfOffline = true,
                        autoLaunch = false
                    }
                })
            }, CancellationToken.None);

            if (testCase.ShouldReturnInvalidParams)
            {
                AssertError(notify, -32602, "invalid_params");
                continue;
            }

            AssertSuccess(notify);
            var invocationId = JsonSerializer.SerializeToElement(notify.Result).GetProperty("invocationId").GetString();

            var pollGlobal = await PollAsync(handler, appRegistry, "spec-6.3.10-scope-global", maxCount: 1, waitMs: 100);
            AssertSuccess(pollGlobal);
            var globalItems = JsonSerializer.SerializeToElement(pollGlobal.Result).GetProperty("items").EnumerateArray().ToList();
            Assert.Single(globalItems);
            Assert.Equal(invocationId, globalItems[0].GetProperty("invocationId").GetString());

            var pollScoped = await PollAsync(handler, appRegistry, "spec-6.3.10-scope-scoped", maxCount: 1, waitMs: 0);
            AssertSuccess(pollScoped);
            var scopedItems = JsonSerializer.SerializeToElement(pollScoped.Result).GetProperty("items").EnumerateArray().ToList();
            Assert.Empty(scopedItems);
        }
    }

    [Fact]
    [Trait("SpecRef", "6.3.13")]
    public async Task Spec_6_3_13_Notify_WhenTargetInstanceAndAutoLaunchTrue_ShouldReturnInvalidParams()
    {
        using var appRegistry = new AppRegistry(new SystemClock(), Mock.Of<ILogger<AppRegistry>>());
        var handler = CreateInvocationHandler(appRegistry);

        var response = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "spec-6.3.10-invalid-target-instance",
            Method = "hub.invoke.notify",
            Params = JsonSerializer.SerializeToElement(new
            {
                appId = "spec-6.3.10-invalid-target-instance",
                target = new
                {
                    scope = string.Empty,
                    instanceId = "inst-target"
                },
                method = "asset.notify.invalid",
                options = new
                {
                    ttlMs = 60000,
                    queueIfOffline = true,
                    autoLaunch = true
                }
            })
        }, CancellationToken.None);

        AssertError(response, -32602, "invalid_params");
    }

    [Fact]
    [Trait("SpecRef", "6.3.13")]
    public async Task Spec_6_3_13_Notify_WhenAutoLaunchTrueAndQueueIfOfflineFalse_ShouldReturnInvalidParams()
    {
        using var appRegistry = new AppRegistry(new SystemClock(), Mock.Of<ILogger<AppRegistry>>());
        var handler = CreateInvocationHandler(appRegistry);

        var response = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "spec-6.3.10-invalid-auto-launch-queue",
            Method = "hub.invoke.notify",
            Params = JsonSerializer.SerializeToElement(new
            {
                appId = "spec-6.3.10-invalid-auto-launch-queue",
                target = new
                {
                    scope = string.Empty,
                    instanceId = (string?)null
                },
                method = "asset.notify.invalid",
                options = new
                {
                    ttlMs = 60000,
                    queueIfOffline = false,
                    autoLaunch = true
                }
            })
        }, CancellationToken.None);

        AssertError(response, -32602, "invalid_params");
    }

    [Fact]
    [Trait("SpecRef", "6.3.13")]
    public async Task Spec_6_3_13_Notify_WhenRpcDisabled_ShouldReturnForbiddenWithReason()
    {
        const string appId = "spec-6.3.10-rpc-disabled";
        WriteDefinition(appId, rpcEnabled: false);

        using var appRegistry = new AppRegistry(new SystemClock(), Mock.Of<ILogger<AppRegistry>>());
        var handler = CreateInvocationHandler(appRegistry);

        var response = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "spec-6.3.10-rpc-disabled",
            Method = "hub.invoke.notify",
            Params = JsonSerializer.SerializeToElement(new
            {
                appId,
                target = new
                {
                    scope = string.Empty,
                    instanceId = (string?)null
                },
                method = "asset.notify.disabled",
                options = new
                {
                    ttlMs = 60000,
                    queueIfOffline = true,
                    autoLaunch = false
                }
            })
        }, CancellationToken.None);

        AssertError(response, -32002, "forbidden");
        var data = JsonSerializer.SerializeToElement(response.Error!.Data);
        Assert.Equal("rpc_disabled", data.GetProperty("reason").GetString());
    }

    [Fact]
    [Trait("SpecRef", "6.3.14")]
    public async Task Spec_6_3_14_Request_WhenWaitTimeoutGreaterThanTtl_ShouldReturnInvalidParams()
    {
        using var appRegistry = new AppRegistry(new SystemClock(), Mock.Of<ILogger<AppRegistry>>());
        var handler = CreateInvocationHandler(appRegistry);

        var response = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "spec-6.3.11-invalid-wait-timeout",
            Method = "hub.invoke.request",
            Params = JsonSerializer.SerializeToElement(new
            {
                appId = "spec-6.3.11-invalid-wait-timeout",
                target = new { scope = string.Empty, instanceId = (string?)null },
                method = "asset.request.invalid",
                options = new
                {
                    ttlMs = 1000,
                    waitTimeoutMs = 1001,
                    queueIfOffline = true,
                    autoLaunch = false
                }
            })
        }, CancellationToken.None);

        AssertError(response, -32602, "invalid_params");
    }

    [Fact]
    [Trait("SpecRef", "6.3.14")]
    public async Task Spec_6_3_14_Request_WhenOptionsOmittedWithExplicitGlobalScope_ShouldApplyDefaults()
    {
        const string onlineAppId = "spec-6.3.11-default-options-online";
        const string onlineInstanceId = "spec-6.3.11-default-options-online-instance";
        const string offlineAppId = "spec-6.3.11-default-options-offline";

        WriteDefinition(onlineAppId, rpcEnabled: true);
        WriteDefinition(offlineAppId, rpcEnabled: true);

        using var appRegistry = new AppRegistry(new SystemClock(), Mock.Of<ILogger<AppRegistry>>());
        RegisterInstance(appRegistry, onlineInstanceId, onlineAppId, scope: null, poll: true, respond: true, pid: 7207);
        var handler = CreateInvocationHandler(appRegistry);

        var requestTask = handler.HandleAsync(new JsonRpcRequest
        {
            Id = "spec-6.3.11-default-options-online-request",
            Method = "hub.invoke.request",
            Params = JsonSerializer.SerializeToElement(new
            {
                appId = onlineAppId,
                target = new { scope = string.Empty },
                method = "asset.request.defaults",
                args = new { value = 1 }
            })
        }, CancellationToken.None);

        var poll = await PollAsync(handler, appRegistry, onlineInstanceId, maxCount: 1, waitMs: 800);
        AssertSuccess(poll);
        var pollItems = JsonSerializer.SerializeToElement(poll.Result).GetProperty("items").EnumerateArray().ToList();
        Assert.Single(pollItems);
        var invocationId = pollItems[0].GetProperty("invocationId").GetString();
        var leaseToken = pollItems[0].GetProperty("delivery").GetProperty("leaseToken").GetString();
        Assert.Equal(300000, pollItems[0].GetProperty("options").GetProperty("ttlMs").GetInt32());

        var respond = await RespondValueAsync(
            handler,
            appRegistry,
            "spec-6.3.11-default-options-online-respond",
            onlineInstanceId,
            invocationId!,
            leaseToken!,
            new
            {
                ok = true
            });
        AssertSuccess(respond);

        var requestResponse = await requestTask;
        AssertSuccess(requestResponse);
        var requestResult = JsonSerializer.SerializeToElement(requestResponse.Result);
        Assert.Equal(invocationId, requestResult.GetProperty("invocationId").GetString());
        Assert.True(requestResult.GetProperty("value").GetProperty("ok").GetBoolean());

        var offlineResponse = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "spec-6.3.11-default-options-offline-request",
            Method = "hub.invoke.request",
            Params = JsonSerializer.SerializeToElement(new
            {
                appId = offlineAppId,
                target = new { scope = string.Empty, instanceId = (string?)null },
                method = "asset.request.defaults.offline",
                args = new { value = 2 }
            })
        }, CancellationToken.None);

        AssertError(offlineResponse, -32020, "launch_failed");
        var offlineErrorData = JsonSerializer.SerializeToElement(offlineResponse.Error!.Data);
        Assert.Equal("launch_config_missing", offlineErrorData.GetProperty("reason").GetString());
    }

    [Fact]
    [Trait("SpecRef", "6.3.14")]
    public async Task Spec_6_3_14_Request_WhenTargetInstanceSpecifiedAndAutoLaunchOmitted_ShouldDefaultToFalse()
    {
        const string appId = "spec-6.3.11-target-instance-default-auto-launch";
        const string observerInstanceId = "spec-6.3.11-target-instance-default-auto-launch-observer";
        WriteDefinition(appId, rpcEnabled: true);

        using var appRegistry = new AppRegistry(new SystemClock(), Mock.Of<ILogger<AppRegistry>>());
        RegisterInstance(appRegistry, observerInstanceId, appId, scope: null, poll: true, respond: true, pid: 7208);
        var handler = CreateInvocationHandler(appRegistry);

        var response = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "spec-6.3.11-target-instance-default-auto-launch",
            Method = "hub.invoke.request",
            Params = JsonSerializer.SerializeToElement(new
            {
                appId,
                target = new { scope = string.Empty, instanceId = "missing-instance" },
                method = "asset.request.target",
                args = new { value = 3 },
                options = new
                {
                    ttlMs = 2000,
                    waitTimeoutMs = 120,
                    queueIfOffline = true
                }
            })
        }, CancellationToken.None);

        AssertError(response, -32012, "invocation_timeout");
        var data = JsonSerializer.SerializeToElement(response.Error!.Data);
        Assert.True(data.TryGetProperty("invocationId", out var invocationId));
        Assert.False(string.IsNullOrWhiteSpace(invocationId.GetString()));

        var observerPoll = await PollAsync(handler, appRegistry, observerInstanceId, maxCount: 1, waitMs: 0);
        AssertSuccess(observerPoll);
        var observerItems = JsonSerializer.SerializeToElement(observerPoll.Result).GetProperty("items").EnumerateArray().ToList();
        Assert.Empty(observerItems);
    }

    [Fact]
    [Trait("SpecRef", "6.3.14")]
    public async Task Spec_6_3_14_Request_WhenTargetInstanceAndAutoLaunchTrue_ShouldReturnInvalidParams()
    {
        using var appRegistry = new AppRegistry(new SystemClock(), Mock.Of<ILogger<AppRegistry>>());
        var handler = CreateInvocationHandler(appRegistry);

        var response = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "spec-6.3.11-invalid-target-instance",
            Method = "hub.invoke.request",
            Params = JsonSerializer.SerializeToElement(new
            {
                appId = "spec-6.3.11-invalid-target-instance",
                target = new { scope = string.Empty, instanceId = "inst-target" },
                method = "asset.request.invalid",
                options = new
                {
                    ttlMs = 2000,
                    waitTimeoutMs = 1000,
                    queueIfOffline = true,
                    autoLaunch = true
                }
            })
        }, CancellationToken.None);

        AssertError(response, -32602, "invalid_params");
    }

    [Fact]
    [Trait("SpecRef", "6.3.14")]
    public async Task Spec_6_3_14_Request_WhenAutoLaunchTrueAndQueueIfOfflineFalse_ShouldReturnInvalidParams()
    {
        using var appRegistry = new AppRegistry(new SystemClock(), Mock.Of<ILogger<AppRegistry>>());
        var handler = CreateInvocationHandler(appRegistry);

        var response = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "spec-6.3.11-invalid-auto-launch-queue",
            Method = "hub.invoke.request",
            Params = JsonSerializer.SerializeToElement(new
            {
                appId = "spec-6.3.11-invalid-auto-launch-queue",
                target = new { scope = string.Empty, instanceId = (string?)null },
                method = "asset.request.invalid",
                options = new
                {
                    ttlMs = 2000,
                    waitTimeoutMs = 1000,
                    queueIfOffline = false,
                    autoLaunch = true
                }
            })
        }, CancellationToken.None);

        AssertError(response, -32602, "invalid_params");
    }

    [Fact]
    [Trait("SpecRef", "6.3.14")]
    public async Task Spec_6_3_14_Request_WhenTtlElapsedBeforeCompletion_ShouldReturnInvocationExpired()
    {
        const string appId = "spec-6.3.11-ttl-expired";
        WriteDefinition(appId, rpcEnabled: true);

        var clock = new StepClock(DateTime.UtcNow, TimeSpan.FromMilliseconds(600));
        using var appRegistry = new AppRegistry(clock, Mock.Of<ILogger<AppRegistry>>());
        var handler = CreateInvocationHandler(appRegistry, clock: clock);

        var response = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "spec-6.3.11-ttl-expired",
            Method = "hub.invoke.request",
            Params = JsonSerializer.SerializeToElement(new
            {
                appId,
                target = new { scope = string.Empty, instanceId = (string?)null },
                method = "asset.request.expire",
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
        var data = JsonSerializer.SerializeToElement(response.Error!.Data);
        Assert.True(data.TryGetProperty("invocationId", out var invocationId));
        Assert.False(string.IsNullOrWhiteSpace(invocationId.GetString()));
        Assert.True(data.TryGetProperty("elapsedMs", out var elapsedMs));
        Assert.True(elapsedMs.ValueKind == JsonValueKind.Number && elapsedMs.TryGetInt32(out _));
    }

    [Fact]
    [Trait("SpecRef", "6.3.14")]
    public async Task Spec_6_3_14_Request_WhenRpcDisabled_ShouldReturnForbiddenWithReason()
    {
        const string appId = "spec-6.3.11-rpc-disabled";
        WriteDefinition(appId, rpcEnabled: false);

        using var appRegistry = new AppRegistry(new SystemClock(), Mock.Of<ILogger<AppRegistry>>());
        var handler = CreateInvocationHandler(appRegistry);

        var response = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "spec-6.3.11-rpc-disabled",
            Method = "hub.invoke.request",
            Params = JsonSerializer.SerializeToElement(new
            {
                appId,
                target = new { scope = string.Empty, instanceId = (string?)null },
                method = "asset.request.disabled",
                options = new
                {
                    ttlMs = 3000,
                    waitTimeoutMs = 1000,
                    queueIfOffline = true,
                    autoLaunch = false
                }
            })
        }, CancellationToken.None);

        AssertError(response, -32002, "forbidden");
        var data = JsonSerializer.SerializeToElement(response.Error!.Data);
        Assert.Equal("rpc_disabled", data.GetProperty("reason").GetString());
    }

    [Fact]
    [Trait("SpecRef", "6.3.14")]
    public async Task Spec_6_3_14_Request_WhenCalleeRespondsError_ShouldReturnInvocationFailed()
    {
        const string appId = "spec-6.3.11-invocation-failed";
        const string instanceId = "spec-6.3.11-invocation-failed-instance";
        WriteDefinition(appId, rpcEnabled: true);

        using var appRegistry = new AppRegistry(new SystemClock(), Mock.Of<ILogger<AppRegistry>>());
        RegisterInstance(appRegistry, instanceId, appId, scope: null, poll: true, respond: true, pid: 7200);
        var handler = CreateInvocationHandler(appRegistry);

        var requestTask = handler.HandleAsync(new JsonRpcRequest
        {
            Id = "spec-6.3.11-invocation-failed-request",
            Method = "hub.invoke.request",
            Params = JsonSerializer.SerializeToElement(new
            {
                appId,
                target = new { scope = string.Empty, instanceId = (string?)null },
                method = "asset.request.failed",
                options = new
                {
                    ttlMs = 5000,
                    waitTimeoutMs = 2000,
                    queueIfOffline = true,
                    autoLaunch = false
                }
            })
        }, CancellationToken.None);

        var poll = await PollAsync(handler, appRegistry, instanceId, maxCount: 1, waitMs: 800);
        AssertSuccess(poll);
        var pollItems = JsonSerializer.SerializeToElement(poll.Result).GetProperty("items").EnumerateArray().ToList();
        Assert.Single(pollItems);
        var invocationId = pollItems[0].GetProperty("invocationId").GetString();
        var leaseToken = pollItems[0].GetProperty("delivery").GetProperty("leaseToken").GetString();

        var respond = await RespondErrorAsync(
            handler,
            appRegistry,
            "spec-6.3.11-invocation-failed-respond",
            instanceId,
            invocationId!,
            leaseToken!,
            new
            {
                code = 1001,
                message = "callee_error",
                data = new
                {
                    reason = "bad_input"
                }
            });
        AssertSuccess(respond);

        var requestResponse = await requestTask;
        AssertError(requestResponse, -32050, "invocation_failed");
        var errorData = JsonSerializer.SerializeToElement(requestResponse.Error!.Data);
        Assert.Equal(invocationId, errorData.GetProperty("invocationId").GetString());
        var calleeError = errorData.GetProperty("calleeError");
        Assert.Equal(1001, calleeError.GetProperty("code").GetInt32());
        Assert.Equal("callee_error", calleeError.GetProperty("message").GetString());
        Assert.Equal("bad_input", calleeError.GetProperty("data").GetProperty("reason").GetString());
    }

    [Theory]
    [InlineData("scalar")]
    [InlineData("null")]
    [Trait("SpecRef", "6.3.14")]
    public async Task Spec_6_3_14_Request_WhenCalleeErrorDataIsJsonValue_ShouldPreserveData(string dataCase)
    {
        var appId = $"spec-callee-json-value-{dataCase}";
        var instanceId = $"spec-callee-json-value-{dataCase}-instance";
        WriteDefinition(appId, rpcEnabled: true);

        using var appRegistry = new AppRegistry(new SystemClock(), Mock.Of<ILogger<AppRegistry>>());
        RegisterInstance(appRegistry, instanceId, appId, scope: null, poll: true, respond: true, pid: 7201);
        var handler = CreateInvocationHandler(appRegistry);

        var requestTask = handler.HandleAsync(new JsonRpcRequest
        {
            Id = $"spec-callee-json-value-{dataCase}-request",
            Method = "hub.invoke.request",
            Params = JsonSerializer.SerializeToElement(new
            {
                appId,
                target = new { scope = ScopeContract.Global, instanceId = (string?)null },
                method = "asset.request.failed",
                options = new
                {
                    ttlMs = 5000,
                    waitTimeoutMs = 2000,
                    queueIfOffline = true,
                    autoLaunch = false
                }
            })
        }, CancellationToken.None);

        var poll = await PollAsync(handler, appRegistry, instanceId, maxCount: 1, waitMs: 800);
        AssertSuccess(poll);
        var pollItem = Assert.Single(JsonSerializer.SerializeToElement(poll.Result).GetProperty("items").EnumerateArray());
        var invocationId = pollItem.GetProperty("invocationId").GetString();
        var leaseToken = pollItem.GetProperty("delivery").GetProperty("leaseToken").GetString();

        var errorElement = dataCase == "null"
            ? JsonSerializer.SerializeToElement(new { code = 1001, message = "callee_error", data = (object?)null })
            : JsonSerializer.SerializeToElement(new { code = 1001, message = "callee_error", data = "invalid-name" });

        var respond = await RespondErrorAsync(
            handler,
            appRegistry,
            $"spec-callee-json-value-{dataCase}-respond",
            instanceId,
            invocationId!,
            leaseToken!,
            errorElement);
        AssertSuccess(respond);

        var requestResponse = await requestTask;
        AssertError(requestResponse, -32050, "invocation_failed");
        var calleeError = JsonSerializer.SerializeToElement(requestResponse.Error!.Data)
            .GetProperty("calleeError");
        Assert.Equal(1001, calleeError.GetProperty("code").GetInt32());
        Assert.Equal("callee_error", calleeError.GetProperty("message").GetString());
        var data = calleeError.GetProperty("data");
        if (dataCase == "null")
        {
            Assert.Equal(JsonValueKind.Null, data.ValueKind);
        }
        else
        {
            Assert.Equal("invalid-name", data.GetString());
        }
    }

    [Fact]
    [Trait("SpecRef", "6.3.14")]
    public async Task Spec_6_3_14_Request_WhenScopeOmittedOrNull_ShouldReturnInvalidParams_AndEmptyShouldRouteOnlyToGlobal()
    {
        const string appId = "spec-6.3.11-explicit-global-scope";
        WriteDefinition(appId, rpcEnabled: true);

        using var appRegistry = new AppRegistry(new SystemClock(), Mock.Of<ILogger<AppRegistry>>());
        RegisterInstance(appRegistry, "spec-6.3.11-global", appId, scope: null, poll: true, respond: true, pid: 7201);
        RegisterInstance(appRegistry, "spec-6.3.11-scoped", appId, scope: "workspace-A", poll: true, respond: true, pid: 7202);

        var handler = CreateInvocationHandler(appRegistry);

        foreach (var testCase in new[]
                 {
                     new { Name = "omitted", Target = (object)new { }, ShouldReturnInvalidParams = true },
                     new { Name = "null", Target = (object)new { scope = (string?)null }, ShouldReturnInvalidParams = true },
                     new { Name = "empty", Target = (object)new { scope = string.Empty }, ShouldReturnInvalidParams = false }
                 })
        {
            var requestTask = handler.HandleAsync(new JsonRpcRequest
            {
                Id = $"spec-6.3.11-scope-{testCase.Name}",
                Method = "hub.invoke.request",
                Params = JsonSerializer.SerializeToElement(new
                {
                    appId,
                    target = testCase.Target,
                    method = "asset.request.scope",
                    args = new { caseName = testCase.Name },
                    options = new
                    {
                        ttlMs = 5000,
                        waitTimeoutMs = 2000,
                        queueIfOffline = true,
                        autoLaunch = false
                    }
                })
            }, CancellationToken.None);

            if (testCase.ShouldReturnInvalidParams)
            {
                var invalidResponse = await requestTask;
                AssertError(invalidResponse, -32602, "invalid_params");
                continue;
            }

            var pollGlobal = await PollAsync(handler, appRegistry, "spec-6.3.11-global", maxCount: 1, waitMs: 800);
            AssertSuccess(pollGlobal);
            var globalItems = JsonSerializer.SerializeToElement(pollGlobal.Result).GetProperty("items").EnumerateArray().ToList();
            Assert.Single(globalItems);
            var invocationId = globalItems[0].GetProperty("invocationId").GetString();
            var leaseToken = globalItems[0].GetProperty("delivery").GetProperty("leaseToken").GetString();

            var pollScoped = await PollAsync(handler, appRegistry, "spec-6.3.11-scoped", maxCount: 1, waitMs: 0);
            AssertSuccess(pollScoped);
            var scopedItems = JsonSerializer.SerializeToElement(pollScoped.Result).GetProperty("items").EnumerateArray().ToList();
            Assert.Empty(scopedItems);

            var respond = await RespondValueAsync(
                handler,
                appRegistry,
                $"spec-6.3.11-respond-{testCase.Name}",
                "spec-6.3.11-global",
                invocationId!,
                leaseToken!,
                new { caseName = testCase.Name });
            AssertSuccess(respond);

            var requestResponse = await requestTask;
            AssertSuccess(requestResponse);
            var requestResult = JsonSerializer.SerializeToElement(requestResponse.Result);
            Assert.Equal(invocationId, requestResult.GetProperty("invocationId").GetString());
            Assert.Equal(testCase.Name, requestResult.GetProperty("value").GetProperty("caseName").GetString());
        }
    }

    [Fact]
    [Trait("SpecRef", "6.3.15")]
    public async Task Spec_6_3_15_Poll_WhenInstanceUnregistered_ShouldReturnInstanceNotFound()
    {
        using var appRegistry = new AppRegistry(new SystemClock(), Mock.Of<ILogger<AppRegistry>>());
        var handler = CreateInvocationHandler(appRegistry);

        var response = await PollAsync(
            handler,
            appRegistry,
            "spec-6.3.12-missing-instance",
            maxCount: 1,
            waitMs: 0,
            instanceSessionToken: "missing-instance-token");

        AssertError(response, -32010, "instance_not_found");
    }

    [Fact]
    [Trait("SpecRef", "6.3.15")]
    public async Task Spec_6_3_15_Poll_WhenInvokePollDisabled_ShouldReturnForbiddenWithReason()
    {
        const string appId = "spec-6.3.12-poll-disabled";
        WriteDefinition(appId, rpcEnabled: true);

        using var appRegistry = new AppRegistry(new SystemClock(), Mock.Of<ILogger<AppRegistry>>());
        RegisterInstance(appRegistry, "spec-6.3.12-poll-disabled", appId, scope: null, poll: false, respond: true, pid: 7301);

        var handler = CreateInvocationHandler(appRegistry);
        var response = await PollAsync(handler, appRegistry, "spec-6.3.12-poll-disabled", maxCount: 1, waitMs: 0);

        AssertError(response, -32002, "forbidden");
        var data = JsonSerializer.SerializeToElement(response.Error!.Data);
        Assert.Equal("poll_not_enabled", data.GetProperty("reason").GetString());
    }

    [Fact]
    [Trait("SpecRef", "6.3.15")]
    public async Task Spec_6_3_15_Poll_WhenNoItems_ShouldLongPollUntilWaitMsThenReturnEmptyItems()
    {
        const string appId = "spec-6.3.12-long-poll";
        WriteDefinition(appId, rpcEnabled: true);

        using var appRegistry = new AppRegistry(new SystemClock(), Mock.Of<ILogger<AppRegistry>>());
        RegisterInstance(appRegistry, "spec-6.3.12-long-poll", appId, scope: null, poll: true, respond: true, pid: 7302);

        var handler = CreateInvocationHandler(appRegistry);

        var stopwatch = Stopwatch.StartNew();
        var response = await PollAsync(handler, appRegistry, "spec-6.3.12-long-poll", maxCount: 1, waitMs: 140);
        stopwatch.Stop();

        AssertSuccess(response);
        var items = JsonSerializer.SerializeToElement(response.Result).GetProperty("items").EnumerateArray().ToList();
        Assert.Empty(items);
        Assert.True(stopwatch.ElapsedMilliseconds >= 100);
    }

    [Fact]
    [Trait("SpecRef", "6.3.15")]
    public async Task Spec_6_3_15_Poll_WhenSucceeds_ShouldReturnLeaseSeconds()
    {
        const string appId = "spec-6.3.12-last-seen";
        const string instanceId = "spec-6.3.12-last-seen-instance";
        WriteDefinition(appId, rpcEnabled: true);

        using var appRegistry = new AppRegistry(new SystemClock(), Mock.Of<ILogger<AppRegistry>>());
        RegisterInstance(appRegistry, instanceId, appId, scope: null, poll: true, respond: true, pid: 7303);

        var handler = CreateInvocationHandler(appRegistry);

        var notify = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "spec-6.3.12-last-seen-notify",
            Method = "hub.invoke.notify",
            Params = JsonSerializer.SerializeToElement(new
            {
                appId,
                target = new { scope = string.Empty, instanceId = (string?)null },
                method = "asset.notify.poll",
                options = new
                {
                    ttlMs = 60000,
                    queueIfOffline = false,
                    autoLaunch = false
                }
            })
        }, CancellationToken.None);
        AssertSuccess(notify);

        var poll = await PollAsync(handler, appRegistry, instanceId, maxCount: 1, waitMs: 200);
        AssertSuccess(poll);

        var items = JsonSerializer.SerializeToElement(poll.Result).GetProperty("items").EnumerateArray().ToList();
        Assert.Single(items);
        var leaseSeconds = items[0].GetProperty("delivery").GetProperty("leaseSeconds").GetInt32();
        var leaseToken = items[0].GetProperty("delivery").GetProperty("leaseToken").GetString();
        Assert.True(leaseSeconds >= 1);
        Assert.False(string.IsNullOrWhiteSpace(leaseToken));
    }

    [Fact]
    [Trait("SpecRef", "6.3.16")]
    public async Task Spec_6_3_16_Respond_WhenInstanceUnregistered_ShouldReturnInstanceNotFound()
    {
        using var appRegistry = new AppRegistry(new SystemClock(), Mock.Of<ILogger<AppRegistry>>());
        var handler = CreateInvocationHandler(appRegistry);

        var response = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "spec-6.3.13-unknown-instance",
            Method = "hub.invoke.respond",
            Params = JsonSerializer.SerializeToElement(new
            {
                instanceId = "spec-6.3.13-unknown-instance",
                instanceSessionToken = "missing-instance-token",
                invocationId = "invk-missing",
                leaseToken = "missing-lease-token",
                value = new { ok = true }
            })
        }, CancellationToken.None);

        AssertError(response, -32010, "instance_not_found");
        var data = JsonSerializer.SerializeToElement(response.Error!.Data);
        Assert.Equal("unknown_instance", data.GetProperty("reason").GetString());
    }

    [Fact]
    [Trait("SpecRef", "6.3.16")]
    public async Task Spec_6_3_16_Respond_WhenInvokeRespondDisabled_ShouldReturnForbiddenWithReason()
    {
        const string appId = "spec-6.3.13-respond-disabled";
        WriteDefinition(appId, rpcEnabled: true);

        using var appRegistry = new AppRegistry(new SystemClock(), Mock.Of<ILogger<AppRegistry>>());
        RegisterInstance(appRegistry, "spec-6.3.13-respond-disabled", appId, scope: null, poll: true, respond: false, pid: 7401);

        var handler = CreateInvocationHandler(appRegistry);
        var response = await RespondValueAsync(
            handler,
            appRegistry,
            "spec-6.3.13-respond-disabled",
            "spec-6.3.13-respond-disabled",
            "invk-missing",
            "missing-lease-token",
            new { ok = true });

        AssertError(response, -32002, "forbidden");
        var data = JsonSerializer.SerializeToElement(response.Error!.Data);
        Assert.Equal("respond_not_enabled", data.GetProperty("reason").GetString());
    }

    [Fact]
    [Trait("SpecRef", "6.3.16")]
    public async Task Spec_6_3_16_Respond_WhenValueAndErrorXorInvalid_ShouldReturnInvalidParams()
    {
        using var appRegistry = new AppRegistry(new SystemClock(), Mock.Of<ILogger<AppRegistry>>());
        var handler = CreateInvocationHandler(appRegistry);

        var both = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "spec-6.3.13-xor-both",
            Method = "hub.invoke.respond",
            Params = JsonSerializer.SerializeToElement(new
            {
                instanceId = "any-instance",
                invocationId = "invk-any",
                value = new { ok = true },
                error = new { code = 1001, message = "app_error" }
            })
        }, CancellationToken.None);
        AssertError(both, -32602, "invalid_params");

        var none = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "spec-6.3.13-xor-none",
            Method = "hub.invoke.respond",
            Params = JsonSerializer.SerializeToElement(new
            {
                instanceId = "any-instance",
                invocationId = "invk-any"
            })
        }, CancellationToken.None);
        AssertError(none, -32602, "invalid_params");
    }

    [Fact]
    [Trait("SpecRef", "6.3.16")]
    public async Task Spec_6_3_16_Respond_WhenErrorPayloadMalformed_ShouldReturnInvalidParams()
    {
        using var appRegistry = new AppRegistry(new SystemClock(), Mock.Of<ILogger<AppRegistry>>());
        var handler = CreateInvocationHandler(appRegistry);

        var nonObject = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "spec-6.3.13-error-non-object",
            Method = "hub.invoke.respond",
            Params = JsonSerializer.SerializeToElement(new
            {
                instanceId = "any-instance",
                invocationId = "invk-any",
                error = "boom"
            })
        }, CancellationToken.None);
        AssertError(nonObject, -32602, "invalid_params");

        var missingCode = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "spec-6.3.13-error-missing-code",
            Method = "hub.invoke.respond",
            Params = JsonSerializer.SerializeToElement(new
            {
                instanceId = "any-instance",
                invocationId = "invk-any",
                error = new { message = "app_error" }
            })
        }, CancellationToken.None);
        AssertError(missingCode, -32602, "invalid_params");

        var invalidData = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "spec-6.3.13-error-invalid-data",
            Method = "hub.invoke.respond",
            Params = JsonSerializer.SerializeToElement(new
            {
                instanceId = "any-instance",
                invocationId = "invk-any",
                error = new { code = 1001, message = "app_error", data = 42 }
            })
        }, CancellationToken.None);
        AssertError(invalidData, -32602, "invalid_params");
    }

    [Fact]
    [Trait("SpecRef", "6.3.16")]
    public async Task Spec_6_3_16_Respond_WhenNonLeaseHolderOrDuplicate_ShouldReturnDeliveryConflict()
    {
        const string appId = "spec-6.3.13-delivery-conflict";
        const string holderInstanceId = "spec-6.3.13-holder";
        const string otherInstanceId = "spec-6.3.13-other";
        WriteDefinition(appId, rpcEnabled: true);

        using var appRegistry = new AppRegistry(new SystemClock(), Mock.Of<ILogger<AppRegistry>>());
        RegisterInstance(appRegistry, holderInstanceId, appId, scope: null, poll: true, respond: true, pid: 7402);
        RegisterInstance(appRegistry, otherInstanceId, appId, scope: null, poll: true, respond: true, pid: 7403);

        var handler = CreateInvocationHandler(appRegistry);

        var notify = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "spec-6.3.13-delivery-conflict-notify",
            Method = "hub.invoke.notify",
            Params = JsonSerializer.SerializeToElement(new
            {
                appId,
                target = new { scope = string.Empty, instanceId = holderInstanceId },
                method = "asset.respond.conflict",
                options = new
                {
                    ttlMs = 60000,
                    queueIfOffline = true,
                    autoLaunch = false
                }
            })
        }, CancellationToken.None);
        AssertSuccess(notify);

        var poll = await PollAsync(handler, appRegistry, holderInstanceId, maxCount: 1, waitMs: 800);
        AssertSuccess(poll);
        var items = JsonSerializer.SerializeToElement(poll.Result).GetProperty("items").EnumerateArray().ToList();
        Assert.Single(items);
        var invocationId = items[0].GetProperty("invocationId").GetString();
        var leaseToken = items[0].GetProperty("delivery").GetProperty("leaseToken").GetString();

        var nonHolder = await RespondValueAsync(
            handler,
            appRegistry,
            "spec-6.3.13-delivery-conflict-non-holder",
            otherInstanceId,
            invocationId!,
            leaseToken!,
            new { ok = true });
        AssertError(nonHolder, -32030, "delivery_conflict");

        var first = await RespondValueAsync(
            handler,
            appRegistry,
            "spec-6.3.13-delivery-conflict-first",
            holderInstanceId,
            invocationId!,
            leaseToken!,
            new { ok = true });
        AssertSuccess(first);

        var duplicate = await RespondValueAsync(
            handler,
            appRegistry,
            "spec-6.3.13-delivery-conflict-duplicate",
            holderInstanceId,
            invocationId!,
            leaseToken!,
            new { ok = true });
        AssertError(duplicate, -32030, "delivery_conflict");
    }

    [Fact]
    [Trait("SpecRef", "6.3.16")]
    public async Task Spec_6_3_16_Respond_WhenInvocationTimedOutOrExpired_ShouldReturnInvocationExpired()
    {
        const string timeoutAppId = "spec-6.3.13-timeout";
        const string timeoutInstanceId = "spec-6.3.13-timeout-instance";
        WriteDefinition(timeoutAppId, rpcEnabled: true);

        using var timeoutRegistry = new AppRegistry(new SystemClock(), Mock.Of<ILogger<AppRegistry>>());
        RegisterInstance(timeoutRegistry, timeoutInstanceId, timeoutAppId, scope: null, poll: true, respond: true, pid: 7404);

        var timeoutHandler = CreateInvocationHandler(timeoutRegistry);

        var timeoutRequestTask = timeoutHandler.HandleAsync(new JsonRpcRequest
        {
            Id = "spec-6.3.13-timeout-request",
            Method = "hub.invoke.request",
            Params = JsonSerializer.SerializeToElement(new
            {
                appId = timeoutAppId,
                target = new { scope = string.Empty, instanceId = (string?)null },
                method = "asset.respond.timeout",
                options = new
                {
                    ttlMs = 5000,
                    waitTimeoutMs = 300,
                    queueIfOffline = true,
                    autoLaunch = false
                }
            })
        }, CancellationToken.None);

        var timeoutPoll = await PollAsync(timeoutHandler, timeoutRegistry, timeoutInstanceId, maxCount: 1, waitMs: 800);
        AssertSuccess(timeoutPoll);
        var timeoutItems = JsonSerializer.SerializeToElement(timeoutPoll.Result).GetProperty("items").EnumerateArray().ToList();
        Assert.Single(timeoutItems);
        var timeoutInvocationId = timeoutItems[0].GetProperty("invocationId").GetString();
        var timeoutLeaseToken = timeoutItems[0].GetProperty("delivery").GetProperty("leaseToken").GetString();

        var timeoutResponse = await timeoutRequestTask;
        AssertError(timeoutResponse, -32012, "invocation_timeout");

        var lateTimeoutRespond = await RespondValueAsync(
            timeoutHandler,
            timeoutRegistry,
            "spec-6.3.13-timeout-late-respond",
            timeoutInstanceId,
            timeoutInvocationId!,
            timeoutLeaseToken!,
            new { ok = true });
        AssertError(lateTimeoutRespond, -32011, "invocation_expired");

        const string expiredAppId = "spec-6.3.13-expired";
        WriteDefinition(expiredAppId, rpcEnabled: true);
        var expiredClock = new StepClock(DateTime.UtcNow, TimeSpan.FromMilliseconds(600));
        using var expiredRegistry = new AppRegistry(expiredClock, Mock.Of<ILogger<AppRegistry>>());
        RegisterInstance(expiredRegistry, "spec-6.3.13-expired-helper", "helper.app", scope: null, poll: true, respond: true, pid: 7405);
        var expiredHandler = CreateInvocationHandler(expiredRegistry, clock: expiredClock);

        var expiredResponse = await expiredHandler.HandleAsync(new JsonRpcRequest
        {
            Id = "spec-6.3.13-expired-request",
            Method = "hub.invoke.request",
            Params = JsonSerializer.SerializeToElement(new
            {
                appId = expiredAppId,
                target = new { scope = string.Empty, instanceId = (string?)null },
                method = "asset.respond.expired",
                options = new
                {
                    ttlMs = 1000,
                    waitTimeoutMs = 1000,
                    queueIfOffline = true,
                    autoLaunch = false
                }
            })
        }, CancellationToken.None);

        AssertError(expiredResponse, -32011, "invocation_expired");
        var expiredData = JsonSerializer.SerializeToElement(expiredResponse.Error!.Data);
        var expiredInvocationId = expiredData.GetProperty("invocationId").GetString();

        var lateExpiredRespond = await RespondValueAsync(
            expiredHandler,
            expiredRegistry,
            "spec-6.3.13-expired-late-respond",
            "spec-6.3.13-expired-helper",
            expiredInvocationId!,
            "expired-lease-token",
            new { ok = true });

        AssertError(lateExpiredRespond, -32011, "invocation_expired");
    }

    [Fact]
    [Trait("SpecRef", "6.3.16")]
    public async Task Spec_6_3_16_Respond_WhenSucceeds_ShouldReturnOk()
    {
        const string appId = "spec-6.3.13-last-seen";
        const string instanceId = "spec-6.3.13-last-seen-instance";
        WriteDefinition(appId, rpcEnabled: true);

        using var appRegistry = new AppRegistry(new SystemClock(), Mock.Of<ILogger<AppRegistry>>());
        RegisterInstance(appRegistry, instanceId, appId, scope: null, poll: true, respond: true, pid: 7406);

        var handler = CreateInvocationHandler(appRegistry);

        var notify = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "spec-6.3.13-last-seen-notify",
            Method = "hub.invoke.notify",
            Params = JsonSerializer.SerializeToElement(new
            {
                appId,
                target = new { scope = string.Empty, instanceId = (string?)null },
                method = "asset.respond.last-seen",
                options = new
                {
                    ttlMs = 60000,
                    queueIfOffline = false,
                    autoLaunch = false
                }
            })
        }, CancellationToken.None);
        AssertSuccess(notify);

        var poll = await PollAsync(handler, appRegistry, instanceId, maxCount: 1, waitMs: 800);
        AssertSuccess(poll);
        var items = JsonSerializer.SerializeToElement(poll.Result).GetProperty("items").EnumerateArray().ToList();
        Assert.Single(items);
        var invocationId = items[0].GetProperty("invocationId").GetString();
        var leaseToken = items[0].GetProperty("delivery").GetProperty("leaseToken").GetString();

        var respond = await RespondValueAsync(
            handler,
            appRegistry,
            "spec-6.3.13-last-seen-respond",
            instanceId,
            invocationId!,
            leaseToken!,
            new { ok = true });

        AssertSuccess(respond);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }

    private InvocationHandler CreateInvocationHandler(AppRegistry appRegistry, IClock? clock = null, IProcessLauncher? processLauncher = null)
    {
        var effectiveClock = clock ?? new SystemClock();
        var definitionLoader = new DefinitionLoader(DefinitionCatalogTestHelper.GetCatalogPath(_tempDirectory), Mock.Of<ILogger<DefinitionLoader>>());
        var definitionProvider = new DefinitionProvider(definitionLoader);
        definitionProvider.Refresh();

        var routingService = new InvocationRoutingService(appRegistry, Mock.Of<ILogger<InvocationRoutingService>>());
        var store = new InvocationStore(Mock.Of<ILogger<InvocationStore>>(), routingService, effectiveClock);
        var waiter = new InvocationRequestWaiter(Mock.Of<ILogger<InvocationRequestWaiter>>());

        var runtimeProvider = new Mock<IRuntimeHttpBaseUrlProvider>();
        runtimeProvider.Setup(provider => provider.GetHttpBaseUrl()).Returns("http://127.0.0.1:7351");

        var coordinator = new LaunchCoordinator(
            definitionProvider,
            appRegistry,
            runtimeProvider.Object,
            processLauncher ?? new ProcessLauncher(),
            effectiveClock,
            Mock.Of<ILogger<LaunchCoordinator>>());

        return new InvocationHandler(
            appRegistry,
            definitionProvider,
            routingService,
            store,
            waiter,
            coordinator,
            effectiveClock,
            Mock.Of<ILogger<InvocationHandler>>());
    }

    private static async Task<JsonRpcResponse> PollAsync(
        InvocationHandler handler,
        AppRegistry appRegistry,
        string instanceId,
        int maxCount,
        int waitMs,
        string? instanceSessionToken = null)
    {
        return await handler.HandleAsync(new JsonRpcRequest
        {
            Id = $"poll-{instanceId}-{Guid.NewGuid():N}",
            Method = "hub.invoke.poll",
            Params = JsonSerializer.SerializeToElement(new
            {
                instanceId,
                instanceSessionToken = instanceSessionToken ?? GetInstanceSessionToken(appRegistry, instanceId),
                maxCount,
                waitMs
            })
        }, CancellationToken.None);
    }

    private static async Task<JsonRpcResponse> RespondValueAsync(
        InvocationHandler handler,
        AppRegistry appRegistry,
        string requestId,
        string instanceId,
        string invocationId,
        string leaseToken,
        object value)
    {
        return await handler.HandleAsync(new JsonRpcRequest
        {
            Id = requestId,
            Method = "hub.invoke.respond",
            Params = JsonSerializer.SerializeToElement(new
            {
                instanceId,
                instanceSessionToken = GetInstanceSessionToken(appRegistry, instanceId),
                invocationId,
                leaseToken,
                value
            })
        }, CancellationToken.None);
    }

    private static async Task<JsonRpcResponse> RespondErrorAsync(
        InvocationHandler handler,
        AppRegistry appRegistry,
        string requestId,
        string instanceId,
        string invocationId,
        string leaseToken,
        object error)
    {
        return await handler.HandleAsync(new JsonRpcRequest
        {
            Id = requestId,
            Method = "hub.invoke.respond",
            Params = JsonSerializer.SerializeToElement(new
            {
                instanceId,
                instanceSessionToken = GetInstanceSessionToken(appRegistry, instanceId),
                invocationId,
                leaseToken,
                error
            })
        }, CancellationToken.None);
    }

    private static string GetInstanceSessionToken(AppRegistry appRegistry, string instanceId)
    {
        return appRegistry.GetCurrentInstanceSessionToken(instanceId)
               ?? throw new InvalidOperationException($"Instance '{instanceId}' session token was not registered.");
    }

    private static AppInstance RegisterInstance(
        AppRegistry appRegistry,
        string instanceId,
        string appId,
        string? scope,
        bool poll,
        bool respond,
        int pid)
    {
        var normalizedScope = scope ?? ScopeContract.Global;
        return appRegistry.RegisterInstance(new AppInstance
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
        });
    }

    private void WriteDefinition(string appId, bool rpcEnabled)
    {
        var payload = new
        {
            appId,
            scope = ScopeContract.Global,
            displayName = appId,
            capabilities = new
            {
                rpc = rpcEnabled,
                events = false
            }
        };

        DefinitionCatalogTestHelper.UpsertDefinition(
            DefinitionCatalogTestHelper.GetCatalogPath(_tempDirectory),
            JsonSerializer.Serialize(payload));
    }

    private static void AssertSuccess(JsonRpcResponse response)
    {
        Assert.Null(response.Error);
        Assert.NotNull(response.Result);
        var result = JsonSerializer.SerializeToElement(response.Result);
        Assert.True(result.GetProperty("ok").GetBoolean());
    }

    private static void AssertError(JsonRpcResponse response, int expectedCode, string expectedMessage)
    {
        Assert.NotNull(response.Error);
        Assert.Equal(expectedCode, response.Error!.Code);
        Assert.Equal(expectedMessage, response.Error.Message);
    }

    private sealed class StepClock : IClock
    {
        private readonly DateTime _start;
        private readonly TimeSpan _step;
        private int _readCount;

        public StepClock(DateTime start, TimeSpan step)
        {
            _start = start;
            _step = step;
        }

        public DateTime UtcNow
        {
            get
            {
                var count = Interlocked.Increment(ref _readCount);
                return _start.AddTicks(_step.Ticks * count);
            }
        }
    }
}
