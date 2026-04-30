namespace DevHub.Tests;

using System.Text.Json;
using DevHub.Core.Models;
using DevHub.Core.Models.Rpc;
using DevHub.Core.Services;
using DevHub.Core.Services.Abstractions;
using DevHub.Core.Services.Rpc.Handlers;
using Microsoft.Extensions.Logging;
using Moq;

[Trait("Category", "Impl")]
public class SpecConformanceTests : IDisposable
{
    private const string InstancePassword = "spec-conformance-password";
    private readonly Mock<ILogger<DefinitionLoader>> _definitionLogger = new();
    private readonly Mock<ILogger<AppRegistry>> _registryLogger = new();
    private readonly Mock<ILogger<AppInstancesHandler>> _instancesLogger = new();
    private readonly string _tempDirectory;

    public SpecConformanceTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "DevHubSpecTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
    }

    [Fact]
    public void Impl_DefinitionLoader_Load_ShouldIgnoreInvalidDefinitionEntries()
    {
        DefinitionCatalogTestHelper.WriteCatalogText(
            DefinitionCatalogTestHelper.GetCatalogPath(_tempDirectory),
            """
            {
              "version": 1,
              "definitions": [
                {
                  "appId": "valid-app",
                  "scopes": [
                    {
                      "scope": "",
                      "displayName": "Valid App"
                    },
                    {
                      "displayName": "Missing Scope"
                    }
                  ]
                },
                {
                  "appId": "invalid app id",
                  "scopes": [
                    {
                      "scope": "",
                      "displayName": "Invalid AppId"
                    }
                  ]
                },
                {
                  "appId": "bad-entry",
                  "scopes": [
                    "not-an-object"
                  ]
                }
              ]
            }
            """);

        var definitionLoader = new DefinitionLoader(DefinitionCatalogTestHelper.GetCatalogPath(_tempDirectory), _definitionLogger.Object);

        // Act
        definitionLoader.Load();
        var definitions = definitionLoader.GetAllDefinitions();

        // Assert
        Assert.Single(definitions);
        Assert.Equal("valid-app", definitions[0].AppId);
    }

    [Fact]
    public async Task Impl_AppDefinitionsHandler_ListDefinitions_ShouldReturnDefinitions()
    {
        WriteJson("list-target.json", new
        {
            appId = "list-target",
            scope = ScopeContract.Global,
            displayName = "List Target"
        });

        var definitionLoader = new DefinitionLoader(DefinitionCatalogTestHelper.GetCatalogPath(_tempDirectory), _definitionLogger.Object);
        var definitionProvider = new DefinitionProvider(definitionLoader);
        definitionProvider.Refresh();
        var handler = new AppDefinitionsHandler(definitionProvider,
            Mock.Of<ILogger<AppDefinitionsHandler>>());

        var request = new JsonRpcRequest
        {
            Id = "req-list",
            Method = "hub.apps.listDefinitions",
            Params = JsonSerializer.SerializeToElement(new { scope = (string?)null })
        };

        var response = await handler.HandleAsync(request, CancellationToken.None);

        Assert.Null(response.Error);
        Assert.NotNull(response.Result);

        var resultElement = JsonSerializer.SerializeToElement(response.Result);
        Assert.True(resultElement.GetProperty("ok").GetBoolean());
        Assert.True(resultElement.TryGetProperty("definitions", out var definitionsElement));
        Assert.Contains(definitionsElement.EnumerateArray(), d => d.GetProperty("appId").GetString() == "list-target");
    }

    [Fact]
    public async Task Impl_AppDefinitionsHandler_GetDefinition_ShouldReturnDefinitionWhenExists()
    {
        WriteJson("get-target.json", new
        {
            appId = "get-target",
            scope = ScopeContract.Global,
            displayName = "Get Target"
        });

        var definitionLoader = new DefinitionLoader(DefinitionCatalogTestHelper.GetCatalogPath(_tempDirectory), _definitionLogger.Object);
        var definitionProvider = new DefinitionProvider(definitionLoader);
        definitionProvider.Refresh();
        var handler = new AppDefinitionsHandler(definitionProvider,
            Mock.Of<ILogger<AppDefinitionsHandler>>());

        var request = new JsonRpcRequest
        {
            Id = "req-get",
            Method = "hub.apps.getDefinition",
            Params = JsonSerializer.SerializeToElement(new { appId = "get-target", scope = ScopeContract.Global })
        };

        var response = await handler.HandleAsync(request, CancellationToken.None);

        Assert.Null(response.Error);
        Assert.NotNull(response.Result);

        var resultElement = JsonSerializer.SerializeToElement(response.Result);
        Assert.True(resultElement.GetProperty("ok").GetBoolean());
        var definitionElement = resultElement.GetProperty("definition");
        Assert.Equal("get-target", definitionElement.GetProperty("appId").GetString());
    }

    [Fact]
    public async Task Impl_AppDefinitionsHandler_GetDefinition_WhenMissing_ShouldReturnAppDefinitionNotFound()
    {
        var definitionLoader = new DefinitionLoader(DefinitionCatalogTestHelper.GetCatalogPath(_tempDirectory), _definitionLogger.Object);
        var definitionProvider = new DefinitionProvider(definitionLoader);
        definitionProvider.Refresh();
        var handler = new AppDefinitionsHandler(definitionProvider,
            Mock.Of<ILogger<AppDefinitionsHandler>>());

        var request = new JsonRpcRequest
        {
            Id = "req-get-missing",
            Method = "hub.apps.getDefinition",
            Params = JsonSerializer.SerializeToElement(new { appId = "missing-app", scope = ScopeContract.Global })
        };

        var response = await handler.HandleAsync(request, CancellationToken.None);

        Assert.NotNull(response.Error);
        Assert.Equal(-32014, response.Error.Code);
        Assert.Equal("app_definition_not_found", response.Error.Message);

        var data = JsonSerializer.SerializeToElement(response.Error.Data);
        Assert.Equal("missing-app", data.GetProperty("appId").GetString());
    }

    [Fact]
    public async Task Impl_AppInstancesHandler_RegisterInstance_ShouldReturnInstanceInResult()
    {
        // Arrange
        var appRegistry = new AppRegistry(new SystemClock(), _registryLogger.Object);
        var handler = new AppInstancesHandler(appRegistry, new SystemClock(), _instancesLogger.Object);
        var request = new JsonRpcRequest
        {
            Id = "req-1",
            Method = "hub.apps.registerInstance",
            Params = JsonSerializer.SerializeToElement(CreateRegisterParams(new
            {
                instanceId = "test-instance-001",
                appId = "test-app",
                scope = ScopeContract.Global,
                pid = 12345,
                invoke = new { poll = true, respond = true }
            }))
        };

        // Act
        var response = await handler.HandleAsync(request, CancellationToken.None);

        // Assert
        Assert.Null(response.Error);
        Assert.NotNull(response.Result);

        var resultElement = JsonSerializer.SerializeToElement(response.Result);
        Assert.True(resultElement.GetProperty("ok").GetBoolean());
        Assert.True(resultElement.TryGetProperty("instance", out var instanceElement));
        Assert.Equal("test-instance-001", instanceElement.GetProperty("instanceId").GetString());
        Assert.Equal("test-app", instanceElement.GetProperty("appId").GetString());
        Assert.True(instanceElement.TryGetProperty("registeredAtUtc", out _));
        Assert.True(instanceElement.TryGetProperty("lastSeenUtc", out _));
        Assert.True(instanceElement.TryGetProperty("invoke", out var invokeElement));
        Assert.True(invokeElement.GetProperty("poll").GetBoolean());
        Assert.True(invokeElement.GetProperty("respond").GetBoolean());
    }

    [Fact]
    public async Task Impl_AppInstancesHandler_RegisterInstance_InvalidInputs_ShouldReturnInvalidParams()
    {
        // Arrange
        var appRegistry = new AppRegistry(new SystemClock(), _registryLogger.Object);
        var handler = new AppInstancesHandler(appRegistry, new SystemClock(), _instancesLogger.Object);

        // pid 非法
        var invalidPidRequest = new JsonRpcRequest
        {
            Id = "req-2",
            Method = "hub.apps.registerInstance",
            Params = JsonSerializer.SerializeToElement(CreateRegisterParams(new
            {
                instanceId = "test-instance-002",
                appId = "test-app",
                pid = 0,
                invoke = new { poll = true, respond = true }
            }))
        };

        // scope 类型非法（非 string/null）
        var invalidScopeRequest = new JsonRpcRequest
        {
            Id = "req-3",
            Method = "hub.apps.registerInstance",
            Params = JsonSerializer.SerializeToElement(CreateRegisterParams(new
            {
                instanceId = "test-instance-003",
                appId = "test-app",
                scope = 123,
                pid = 123,
                invoke = new { poll = true, respond = true }
            }))
        };

        // invoke 缺失
        var missingInvokeRequest = new JsonRpcRequest
        {
            Id = "req-4",
            Method = "hub.apps.registerInstance",
            Params = JsonSerializer.SerializeToElement(CreateRegisterParams(new
            {
                instanceId = "test-instance-004",
                appId = "test-app",
                pid = 123
            }))
        };

        // Act
        var invalidPidResponse = await handler.HandleAsync(invalidPidRequest, CancellationToken.None);
        var invalidScopeResponse = await handler.HandleAsync(invalidScopeRequest, CancellationToken.None);
        var missingInvokeResponse = await handler.HandleAsync(missingInvokeRequest, CancellationToken.None);

        // Assert
        Assert.Equal(-32602, invalidPidResponse.Error?.Code);
        Assert.Equal("invalid_params", invalidPidResponse.Error?.Message);

        Assert.Equal(-32602, invalidScopeResponse.Error?.Code);
        Assert.Equal("invalid_params", invalidScopeResponse.Error?.Message);
        var invalidScopeData = JsonSerializer.SerializeToElement(invalidScopeResponse.Error?.Data);
        Assert.Equal("invalid_scope", invalidScopeData.GetProperty("reason").GetString());

        Assert.Equal(-32602, missingInvokeResponse.Error?.Code);
        Assert.Equal("invalid_params", missingInvokeResponse.Error?.Message);
    }

    [Fact]
    public async Task Impl_AppInstancesHandler_UnregisterInstance_ShouldBeIdempotent()
    {
        var appRegistry = new AppRegistry(new SystemClock(), _registryLogger.Object);
        var handler = new AppInstancesHandler(appRegistry, new SystemClock(), _instancesLogger.Object);

        var registerRequest = new JsonRpcRequest
        {
            Id = "req-register",
            Method = "hub.apps.registerInstance",
            Params = JsonSerializer.SerializeToElement(CreateRegisterParams(new
            {
                instanceId = "test-instance-unregister",
                appId = "test-app",
                scope = ScopeContract.Global,
                pid = 45678,
                invoke = new { poll = true, respond = true }
            }))
        };

        var registerResponse = await handler.HandleAsync(registerRequest, CancellationToken.None);
        Assert.Null(registerResponse.Error);
        var instanceSessionToken = ExtractInstanceSessionToken(registerResponse);

        var unregisterRequest = new JsonRpcRequest
        {
            Id = "req-unregister-1",
            Method = "hub.apps.unregisterInstance",
            Params = JsonSerializer.SerializeToElement(CreateUnregisterParams("test-instance-unregister", instanceSessionToken))
        };

        var firstResponse = await handler.HandleAsync(unregisterRequest, CancellationToken.None);
        var secondResponse = await handler.HandleAsync(unregisterRequest, CancellationToken.None);

        Assert.Null(firstResponse.Error);
        Assert.Null(secondResponse.Error);

        var firstResult = JsonSerializer.SerializeToElement(firstResponse.Result);
        var secondResult = JsonSerializer.SerializeToElement(secondResponse.Result);
        Assert.True(firstResult.GetProperty("ok").GetBoolean());
        Assert.True(secondResult.GetProperty("ok").GetBoolean());

        var listResponse = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "req-list-after-unregister",
            Method = "hub.apps.listInstances",
            Params = JsonSerializer.SerializeToElement(new
            {
                appId = "test-app",
                scope = (string?)null,
                includeOffline = true
            })
        }, CancellationToken.None);

        Assert.Null(listResponse.Error);
        var listResult = JsonSerializer.SerializeToElement(listResponse.Result);
        var instancesElement = listResult.GetProperty("instances");
        Assert.DoesNotContain(instancesElement.EnumerateArray(), i => i.GetProperty("instanceId").GetString() == "test-instance-unregister");
    }

    [Fact]
    public async Task Impl_AppInstancesHandler_ListInstances_DefaultIncludeOfflineFalse_ShouldFilterOfflineInstances()
    {
        var clock = new MutableClock(DateTime.UtcNow);
        var appRegistry = new AppRegistry(clock, _registryLogger.Object);
        var handler = new AppInstancesHandler(appRegistry, clock, _instancesLogger.Object);

        await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "req-register-offline",
            Method = "hub.apps.registerInstance",
            Params = JsonSerializer.SerializeToElement(CreateRegisterParams(new
            {
                instanceId = "instance-offline",
                appId = "list-offline-default.app",
                scope = ScopeContract.Global,
                pid = 5102,
                invoke = new { poll = true, respond = true }
            }))
        }, CancellationToken.None);

        clock.Advance(TimeSpan.FromSeconds(31));

        await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "req-register-online",
            Method = "hub.apps.registerInstance",
            Params = JsonSerializer.SerializeToElement(CreateRegisterParams(new
            {
                instanceId = "instance-online",
                appId = "list-offline-default.app",
                scope = ScopeContract.Global,
                pid = 5101,
                invoke = new { poll = true, respond = true }
            }))
        }, CancellationToken.None);

        var defaultListResponse = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "req-list-default",
            Method = "hub.apps.listInstances",
            Params = JsonSerializer.SerializeToElement(new
            {
                appId = "list-offline-default.app",
                scope = ScopeContract.Global
            })
        }, CancellationToken.None);

        Assert.Null(defaultListResponse.Error);
        var defaultListResult = JsonSerializer.SerializeToElement(defaultListResponse.Result);
        var defaultInstances = defaultListResult.GetProperty("instances").EnumerateArray().ToList();

        Assert.Contains(defaultInstances, i => i.GetProperty("instanceId").GetString() == "instance-online");
        Assert.DoesNotContain(defaultInstances, i => i.GetProperty("instanceId").GetString() == "instance-offline");
    }

    [Fact]
    public async Task Impl_AppInstancesHandler_ListInstances_WhenScopeNull_ShouldReturnAllScopes()
    {
        var appRegistry = new AppRegistry(new SystemClock(), _registryLogger.Object);
        var handler = new AppInstancesHandler(appRegistry, new SystemClock(), _instancesLogger.Object);

        await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "req-register-global",
            Method = "hub.apps.registerInstance",
            Params = JsonSerializer.SerializeToElement(CreateRegisterParams(new
            {
                instanceId = "instance-global",
                appId = "list-all-scopes.app",
                scope = ScopeContract.Global,
                pid = 5201,
                invoke = new { poll = true, respond = true }
            }))
        }, CancellationToken.None);

        await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "req-register-scoped",
            Method = "hub.apps.registerInstance",
            Params = JsonSerializer.SerializeToElement(CreateRegisterParams(new
            {
                instanceId = "instance-scoped",
                appId = "list-all-scopes.app",
                scope = "workspace-A",
                pid = 5202,
                invoke = new { poll = true, respond = true }
            }))
        }, CancellationToken.None);

        var listResponse = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "req-list-all-scopes-ignore-scope",
            Method = "hub.apps.listInstances",
            Params = JsonSerializer.SerializeToElement(new
            {
                appId = "list-all-scopes.app",
                scope = (string?)null,
                includeOffline = true
            })
        }, CancellationToken.None);

        Assert.Null(listResponse.Error);
        var listResult = JsonSerializer.SerializeToElement(listResponse.Result);
        var instances = listResult.GetProperty("instances").EnumerateArray().ToList();

        Assert.Contains(instances, i => i.GetProperty("instanceId").GetString() == "instance-global");
        Assert.Contains(instances, i => i.GetProperty("instanceId").GetString() == "instance-scoped");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }

    private void WriteJson(string fileName, object payload)
    {
        _ = fileName;
        DefinitionCatalogTestHelper.UpsertDefinition(
            DefinitionCatalogTestHelper.GetCatalogPath(_tempDirectory),
            JsonSerializer.Serialize(payload));
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
}
