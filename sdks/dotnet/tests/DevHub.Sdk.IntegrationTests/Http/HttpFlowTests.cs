using System.Net.Http.Headers;
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
        Assert.Equal(1, (int)ping.Echo!["value"]!);

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
        Assert.Equal("missing_header", (string?)exception.Data!["reason"]!);
        Assert.Equal("X-DevHub-ClientId", (string?)exception.Data!["header"]!);
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
        Assert.Equal("invalid_token", (string?)exception.Data!["reason"]!);
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
        Assert.Equal("mismatch", (string?)exception.Data!["reason"]!);
        Assert.Equal("2", (string?)exception.Data!["received"]!);
    }

    [Fact]
    public async Task M5_E2E_001_RuntimeDiscovery_WhenEnvironmentOverrideSet_ShouldCreateClientWithoutExplicitDataDir()
    {
        await using var host = await DevHubHostFixture.StartAsync();
        using var scope = new EnvironmentVariableScope("DEVHUB_DATA_DIR", host.DataDirectory);
        await using var client = await DevHubClient.FromRuntimeAsync(new DevHubClientOptions
        {
            ClientId = "env-data-client"
        });

        var ping = await client.PingAsync();
        Assert.True(ping.Ok);
    }

    [Fact]
    public async Task Impl_RuntimeDiscovery_WhenUsingDifferentDataDirectories_ShouldKeepParallelHostsIsolated()
    {
        await using var firstHost = await DevHubHostFixture.StartAsync();
        await using var secondHost = await DevHubHostFixture.StartAsync();

        await firstHost.WriteDefinitionAsync(new AppDefinition
        {
            AppId = "parallel.first.app",
            DisplayName = "Parallel First App"
        });

        await secondHost.WriteDefinitionAsync(new AppDefinition
        {
            AppId = "parallel.second.app",
            DisplayName = "Parallel Second App"
        });

        await using var firstClient = await firstHost.CreateClientAsync("parallel-client-1");
        await using var secondClient = await secondHost.CreateClientAsync("parallel-client-2");

        var firstDefinitions = await firstClient.ListDefinitionsAsync();
        var secondDefinitions = await secondClient.ListDefinitionsAsync();

        Assert.Contains(firstDefinitions, definition => definition.AppId == "parallel.first.app");
        Assert.DoesNotContain(firstDefinitions, definition => definition.AppId == "parallel.second.app");
        Assert.Contains(secondDefinitions, definition => definition.AppId == "parallel.second.app");
        Assert.DoesNotContain(secondDefinitions, definition => definition.AppId == "parallel.first.app");
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

    private sealed class EnvironmentVariableScope : IDisposable
    {
        private readonly string _name;
        private readonly string? _originalValue;

        public EnvironmentVariableScope(string name, string? value)
        {
            _name = name;
            _originalValue = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(_name, _originalValue);
        }
    }
}
