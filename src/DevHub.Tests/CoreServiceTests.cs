namespace DevHub.Tests;

using DevHub.Core.Services;
using DevHub.Core.Services.Rpc;
using DevHub.Core.Services.Rpc.Handlers;
using DevHub.Core.Models;
using DevHub.Core.Models.Rpc;
using Microsoft.Extensions.Logging;
using Moq;

public class CoreServiceTests
{
    private readonly Mock<ILogger<FileSystemManager>> _mockFsLogger;
    private readonly Mock<ILogger<DefinitionLoader>> _mockDefinitionLogger;
    private readonly Mock<ILogger<AppRegistry>> _mockRegistryLogger;
    private readonly Mock<ILogger<HubPingHandler>> _mockHubPingLogger;
    private readonly Mock<ILogger<AppDefinitionsHandler>> _mockDefinitionsHandlerLogger;

    public CoreServiceTests()
    {
        _mockFsLogger = new Mock<ILogger<FileSystemManager>>();
        _mockDefinitionLogger = new Mock<ILogger<DefinitionLoader>>();
        _mockRegistryLogger = new Mock<ILogger<AppRegistry>>();
        _mockHubPingLogger = new Mock<ILogger<HubPingHandler>>();
        _mockDefinitionsHandlerLogger = new Mock<ILogger<AppDefinitionsHandler>>();
    }

    [Fact]
    public void FileSystemManager_GetToken_ShouldWriteTokenToRuntimeOverrideDirectory()
    {
        var testRoot = TestHelpers.GetTestDirectory();
        var runtimeDirectory = Path.Combine(testRoot, "runtime");

        try
        {
            using var runtimeScope = new EnvironmentVariableScope("DEVHUB_RUNTIME_DIR", runtimeDirectory);

            var fileSystemManager = new FileSystemManager(_mockFsLogger.Object, testRoot);
            var token = fileSystemManager.GetToken();

            Assert.False(string.IsNullOrWhiteSpace(token));
            Assert.True(File.Exists(Path.Combine(runtimeDirectory, "token.txt")));
        }
        finally
        {
            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void FileSystemManager_GetToken_ShouldGenerateValidToken()
    {
        var testRoot = TestHelpers.GetTestDirectory();
        var runtimeDirectory = Path.Combine(testRoot, "runtime");

        try
        {
            using var runtimeScope = new EnvironmentVariableScope("DEVHUB_RUNTIME_DIR", runtimeDirectory);

            var fileSystemManager = new FileSystemManager(_mockFsLogger.Object, testRoot);
            var token1 = fileSystemManager.GetToken();
            var token2 = fileSystemManager.GetToken();

            Assert.False(string.IsNullOrWhiteSpace(token1));
            Assert.Equal(token1, token2);
        }
        finally
        {
            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void FileSystemManager_GetToken_NewManager_ShouldRotateTokenForNewSession()
    {
        var testRoot = TestHelpers.GetTestDirectory();
        var runtimeDirectory = Path.Combine(testRoot, "runtime");

        try
        {
            using var runtimeScope = new EnvironmentVariableScope("DEVHUB_RUNTIME_DIR", runtimeDirectory);

            var fileSystemManager1 = new FileSystemManager(_mockFsLogger.Object, testRoot);
            var fileSystemManager2 = new FileSystemManager(_mockFsLogger.Object, testRoot);

            var token1 = fileSystemManager1.GetToken();
            var token2 = fileSystemManager2.GetToken();

            Assert.False(string.IsNullOrWhiteSpace(token1));
            Assert.False(string.IsNullOrWhiteSpace(token2));
            Assert.NotEqual(token1, token2);
        }
        finally
        {
            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, recursive: true);
            }
        }
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

        var beforeHeartbeat = instance.LastSeenUtc;

        appRegistry.Heartbeat("test-instance-2", out _);
        var updatedInstance = appRegistry.GetInstance("test-instance-2");

        // Assert
        Assert.NotNull(updatedInstance);
        Assert.True(updatedInstance.LastSeenUtc > beforeHeartbeat);
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
    public async Task HubPingHandler_ShouldReturnCorrectResponse()
    {
        // Arrange
        var handler = new HubPingHandler(_mockHubPingLogger.Object);
        var request = new JsonRpcRequest
        {
            Id = "1",
            Method = "hub.ping",
            Params = new Dictionary<string, object>()
        };

        // Act
        var response = await handler.HandleAsync(request, CancellationToken.None);

        // Assert
        Assert.NotNull(response);
        Assert.Equal("1", response.Id);
        Assert.Null(response.Error);
        Assert.NotNull(response.Result);
    }

    [Fact]
    public async Task AppDefinitionsHandler_MethodNotFound_ShouldReturnError()
    {
        // Arrange
        var testDirectory = TestHelpers.GetTestDirectory();

        try
        {
            var mockDefinitionLoader = new Mock<DefinitionLoader>(testDirectory, _mockDefinitionLogger.Object);
            var handler = new AppDefinitionsHandler(mockDefinitionLoader.Object, _mockDefinitionsHandlerLogger.Object);
            var request = new JsonRpcRequest
            {
                Id = "2",
                Method = "hub.apps.invalidMethod",
                Params = new Dictionary<string, object>()
            };

            // Act
            var response = await handler.HandleAsync(request, CancellationToken.None);

            // Assert
            Assert.NotNull(response);
            Assert.Equal("2", response.Id);
            Assert.NotNull(response.Error);
            Assert.Equal(-32601, response.Error.Code); // Method not found
        }
        finally
        {
            if (Directory.Exists(testDirectory))
            {
                Directory.Delete(testDirectory, recursive: true);
            }
        }
    }
}

public static class TestHelpers
{
    public static string GetTestDirectory()
    {
        var testDirectory = Path.Combine(Path.GetTempPath(), "DevHubTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testDirectory);
        return testDirectory;
    }
}
