using System.Text.Json;
using DevHub.Sdk.IntegrationTests.TestHost;
using DevHub.Sdk.Models;

namespace DevHub.Sdk.IntegrationTests.Http;

/// <summary>
/// Invocation 黑盒测试。
/// </summary>
public sealed class InvocationFlowTests
{
    [Fact]
    public async Task M5_E2E_003_NotifyAndPoll_ShouldRoundTrip()
    {
        await using var host = await DevHubHostFixture.StartAsync();
        await host.WriteDefinitionAsync(new AppDefinition
        {
            AppId = "invoke.notify.app",
            DisplayName = "invoke.notify.app"
        });

        await using var client = await host.CreateClientAsync("invoke-notify-client");
        await client.RegisterInstanceAsync(CreateInstance("invoke.notify.app", "notify-inst-1", scope: null));

        var notifyResult = await client.NotifyAsync(new InvokeRequest
        {
            AppId = "invoke.notify.app",
            Method = "test.notify",
            Args = new { message = "hello" }
        });

        var pollResult = await client.PollAsync(new PollRequest
        {
            InstanceId = "notify-inst-1",
            WaitMs = 0
        });

        Assert.True(notifyResult.Ok);
        Assert.Single(pollResult.Items);
        Assert.Equal(notifyResult.InvocationId, pollResult.Items[0].InvocationId);
        Assert.Equal(InvocationKind.Notify, pollResult.Items[0].Kind);
        Assert.Equal("hello", pollResult.Items[0].Args!.Value.GetProperty("message").GetString());
    }

    [Fact]
    public async Task M5_E2E_003_And_008_RequestRespondValue_ShouldReturnResult_AndSecondRespondShouldConflict()
    {
        await using var host = await DevHubHostFixture.StartAsync();
        await host.WriteDefinitionAsync(new AppDefinition
        {
            AppId = "invoke.request.app",
            DisplayName = "invoke.request.app"
        });

        await using var client = await host.CreateClientAsync("invoke-request-client");
        await client.RegisterInstanceAsync(CreateInstance("invoke.request.app", "request-inst-1", scope: null));

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

        var pollResult = await client.PollAsync(new PollRequest
        {
            InstanceId = "request-inst-1",
            WaitMs = 100
        });
        var invocation = Assert.Single(pollResult.Items);

        await client.RespondAsync(new RespondRequest
        {
            InstanceId = "request-inst-1",
            InvocationId = invocation.InvocationId,
            Value = new { ok = true, value = 2 }
        });

        var requestResult = await requestTask;
        Assert.True(requestResult.Ok);
        Assert.Equal(2, requestResult.Value!.Value.GetProperty("value").GetInt32());

        var conflict = await Assert.ThrowsAsync<DevHubRpcException>(() => client.RespondAsync(new RespondRequest
        {
            InstanceId = "request-inst-1",
            InvocationId = invocation.InvocationId,
            Value = new { ok = true }
        }));
        Assert.Equal(-32030, conflict.Code);
    }

    [Fact]
    public async Task M5_E2E_008_RequestRespondError_ShouldMapInvocationFailed()
    {
        await using var host = await DevHubHostFixture.StartAsync();
        await host.WriteDefinitionAsync(new AppDefinition
        {
            AppId = "invoke.error.app",
            DisplayName = "invoke.error.app"
        });

        await using var client = await host.CreateClientAsync("invoke-error-client");
        await client.RegisterInstanceAsync(CreateInstance("invoke.error.app", "error-inst-1", scope: null));

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

        var pollResult = await client.PollAsync(new PollRequest
        {
            InstanceId = "error-inst-1",
            WaitMs = 100
        });
        var invocation = Assert.Single(pollResult.Items);

        await client.RespondAsync(new RespondRequest
        {
            InstanceId = "error-inst-1",
            InvocationId = invocation.InvocationId,
            Error = DevHubCalleeError.Create(1001, "app_error", new { reason = "boom" })
        });

        var exception = await Assert.ThrowsAsync<DevHubRpcException>(() => requestTask);
        Assert.Equal(-32050, exception.Code);
        Assert.Equal(1001, exception.Data!.Value.GetProperty("calleeError").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task M5_E2E_007_RequestTimeoutAndExpired_ShouldMapExpectedErrors()
    {
        await using var host = await DevHubHostFixture.StartAsync();
        await host.WriteDefinitionAsync(new AppDefinition
        {
            AppId = "invoke.timeout.app",
            DisplayName = "invoke.timeout.app"
        });

        await using var client = await host.CreateClientAsync("invoke-timeout-client");
        await client.RegisterInstanceAsync(CreateInstance("invoke.timeout.app", "timeout-inst-1", scope: null));

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
    public async Task M5_E2E_006_And_011_ScopeRules_ShouldRouteToExpectedInstance()
    {
        await using var host = await DevHubHostFixture.StartAsync();
        await host.WriteDefinitionAsync(new AppDefinition
        {
            AppId = "invoke.scope.app",
            DisplayName = "invoke.scope.app"
        });

        await using var client = await host.CreateClientAsync("invoke-scope-client");
        await client.RegisterInstanceAsync(CreateInstance("invoke.scope.app", "scope-global-inst", scope: null));
        await client.RegisterInstanceAsync(CreateInstance("invoke.scope.app", "scope-a-inst", scope: "scope-a"));
        await client.RegisterInstanceAsync(CreateInstance("invoke.scope.app", "scope-literal-global-inst", scope: "global"));

        _ = await client.NotifyAsync(new InvokeRequest
        {
            AppId = "invoke.scope.app",
            Method = "test.default-global"
        });
        var globalDefault = await client.PollAsync(new PollRequest { InstanceId = "scope-global-inst", WaitMs = 100 });
        Assert.Single(globalDefault.Items);
        Assert.Empty((await client.PollAsync(new PollRequest { InstanceId = "scope-a-inst", WaitMs = 0 })).Items);

        _ = await client.NotifyAsync(new InvokeRequest
        {
            AppId = "invoke.scope.app",
            Method = "test.scope-a",
            Target = new InvocationTarget { Scope = "scope-a" }
        });
        var scopeAItems = await client.PollAsync(new PollRequest { InstanceId = "scope-a-inst", WaitMs = 100 });
        Assert.Single(scopeAItems.Items);

        _ = await client.NotifyAsync(new InvokeRequest
        {
            AppId = "invoke.scope.app",
            Method = "test.empty-scope",
            Target = new InvocationTarget { Scope = string.Empty }
        });
        var emptyScopeItems = await client.PollAsync(new PollRequest { InstanceId = "scope-global-inst", WaitMs = 100 });
        Assert.Single(emptyScopeItems.Items);

        _ = await client.NotifyAsync(new InvokeRequest
        {
            AppId = "invoke.scope.app",
            Method = "test.literal-global",
            Target = new InvocationTarget { Scope = "global" }
        });
        var literalGlobalItems = await client.PollAsync(new PollRequest { InstanceId = "scope-literal-global-inst", WaitMs = 100 });
        Assert.Single(literalGlobalItems.Items);
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
}
