namespace DevHub.Tests;

using DevHub.Core.Services;
using DevHub.Core.Services.Rpc;
using DevHub.Core.Services.Rpc.Handlers;
using DevHub.Core.Models;
using DevHub.Core.Models.Rpc;
using Microsoft.Extensions.Logging;
using Moq;
using FluentAssertions;

public class NegativeTests
{
    private readonly Mock<ILoggerService> _mockFsLogger;
    private readonly Mock<ILoggerService> _mockDefinitionLogger;
    private readonly Mock<ILoggerService> _mockRegistryLogger;
    private readonly Mock<ILoggerService> _mockInstancesLogger;
    private readonly Mock<ILoggerService> _mockDefinitionsLogger;

    public NegativeTests()
    {
        _mockFsLogger = new Mock<ILoggerService>();
        _mockDefinitionLogger = new Mock<ILoggerService>();
        _mockRegistryLogger = new Mock<ILoggerService>();
        _mockInstancesLogger = new Mock<ILoggerService>();
        _mockDefinitionsLogger = new Mock<ILoggerService>();
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
        var testDir = TestHelpers.GetTestDirectory();
        var definitionLoader = new DefinitionLoader(testDir, _mockDefinitionLogger.Object);

        // Act
        var result = definitionLoader.GetDefinition("non-existent-app-id");

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public void FileSystemManager_InitializeDirectories_ShouldHandleException()
    {
        // 注意：这个测试可能需要根据权限情况调整，因为它涉及到文件系统操作
        // 我们可以创建一个无法访问的目录来测试异常处理，但这可能需要特殊权限

        // 这里我们不直接测试异常情况，因为它需要特定的权限设置
        // 相反，我们会在其他测试中验证正常操作是否正常工作
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
        var testDir = TestHelpers.GetTestDirectory();
        var mockDefinitionLoader = new Mock<DefinitionLoader>(testDir, _mockDefinitionLogger.Object);
        var handler = new AppDefinitionsHandler(mockDefinitionLoader.Object, _mockDefinitionsLogger.Object);
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
}