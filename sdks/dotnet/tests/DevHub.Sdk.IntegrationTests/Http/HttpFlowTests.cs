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
    private const string InstancePassword = "sdk-http-flow-password";

    [Fact]
    public async Task PingAndAppsFlow_ShouldSucceed()
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

        var definitionResult = await client.GetDefinitionAsync("http.flow.app", null);
        Assert.Equal("HTTP Flow App", definitionResult.DisplayName);
        Assert.Null(definitionResult.Scope);

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
        }, InstancePassword);

        Assert.Equal("http-flow-inst-1", registered.InstanceId);

        var instances = await client.ListInstancesAsync(new ListInstancesRequest
        {
            AppId = "http.flow.app"
        });
        Assert.Single(instances);

        var lastSeenUtc = await client.HeartbeatAsync("http-flow-inst-1");
        Assert.NotEqual(default, lastSeenUtc);

        await client.UnregisterInstanceAsync("http-flow-inst-1", InstancePassword);
        var instancesAfterUnregister = await client.ListInstancesAsync(new ListInstancesRequest
        {
            AppId = "http.flow.app"
        });
        Assert.Empty(instancesAfterUnregister);
    }

    [Fact]
    public async Task Impl_DefinitionManagement_ShouldValidateUpsertDeleteAndMapErrors()
    {
        await using var host = await DevHubHostFixture.StartAsync();
        await using var client = await host.CreateClientAsync("definition-management-client");

        var invalid = await client.ValidateDefinitionAsync(new AppDefinition
        {
            AppId = "Invalid App Id",
            DisplayName = "Broken Definition"
        });

        Assert.True(invalid.Ok);
        Assert.False(invalid.Valid);
        Assert.NotEmpty(invalid.Errors);
        Assert.Contains(invalid.Errors, issue => issue.Path == "definition.appId");

        var validDefinition = new AppDefinition
        {
            AppId = "definition.http.app",
            DisplayName = "Definition HTTP App",
            Description = "definition integration test"
        };

        var valid = await client.ValidateDefinitionAsync(validDefinition);
        Assert.True(valid.Valid);
        Assert.Empty(valid.Errors);

        var upserted = await client.UpsertDefinitionAsync(validDefinition);
        Assert.Equal(validDefinition.AppId, upserted.AppId);

        var fetched = await client.GetDefinitionAsync(validDefinition.AppId, validDefinition.Scope);
        Assert.Equal(validDefinition.DisplayName, fetched.DisplayName);
        Assert.Equal(validDefinition.Scope, fetched.Scope);

        var invalidException = await Assert.ThrowsAsync<DevHubRpcException>(() => client.UpsertDefinitionAsync(new AppDefinition
        {
            AppId = "Invalid App Id",
            DisplayName = "Broken Definition"
        }));
        Assert.Equal(-32602, invalidException.Code);
        Assert.Equal("definition_invalid", invalidException.Reason);
        Assert.True(invalidException.TryGetDataProperty("errors", out var errorsElement));
        Assert.Equal(JsonValueKind.Array, errorsElement.ValueKind);

        await client.DeleteDefinitionAsync(validDefinition.AppId, validDefinition.Scope);

        var notFoundException = await Assert.ThrowsAsync<DevHubRpcException>(() => client.GetDefinitionAsync(validDefinition.AppId, validDefinition.Scope));
        Assert.Equal(-32014, notFoundException.Code);
        Assert.Equal("app_definition_not_found", notFoundException.Message);
    }

    [Fact]
    public async Task Impl_RegisterUnregister_WithPasswordMismatch_ShouldMapForbidden()
    {
        await using var host = await DevHubHostFixture.StartAsync();
        await host.WriteDefinitionAsync(new AppDefinition
        {
            AppId = "password.flow.app",
            DisplayName = "Password Flow App"
        });

        await using var client = await host.CreateClientAsync("password-flow-client");
        var registration = new AppInstanceRegistration
        {
            InstanceId = "password-flow-inst-1",
            AppId = "password.flow.app",
            Pid = Environment.ProcessId,
            Invoke = new InvokeCapability
            {
                Poll = true,
                Respond = true
            }
        };

        _ = await client.RegisterInstanceAsync(registration, InstancePassword);

        var registerException = await Assert.ThrowsAsync<DevHubRpcException>(() => client.RegisterInstanceAsync(
            new AppInstanceRegistration
            {
                InstanceId = registration.InstanceId,
                AppId = registration.AppId,
                Pid = registration.Pid + 1,
                Invoke = registration.Invoke
            },
            "wrong-password"));
        Assert.Equal(-32002, registerException.Code);
        Assert.Equal("instance_password_mismatch", registerException.Reason);

        var unregisterException = await Assert.ThrowsAsync<DevHubRpcException>(() => client.UnregisterInstanceAsync(registration.InstanceId, "wrong-password"));
        Assert.Equal(-32002, unregisterException.Code);
        Assert.Equal("instance_password_mismatch", unregisterException.Reason);

        await client.UnregisterInstanceAsync(registration.InstanceId, InstancePassword);
    }

    [Fact]
    public async Task Ping_WhenClientIdHeaderMissing_ShouldMapInvalidRequest()
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

    [Fact]
    public async Task RuntimeDiscovery_WhenEnvironmentOverrideSet_ShouldCreateClientWithoutExplicitDataDir()
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
