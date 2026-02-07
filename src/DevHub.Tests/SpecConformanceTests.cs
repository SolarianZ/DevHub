namespace DevHub.Tests;

using System.Text.Json;
using DevHub.Core.Models.Rpc;
using DevHub.Core.Services;
using DevHub.Core.Services.Rpc.Handlers;
using Microsoft.Extensions.Logging;
using Moq;

public class SpecConformanceTests : IDisposable
{
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
    public void DefinitionLoader_Load_ShouldIgnoreInvalidDefinitionFiles()
    {
        // Arrange
        WriteJson("valid-app.json", new
        {
            appId = "valid-app",
            displayName = "Valid App",
            scopePolicy = "any"
        });

        WriteJson("invalid app id.json", new
        {
            appId = "invalid app id",
            displayName = "Invalid AppId",
            scopePolicy = "any"
        });

        WriteJson("invalid-scope.json", new
        {
            appId = "invalid-scope",
            displayName = "Invalid Scope",
            scopePolicy = "invalid"
        });

        // 文件名与 appId 不一致
        WriteJson("mismatch-name.json", new
        {
            appId = "real-name",
            displayName = "Mismatch Name",
            scopePolicy = "any"
        });

        var definitionLoader = new DefinitionLoader(_tempDirectory, _definitionLogger.Object);

        // Act
        definitionLoader.Load();
        var definitions = definitionLoader.GetAllDefinitions();

        // Assert
        Assert.Single(definitions);
        Assert.Equal("valid-app", definitions[0].AppId);
    }

    [Fact]
    public async Task AppDefinitionsHandler_ListDefinitions_ShouldReturnDefinitions()
    {
        WriteJson("list-target.json", new
        {
            appId = "list-target",
            displayName = "List Target",
            scopePolicy = "any"
        });

        var definitionLoader = new DefinitionLoader(_tempDirectory, _definitionLogger.Object);
        var handler = new AppDefinitionsHandler(
            definitionLoader,
            Mock.Of<ILogger<AppDefinitionsHandler>>());

        var request = new JsonRpcRequest
        {
            Id = "req-list",
            Method = "hub.apps.listDefinitions",
            Params = JsonSerializer.SerializeToElement(new { })
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
    public async Task AppDefinitionsHandler_GetDefinition_ShouldReturnDefinitionWhenExists()
    {
        WriteJson("get-target.json", new
        {
            appId = "get-target",
            displayName = "Get Target",
            scopePolicy = "any"
        });

        var definitionLoader = new DefinitionLoader(_tempDirectory, _definitionLogger.Object);
        var handler = new AppDefinitionsHandler(
            definitionLoader,
            Mock.Of<ILogger<AppDefinitionsHandler>>());

        var request = new JsonRpcRequest
        {
            Id = "req-get",
            Method = "hub.apps.getDefinition",
            Params = JsonSerializer.SerializeToElement(new { appId = "get-target" })
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
    public async Task AppInstancesHandler_RegisterInstance_ShouldReturnInstanceInResult()
    {
        // Arrange
        var appRegistry = new AppRegistry(_registryLogger.Object);
        var definitionLoader = new DefinitionLoader(_tempDirectory, _definitionLogger.Object);
        var handler = new AppInstancesHandler(appRegistry, _instancesLogger.Object, definitionLoader);
        var request = new JsonRpcRequest
        {
            Id = "req-1",
            Method = "hub.apps.registerInstance",
            Params = JsonSerializer.SerializeToElement(new
            {
                instance = new
                {
                    instanceId = "test-instance-001",
                    appId = "test-app",
                    scope = (string?)null,
                    pid = 12345,
                    invoke = new { poll = true, respond = true }
                }
            })
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
    public async Task AppInstancesHandler_RegisterInstance_InvalidInputs_ShouldReturnInvalidParams()
    {
        // Arrange
        var appRegistry = new AppRegistry(_registryLogger.Object);
        var definitionLoader = new DefinitionLoader(_tempDirectory, _definitionLogger.Object);
        var handler = new AppInstancesHandler(appRegistry, _instancesLogger.Object, definitionLoader);

        // pid 非法
        var invalidPidRequest = new JsonRpcRequest
        {
            Id = "req-2",
            Method = "hub.apps.registerInstance",
            Params = JsonSerializer.SerializeToElement(new
            {
                instance = new
                {
                    instanceId = "test-instance-002",
                    appId = "test-app",
                    pid = 0,
                    invoke = new { poll = true, respond = true }
                }
            })
        };

        // scope 为空字符串
        var invalidScopeRequest = new JsonRpcRequest
        {
            Id = "req-3",
            Method = "hub.apps.registerInstance",
            Params = JsonSerializer.SerializeToElement(new
            {
                instance = new
                {
                    instanceId = "test-instance-003",
                    appId = "test-app",
                    scope = "",
                    pid = 123,
                    invoke = new { poll = true, respond = true }
                }
            })
        };

        // invoke 缺失
        var missingInvokeRequest = new JsonRpcRequest
        {
            Id = "req-4",
            Method = "hub.apps.registerInstance",
            Params = JsonSerializer.SerializeToElement(new
            {
                instance = new
                {
                    instanceId = "test-instance-004",
                    appId = "test-app",
                    pid = 123
                }
            })
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

        Assert.Equal(-32602, missingInvokeResponse.Error?.Code);
        Assert.Equal("invalid_params", missingInvokeResponse.Error?.Message);
    }

    [Fact]
    public async Task AppInstancesHandler_UnregisterInstance_ShouldBeIdempotent()
    {
        var appRegistry = new AppRegistry(_registryLogger.Object);
        var definitionLoader = new DefinitionLoader(_tempDirectory, _definitionLogger.Object);
        var handler = new AppInstancesHandler(appRegistry, _instancesLogger.Object, definitionLoader);

        var registerRequest = new JsonRpcRequest
        {
            Id = "req-register",
            Method = "hub.apps.registerInstance",
            Params = JsonSerializer.SerializeToElement(new
            {
                instance = new
                {
                    instanceId = "test-instance-unregister",
                    appId = "test-app",
                    scope = (string?)null,
                    pid = 45678,
                    invoke = new { poll = true, respond = true }
                }
            })
        };

        await handler.HandleAsync(registerRequest, CancellationToken.None);

        var unregisterRequest = new JsonRpcRequest
        {
            Id = "req-unregister-1",
            Method = "hub.apps.unregisterInstance",
            Params = JsonSerializer.SerializeToElement(new { instanceId = "test-instance-unregister" })
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
                includeAllScopes = true,
                includeOffline = true
            })
        }, CancellationToken.None);

        Assert.Null(listResponse.Error);
        var listResult = JsonSerializer.SerializeToElement(listResponse.Result);
        var instancesElement = listResult.GetProperty("instances");
        Assert.DoesNotContain(instancesElement.EnumerateArray(), i => i.GetProperty("instanceId").GetString() == "test-instance-unregister");
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
        var fullPath = Path.Combine(_tempDirectory, fileName);
        File.WriteAllText(fullPath, JsonSerializer.Serialize(payload));
    }
}
