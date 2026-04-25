using System.Text.Json;
using DevHub.Sdk.IntegrationTests.TestHost;
using DevHub.Sdk.Models;

namespace DevHub.Sdk.IntegrationTests.Http;

/// <summary>
/// Invocation 黑盒测试。
/// </summary>
public sealed class InvocationFlowTests
{
    private const string InstancePassword = "sdk-invocation-password";

    [Fact]
    public async Task NotifyAndPoll_ShouldRoundTrip()
    {
        await using var host = await DevHubHostFixture.StartAsync();
        await host.WriteDefinitionAsync(new AppDefinition
        {
            AppId = "invoke.notify.app",
            Scope = string.Empty,
            DisplayName = "invoke.notify.app"
        });

        await using var client = await host.CreateClientAsync("invoke-notify-client");
        var registered = await RegisterInstanceAsync(client, "invoke.notify.app", "notify-inst-1", scope: string.Empty);

        var notifyResult = await client.NotifyAsync(new InvokeRequest
        {
            AppId = "invoke.notify.app",
            Method = "test.notify",
            Args = new { message = "hello" },
            Target = new InvocationTarget
            {
                Scope = string.Empty
            }
        });

        Assert.True(notifyResult.Ok);
        var invocation = await WaitForSingleInvocationAsync(client, registered.InstanceId, registered.InstanceSessionToken!);
        Assert.Equal(notifyResult.InvocationId, invocation.InvocationId);
        Assert.Equal(InvocationKind.Notify, invocation.Kind);
        Assert.Equal("hello", invocation.Args!.Value.GetProperty("message").GetString());
    }

    [Fact]
    public async Task RequestRespondValue_ShouldReturnResult_AndSecondRespondShouldConflict()
    {
        await using var host = await DevHubHostFixture.StartAsync();
        await host.WriteDefinitionAsync(new AppDefinition
        {
            AppId = "invoke.request.app",
            Scope = string.Empty,
            DisplayName = "invoke.request.app"
        });

        await using var client = await host.CreateClientAsync("invoke-request-client");
        var registered = await RegisterInstanceAsync(client, "invoke.request.app", "request-inst-1", scope: string.Empty);

        var requestTask = client.RequestAsync(new InvokeRequest
        {
            AppId = "invoke.request.app",
            Method = "test.request",
            Target = new InvocationTarget
            {
                Scope = string.Empty
            },
            Args = new { input = 1 },
            Options = new InvocationOptions
            {
                TtlMs = 5000,
                WaitTimeoutMs = 3000
            }
        });

        var invocation = await WaitForSingleInvocationAsync(client, registered.InstanceId, registered.InstanceSessionToken!);

        await client.RespondAsync(new RespondRequest
        {
            InstanceId = registered.InstanceId,
            InstanceSessionToken = registered.InstanceSessionToken!,
            InvocationId = invocation.InvocationId,
            Value = new { ok = true, value = 2 }
        });

        var requestResult = await requestTask;
        Assert.True(requestResult.Ok);
        Assert.Equal(2, requestResult.Value!.Value.GetProperty("value").GetInt32());

        var conflict = await Assert.ThrowsAsync<DevHubRpcException>(() => client.RespondAsync(new RespondRequest
        {
            InstanceId = registered.InstanceId,
            InstanceSessionToken = registered.InstanceSessionToken!,
            InvocationId = invocation.InvocationId,
            Value = new { ok = true }
        }));
        Assert.Equal(-32030, conflict.Code);
    }

    [Fact]
    public async Task RequestRespondNullValue_ShouldReturnJsonNull()
    {
        await using var host = await DevHubHostFixture.StartAsync();
        await host.WriteDefinitionAsync(new AppDefinition
        {
            AppId = "invoke.request.null.app",
            Scope = string.Empty,
            DisplayName = "invoke.request.null.app"
        });

        await using var client = await host.CreateClientAsync("invoke-request-null-client");
        var registered = await RegisterInstanceAsync(client, "invoke.request.null.app", "request-null-inst-1", scope: string.Empty);

        var requestTask = client.RequestAsync(new InvokeRequest
        {
            AppId = "invoke.request.null.app",
            Method = "test.request.null",
            Target = new InvocationTarget
            {
                Scope = string.Empty
            },
            Options = new InvocationOptions
            {
                TtlMs = 5000,
                WaitTimeoutMs = 3000
            }
        });

        var invocation = await WaitForSingleInvocationAsync(client, registered.InstanceId, registered.InstanceSessionToken!);

        await client.RespondAsync(new RespondRequest
        {
            InstanceId = registered.InstanceId,
            InstanceSessionToken = registered.InstanceSessionToken!,
            InvocationId = invocation.InvocationId,
            Value = null
        });

        var requestResult = await requestTask;
        Assert.True(requestResult.Ok);
        Assert.Null(requestResult.Value);
    }

    [Fact]
    public async Task RequestRespondError_ShouldMapInvocationFailed()
    {
        await using var host = await DevHubHostFixture.StartAsync();
        await host.WriteDefinitionAsync(new AppDefinition
        {
            AppId = "invoke.error.app",
            Scope = string.Empty,
            DisplayName = "invoke.error.app"
        });

        await using var client = await host.CreateClientAsync("invoke-error-client");
        var registered = await RegisterInstanceAsync(client, "invoke.error.app", "error-inst-1", scope: string.Empty);

        var requestTask = client.RequestAsync(new InvokeRequest
        {
            AppId = "invoke.error.app",
            Method = "test.request",
            Target = new InvocationTarget
            {
                Scope = string.Empty
            },
            Options = new InvocationOptions
            {
                TtlMs = 5000,
                WaitTimeoutMs = 3000
            }
        });

        var invocation = await WaitForSingleInvocationAsync(client, registered.InstanceId, registered.InstanceSessionToken!);

        await client.RespondAsync(new RespondRequest
        {
            InstanceId = registered.InstanceId,
            InstanceSessionToken = registered.InstanceSessionToken!,
            InvocationId = invocation.InvocationId,
            Error = DevHubCalleeError.Create(1001, "app_error", new { reason = "boom" })
        });

        var exception = await Assert.ThrowsAsync<DevHubRpcException>(() => requestTask);
        Assert.Equal(-32050, exception.Code);
        Assert.Equal(invocation.InvocationId, exception.InvocationId);
        Assert.NotNull(exception.CalleeError);
        Assert.Equal(1001, exception.CalleeError!.Code);
        Assert.Equal("app_error", exception.CalleeError.Message);
        Assert.Equal(1001, exception.ErrorData!.Value.GetProperty("calleeError").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task RequestTimeoutAndExpired_ShouldMapExpectedErrors()
    {
        await using var host = await DevHubHostFixture.StartAsync();
        await host.WriteDefinitionAsync(new AppDefinition
        {
            AppId = "invoke.timeout.app",
            Scope = string.Empty,
            DisplayName = "invoke.timeout.app"
        });

        await using var client = await host.CreateClientAsync("invoke-timeout-client");
        _ = await RegisterInstanceAsync(client, "invoke.timeout.app", "timeout-inst-1", scope: string.Empty);

        var timeoutException = await Assert.ThrowsAsync<DevHubRpcException>(() => client.RequestAsync(new InvokeRequest
        {
            AppId = "invoke.timeout.app",
            Method = "test.timeout",
            Target = new InvocationTarget
            {
                Scope = string.Empty
            },
            Options = new InvocationOptions
            {
                TtlMs = 1500,
                WaitTimeoutMs = 1000
            }
        }));
        Assert.Equal(-32012, timeoutException.Code);

        var expiredException = await Assert.ThrowsAsync<DevHubRpcException>(() => client.RequestAsync(new InvokeRequest
        {
            AppId = "invoke.timeout.app",
            Method = "test.expired",
            Target = new InvocationTarget
            {
                Scope = string.Empty
            },
            Options = new InvocationOptions
            {
                TtlMs = 1000,
                WaitTimeoutMs = 1000
            }
        }));
        Assert.Equal(-32011, expiredException.Code);
    }

    [Fact]
    public async Task ScopeRules_ShouldRouteToExpectedInstance()
    {
        await using var host = await DevHubHostFixture.StartAsync();
        await host.WriteDefinitionAsync(new AppDefinition
        {
            AppId = "invoke.scope.app",
            Scope = string.Empty,
            DisplayName = "invoke.scope.app"
        });
        await host.WriteDefinitionAsync(new AppDefinition
        {
            AppId = "invoke.scope.app",
            Scope = "scope-a",
            DisplayName = "invoke.scope.app.scope-a"
        });
        await host.WriteDefinitionAsync(new AppDefinition
        {
            AppId = "invoke.scope.app",
            Scope = "global",
            DisplayName = "invoke.scope.app.literal-global"
        });

        await using var client = await host.CreateClientAsync("invoke-scope-client");
        var globalInstance = await RegisterInstanceAsync(client, "invoke.scope.app", "scope-global-inst", scope: string.Empty);
        var scopeAInstance = await RegisterInstanceAsync(client, "invoke.scope.app", "scope-a-inst", scope: "scope-a");
        var literalGlobalInstance = await RegisterInstanceAsync(client, "invoke.scope.app", "scope-literal-global-inst", scope: "global");

        _ = await client.NotifyAsync(new InvokeRequest
        {
            AppId = "invoke.scope.app",
            Method = "test.default-global",
            Target = new InvocationTarget { Scope = string.Empty }
        });
        _ = await WaitForSingleInvocationAsync(client, globalInstance.InstanceId, globalInstance.InstanceSessionToken!);
        Assert.Empty((await client.PollAsync(new PollRequest
        {
            InstanceId = scopeAInstance.InstanceId,
            InstanceSessionToken = scopeAInstance.InstanceSessionToken!,
            WaitMs = 0
        })).Items);

        _ = await client.NotifyAsync(new InvokeRequest
        {
            AppId = "invoke.scope.app",
            Method = "test.scope-a",
            Target = new InvocationTarget { Scope = "scope-a" }
        });
        _ = await WaitForSingleInvocationAsync(client, scopeAInstance.InstanceId, scopeAInstance.InstanceSessionToken!);

        _ = await client.NotifyAsync(new InvokeRequest
        {
            AppId = "invoke.scope.app",
            Method = "test.empty-scope",
            Target = new InvocationTarget { Scope = string.Empty }
        });
        _ = await WaitForSingleInvocationAsync(client, globalInstance.InstanceId, globalInstance.InstanceSessionToken!);

        _ = await client.NotifyAsync(new InvokeRequest
        {
            AppId = "invoke.scope.app",
            Method = "test.literal-global",
            Target = new InvocationTarget { Scope = "global" }
        });
        _ = await WaitForSingleInvocationAsync(client, literalGlobalInstance.InstanceId, literalGlobalInstance.InstanceSessionToken!);
    }

    [Fact]
    public async Task PollAndRespond_WithSessionTokenMismatch_ShouldMapForbidden()
    {
        await using var host = await DevHubHostFixture.StartAsync();
        await host.WriteDefinitionAsync(new AppDefinition
        {
            AppId = "invoke.token.guard.app",
            Scope = string.Empty,
            DisplayName = "invoke.token.guard.app"
        });

        await using var client = await host.CreateClientAsync("invoke-token-guard-client");
        var registered = await RegisterInstanceAsync(client, "invoke.token.guard.app", "token-guard-inst-1", scope: string.Empty);

        var pollException = await Assert.ThrowsAsync<DevHubRpcException>(() => client.PollAsync(new PollRequest
        {
            InstanceId = registered.InstanceId,
            InstanceSessionToken = $"wrong-{registered.InstanceSessionToken}",
            WaitMs = 0
        }));
        Assert.Equal(-32002, pollException.Code);
        Assert.Equal("instance_session_token_mismatch", pollException.Reason);

        var requestTask = client.RequestAsync(new InvokeRequest
        {
            AppId = "invoke.token.guard.app",
            Method = "test.guard",
            Target = new InvocationTarget
            {
                Scope = string.Empty
            },
            Options = new InvocationOptions
            {
                TtlMs = 5000,
                WaitTimeoutMs = 3000
            }
        });

        var invocation = await WaitForSingleInvocationAsync(client, registered.InstanceId, registered.InstanceSessionToken!);

        var respondException = await Assert.ThrowsAsync<DevHubRpcException>(() => client.RespondAsync(new RespondRequest
        {
            InstanceId = registered.InstanceId,
            InstanceSessionToken = $"wrong-{registered.InstanceSessionToken}",
            InvocationId = invocation.InvocationId,
            Value = new { ok = true }
        }));
        Assert.Equal(-32002, respondException.Code);
        Assert.Equal("instance_session_token_mismatch", respondException.Reason);

        await client.RespondAsync(new RespondRequest
        {
            InstanceId = registered.InstanceId,
            InstanceSessionToken = registered.InstanceSessionToken!,
            InvocationId = invocation.InvocationId,
            Value = new { ok = true, value = 1 }
        });

        var requestResult = await requestTask;
        Assert.True(requestResult.Ok);
        Assert.Equal(1, requestResult.Value!.Value.GetProperty("value").GetInt32());
    }

    private static AppInstanceRegistration CreateInstance(string appId, string instanceId, string scope)
    {
        return new AppInstanceRegistration
        {
            InstanceId = instanceId,
            AppId = appId,
            Scope = scope,
            Pid = Environment.ProcessId,
            Invoke = new InvokeCapability
            {
                Poll = true,
                Respond = true
            }
        };
    }

    private static Task<AppInstance> RegisterInstanceAsync(DevHubClient client, string appId, string instanceId, string scope)
    {
        return client.RegisterInstanceAsync(CreateInstance(appId, instanceId, scope), InstancePassword);
    }

    private static async Task<Invocation> WaitForSingleInvocationAsync(
        DevHubClient client,
        string instanceId,
        string instanceSessionToken,
        int timeoutMs = 3000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        var lastCount = 0;

        while (DateTime.UtcNow < deadline)
        {
            var remaining = deadline - DateTime.UtcNow;
            var waitMs = remaining <= TimeSpan.Zero
                ? 0
                : Math.Min((int)Math.Ceiling(remaining.TotalMilliseconds), 250);

            var pollResult = await client.PollAsync(new PollRequest
            {
                InstanceId = instanceId,
                InstanceSessionToken = instanceSessionToken,
                WaitMs = waitMs
            });

            lastCount = pollResult.Items.Count;
            if (lastCount == 1)
            {
                return pollResult.Items[0];
            }

            if (lastCount > 1)
            {
                Assert.Single(pollResult.Items);
            }
        }

        throw new TimeoutException($"鍦?{timeoutMs}ms 鍐呮湭绛夊埌瀹炰緥 {instanceId} 鐨勫崟鏉¤皟鐢ㄣ€傛渶鍚庝竴娆¤疆璇㈣繑鍥?{lastCount} 涓」鐩€?");
    }
}
