namespace DevHub.Tests;

using System.Text.Json;
using DevHub.Core.Services;
using DevHub.Core.Services.Abstractions;
using DevHub.Core.Services.Rpc;
using DevHub.Core.Services.Rpc.Handlers;
using DevHub.Core.Models;
using DevHub.Core.Models.Rpc;
using Microsoft.Extensions.Logging;
using Moq;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

[Trait("Category", "Impl")]
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
    public void Impl_FileSystemManager_GetToken_ShouldWriteTokenToRuntimeOverrideDirectory()
    {
        var testRoot = TestHelpers.GetTestDirectory();
        var runtimeDirectory = Path.Combine(testRoot, "runtime");

        try
        {
            using var runtimeScope = new EnvironmentVariableScope("DEVHUB_RUNTIME_DIR", runtimeDirectory);

            var fileSystemManager = new FileSystemManager(_mockFsLogger.Object, RuntimePathOptions.Resolve(testRoot));
            var token = fileSystemManager.GetToken();
            var tokenPath = Path.Combine(runtimeDirectory, "token.txt");

            Assert.False(string.IsNullOrWhiteSpace(token));
            Assert.True(File.Exists(tokenPath));
            AssertUnixUserOnlyMode(tokenPath);
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
    public void Impl_FileSystemManager_GetToken_ShouldGenerateValidToken()
    {
        var testRoot = TestHelpers.GetTestDirectory();
        var runtimeDirectory = Path.Combine(testRoot, "runtime");

        try
        {
            using var runtimeScope = new EnvironmentVariableScope("DEVHUB_RUNTIME_DIR", runtimeDirectory);

            var fileSystemManager = new FileSystemManager(_mockFsLogger.Object, RuntimePathOptions.Resolve(testRoot));
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
    [SupportedOSPlatform("windows")]
    public void Impl_FileSystemManager_GetToken_OnWindows_ShouldRemoveExplicitAllowRulesForOtherPrincipals()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var testRoot = TestHelpers.GetTestDirectory();
        var runtimeDirectory = Path.Combine(testRoot, "runtime");

        try
        {
            using var runtimeScope = new EnvironmentVariableScope("DEVHUB_RUNTIME_DIR", runtimeDirectory);

            Directory.CreateDirectory(runtimeDirectory);
            var tokenPath = Path.Combine(runtimeDirectory, "token.txt");
            File.WriteAllText(tokenPath, "legacy-token");

            var currentUserSid = WindowsIdentity.GetCurrent().User;
            Assert.NotNull(currentUserSid);

            var administratorsSid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            var tokenFile = new FileInfo(tokenPath);
            var security = tokenFile.GetAccessControl(AccessControlSections.Access);
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(currentUserSid!, FileSystemRights.FullControl, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(administratorsSid, FileSystemRights.Read, AccessControlType.Allow));
            tokenFile.SetAccessControl(security);

            var fileSystemManager = new FileSystemManager(_mockFsLogger.Object, RuntimePathOptions.Resolve(testRoot));
            var token = fileSystemManager.GetToken();

            Assert.False(string.IsNullOrWhiteSpace(token));
            AssertWindowsUserOnlyAcl(tokenPath);
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
    public void Impl_FileSystemManager_GetToken_NewManager_ShouldRotateTokenForNewSession()
    {
        var testRoot = TestHelpers.GetTestDirectory();
        var runtimeDirectory = Path.Combine(testRoot, "runtime");

        try
        {
            using var runtimeScope = new EnvironmentVariableScope("DEVHUB_RUNTIME_DIR", runtimeDirectory);

            var fileSystemManager1 = new FileSystemManager(_mockFsLogger.Object, RuntimePathOptions.Resolve(testRoot));
            var fileSystemManager2 = new FileSystemManager(_mockFsLogger.Object, RuntimePathOptions.Resolve(testRoot));

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
    public void Impl_FileSystemManager_WriteHubJson_ShouldWriteSpecCompliantRuntimeFile()
    {
        var testRoot = TestHelpers.GetTestDirectory();
        var runtimeDirectory = Path.Combine(testRoot, "runtime");

        try
        {
            using var runtimeScope = new EnvironmentVariableScope("DEVHUB_RUNTIME_DIR", runtimeDirectory);

            var fileSystemManager = new FileSystemManager(_mockFsLogger.Object, RuntimePathOptions.Resolve(testRoot));
            _ = fileSystemManager.GetToken();
            fileSystemManager.WriteHubJson(47231, "test-hub");

            var hubJsonPath = Path.Combine(runtimeDirectory, "hub.json");
            Assert.True(File.Exists(hubJsonPath));
            Assert.False(File.Exists(hubJsonPath + ".tmp"));

            var hubJson = JsonDocument.Parse(File.ReadAllText(hubJsonPath)).RootElement;
            Assert.Equal(1, hubJson.GetProperty("protocolVersion").GetInt32());
            Assert.Equal("http://127.0.0.1:47231", hubJson.GetProperty("httpBaseUrl").GetString());
            Assert.Equal("ws://127.0.0.1:47231/ws", hubJson.GetProperty("wsUrl").GetString());

            var tokenFile = hubJson.GetProperty("tokenFile").GetString();
            Assert.False(string.IsNullOrWhiteSpace(tokenFile));
            Assert.True(Path.IsPathFullyQualified(tokenFile!));
            Assert.Equal(Path.Combine(runtimeDirectory, "token.txt"), tokenFile);

            var runtimeTuning = hubJson.GetProperty("runtimeTuning");
            Assert.Equal(RuntimeTuningOptions.DefaultLeaseSeconds, runtimeTuning.GetProperty("leaseSeconds").GetInt32());
            Assert.Equal(RuntimeTuningOptions.DefaultOnlineThresholdSeconds, runtimeTuning.GetProperty("onlineThresholdSeconds").GetInt32());
            Assert.Equal(RuntimeTuningOptions.DefaultLaunchDedupeWindowSeconds, runtimeTuning.GetProperty("launchDedupeWindowSeconds").GetInt32());

            AssertUnixUserOnlyMode(hubJsonPath);
            AssertUnixUserOnlyMode(tokenFile!);
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
    public void Impl_FileSystemManager_WriteHubJson_WhenOverwritten_ShouldKeepSingleRuntimeFile()
    {
        var testRoot = TestHelpers.GetTestDirectory();
        var runtimeDirectory = Path.Combine(testRoot, "runtime");

        try
        {
            using var runtimeScope = new EnvironmentVariableScope("DEVHUB_RUNTIME_DIR", runtimeDirectory);

            var fileSystemManager = new FileSystemManager(_mockFsLogger.Object, RuntimePathOptions.Resolve(testRoot));
            _ = fileSystemManager.GetToken();

            fileSystemManager.WriteHubJson(48001, "v1");
            fileSystemManager.WriteHubJson(48002, "v2");

            var hubJsonPath = Path.Combine(runtimeDirectory, "hub.json");
            Assert.True(File.Exists(hubJsonPath));
            Assert.False(File.Exists(hubJsonPath + ".tmp"));

            var hubJson = JsonDocument.Parse(File.ReadAllText(hubJsonPath)).RootElement;
            Assert.Equal("http://127.0.0.1:48002", hubJson.GetProperty("httpBaseUrl").GetString());
            Assert.Equal("ws://127.0.0.1:48002/ws", hubJson.GetProperty("wsUrl").GetString());
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
    public void Impl_RuntimeTuningOptions_Resolve_WhenEnvironmentValuesAreValid_ShouldApplyOverridesToHubRuntime()
    {
        var testRoot = TestHelpers.GetTestDirectory();
        var runtimeDirectory = Path.Combine(testRoot, "runtime");

        try
        {
            using var runtimeScope = new EnvironmentVariableScope("DEVHUB_RUNTIME_DIR", runtimeDirectory);
            using var leaseScope = new EnvironmentVariableScope(RuntimeTuningOptions.LeaseSecondsEnvironmentVariable, "45");
            using var onlineScope = new EnvironmentVariableScope(RuntimeTuningOptions.OnlineThresholdSecondsEnvironmentVariable, "20");
            using var dedupeScope = new EnvironmentVariableScope(RuntimeTuningOptions.LaunchDedupeWindowSecondsEnvironmentVariable, "55");

            var tuningOptions = RuntimeTuningOptions.Resolve(Mock.Of<ILogger<RuntimeTuningOptions>>());
            Assert.Equal(45, tuningOptions.LeaseSeconds);
            Assert.Equal(20, tuningOptions.OnlineThresholdSeconds);
            Assert.Equal(55, tuningOptions.LaunchDedupeWindowSeconds);

            var fileSystemManager = new FileSystemManager(_mockFsLogger.Object, RuntimePathOptions.Resolve(testRoot), tuningOptions);
            _ = fileSystemManager.GetToken();
            fileSystemManager.WriteHubJson(49001, "runtime-override");

            var hubJsonPath = Path.Combine(runtimeDirectory, "hub.json");
            var hubJson = JsonDocument.Parse(File.ReadAllText(hubJsonPath)).RootElement;
            var runtimeTuning = hubJson.GetProperty("runtimeTuning");
            Assert.Equal(45, runtimeTuning.GetProperty("leaseSeconds").GetInt32());
            Assert.Equal(20, runtimeTuning.GetProperty("onlineThresholdSeconds").GetInt32());
            Assert.Equal(55, runtimeTuning.GetProperty("launchDedupeWindowSeconds").GetInt32());
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
    public void Impl_RuntimeTuningOptions_Resolve_WhenEnvironmentValuesAreInvalid_ShouldFallbackToDefaults()
    {
        using var leaseScope = new EnvironmentVariableScope(RuntimeTuningOptions.LeaseSecondsEnvironmentVariable, "0");
        using var onlineScope = new EnvironmentVariableScope(RuntimeTuningOptions.OnlineThresholdSecondsEnvironmentVariable, "-1");
        using var dedupeScope = new EnvironmentVariableScope(RuntimeTuningOptions.LaunchDedupeWindowSecondsEnvironmentVariable, "abc");

        var tuningOptions = RuntimeTuningOptions.Resolve(Mock.Of<ILogger<RuntimeTuningOptions>>());

        Assert.Equal(RuntimeTuningOptions.DefaultLeaseSeconds, tuningOptions.LeaseSeconds);
        Assert.Equal(RuntimeTuningOptions.DefaultOnlineThresholdSeconds, tuningOptions.OnlineThresholdSeconds);
        Assert.Equal(RuntimeTuningOptions.DefaultLaunchDedupeWindowSeconds, tuningOptions.LaunchDedupeWindowSeconds);
    }

    [Fact]
    public void Impl_AppRegistry_ListInstances_WhenOnlineThresholdOverridden_ShouldUseConfiguredThreshold()
    {
        using var onlineScope = new EnvironmentVariableScope(RuntimeTuningOptions.OnlineThresholdSecondsEnvironmentVariable, "2");
        var tuningOptions = RuntimeTuningOptions.Resolve(Mock.Of<ILogger<RuntimeTuningOptions>>());

        var now = DateTime.UtcNow;
        var clock = new Mock<IClock>();
        clock.SetupGet(c => c.UtcNow).Returns(() => now);

        var appRegistry = new AppRegistry(clock.Object, _mockRegistryLogger.Object, tuningOptions);
        appRegistry.RegisterInstance(new AppInstance
        {
            InstanceId = "online-threshold-instance",
            AppId = "online-threshold-app",
            Scope = null,
            Pid = 14001,
            Invoke = new InvokeCapability { Poll = true, Respond = true }
        });

        now = now.AddSeconds(1);
        var onlineInstances = appRegistry.ListInstances("online-threshold-app", includeAllScopes: true, includeOffline: false).ToList();
        Assert.Single(onlineInstances);

        now = now.AddSeconds(2);
        var offlineInstances = appRegistry.ListInstances("online-threshold-app", includeAllScopes: true, includeOffline: false).ToList();
        Assert.Empty(offlineInstances);
    }

    [Fact]
    public void Impl_AppRegistry_RegisterInstance_ShouldAddOrUpdateInstance()
    {
        // Arrange
        var appRegistry = new AppRegistry(new SystemClock(), _mockRegistryLogger.Object);
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
    public void Impl_AppRegistry_Heartbeat_ShouldUpdateLastSeen()
    {
        // Arrange
        var appRegistry = new AppRegistry(new SystemClock(), _mockRegistryLogger.Object);
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
    public void Impl_AppRegistry_ListInstances_ShouldFilterByScope()
    {
        // Arrange
        var appRegistry = new AppRegistry(new SystemClock(), _mockRegistryLogger.Object);
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
    public async Task Impl_HubPingHandler_ShouldReturnCorrectResponse()
    {
        // Arrange
        var handler = new HubPingHandler(new SystemClock(), _mockHubPingLogger.Object);
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

        var result = JsonSerializer.SerializeToElement(response.Result);
        Assert.True(result.GetProperty("ok").GetBoolean());

        var serverTimeUtc = result.GetProperty("serverTimeUtc").GetString();
        Assert.False(string.IsNullOrWhiteSpace(serverTimeUtc));
        Assert.True(DateTime.TryParse(serverTimeUtc, out _));
    }

    [Fact]
    public async Task Impl_AppDefinitionsHandler_MethodNotFound_ShouldReturnError()
    {
        // Arrange
        var testDirectory = TestHelpers.GetTestDirectory();

        try
        {
            var definitionLoader = new DefinitionLoader(testDirectory, _mockDefinitionLogger.Object);
            var definitionProvider = new DefinitionProvider(definitionLoader);
            definitionProvider.Refresh();
            var handler = new AppDefinitionsHandler(definitionProvider, _mockDefinitionsHandlerLogger.Object);
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

    private static void AssertUnixUserOnlyMode(string filePath)
    {
        if (OperatingSystem.IsWindows())
        {
            AssertWindowsUserOnlyAcl(filePath);
            return;
        }

        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return;
        }

        var mode = File.GetUnixFileMode(filePath);
        var effective = mode &
            (UnixFileMode.UserRead
            | UnixFileMode.UserWrite
            | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead
            | UnixFileMode.GroupWrite
            | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead
            | UnixFileMode.OtherWrite
            | UnixFileMode.OtherExecute);

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, effective);
    }

    [SupportedOSPlatform("windows")]
    private static void AssertWindowsUserOnlyAcl(string filePath)
    {
        var security = new FileInfo(filePath).GetAccessControl(AccessControlSections.Access);
        var rules = security
            .GetAccessRules(true, true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .Where(rule => rule.AccessControlType == AccessControlType.Allow)
            .ToList();

        var currentUserSid = WindowsIdentity.GetCurrent().User;
        Assert.NotNull(currentUserSid);
        Assert.Contains(rules, rule => Equals(rule.IdentityReference, currentUserSid));
        Assert.DoesNotContain(rules, rule => !Equals(rule.IdentityReference, currentUserSid));
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



