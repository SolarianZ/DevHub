namespace DevHub.Tests;

using DevHub.Core.Services;
using DevHub.Core.Services.Rpc;
using DevHub.Core.Services.Rpc.Handlers;
using DevHub.Core.Models;
using DevHub.Core.Models.Rpc;
using Microsoft.Extensions.Logging;
using Moq;
using FluentAssertions;

public class NegativeTests : IDisposable
{
    private readonly Mock<ILogger<FileSystemManager>> _mockFsLogger;
    private readonly Mock<ILogger<DefinitionLoader>> _mockDefinitionLogger;
    private readonly Mock<ILogger<AppRegistry>> _mockRegistryLogger;
    private readonly Mock<ILogger<AppInstancesHandler>> _mockInstancesLogger;
    private readonly Mock<ILogger<AppDefinitionsHandler>> _mockDefinitionsLogger;
    private readonly string _testDirectory;

    public NegativeTests()
    {
        _mockFsLogger = new Mock<ILogger<FileSystemManager>>();
        _mockDefinitionLogger = new Mock<ILogger<DefinitionLoader>>();
        _mockRegistryLogger = new Mock<ILogger<AppRegistry>>();
        _mockInstancesLogger = new Mock<ILogger<AppInstancesHandler>>();
        _mockDefinitionsLogger = new Mock<ILogger<AppDefinitionsHandler>>();
        _testDirectory = TestHelpers.GetTestDirectory();
    }

    [Fact]
    public void AppRegistry_Heartbeat_NonExistentInstance_ShouldReturnFalse()
    {
        // Arrange
        var appRegistry = new AppRegistry(_mockRegistryLogger.Object);

        // Act
        var result = appRegistry.Heartbeat("non-existent-instance", out _);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public void AppRegistry_Unregister_NonExistentInstance_ShouldReturnFalse()
    {
        // Arrange
        var appRegistry = new AppRegistry(_mockRegistryLogger.Object);

        // Act
        var result = appRegistry.UnregisterInstance("non-existent-instance");

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public void AppRegistry_GetInstance_NonExistentInstance_ShouldReturnNull()
    {
        // Arrange
        var appRegistry = new AppRegistry(_mockRegistryLogger.Object);

        // Act
        var result = appRegistry.GetInstance("non-existent-instance");

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public void DefinitionLoader_GetDefinition_NonExistentAppId_ShouldReturnNull()
    {
        // Arrange
        var definitionLoader = new DefinitionLoader(_testDirectory, _mockDefinitionLogger.Object);

        // Act
        var result = definitionLoader.GetDefinition("non-existent-app-id");

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public void FileSystemManager_InitializeDirectories_ShouldCreateConfiguredDirectories()
    {
        // Arrange
        var runtimeDirectory = Path.Combine(_testDirectory, "runtime");

        using var runtimeScope = new EnvironmentVariableScope("DEVHUB_RUNTIME_DIR", runtimeDirectory);
        var fileSystemManager = new FileSystemManager(_mockFsLogger.Object, _testDirectory);

        // Act
        fileSystemManager.InitializeDirectories();

        // Assert
        Assert.True(Directory.Exists(_testDirectory));
        Assert.True(Directory.Exists(runtimeDirectory));
    }

    [Fact]
    public async Task AppInstancesHandler_RegisterInstance_MissingParams_ShouldReturnError()
    {
        // Arrange
        var appRegistry = new AppRegistry(_mockRegistryLogger.Object);
        var handler = new AppInstancesHandler(appRegistry, _mockInstancesLogger.Object);
        var request = new JsonRpcRequest
        {
            Id = "1",
            Method = "hub.apps.registerInstance",
            Params = new Dictionary<string, object>()
        };

        // Act
        var response = await handler.HandleAsync(request, CancellationToken.None);

        // Assert
        response.Should().NotBeNull();
        response.Id.Should().Be("1");
        response.Error.Should().NotBeNull();
        response.Error.Code.Should().Be(-32602); // Invalid params
    }

    [Fact]
    public async Task AppInstancesHandler_RegisterInstance_InvalidScope_ShouldReturnError()
    {
        // Arrange
        var appRegistry = new AppRegistry(_mockRegistryLogger.Object);
        var handler = new AppInstancesHandler(appRegistry, _mockInstancesLogger.Object);
        var request = new JsonRpcRequest
        {
            Id = "2",
            Method = "hub.apps.registerInstance",
            Params = new Dictionary<string, object>
            {
                { "instance", new Dictionary<string, object>
                    {
                        { "instanceId", "test-instance" },
                        { "appId", "test-app" },
                        { "scope", "global" }
                    }
                }
            }
        };

        // Act
        var response = await handler.HandleAsync(request, CancellationToken.None);

        // Assert
        response.Should().NotBeNull();
        response.Id.Should().Be("2");
        response.Error.Should().NotBeNull();
        response.Error.Code.Should().Be(-32602); // Invalid params
        response.Error.Message.Should().Be("invalid_params");
    }

    [Fact]
    public async Task AppInstancesHandler_ListInstances_InvalidScope_ShouldReturnError()
    {
        // Arrange
        var appRegistry = new AppRegistry(_mockRegistryLogger.Object);
        var handler = new AppInstancesHandler(appRegistry, _mockInstancesLogger.Object);
        var request = new JsonRpcRequest
        {
            Id = "3",
            Method = "hub.apps.listInstances",
            Params = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(@"{""scope"": ""global""}")
        };

        // Act
        var response = await handler.HandleAsync(request, CancellationToken.None);

        // Assert
        response.Should().NotBeNull();
        response.Id.Should().Be("3");
        response.Error.Should().NotBeNull();
        response.Error.Code.Should().Be(-32602); // Invalid params
        response.Error.Message.Should().Be("invalid_params");
    }

    [Fact]
    public async Task AppInstancesHandler_Heartbeat_MissingParams_ShouldReturnError()
    {
        // Arrange
        var appRegistry = new AppRegistry(_mockRegistryLogger.Object);
        var handler = new AppInstancesHandler(appRegistry, _mockInstancesLogger.Object);
        var request = new JsonRpcRequest
        {
            Id = "2",
            Method = "hub.apps.heartbeat",
            Params = new Dictionary<string, object>()
        };

        // Act
        var response = await handler.HandleAsync(request, CancellationToken.None);

        // Assert
        response.Should().NotBeNull();
        response.Id.Should().Be("2");
        response.Error.Should().NotBeNull();
        response.Error.Code.Should().Be(-32602); // Invalid params
    }

    [Fact]
    public async Task AppDefinitionsHandler_GetDefinition_MissingParams_ShouldReturnError()
    {
        // Arrange
        var definitionLoader = new DefinitionLoader(_testDirectory, _mockDefinitionLogger.Object);
        var definitionProvider = new DefinitionProvider(definitionLoader);
        definitionProvider.Refresh();
        var handler = new AppDefinitionsHandler(definitionProvider, _mockDefinitionsLogger.Object);
        var request = new JsonRpcRequest
        {
            Id = "3",
            Method = "hub.apps.getDefinition",
            Params = new Dictionary<string, object>()
        };

        // Act
        var response = await handler.HandleAsync(request, CancellationToken.None);

        // Assert
        response.Should().NotBeNull();
        response.Id.Should().Be("3");
        response.Error.Should().NotBeNull();
        response.Error.Code.Should().Be(-32602); // Invalid params
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, recursive: true);
        }
    }
}

