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
            DisplayName = "invoke.notify.app"
        });

        await using var client = await host.CreateClientAsync("invoke-notify-client");
        await client.RegisterInstanceAsync(CreateInstance("invoke.notify.app", "notify-inst-1", scope: null), InstancePassword);

        var notifyResult = await client.NotifyAsync(new InvokeRequest
        {
            AppId = "invoke.notify.app",
            Method = "test.notify",
            Args = new { message = "hello" }
        });

        Assert.True(notifyResult.Ok);
        var invocation = await WaitForSingleInvocationAsync(client, "notify-inst-1");
        Assert.Equal(notifyResult.InvocationId, invocation.InvocationId);
        Assert.Equal(InvocationKind.Notify, invocation.Kind);
        Assert.Equal("hello", (string?)invocation.Args!["message"]!);
    }

    [Fact]
    public async Task RequestRespondValue_ShouldReturnResult_AndSecondRespondShouldConflict()
    {
        await using var host = await DevHubHostFixture.StartAsync();
        await host.WriteDefinitionAsync(new AppDefinition
        {
            AppId = "invoke.request.app",
            DisplayName = "invoke.request.app"
        });

        await using var client = await host.CreateClientAsync("invoke-request-client");
        await client.RegisterInstanceAsync(CreateInstance("invoke.request.app", "request-inst-1", scope: null), InstancePassword);

        var requestTask = client.RequestAsync(new InvokeRequest
        {
            AppId = "invoke.request.app",
            Method = "test.request",
            Args = new { input = 1 },
            Options = new InvocationOptions
            {
                TtlMs = 5000,
                WaitTimeoutMs = 3000
            }
        });

        var invocation = await WaitForSingleInvocationAsync(client, "request-inst-1");

        await client.RespondAsync(new RespondRequest
        {
            InstanceId = "request-inst-1",
            InvocationId = invocation.InvocationId,
            Value = new { ok = true, value = 2 }
        });

        var requestResult = await requestTask;
        Assert.True(requestResult.Ok);
        Assert.Equal(2, (int)requestResult.Value!["value"]!);

        var conflict = await Assert.ThrowsAsync<DevHubRpcException>(() => client.RespondAsync(new RespondRequest
        {
            InstanceId = "request-inst-1",
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
            DisplayName = "invoke.request.null.app"
        });

        await using var client = await host.CreateClientAsync("invoke-request-null-client");
        await client.RegisterInstanceAsync(CreateInstance("invoke.request.null.app", "request-null-inst-1", scope: null), InstancePassword);

        var requestTask = client.RequestAsync(new InvokeRequest
        {
            AppId = "invoke.request.null.app",
            Method = "test.request.null",
            Options = new InvocationOptions
            {
                TtlMs = 5000,
                WaitTimeoutMs = 3000
            }
        });

        var invocation = await WaitForSingleInvocationAsync(client, "request-null-inst-1");

        await client.RespondAsync(new RespondRequest
        {
            InstanceId = "request-null-inst-1",
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
            DisplayName = "invoke.error.app"
        });

        await using var client = await host.CreateClientAsync("invoke-error-client");
        await client.RegisterInstanceAsync(CreateInstance("invoke.error.app", "error-inst-1", scope: null), InstancePassword);

        var requestTask = client.RequestAsync(new InvokeRequest
        {
            AppId = "invoke.error.app",
            Method = "test.request",
            Options = new InvocationOptions
            {
                TtlMs = 5000,
                WaitTimeoutMs = 3000
            }
        });

        var invocation = await WaitForSingleInvocationAsync(client, "error-inst-1");

        await client.RespondAsync(new RespondRequest
        {
            InstanceId = "error-inst-1",
            InvocationId = invocation.InvocationId,
            Error = DevHubCalleeError.Create(1001, "app_error", new { reason = "boom" })
        });

        var exception = await Assert.ThrowsAsync<DevHubRpcException>(() => requestTask);
        Assert.Equal(-32050, exception.Code);
        Assert.Equal(invocation.InvocationId, exception.InvocationId);
        Assert.NotNull(exception.CalleeError);
        Assert.Equal(1001, exception.CalleeError!.Code);
        Assert.Equal("app_error", exception.CalleeError.Message);
        Assert.Equal(1001, (int)exception.Data!["calleeError"]!["code"]!);
    }

    [Fact]
    public async Task RequestTimeoutAndExpired_ShouldMapExpectedErrors()
    {
        await using var host = await DevHubHostFixture.StartAsync();
        await host.WriteDefinitionAsync(new AppDefinition
        {
            AppId = "invoke.timeout.app",
            DisplayName = "invoke.timeout.app"
        });

        await using var client = await host.CreateClientAsync("invoke-timeout-client");
        await client.RegisterInstanceAsync(CreateInstance("invoke.timeout.app", "timeout-inst-1", scope: null), InstancePassword);

        var timeoutException = await Assert.ThrowsAsync<DevHubRpcException>(() => client.RequestAsync(new InvokeRequest
        {
            AppId = "invoke.timeout.app",
            Method = "test.timeout",
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
            DisplayName = "invoke.scope.app"
        });

        await using var client = await host.CreateClientAsync("invoke-scope-client");
        await client.RegisterInstanceAsync(CreateInstance("invoke.scope.app", "scope-global-inst", scope: null), InstancePassword);
        await client.RegisterInstanceAsync(CreateInstance("invoke.scope.app", "scope-a-inst", scope: "scope-a"), InstancePassword);
        await client.RegisterInstanceAsync(CreateInstance("invoke.scope.app", "scope-literal-global-inst", scope: "global"), InstancePassword);

        _ = await client.NotifyAsync(new InvokeRequest
        {
            AppId = "invoke.scope.app",
            Method = "test.default-global"
        });
        _ = await WaitForSingleInvocationAsync(client, "scope-global-inst");
        Assert.Empty((await client.PollAsync(new PollRequest { InstanceId = "scope-a-inst", WaitMs = 0 })).Items);

        _ = await client.NotifyAsync(new InvokeRequest
        {
            AppId = "invoke.scope.app",
            Method = "test.scope-a",
            Target = new InvocationTarget { Scope = "scope-a" }
        });
        _ = await WaitForSingleInvocationAsync(client, "scope-a-inst");

        _ = await client.NotifyAsync(new InvokeRequest
        {
            AppId = "invoke.scope.app",
            Method = "test.empty-scope",
            Target = new InvocationTarget { Scope = string.Empty }
        });
        _ = await WaitForSingleInvocationAsync(client, "scope-global-inst");

        _ = await client.NotifyAsync(new InvokeRequest
        {
            AppId = "invoke.scope.app",
            Method = "test.literal-global",
            Target = new InvocationTarget { Scope = "global" }
        });
        _ = await WaitForSingleInvocationAsync(client, "scope-literal-global-inst");
    }

    private static AppInstanceRegistration CreateInstance(string appId, string instanceId, string? scope)
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

    private static async Task<Invocation> WaitForSingleInvocationAsync(DevHubClient client, string instanceId, int timeoutMs = 3000)
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
