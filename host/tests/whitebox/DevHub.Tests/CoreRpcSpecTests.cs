namespace DevHub.Tests;

using System.Globalization;
using System.Text.Json;
using DevHub.Core.Models;
using DevHub.Core.Models.Rpc;
using DevHub.Core.Services;
using DevHub.Core.Services.Abstractions;
using DevHub.Core.Services.Rpc.Handlers;
using Microsoft.Extensions.Logging;
using Moq;

/// <summary>
/// 核心 RPC 规范白盒测试。
/// </summary>
[Trait("Category", "Spec")]
public class CoreRpcSpecTests : IDisposable
{
    private const string InstancePassword = "core-rpc-password";
    private readonly Mock<ILogger<DefinitionLoader>> _definitionLogger = new();
    private readonly Mock<ILogger<AppRegistry>> _registryLogger = new();
    private readonly Mock<ILogger<AppInstancesHandler>> _instancesLogger = new();
    private readonly string _tempDirectory;

    public CoreRpcSpecTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "DevHubCoreRpcSpecTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
    }

    [Fact]
    [Trait("SpecRef", "6.3.1")]
    public async Task Spec_6_3_1_HubPing_WhenEchoOmitted_ShouldReturnOkAndServerTime()
    {
        var handler = new HubPingHandler(new SystemClock(), Mock.Of<ILogger<HubPingHandler>>());
        var response = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "spec-6.3.1-no-echo",
            Method = "hub.ping",
            Params = JsonSerializer.SerializeToElement(new { })
        }, CancellationToken.None);

        Assert.Null(response.Error);
        var result = JsonSerializer.SerializeToElement(response.Result);
        Assert.True(result.GetProperty("ok").GetBoolean());

        var serverTimeUtc = result.GetProperty("serverTimeUtc").GetString();
        Assert.False(string.IsNullOrWhiteSpace(serverTimeUtc));
        Assert.True(DateTimeOffset.TryParse(serverTimeUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsedServerTime));
        Assert.Equal(TimeSpan.Zero, parsedServerTime.Offset);
    }

    [Fact]
    [Trait("SpecRef", "6.3.1")]
    public async Task Spec_6_3_1_HubPing_WhenEchoProvided_ShouldEchoBack()
    {
        var handler = new HubPingHandler(new SystemClock(), Mock.Of<ILogger<HubPingHandler>>());
        var parameters = JsonSerializer.SerializeToElement(new
        {
            echo = new
            {
                message = "pong",
                id = 1001,
                tags = new[] { "spec", "ping" }
            }
        });
        var expectedEcho = parameters.GetProperty("echo").Clone();

        var response = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "spec-6.3.1-with-echo",
            Method = "hub.ping",
            Params = parameters
        }, CancellationToken.None);

        Assert.Null(response.Error);
        var result = JsonSerializer.SerializeToElement(response.Result);
        Assert.True(result.GetProperty("ok").GetBoolean());
        Assert.True(result.TryGetProperty("echo", out var actualEcho));
        Assert.True(JsonElement.DeepEquals(expectedEcho, actualEcho));
    }

    [Fact]
    [Trait("SpecRef", "6.3.1.1")]
    public async Task Spec_6_3_1_1_HubGetVersion_WhenParamsOmitted_ShouldReturnSemVerVersion()
    {
        var handler = new HubGetVersionHandler(
            Mock.Of<IHubVersionSource>(source => source.CurrentVersion == "0.7.0-preview.1+build.2"),
            Mock.Of<ILogger<HubGetVersionHandler>>());

        var response = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "spec-6.3.1.1-get-version",
            Method = "hub.getVersion"
        }, CancellationToken.None);

        Assert.Null(response.Error);
        var result = JsonSerializer.SerializeToElement(response.Result);
        Assert.True(result.GetProperty("ok").GetBoolean());
        Assert.Equal("0.7.0-preview.1+build.2", result.GetProperty("version").GetString());
    }

    [Fact]
    [Trait("SpecRef", "6.3.1.1")]
    public async Task Spec_6_3_1_1_HubGetVersion_WhenParamsContainUnexpectedField_ShouldReturnInvalidParams()
    {
        var handler = new HubGetVersionHandler(
            Mock.Of<IHubVersionSource>(source => source.CurrentVersion == "0.7.0"),
            Mock.Of<ILogger<HubGetVersionHandler>>());

        var response = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "spec-6.3.1.1-get-version-invalid",
            Method = "hub.getVersion",
            Params = JsonSerializer.SerializeToElement(new { verbose = true })
        }, CancellationToken.None);

        Assert.NotNull(response.Error);
        Assert.Equal(-32602, response.Error.Code);
        Assert.Equal("invalid_params", response.Error.Message);
    }

    [Fact]
    [Trait("SpecRef", "6.3.3")]
    public async Task Spec_6_3_3_ListDefinitions_ShouldReturnDefinitions()
    {
        WriteDefinition(new
        {
            appId = "spec-6.3.3-target",
            scope = ScopeContract.Global,
            displayName = "Spec 6.3.3 Target"
        });

        var handler = CreateDefinitionsHandler();
        var response = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "spec-6.3.3-list",
            Method = "hub.apps.listDefinitions",
            Params = JsonSerializer.SerializeToElement(new { scope = (string?)null })
        }, CancellationToken.None);

        Assert.Null(response.Error);
        var result = JsonSerializer.SerializeToElement(response.Result);
        Assert.True(result.GetProperty("ok").GetBoolean());
        var definitions = result.GetProperty("definitions").EnumerateArray().ToList();
        Assert.Contains(definitions, definition => definition.GetProperty("appId").GetString() == "spec-6.3.3-target");
    }

    [Fact]
    [Trait("SpecRef", "6.3.4")]
    public async Task Spec_6_3_4_GetDefinition_ShouldReturnDefinitionWhenExists()
    {
        WriteDefinition(new
        {
            appId = "spec-6.3.4-target",
            scope = ScopeContract.Global,
            displayName = "Spec 6.3.4 Target"
        });

        var handler = CreateDefinitionsHandler();
        var response = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "spec-6.3.4-get",
            Method = "hub.apps.getDefinition",
            Params = JsonSerializer.SerializeToElement(new { appId = "spec-6.3.4-target", scope = ScopeContract.Global })
        }, CancellationToken.None);

        Assert.Null(response.Error);
        var result = JsonSerializer.SerializeToElement(response.Result);
        Assert.True(result.GetProperty("ok").GetBoolean());
        Assert.Equal("spec-6.3.4-target", result.GetProperty("definition").GetProperty("appId").GetString());
    }

    [Fact]
    [Trait("SpecRef", "6.3.4")]
    public async Task Spec_6_3_4_GetDefinition_WhenMissing_ShouldReturnAppDefinitionNotFound()
    {
        var handler = CreateDefinitionsHandler();
        var response = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "spec-6.3.4-get-missing",
            Method = "hub.apps.getDefinition",
            Params = JsonSerializer.SerializeToElement(new { appId = "spec-6.3.4-missing", scope = ScopeContract.Global })
        }, CancellationToken.None);

        Assert.NotNull(response.Error);
        Assert.Equal(-32014, response.Error.Code);
        Assert.Equal("app_definition_not_found", response.Error.Message);
        var errorData = JsonSerializer.SerializeToElement(response.Error.Data);
        Assert.Equal("spec-6.3.4-missing", errorData.GetProperty("appId").GetString());
    }

    [Fact]
    [Trait("SpecRef", "6.3.5")]
    public async Task Spec_6_3_5_RegisterInstance_ShouldReturnInstanceWithServerManagedTimestamps()
    {
        var appRegistry = new AppRegistry(new SystemClock(), _registryLogger.Object);
        var handler = new AppInstancesHandler(appRegistry, new SystemClock(), _instancesLogger.Object);

        var response = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "spec-6.3.5-register",
            Method = "hub.apps.registerInstance",
            Params = JsonSerializer.SerializeToElement(CreateRegisterParams(new
            {
                instanceId = "spec-6.3.5-instance",
                appId = "spec-6.3.5.app",
                scope = ScopeContract.Global,
                pid = 6101,
                invoke = new
                {
                    poll = true,
                    respond = true
                }
            }))
        }, CancellationToken.None);

        Assert.Null(response.Error);
        var result = JsonSerializer.SerializeToElement(response.Result);
        Assert.True(result.GetProperty("ok").GetBoolean());

        var instance = result.GetProperty("instance");
        Assert.Equal("spec-6.3.5-instance", instance.GetProperty("instanceId").GetString());
        Assert.True(instance.TryGetProperty("registeredAtUtc", out var registeredAtRaw));
        Assert.True(instance.TryGetProperty("lastSeenUtc", out var lastSeenRaw));
        Assert.True(DateTime.TryParse(registeredAtRaw.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var registeredAtUtc));
        Assert.True(DateTime.TryParse(lastSeenRaw.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var lastSeenUtc));
        Assert.True(lastSeenUtc >= registeredAtUtc);
    }

    [Fact]
    [Trait("SpecRef", "6.3.5")]
    public async Task Spec_6_3_5_RegisterInstance_WhenSameInstanceRegistersAgain_ShouldRefreshLastSeenUtc()
    {
        var clock = new MutableClock(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var appRegistry = new AppRegistry(clock, _registryLogger.Object);
        var handler = new AppInstancesHandler(appRegistry, clock, _instancesLogger.Object);

        var firstResponse = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "spec-6.3.5-register-refresh-first",
            Method = "hub.apps.registerInstance",
            Params = JsonSerializer.SerializeToElement(CreateRegisterParams(new
            {
                instanceId = "spec-6.3.5-refresh-instance",
                appId = "spec-6.3.5.refresh.app",
                scope = ScopeContract.Global,
                pid = 6103,
                invoke = new
                {
                    poll = true,
                    respond = true
                }
            }))
        }, CancellationToken.None);

        Assert.Null(firstResponse.Error);
        var firstResult = JsonSerializer.SerializeToElement(firstResponse.Result);
        var firstLastSeenUtc = DateTime.Parse(
            firstResult.GetProperty("instance").GetProperty("lastSeenUtc").GetString()!,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind);

        clock.Advance(TimeSpan.FromSeconds(2));

        var secondResponse = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "spec-6.3.5-register-refresh-second",
            Method = "hub.apps.registerInstance",
            Params = JsonSerializer.SerializeToElement(CreateRegisterParams(new
            {
                instanceId = "spec-6.3.5-refresh-instance",
                appId = "spec-6.3.5.refresh.app",
                scope = ScopeContract.Global,
                pid = 6103,
                invoke = new
                {
                    poll = true,
                    respond = true
                }
            }))
        }, CancellationToken.None);

        Assert.Null(secondResponse.Error);
        var secondResult = JsonSerializer.SerializeToElement(secondResponse.Result);
        var secondLastSeenUtc = DateTime.Parse(
            secondResult.GetProperty("instance").GetProperty("lastSeenUtc").GetString()!,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind);

        Assert.True(secondLastSeenUtc > firstLastSeenUtc);
    }

    [Fact]
    [Trait("SpecRef", "6.3.5")]
    public async Task Spec_6_3_5_RegisterInstance_InvalidScope_ShouldReturnInvalidParams()
    {
        var appRegistry = new AppRegistry(new SystemClock(), _registryLogger.Object);
        var handler = new AppInstancesHandler(appRegistry, new SystemClock(), _instancesLogger.Object);

        var response = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "spec-6.3.5-invalid-scope",
            Method = "hub.apps.registerInstance",
            Params = JsonSerializer.SerializeToElement(CreateRegisterParams(new
            {
                instanceId = "spec-6.3.5-invalid-scope",
                appId = "spec-6.3.5.app",
                scope = 1,
                pid = 6102,
                invoke = new
                {
                    poll = true,
                    respond = true
                }
            }))
        }, CancellationToken.None);

        Assert.NotNull(response.Error);
        Assert.Equal(-32602, response.Error.Code);
        Assert.Equal("invalid_params", response.Error.Message);
        var errorData = JsonSerializer.SerializeToElement(response.Error.Data);
        Assert.Equal("invalid_scope", errorData.GetProperty("reason").GetString());
    }

    [Fact]
    [Trait("SpecRef", "6.3.7")]
    public async Task Spec_6_3_7_UnregisterInstance_ShouldBeIdempotent()
    {
        var appRegistry = new AppRegistry(new SystemClock(), _registryLogger.Object);
        var handler = new AppInstancesHandler(appRegistry, new SystemClock(), _instancesLogger.Object);

        var registerResponse = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "spec-6.3.7-register",
            Method = "hub.apps.registerInstance",
            Params = JsonSerializer.SerializeToElement(CreateRegisterParams(new
            {
                instanceId = "spec-6.3.7-instance",
                appId = "spec-6.3.7.app",
                scope = ScopeContract.Global,
                pid = 6201,
                invoke = new
                {
                    poll = true,
                    respond = true
                }
            }))
        }, CancellationToken.None);
        Assert.Null(registerResponse.Error);
        var instanceSessionToken = ExtractInstanceSessionToken(registerResponse);

        var firstResponse = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "spec-6.3.7-unregister-first",
            Method = "hub.apps.unregisterInstance",
            Params = JsonSerializer.SerializeToElement(CreateUnregisterParams("spec-6.3.7-instance", instanceSessionToken))
        }, CancellationToken.None);

        var secondResponse = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "spec-6.3.7-unregister-second",
            Method = "hub.apps.unregisterInstance",
            Params = JsonSerializer.SerializeToElement(CreateUnregisterParams("spec-6.3.7-instance", instanceSessionToken))
        }, CancellationToken.None);

        Assert.Null(firstResponse.Error);
        Assert.Null(secondResponse.Error);
        Assert.True(JsonSerializer.SerializeToElement(firstResponse.Result).GetProperty("ok").GetBoolean());
        Assert.True(JsonSerializer.SerializeToElement(secondResponse.Result).GetProperty("ok").GetBoolean());
    }

    [Fact]
    [Trait("SpecRef", "6.3.8")]
    public async Task Spec_6_3_8_ListInstances_GlobalScopeAndIncludeOfflineFalse_ShouldApply()
    {
        var clock = new MutableClock(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var appRegistry = new AppRegistry(clock, _registryLogger.Object);
        var handler = new AppInstancesHandler(appRegistry, clock, _instancesLogger.Object);

        var globalOnlineToken = await RegisterInstanceAsync(handler, "spec-6.3.8-global-online", "spec-6.3.8.app", null, 6301);
        await RegisterInstanceAsync(handler, "spec-6.3.8-global-offline", "spec-6.3.8.app", null, 6302);
        var scopedOnlineToken = await RegisterInstanceAsync(handler, "spec-6.3.8-scoped-online", "spec-6.3.8.app", "workspace-A", 6303);

        clock.Advance(TimeSpan.FromSeconds(31));
        await HeartbeatInstanceAsync(handler, "spec-6.3.8-global-online", globalOnlineToken);
        await HeartbeatInstanceAsync(handler, "spec-6.3.8-scoped-online", scopedOnlineToken);

        var defaultResponse = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "spec-6.3.8-list-default",
            Method = "hub.apps.listInstances",
            Params = JsonSerializer.SerializeToElement(new
            {
                appId = "spec-6.3.8.app",
                scope = ScopeContract.Global
            })
        }, CancellationToken.None);

        Assert.Null(defaultResponse.Error);
        var defaultInstances = JsonSerializer.SerializeToElement(defaultResponse.Result).GetProperty("instances").EnumerateArray().ToList();
        Assert.Contains(defaultInstances, instance => instance.GetProperty("instanceId").GetString() == "spec-6.3.8-global-online");
        Assert.DoesNotContain(defaultInstances, instance => instance.GetProperty("instanceId").GetString() == "spec-6.3.8-global-offline");
        Assert.DoesNotContain(defaultInstances, instance => instance.GetProperty("instanceId").GetString() == "spec-6.3.8-scoped-online");

        var scopedResponse = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "spec-6.3.8-list-scoped",
            Method = "hub.apps.listInstances",
            Params = JsonSerializer.SerializeToElement(new
            {
                appId = "spec-6.3.8.app",
                scope = "workspace-A"
            })
        }, CancellationToken.None);

        Assert.Null(scopedResponse.Error);
        var scopedInstances = JsonSerializer.SerializeToElement(scopedResponse.Result).GetProperty("instances").EnumerateArray().ToList();
        Assert.Contains(scopedInstances, instance => instance.GetProperty("instanceId").GetString() == "spec-6.3.8-scoped-online");
        Assert.DoesNotContain(scopedInstances, instance => instance.GetProperty("instanceId").GetString() == "spec-6.3.8-global-online");

        var includeOfflineResponse = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "spec-6.3.8-list-include-offline",
            Method = "hub.apps.listInstances",
            Params = JsonSerializer.SerializeToElement(new
            {
                appId = "spec-6.3.8.app",
                scope = ScopeContract.Global,
                includeOffline = true
            })
        }, CancellationToken.None);

        Assert.Null(includeOfflineResponse.Error);
        var includeOfflineInstances = JsonSerializer.SerializeToElement(includeOfflineResponse.Result).GetProperty("instances").EnumerateArray().ToList();
        Assert.Contains(includeOfflineInstances, instance => instance.GetProperty("instanceId").GetString() == "spec-6.3.8-global-offline");
    }

    [Fact]
    [Trait("SpecRef", "6.3.8")]
    public async Task Spec_6_3_8_ListInstances_WhenScopeNull_ShouldReturnAllScopes()
    {
        var appRegistry = new AppRegistry(new SystemClock(), _registryLogger.Object);
        var handler = new AppInstancesHandler(appRegistry, new SystemClock(), _instancesLogger.Object);

        await RegisterInstanceAsync(handler, "spec-6.3.8-scope-all-global", "spec-6.3.8.scope-all.app", null, 6401);
        await RegisterInstanceAsync(handler, "spec-6.3.8-scope-all-scoped", "spec-6.3.8.scope-all.app", "workspace-B", 6402);

        var response = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "spec-6.3.8-list-all-scopes",
            Method = "hub.apps.listInstances",
            Params = JsonSerializer.SerializeToElement(new
            {
                appId = "spec-6.3.8.scope-all.app",
                scope = (string?)null,
                includeOffline = true
            })
        }, CancellationToken.None);

        Assert.Null(response.Error);
        var instances = JsonSerializer.SerializeToElement(response.Result).GetProperty("instances").EnumerateArray().ToList();
        Assert.Contains(instances, instance => instance.GetProperty("instanceId").GetString() == "spec-6.3.8-scope-all-global");
        Assert.Contains(instances, instance => instance.GetProperty("instanceId").GetString() == "spec-6.3.8-scope-all-scoped");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }

    private AppDefinitionsHandler CreateDefinitionsHandler()
    {
        var definitionLoader = new DefinitionLoader(_tempDirectory, _definitionLogger.Object);
        var definitionProvider = new DefinitionProvider(definitionLoader);
        definitionProvider.Refresh();
        return new AppDefinitionsHandler(definitionProvider, Mock.Of<ILogger<AppDefinitionsHandler>>());
    }

    private async Task<string> RegisterInstanceAsync(AppInstancesHandler handler, string instanceId, string appId, string? scope, int pid)
    {
        var normalizedScope = scope ?? ScopeContract.Global;
        var response = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "register-" + instanceId,
            Method = "hub.apps.registerInstance",
            Params = JsonSerializer.SerializeToElement(CreateRegisterParams(new
            {
                instanceId,
                appId,
                scope = normalizedScope,
                pid,
                invoke = new
                {
                    poll = true,
                    respond = true
                }
            }))
        }, CancellationToken.None);

        Assert.Null(response.Error);
        return ExtractInstanceSessionToken(response);
    }

    private async Task HeartbeatInstanceAsync(AppInstancesHandler handler, string instanceId, string instanceSessionToken)
    {
        var response = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "heartbeat-" + instanceId,
            Method = "hub.apps.heartbeat",
            Params = JsonSerializer.SerializeToElement(new
            {
                instanceId,
                instanceSessionToken
            })
        }, CancellationToken.None);

        Assert.Null(response.Error);
    }

    private static object CreateRegisterParams(object instance)
    {
        return new
        {
            password = InstancePassword,
            instance
        };
    }

    private static string ExtractInstanceSessionToken(JsonRpcResponse response)
    {
        return JsonSerializer.SerializeToElement(response.Result).GetProperty("instanceSessionToken").GetString()!;
    }

    private static object CreateUnregisterParams(string instanceId, string instanceSessionToken)
    {
        return new
        {
            instanceId,
            instanceSessionToken
        };
    }

    private sealed class MutableClock : IClock
    {
        public MutableClock(DateTime utcNow)
        {
            UtcNow = utcNow;
        }

        public DateTime UtcNow { get; private set; }

        public void Advance(TimeSpan duration)
        {
            UtcNow = UtcNow.Add(duration);
        }
    }

    private void WriteDefinition(object payload)
    {
        var json = JsonSerializer.SerializeToElement(payload);
        var appId = json.GetProperty("appId").GetString();
        var scope = json.TryGetProperty("scope", out var scopeElement) && scopeElement.ValueKind != JsonValueKind.Null
            ? scopeElement.GetString()
            : ScopeContract.Global;
        var fullPath = Path.Combine(_tempDirectory, AppDefinitionIdentity.Create(appId!, scope ?? ScopeContract.Global).GetFileName());
        File.WriteAllText(fullPath, JsonSerializer.Serialize(payload));
    }
}
