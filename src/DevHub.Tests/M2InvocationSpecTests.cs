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
/// M2 invocation 规范白盒测试。
/// </summary>
[Trait("Category", "Spec")]
public class M2InvocationSpecTests : IDisposable
{
    private readonly string _tempDirectory;

    public M2InvocationSpecTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "DevHubM2InvocationSpecTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
    }

    [Fact]
    [Trait("SpecRef", "6.3.10")]
    public async Task Spec_6_3_10_Notify_WhenOptionsOmitted_ShouldUseDefaultTtlAndRouteGlobal()
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
                target = new { },
                method = "asset.notify.defaults",
                args = new { value = 1 }
            })
        }, CancellationToken.None);

        AssertSuccess(notify);
        var notifyResult = JsonSerializer.SerializeToElement(notify.Result);
        var invocationId = notifyResult.GetProperty("invocationId").GetString();

        var pollGlobal = await PollAsync(handler, "spec-6.3.10-global", maxCount: 1, waitMs: 100);
        AssertSuccess(pollGlobal);
        var globalItems = JsonSerializer.SerializeToElement(pollGlobal.Result).GetProperty("items").EnumerateArray().ToList();
        Assert.Single(globalItems);
        Assert.Equal(invocationId, globalItems[0].GetProperty("invocationId").GetString());
        Assert.Equal(60000, globalItems[0].GetProperty("options").GetProperty("ttlMs").GetInt32());

        var pollScoped = await PollAsync(handler, "spec-6.3.10-scoped", maxCount: 1, waitMs: 0);
        AssertSuccess(pollScoped);
        var scopedItems = JsonSerializer.SerializeToElement(pollScoped.Result).GetProperty("items").EnumerateArray().ToList();
        Assert.Empty(scopedItems);
    }

    [Fact]
    [Trait("SpecRef", "6.3.10")]
    public async Task Spec_6_3_10_Notify_WhenScopeOmittedNullOrEmpty_ShouldRouteOnlyToGlobal()
    {
        const string appId = "spec-6.3.10-scope-normalization";
        WriteDefinition(appId, rpcEnabled: true);

        using var appRegistry = new AppRegistry(new SystemClock(), Mock.Of<ILogger<AppRegistry>>());
        RegisterInstance(appRegistry, "spec-6.3.10-scope-global", appId, scope: null, poll: true, respond: true, pid: 7103);
        RegisterInstance(appRegistry, "spec-6.3.10-scope-scoped", appId, scope: "workspace-A", poll: true, respond: true, pid: 7104);

        var handler = CreateInvocationHandler(appRegistry);

        foreach (var testCase in new[]
                 {
                     new { Name = "omitted", Target = (object)new { } },
                     new { Name = "null", Target = (object)new { scope = (string?)null } },
                     new { Name = "empty", Target = (object)new { scope = string.Empty } }
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

            AssertSuccess(notify);
            var invocationId = JsonSerializer.SerializeToElement(notify.Result).GetProperty("invocationId").GetString();

            var pollGlobal = await PollAsync(handler, "spec-6.3.10-scope-global", maxCount: 1, waitMs: 100);
            AssertSuccess(pollGlobal);
            var globalItems = JsonSerializer.SerializeToElement(pollGlobal.Result).GetProperty("items").EnumerateArray().ToList();
            Assert.Single(globalItems);
            Assert.Equal(invocationId, globalItems[0].GetProperty("invocationId").GetString());

            var pollScoped = await PollAsync(handler, "spec-6.3.10-scope-scoped", maxCount: 1, waitMs: 0);
            AssertSuccess(pollScoped);
            var scopedItems = JsonSerializer.SerializeToElement(pollScoped.Result).GetProperty("items").EnumerateArray().ToList();
            Assert.Empty(scopedItems);
        }
    }

    [Fact]
    [Trait("SpecRef", "6.3.10")]
    public async Task Spec_6_3_10_Notify_WhenTargetInstanceAndAutoLaunchTrue_ShouldReturnInvalidParams()
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
                    scope = (string?)null,
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
    [Trait("SpecRef", "6.3.10")]
    public async Task Spec_6_3_10_Notify_WhenAutoLaunchTrueAndQueueIfOfflineFalse_ShouldReturnInvalidParams()
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
                    scope = (string?)null,
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
    [Trait("SpecRef", "6.3.10")]
    public async Task Spec_6_3_10_Notify_WhenRpcDisabled_ShouldReturnForbiddenWithReason()
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
                    scope = (string?)null,
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
    [Trait("SpecRef", "6.3.11")]
    public async Task Spec_6_3_11_Request_WhenWaitTimeoutGreaterThanTtl_ShouldReturnInvalidParams()
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
                target = new { scope = (string?)null, instanceId = (string?)null },
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
    [Trait("SpecRef", "6.3.11")]
    public async Task Spec_6_3_11_Request_WhenTargetInstanceAndAutoLaunchTrue_ShouldReturnInvalidParams()
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
                target = new { scope = (string?)null, instanceId = "inst-target" },
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
    [Trait("SpecRef", "6.3.11")]
    public async Task Spec_6_3_11_Request_WhenAutoLaunchTrueAndQueueIfOfflineFalse_ShouldReturnInvalidParams()
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
                target = new { scope = (string?)null, instanceId = (string?)null },
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
    [Trait("SpecRef", "6.3.11")]
    public async Task Spec_6_3_11_Request_WhenTtlElapsedBeforeCompletion_ShouldReturnInvocationExpired()
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
                target = new { scope = (string?)null, instanceId = (string?)null },
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
    [Trait("SpecRef", "6.3.11")]
    public async Task Spec_6_3_11_Request_WhenRpcDisabled_ShouldReturnForbiddenWithReason()
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
                target = new { scope = (string?)null, instanceId = (string?)null },
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
    [Trait("SpecRef", "6.3.11")]
    public async Task Spec_6_3_11_Request_WhenScopeOmittedNullOrEmpty_ShouldRouteOnlyToGlobal()
    {
        const string appId = "spec-6.3.11-scope-normalization";
        WriteDefinition(appId, rpcEnabled: true);

        using var appRegistry = new AppRegistry(new SystemClock(), Mock.Of<ILogger<AppRegistry>>());
        RegisterInstance(appRegistry, "spec-6.3.11-global", appId, scope: null, poll: true, respond: true, pid: 7201);
        RegisterInstance(appRegistry, "spec-6.3.11-scoped", appId, scope: "workspace-A", poll: true, respond: true, pid: 7202);

        var handler = CreateInvocationHandler(appRegistry);

        foreach (var testCase in new[]
                 {
                     new { Name = "omitted", Target = (object)new { } },
                     new { Name = "null", Target = (object)new { scope = (string?)null } },
                     new { Name = "empty", Target = (object)new { scope = string.Empty } }
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

            var pollGlobal = await PollAsync(handler, "spec-6.3.11-global", maxCount: 1, waitMs: 800);
            AssertSuccess(pollGlobal);
            var globalItems = JsonSerializer.SerializeToElement(pollGlobal.Result).GetProperty("items").EnumerateArray().ToList();
            Assert.Single(globalItems);
            var invocationId = globalItems[0].GetProperty("invocationId").GetString();

            var pollScoped = await PollAsync(handler, "spec-6.3.11-scoped", maxCount: 1, waitMs: 0);
            AssertSuccess(pollScoped);
            var scopedItems = JsonSerializer.SerializeToElement(pollScoped.Result).GetProperty("items").EnumerateArray().ToList();
            Assert.Empty(scopedItems);

            var respond = await handler.HandleAsync(new JsonRpcRequest
            {
                Id = $"spec-6.3.11-respond-{testCase.Name}",
                Method = "hub.invoke.respond",
                Params = JsonSerializer.SerializeToElement(new
                {
                    instanceId = "spec-6.3.11-global",
                    invocationId,
                    value = new { caseName = testCase.Name }
                })
            }, CancellationToken.None);
            AssertSuccess(respond);

            var requestResponse = await requestTask;
            AssertSuccess(requestResponse);
            var requestResult = JsonSerializer.SerializeToElement(requestResponse.Result);
            Assert.Equal(invocationId, requestResult.GetProperty("invocationId").GetString());
            Assert.Equal(testCase.Name, requestResult.GetProperty("value").GetProperty("caseName").GetString());
        }
    }

    [Fact]
    [Trait("SpecRef", "6.3.12")]
    public async Task Spec_6_3_12_Poll_WhenInstanceUnregistered_ShouldReturnInstanceNotFound()
    {
        using var appRegistry = new AppRegistry(new SystemClock(), Mock.Of<ILogger<AppRegistry>>());
        var handler = CreateInvocationHandler(appRegistry);

        var response = await PollAsync(handler, "spec-6.3.12-missing-instance", maxCount: 1, waitMs: 0);

        AssertError(response, -32010, "instance_not_found");
    }

    [Fact]
    [Trait("SpecRef", "6.3.12")]
    public async Task Spec_6_3_12_Poll_WhenInvokePollDisabled_ShouldReturnForbiddenWithReason()
    {
        const string appId = "spec-6.3.12-poll-disabled";
        WriteDefinition(appId, rpcEnabled: true);

        using var appRegistry = new AppRegistry(new SystemClock(), Mock.Of<ILogger<AppRegistry>>());
        RegisterInstance(appRegistry, "spec-6.3.12-poll-disabled", appId, scope: null, poll: false, respond: true, pid: 7301);

        var handler = CreateInvocationHandler(appRegistry);
        var response = await PollAsync(handler, "spec-6.3.12-poll-disabled", maxCount: 1, waitMs: 0);

        AssertError(response, -32002, "forbidden");
        var data = JsonSerializer.SerializeToElement(response.Error!.Data);
        Assert.Equal("poll_not_enabled", data.GetProperty("reason").GetString());
    }

    [Fact]
    [Trait("SpecRef", "6.3.12")]
    public async Task Spec_6_3_12_Poll_WhenNoItems_ShouldLongPollUntilWaitMsThenReturnEmptyItems()
    {
        const string appId = "spec-6.3.12-long-poll";
        WriteDefinition(appId, rpcEnabled: true);

        using var appRegistry = new AppRegistry(new SystemClock(), Mock.Of<ILogger<AppRegistry>>());
        RegisterInstance(appRegistry, "spec-6.3.12-long-poll", appId, scope: null, poll: true, respond: true, pid: 7302);

        var handler = CreateInvocationHandler(appRegistry);

        var stopwatch = Stopwatch.StartNew();
        var response = await PollAsync(handler, "spec-6.3.12-long-poll", maxCount: 1, waitMs: 140);
        stopwatch.Stop();

        AssertSuccess(response);
        var items = JsonSerializer.SerializeToElement(response.Result).GetProperty("items").EnumerateArray().ToList();
        Assert.Empty(items);
        Assert.True(stopwatch.ElapsedMilliseconds >= 100);
    }

    [Fact]
    [Trait("SpecRef", "6.3.12")]
    public async Task Spec_6_3_12_Poll_WhenSucceeds_ShouldUpdateLastSeenAndReturnLeaseSeconds()
    {
        const string appId = "spec-6.3.12-last-seen";
        const string instanceId = "spec-6.3.12-last-seen-instance";
        WriteDefinition(appId, rpcEnabled: true);

        using var appRegistry = new AppRegistry(new SystemClock(), Mock.Of<ILogger<AppRegistry>>());
        var instance = RegisterInstance(appRegistry, instanceId, appId, scope: null, poll: true, respond: true, pid: 7303);
        instance.LastSeenUtc = DateTime.UtcNow.AddSeconds(-10);
        var baseline = instance.LastSeenUtc;

        var handler = CreateInvocationHandler(appRegistry);

        var notify = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "spec-6.3.12-last-seen-notify",
            Method = "hub.invoke.notify",
            Params = JsonSerializer.SerializeToElement(new
            {
                appId,
                target = new { scope = (string?)null, instanceId = (string?)null },
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

        var poll = await PollAsync(handler, instanceId, maxCount: 1, waitMs: 200);
        AssertSuccess(poll);

        var items = JsonSerializer.SerializeToElement(poll.Result).GetProperty("items").EnumerateArray().ToList();
        Assert.Single(items);
        var leaseSeconds = items[0].GetProperty("delivery").GetProperty("leaseSeconds").GetInt32();
        Assert.True(leaseSeconds >= 1);

        var updated = appRegistry.GetInstance(instanceId);
        Assert.NotNull(updated);
        Assert.True(updated!.LastSeenUtc > baseline);
    }

    [Fact]
    [Trait("SpecRef", "6.3.13")]
    public async Task Spec_6_3_13_Respond_WhenInstanceUnregistered_ShouldReturnInstanceNotFound()
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
                invocationId = "invk-missing",
                value = new { ok = true }
            })
        }, CancellationToken.None);

        AssertError(response, -32010, "instance_not_found");
        var data = JsonSerializer.SerializeToElement(response.Error!.Data);
        Assert.Equal("unknown_instance", data.GetProperty("reason").GetString());
    }

    [Fact]
    [Trait("SpecRef", "6.3.13")]
    public async Task Spec_6_3_13_Respond_WhenInvokeRespondDisabled_ShouldReturnForbiddenWithReason()
    {
        const string appId = "spec-6.3.13-respond-disabled";
        WriteDefinition(appId, rpcEnabled: true);

        using var appRegistry = new AppRegistry(new SystemClock(), Mock.Of<ILogger<AppRegistry>>());
        RegisterInstance(appRegistry, "spec-6.3.13-respond-disabled", appId, scope: null, poll: true, respond: false, pid: 7401);

        var handler = CreateInvocationHandler(appRegistry);
        var response = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "spec-6.3.13-respond-disabled",
            Method = "hub.invoke.respond",
            Params = JsonSerializer.SerializeToElement(new
            {
                instanceId = "spec-6.3.13-respond-disabled",
                invocationId = "invk-missing",
                value = new { ok = true }
            })
        }, CancellationToken.None);

        AssertError(response, -32002, "forbidden");
        var data = JsonSerializer.SerializeToElement(response.Error!.Data);
        Assert.Equal("respond_not_enabled", data.GetProperty("reason").GetString());
    }

    [Fact]
    [Trait("SpecRef", "6.3.13")]
    public async Task Spec_6_3_13_Respond_WhenValueAndErrorXorInvalid_ShouldReturnInvalidParams()
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
    [Trait("SpecRef", "6.3.13")]
    public async Task Spec_6_3_13_Respond_WhenNonLeaseHolderOrDuplicate_ShouldReturnDeliveryConflict()
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
                target = new { scope = (string?)null, instanceId = holderInstanceId },
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

        var poll = await PollAsync(handler, holderInstanceId, maxCount: 1, waitMs: 800);
        AssertSuccess(poll);
        var items = JsonSerializer.SerializeToElement(poll.Result).GetProperty("items").EnumerateArray().ToList();
        Assert.Single(items);
        var invocationId = items[0].GetProperty("invocationId").GetString();

        var nonHolder = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "spec-6.3.13-delivery-conflict-non-holder",
            Method = "hub.invoke.respond",
            Params = JsonSerializer.SerializeToElement(new
            {
                instanceId = otherInstanceId,
                invocationId,
                value = new { ok = true }
            })
        }, CancellationToken.None);
        AssertError(nonHolder, -32030, "delivery_conflict");

        var first = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "spec-6.3.13-delivery-conflict-first",
            Method = "hub.invoke.respond",
            Params = JsonSerializer.SerializeToElement(new
            {
                instanceId = holderInstanceId,
                invocationId,
                value = new { ok = true }
            })
        }, CancellationToken.None);
        AssertSuccess(first);

        var duplicate = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "spec-6.3.13-delivery-conflict-duplicate",
            Method = "hub.invoke.respond",
            Params = JsonSerializer.SerializeToElement(new
            {
                instanceId = holderInstanceId,
                invocationId,
                value = new { ok = true }
            })
        }, CancellationToken.None);
        AssertError(duplicate, -32030, "delivery_conflict");
    }

    [Fact]
    [Trait("SpecRef", "6.3.13")]
    public async Task Spec_6_3_13_Respond_WhenInvocationTimedOutOrExpired_ShouldReturnInvocationExpired()
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
                target = new { scope = (string?)null, instanceId = (string?)null },
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

        var timeoutPoll = await PollAsync(timeoutHandler, timeoutInstanceId, maxCount: 1, waitMs: 800);
        AssertSuccess(timeoutPoll);
        var timeoutItems = JsonSerializer.SerializeToElement(timeoutPoll.Result).GetProperty("items").EnumerateArray().ToList();
        Assert.Single(timeoutItems);
        var timeoutInvocationId = timeoutItems[0].GetProperty("invocationId").GetString();

        var timeoutResponse = await timeoutRequestTask;
        AssertError(timeoutResponse, -32012, "invocation_timeout");

        var lateTimeoutRespond = await timeoutHandler.HandleAsync(new JsonRpcRequest
        {
            Id = "spec-6.3.13-timeout-late-respond",
            Method = "hub.invoke.respond",
            Params = JsonSerializer.SerializeToElement(new
            {
                instanceId = timeoutInstanceId,
                invocationId = timeoutInvocationId,
                value = new { ok = true }
            })
        }, CancellationToken.None);
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
                target = new { scope = (string?)null, instanceId = (string?)null },
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

        var lateExpiredRespond = await expiredHandler.HandleAsync(new JsonRpcRequest
        {
            Id = "spec-6.3.13-expired-late-respond",
            Method = "hub.invoke.respond",
            Params = JsonSerializer.SerializeToElement(new
            {
                instanceId = "spec-6.3.13-expired-helper",
                invocationId = expiredInvocationId,
                value = new { ok = true }
            })
        }, CancellationToken.None);

        AssertError(lateExpiredRespond, -32011, "invocation_expired");
    }

    [Fact]
    [Trait("SpecRef", "6.3.13")]
    public async Task Spec_6_3_13_Respond_WhenSucceeds_ShouldUpdateLastSeen()
    {
        const string appId = "spec-6.3.13-last-seen";
        const string instanceId = "spec-6.3.13-last-seen-instance";
        WriteDefinition(appId, rpcEnabled: true);

        using var appRegistry = new AppRegistry(new SystemClock(), Mock.Of<ILogger<AppRegistry>>());
        var instance = RegisterInstance(appRegistry, instanceId, appId, scope: null, poll: true, respond: true, pid: 7406);
        instance.LastSeenUtc = DateTime.UtcNow.AddSeconds(-10);
        var baseline = instance.LastSeenUtc;

        var handler = CreateInvocationHandler(appRegistry);

        var notify = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "spec-6.3.13-last-seen-notify",
            Method = "hub.invoke.notify",
            Params = JsonSerializer.SerializeToElement(new
            {
                appId,
                target = new { scope = (string?)null, instanceId = (string?)null },
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

        var poll = await PollAsync(handler, instanceId, maxCount: 1, waitMs: 800);
        AssertSuccess(poll);
        var items = JsonSerializer.SerializeToElement(poll.Result).GetProperty("items").EnumerateArray().ToList();
        Assert.Single(items);
        var invocationId = items[0].GetProperty("invocationId").GetString();

        var respond = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "spec-6.3.13-last-seen-respond",
            Method = "hub.invoke.respond",
            Params = JsonSerializer.SerializeToElement(new
            {
                instanceId,
                invocationId,
                value = new { ok = true }
            })
        }, CancellationToken.None);

        AssertSuccess(respond);
        var updated = appRegistry.GetInstance(instanceId);
        Assert.NotNull(updated);
        Assert.True(updated!.LastSeenUtc > baseline);
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
        var definitionLoader = new DefinitionLoader(_tempDirectory, Mock.Of<ILogger<DefinitionLoader>>());
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

    private static async Task<JsonRpcResponse> PollAsync(InvocationHandler handler, string instanceId, int maxCount, int waitMs)
    {
        return await handler.HandleAsync(new JsonRpcRequest
        {
            Id = $"poll-{instanceId}-{Guid.NewGuid():N}",
            Method = "hub.invoke.poll",
            Params = JsonSerializer.SerializeToElement(new
            {
                instanceId,
                maxCount,
                waitMs
            })
        }, CancellationToken.None);
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
        return appRegistry.RegisterInstance(new AppInstance
        {
            InstanceId = instanceId,
            AppId = appId,
            Scope = scope,
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
