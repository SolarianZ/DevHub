namespace DevHub.Host.Tests;

using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using DevHub.Core.Services;
using DevHub.Host.Runtime;
using DevHub.Host.Tests.TestHelpers;
using Microsoft.Extensions.Logging;
using Moq;
using static DevHub.Host.Tests.TestHelpers.RuntimeFilePermissionAssertions;

/// <summary>
/// HostRuntimeArtifactManager 基础行为测试。
/// </summary>
[Trait("Category", "Impl")]
public sealed class HostRuntimeArtifactManagerTests
{
    [Fact]
    public void Impl_GetToken_ShouldWriteTokenToConfiguredDataDirectory()
    {
        var testRoot = CreateTestDirectory();
        var runtimeDirectory = Path.Combine(testRoot, "runtime");

        try
        {
            using var dataScope = new EnvironmentVariableScope(RuntimePathOptions.DataDirEnvironmentVariable, testRoot);

            var manager = new HostRuntimeArtifactManager(Mock.Of<ILogger<HostRuntimeArtifactManager>>(), RuntimePathOptions.Resolve());
            var token = manager.GetToken();
            var tokenPath = Path.Combine(runtimeDirectory, "token.txt");

            Assert.False(string.IsNullOrWhiteSpace(token));
            Assert.True(File.Exists(tokenPath));
            AssertCurrentUserOnlyAccess(tokenPath);
        }
        finally
        {
            DeleteDirectoryIfExists(testRoot);
        }
    }

    [Fact]
    public void Impl_GetToken_ShouldGenerateValidToken()
    {
        var testRoot = CreateTestDirectory();

        try
        {
            using var dataScope = new EnvironmentVariableScope(RuntimePathOptions.DataDirEnvironmentVariable, testRoot);

            var manager = new HostRuntimeArtifactManager(Mock.Of<ILogger<HostRuntimeArtifactManager>>(), RuntimePathOptions.Resolve());
            var token1 = manager.GetToken();
            var token2 = manager.GetToken();

            Assert.False(string.IsNullOrWhiteSpace(token1));
            Assert.Equal(token1, token2);
        }
        finally
        {
            DeleteDirectoryIfExists(testRoot);
        }
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void Impl_GetToken_OnWindows_ShouldRemoveExplicitAllowRulesForOtherPrincipals()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var testRoot = CreateTestDirectory();
        var runtimeDirectory = Path.Combine(testRoot, "runtime");

        try
        {
            using var dataScope = new EnvironmentVariableScope(RuntimePathOptions.DataDirEnvironmentVariable, testRoot);

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

            var manager = new HostRuntimeArtifactManager(Mock.Of<ILogger<HostRuntimeArtifactManager>>(), RuntimePathOptions.Resolve());
            var token = manager.GetToken();

            Assert.False(string.IsNullOrWhiteSpace(token));
            AssertCurrentUserOnlyAccess(tokenPath);
        }
        finally
        {
            DeleteDirectoryIfExists(testRoot);
        }
    }

    [Fact]
    public void Impl_GetToken_NewManager_ShouldRotateTokenForNewSession()
    {
        var testRoot = CreateTestDirectory();

        try
        {
            using var dataScope = new EnvironmentVariableScope(RuntimePathOptions.DataDirEnvironmentVariable, testRoot);

            var manager1 = new HostRuntimeArtifactManager(Mock.Of<ILogger<HostRuntimeArtifactManager>>(), RuntimePathOptions.Resolve());
            var manager2 = new HostRuntimeArtifactManager(Mock.Of<ILogger<HostRuntimeArtifactManager>>(), RuntimePathOptions.Resolve());

            var token1 = manager1.GetToken();
            var token2 = manager2.GetToken();

            Assert.False(string.IsNullOrWhiteSpace(token1));
            Assert.False(string.IsNullOrWhiteSpace(token2));
            Assert.NotEqual(token1, token2);
        }
        finally
        {
            DeleteDirectoryIfExists(testRoot);
        }
    }

    [Fact]
    public void Impl_GetToken_WhenAclEnforcementFails_ShouldThrow()
    {
        var testRoot = CreateTestDirectory();

        try
        {
            using var dataScope = new EnvironmentVariableScope(RuntimePathOptions.DataDirEnvironmentVariable, testRoot);

            var manager = new HostRuntimeArtifactManager(
                Mock.Of<ILogger<HostRuntimeArtifactManager>>(),
                RuntimePathOptions.Resolve(),
                RuntimeTuningOptions.Default,
                defaultHubVersion: null,
                _ => throw new UnauthorizedAccessException("acl denied"));

            var exception = Assert.Throws<InvalidOperationException>(() => manager.GetToken());
            Assert.IsType<UnauthorizedAccessException>(exception.InnerException);
        }
        finally
        {
            DeleteDirectoryIfExists(testRoot);
        }
    }

    [Fact]
    public void Impl_GetToken_WhenPublishingToken_ShouldRestrictTempFileBeforeWritingContent()
    {
        var testRoot = CreateTestDirectory();

        try
        {
            using var dataScope = new EnvironmentVariableScope(RuntimePathOptions.DataDirEnvironmentVariable, testRoot);
            var tempRestrictedBeforeContent = false;

            var manager = new HostRuntimeArtifactManager(
                Mock.Of<ILogger<HostRuntimeArtifactManager>>(),
                RuntimePathOptions.Resolve(),
                RuntimeTuningOptions.Default,
                defaultHubVersion: null,
                filePath =>
                {
                    if (Path.GetFileName(filePath).StartsWith(".token.txt.", StringComparison.Ordinal)
                        && File.Exists(filePath)
                        && new FileInfo(filePath).Length == 0)
                    {
                        tempRestrictedBeforeContent = true;
                    }
                });

            _ = manager.GetToken();

            Assert.True(tempRestrictedBeforeContent);
        }
        finally
        {
            DeleteDirectoryIfExists(testRoot);
        }
    }

    [Fact]
    public void Impl_WriteHubJson_ShouldWriteSpecCompliantRuntimeFile()
    {
        var testRoot = CreateTestDirectory();
        var runtimeDirectory = Path.Combine(testRoot, "runtime");

        try
        {
            using var dataScope = new EnvironmentVariableScope(RuntimePathOptions.DataDirEnvironmentVariable, testRoot);

            var manager = new HostRuntimeArtifactManager(Mock.Of<ILogger<HostRuntimeArtifactManager>>(), RuntimePathOptions.Resolve());
            _ = manager.GetToken();
            manager.WriteHubJson(47231, "test-hub");

            var hubJsonPath = Path.Combine(runtimeDirectory, "hub.json");
            Assert.True(File.Exists(hubJsonPath));
            Assert.False(File.Exists(hubJsonPath + ".tmp"));

            var hubJson = JsonDocument.Parse(File.ReadAllText(hubJsonPath)).RootElement;
            Assert.Equal(1, hubJson.GetProperty("protocolVersion").GetInt32());
            Assert.Equal("test-hub", hubJson.GetProperty("hubVersion").GetString());
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

            AssertCurrentUserOnlyAccess(hubJsonPath);
            AssertCurrentUserOnlyAccess(tokenFile!);
        }
        finally
        {
            DeleteDirectoryIfExists(testRoot);
        }
    }

    [Fact]
    public void Impl_WriteHubJson_WhenOverwritten_ShouldKeepSingleRuntimeFile()
    {
        var testRoot = CreateTestDirectory();
        var runtimeDirectory = Path.Combine(testRoot, "runtime");

        try
        {
            using var dataScope = new EnvironmentVariableScope(RuntimePathOptions.DataDirEnvironmentVariable, testRoot);

            var manager = new HostRuntimeArtifactManager(Mock.Of<ILogger<HostRuntimeArtifactManager>>(), RuntimePathOptions.Resolve());
            _ = manager.GetToken();

            manager.WriteHubJson(48001, "v1");
            manager.WriteHubJson(48002, "v2");

            var hubJsonPath = Path.Combine(runtimeDirectory, "hub.json");
            Assert.True(File.Exists(hubJsonPath));
            Assert.False(File.Exists(hubJsonPath + ".tmp"));
            Assert.Empty(Directory.EnumerateFiles(runtimeDirectory, ".hub.json.*.tmp"));

            var hubJson = JsonDocument.Parse(File.ReadAllText(hubJsonPath)).RootElement;
            Assert.Equal("v2", hubJson.GetProperty("hubVersion").GetString());
            Assert.Equal("http://127.0.0.1:48002", hubJson.GetProperty("httpBaseUrl").GetString());
            Assert.Equal("ws://127.0.0.1:48002/ws", hubJson.GetProperty("wsUrl").GetString());
            AssertCurrentUserOnlyAccess(hubJsonPath);
        }
        finally
        {
            DeleteDirectoryIfExists(testRoot);
        }
    }

    [Fact]
    public void Impl_WriteHubJson_WhenPublishingRuntimeFile_ShouldRestrictTempFileBeforeWritingContent()
    {
        var testRoot = CreateTestDirectory();

        try
        {
            using var dataScope = new EnvironmentVariableScope(RuntimePathOptions.DataDirEnvironmentVariable, testRoot);
            var tempRestrictedBeforeContent = false;

            var manager = new HostRuntimeArtifactManager(
                Mock.Of<ILogger<HostRuntimeArtifactManager>>(),
                RuntimePathOptions.Resolve(),
                RuntimeTuningOptions.Default,
                defaultHubVersion: null,
                filePath =>
                {
                    if (Path.GetFileName(filePath).StartsWith(".hub.json.", StringComparison.Ordinal)
                        && File.Exists(filePath)
                        && new FileInfo(filePath).Length == 0)
                    {
                        tempRestrictedBeforeContent = true;
                    }
                });

            manager.WriteHubJson(49003, "runtime-publication");

            Assert.True(tempRestrictedBeforeContent);
        }
        finally
        {
            DeleteDirectoryIfExists(testRoot);
        }
    }

    [Fact]
    public void Impl_WriteHubJson_WhenRuntimeTuningOverridden_ShouldPersistEffectiveRuntimeTuning()
    {
        var testRoot = CreateTestDirectory();
        var runtimeDirectory = Path.Combine(testRoot, "runtime");

        try
        {
            using var dataScope = new EnvironmentVariableScope(RuntimePathOptions.DataDirEnvironmentVariable, testRoot);
            using var leaseScope = new EnvironmentVariableScope(RuntimeTuningOptions.LeaseSecondsEnvironmentVariable, "45");
            using var onlineScope = new EnvironmentVariableScope(RuntimeTuningOptions.OnlineThresholdSecondsEnvironmentVariable, "20");
            using var dedupeScope = new EnvironmentVariableScope(RuntimeTuningOptions.LaunchDedupeWindowSecondsEnvironmentVariable, "55");

            var tuningOptions = RuntimeTuningOptions.Resolve(Mock.Of<ILogger<RuntimeTuningOptions>>());
            var manager = new HostRuntimeArtifactManager(
                Mock.Of<ILogger<HostRuntimeArtifactManager>>(),
                RuntimePathOptions.Resolve(),
                tuningOptions);

            _ = manager.GetToken();
            manager.WriteHubJson(49001, "runtime-override");

            var hubJsonPath = Path.Combine(runtimeDirectory, "hub.json");
            var hubJson = JsonDocument.Parse(File.ReadAllText(hubJsonPath)).RootElement;
            var runtimeTuning = hubJson.GetProperty("runtimeTuning");
            Assert.Equal(45, runtimeTuning.GetProperty("leaseSeconds").GetInt32());
            Assert.Equal(20, runtimeTuning.GetProperty("onlineThresholdSeconds").GetInt32());
            Assert.Equal(55, runtimeTuning.GetProperty("launchDedupeWindowSeconds").GetInt32());
        }
        finally
        {
            DeleteDirectoryIfExists(testRoot);
        }
    }

    [Fact]
    public void Impl_WriteHubJson_WhenAclEnforcementFails_ShouldThrow()
    {
        var testRoot = CreateTestDirectory();

        try
        {
            using var dataScope = new EnvironmentVariableScope(RuntimePathOptions.DataDirEnvironmentVariable, testRoot);

            var manager = new HostRuntimeArtifactManager(
                Mock.Of<ILogger<HostRuntimeArtifactManager>>(),
                RuntimePathOptions.Resolve(),
                RuntimeTuningOptions.Default,
                defaultHubVersion: null,
                filePath =>
                {
                    if (string.Equals(Path.GetFileName(filePath), "hub.json", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new UnauthorizedAccessException("acl denied");
                    }
                });

            var exception = Assert.Throws<InvalidOperationException>(() => manager.WriteHubJson(49002, "acl-failure"));
            Assert.IsType<UnauthorizedAccessException>(exception.InnerException);
        }
        finally
        {
            DeleteDirectoryIfExists(testRoot);
        }
    }

    private static string CreateTestDirectory()
    {
        var testDirectory = Path.Combine(Path.GetTempPath(), "DevHubHostTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testDirectory);
        return testDirectory;
    }

    private static void DeleteDirectoryIfExists(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch
        {
        }
    }

}
