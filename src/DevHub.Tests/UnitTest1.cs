namespace DevHub.Tests;

using DevHub.Core.Services;
using DevHub.Core.Services.Rpc;
using DevHub.Core.Services.Rpc.Handlers;
using DevHub.Core.Models;
using DevHub.Core.Models.Rpc;
using Microsoft.Extensions.Logging;
using Moq;

public class UnitTest1
{
    private readonly Mock<ILogger<FileSystemManager>> _mockFsLogger;
    private readonly Mock<ILogger<DefinitionLoader>> _mockDefinitionLogger;
    private readonly Mock<ILogger<AppRegistry>> _mockRegistryLogger;
    private readonly Mock<ILogger<AppDefinitionsHandler>> _mockDefinitionsLogger;

    public UnitTest1()
    {
        _mockFsLogger = new Mock<ILogger<FileSystemManager>>();
        _mockDefinitionLogger = new Mock<ILogger<DefinitionLoader>>();
        _mockRegistryLogger = new Mock<ILogger<AppRegistry>>();
        _mockDefinitionsLogger = new Mock<ILogger<AppDefinitionsHandler>>();
    }

    [Fact]
    public void FileSystemManager_InitializeDirectories_ShouldCreateDirectories()
    {
        // Arrange
        var fileSystemManager = new FileSystemManager(_mockFsLogger.Object);

        // Act - This should not throw an exception
        fileSystemManager.InitializeDirectories();

        // Assert - Verify directories exist
        var rootPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DevHub");
        Assert.True(Directory.Exists(rootPath));
        Assert.True(Directory.Exists(Path.Combine(rootPath, "runtime")));
        Assert.True(Directory.Exists(Path.Combine(rootPath, "apps", "definitions")));
        Assert.True(Directory.Exists(Path.Combine(rootPath, "apps", "instances")));
        Assert.True(Directory.Exists(Path.Combine(rootPath, "logs")));
    }

    [Fact]
    public void FileSystemManager_GetToken_ShouldGenerateValidToken()
    {
        // Arrange
        var fileSystemManager = new FileSystemManager(_mockFsLogger.Object);

        // Act
        var token1 = fileSystemManager.GetToken();
        var token2 = fileSystemManager.GetToken();

        // Assert
        Assert.False(string.IsNullOrEmpty(token1));
        Assert.Equal(token1, token2); // Should return same token second time
    }

    [Fact]
    public void AppRegistry_RegisterInstance_ShouldAddOrUpdateInstance()
    {
        // Arrange
        var appRegistry = new AppRegistry(_mockRegistryLogger.Object);
        var instance = new AppInstance
        {
            InstanceId = "test-instance-1",
            AppId = "test-app-1",
            Scope = null,
            Pid = 1234,
            RegisteredAtUtc = DateTime.UtcNow,
            LastSeenUtc = DateTime.UtcNow
        };

        // Act
        appRegistry.RegisterInstance(instance);
        var instances = appRegistry.ListInstances();

        // Assert
        Assert.Single(instances);
        Assert.Equal("test-instance-1", instances.First().InstanceId);
        Assert.Equal("test-app-1", instances.First().AppId);
    }

    [Fact]
    public void AppRegistry_Heartbeat_ShouldUpdateLastSeen()
    {
        // Arrange
        var appRegistry = new AppRegistry(_mockRegistryLogger.Object);
        var instance = new AppInstance
        {
            InstanceId = "test-instance-2",
            AppId = "test-app-2",
            Scope = null,
            Pid = 5678,
            RegisteredAtUtc = DateTime.UtcNow,
            LastSeenUtc = DateTime.UtcNow.AddSeconds(-10)
        };
        appRegistry.RegisterInstance(instance);

        // Act
        System.Threading.Thread.Sleep(100); // Wait for time to pass
        appRegistry.Heartbeat("test-instance-2");
        var updatedInstance = appRegistry.GetInstance("test-instance-2");

        // Assert
        Assert.NotNull(updatedInstance);
        Assert.True(updatedInstance.LastSeenUtc > instance.LastSeenUtc);
    }

    [Fact]
    public void AppRegistry_ListInstances_ShouldFilterByScope()
    {
        // Arrange
        var appRegistry = new AppRegistry(_mockRegistryLogger.Object);
        var globalInstance = new AppInstance
        {
            InstanceId = "test-instance-3",
            AppId = "test-app-3",
            Scope = null,
            Pid = 9012,
            RegisteredAtUtc = DateTime.UtcNow,
            LastSeenUtc = DateTime.UtcNow
        };
        var scopedInstance = new AppInstance
        {
            InstanceId = "test-instance-4",
            AppId = "test-app-3",
            Scope = "workspace1",
            Pid = 3456,
            RegisteredAtUtc = DateTime.UtcNow,
            LastSeenUtc = DateTime.UtcNow
        };
        appRegistry.RegisterInstance(globalInstance);
        appRegistry.RegisterInstance(scopedInstance);

        // Act
        var globalInstances = appRegistry.ListInstances(appId: "test-app-3", scope: null);
        var scopedInstances = appRegistry.ListInstances(appId: "test-app-3", scope: "workspace1");
        var allInstances = appRegistry.ListInstances(appId: "test-app-3", includeAllScopes: true);

        // Assert
        Assert.Single(globalInstances);
        Assert.Single(scopedInstances);
        Assert.Equal(2, allInstances.Count());
    }

    [Fact]
    public void HubPingHandler_ShouldReturnCorrectResponse()
    {
        // Arrange
        var handler = new HubPingHandler();
        var request = new JsonRpcRequest
        {
            Id = "1",
            Method = "hub.ping",
            Params = new Dictionary<string, object>()
        };

        // Act
        var response = handler.HandleAsync(request, CancellationToken.None).Result;

        // Assert
        Assert.NotNull(response);
        Assert.Equal("1", response.Id);
        Assert.Null(response.Error);
        Assert.NotNull(response.Result);
    }

    [Fact]
    public void AppDefinitionsHandler_MethodNotFound_ShouldReturnError()
    {
        // Arrange
        var mockDefinitionLoader = new Mock<DefinitionLoader>(TestHelpers.GetTestDirectory(), _mockDefinitionLogger.Object);
        var handler = new AppDefinitionsHandler(mockDefinitionLoader.Object, _mockDefinitionsLogger.Object);
        var request = new JsonRpcRequest
        {
            Id = "2",
            Method = "hub.apps.invalidMethod",
            Params = new Dictionary<string, object>()
        };

        // Act
        var response = handler.HandleAsync(request, CancellationToken.None).Result;

        // Assert
        Assert.NotNull(response);
        Assert.Equal("2", response.Id);
        Assert.NotNull(response.Error);
        Assert.Equal(-32601, response.Error.Code); // Method not found
    }
}

public static class TestHelpers
{
    public static string GetTestDirectory()
    {
        var testDirectory = Path.Combine(Path.GetTempPath(), "DevHubTests");
        if (!Directory.Exists(testDirectory))
        {
            Directory.CreateDirectory(testDirectory);
        }
        return testDirectory;
    }

    public static void CleanupTestDirectory()
    {
        var testDirectory = GetTestDirectory();
        if (Directory.Exists(testDirectory))
        {
            Directory.Delete(testDirectory, true);
        }
    }
}