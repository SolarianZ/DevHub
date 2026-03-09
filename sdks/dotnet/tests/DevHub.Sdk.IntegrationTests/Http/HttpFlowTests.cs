using System.Net.Http.Headers;
using System.Text.Json;
using DevHub.Sdk.IntegrationTests.TestHost;
using DevHub.Sdk.Models;

namespace DevHub.Sdk.IntegrationTests.Http;

/// <summary>
/// HTTP 主链路黑盒测试。
/// </summary>
public sealed class HttpFlowTests
{
    [Fact]
    public async Task M5_E2E_001_And_002_PingAndAppsFlow_ShouldSucceed()
    {
        await using var host = await DevHubHostFixture.StartAsync();
        await host.WriteDefinitionAsync(new AppDefinition
        {
            AppId = "http.flow.app",
            DisplayName = "HTTP Flow App",
            Description = "用于 SDK HTTP 链路测试。"
        });

        await using var client = await host.CreateClientAsync("http-flow-client");

        var ping = await client.PingAsync(new { value = 1 });
        Assert.True(ping.Ok);
        Assert.Equal(1, ping.Echo!.Value.GetProperty("value").GetInt32());

        var definitions = await client.ListDefinitionsAsync();
        Assert.Contains(definitions, definition => definition.AppId == "http.flow.app");

        var definitionResult = await client.GetDefinitionAsync("http.flow.app");
        Assert.Equal("HTTP Flow App", definitionResult.DisplayName);

        var registered = await client.RegisterInstanceAsync(new AppInstanceRegistration
        {
            InstanceId = "http-flow-inst-1",
            AppId = "http.flow.app",
            Pid = Environment.ProcessId,
            Invoke = new InvokeCapability
            {
                Poll = true,
                Respond = true
            },
            Meta = new { source = "integration" }
        });

        Assert.Equal("http-flow-inst-1", registered.InstanceId);

        var instances = await client.ListInstancesAsync(new ListInstancesRequest
        {
            AppId = "http.flow.app"
        });
        Assert.Single(instances);

        var lastSeenUtc = await client.HeartbeatAsync("http-flow-inst-1");
        Assert.NotEqual(default, lastSeenUtc);

        await client.UnregisterInstanceAsync("http-flow-inst-1");
        var instancesAfterUnregister = await client.ListInstancesAsync(new ListInstancesRequest
        {
            AppId = "http.flow.app"
        });
        Assert.Empty(instancesAfterUnregister);
    }

    [Fact]
    public async Task M5_E2E_009_Ping_WhenClientIdHeaderMissing_ShouldMapInvalidRequest()
    {
        await using var host = await DevHubHostFixture.StartAsync();
        await using var client = await host.CreateClientAsync("header-tamper-client", new HeaderTamperingHandler(request =>
        {
            request.Headers.Remove("X-DevHub-ClientId");
        }));

        var exception = await Assert.ThrowsAsync<DevHubRpcException>(() => client.PingAsync(cancellationToken: CancellationToken.None));

        Assert.Equal(-32600, exception.Code);
        Assert.Equal("invalid_request", exception.Message);
        Assert.Equal("missing_header", exception.Data!.Value.GetProperty("reason").GetString());
        Assert.Equal("X-DevHub-ClientId", exception.Data!.Value.GetProperty("header").GetString());
    }

    [Fact]
    public async Task Impl_Ping_WhenAuthorizationInvalid_ShouldMapUnauthorized()
    {
        await using var host = await DevHubHostFixture.StartAsync();
        await using var client = await host.CreateClientAsync("auth-tamper-client", new HeaderTamperingHandler(request =>
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "bad-token");
        }));

        var exception = await Assert.ThrowsAsync<DevHubRpcException>(() => client.PingAsync(cancellationToken: CancellationToken.None));

        Assert.Equal(-32001, exception.Code);
        Assert.Equal("unauthorized", exception.Message);
        Assert.Equal("invalid_token", exception.Data!.Value.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Impl_Ping_WhenProtocolHeaderMismatch_ShouldMapNotSupported()
    {
        await using var host = await DevHubHostFixture.StartAsync();
        await using var client = await host.CreateClientAsync("protocol-tamper-client", new HeaderTamperingHandler(request =>
        {
            request.Headers.Remove("X-DevHub-Protocol");
            request.Headers.TryAddWithoutValidation("X-DevHub-Protocol", "2");
        }));

        var exception = await Assert.ThrowsAsync<DevHubRpcException>(() => client.PingAsync(cancellationToken: CancellationToken.None));

        Assert.Equal(-32099, exception.Code);
        Assert.Equal("not_supported", exception.Message);
        Assert.Equal("mismatch", exception.Data!.Value.GetProperty("reason").GetString());
        Assert.Equal("2", exception.Data!.Value.GetProperty("received").GetString());
    }

    private sealed class HeaderTamperingHandler : DelegatingHandler
    {
        private readonly Action<HttpRequestMessage> _tamperAction;

        public HeaderTamperingHandler(Action<HttpRequestMessage> tamperAction)
            : base(new HttpClientHandler())
        {
            _tamperAction = tamperAction;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _tamperAction(request);
            return base.SendAsync(request, cancellationToken);
        }
    }
}
