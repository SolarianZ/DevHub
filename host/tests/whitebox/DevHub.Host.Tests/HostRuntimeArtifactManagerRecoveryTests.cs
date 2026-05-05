namespace DevHub.Host.Tests;

using System.Text.Json;
using DevHub.Core.Services;
using DevHub.Host.Runtime;
using DevHub.Host.Tests.TestHelpers;
using Microsoft.Extensions.Logging;
using Moq;
using static DevHub.Host.Tests.TestHelpers.RuntimeFilePermissionAssertions;

/// <summary>
/// HostRuntimeArtifactManager 恢复与自愈行为测试。
/// </summary>
[Trait("Category", "Impl")]
public sealed class HostRuntimeArtifactManagerRecoveryTests : IDisposable
{
    private const string DefaultHubVersion = "test-host-version";
    private readonly string _tempDirectory;
    private readonly string _runtimeDirectory;
    private readonly string _definitionsDirectory;
    private readonly string _logsDirectory;

    /// <summary>
    /// 初始化测试上下文。
    /// </summary>
    public HostRuntimeArtifactManagerRecoveryTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "DevHubFileSystemManagerRecoveryTests", Guid.NewGuid().ToString("N"));
        _runtimeDirectory = Path.Combine(_tempDirectory, "runtime");
        _definitionsDirectory = Path.Combine(_tempDirectory, "apps", "definitions");
        _logsDirectory = Path.Combine(_tempDirectory, "logs");

        Directory.CreateDirectory(_tempDirectory);
        Directory.CreateDirectory(_runtimeDirectory);
        Directory.CreateDirectory(_definitionsDirectory);
        Directory.CreateDirectory(_logsDirectory);
    }

    [Fact]
    public void Impl_GetToken_WhenHistoricalTokenExists_ShouldRotateForNewSession()
    {
        using var dataScope = new EnvironmentVariableScope(RuntimePathOptions.DataDirEnvironmentVariable, _tempDirectory);

        var tokenPath = Path.Combine(_runtimeDirectory, "token.txt");
        File.WriteAllText(tokenPath, "legacy-token");

        var manager = new HostRuntimeArtifactManager(Mock.Of<ILogger<HostRuntimeArtifactManager>>(), RuntimePathOptions.Resolve());
        var newToken = manager.GetToken();

        Assert.NotEqual("legacy-token", newToken);
        Assert.Equal(newToken, File.ReadAllText(tokenPath));
    }

    [Fact]
    public void Impl_EnsureRuntimeArtifacts_WhenTokenDeleted_ShouldRestoreCurrentSessionToken()
    {
        using var dataScope = new EnvironmentVariableScope(RuntimePathOptions.DataDirEnvironmentVariable, _tempDirectory);

        var manager = new HostRuntimeArtifactManager(Mock.Of<ILogger<HostRuntimeArtifactManager>>(), RuntimePathOptions.Resolve());
        var token = manager.GetToken();

        var tokenPath = Path.Combine(_runtimeDirectory, "token.txt");
        File.Delete(tokenPath);
        Assert.False(File.Exists(tokenPath));

        manager.EnsureRuntimeArtifacts();

        Assert.True(File.Exists(tokenPath));
        Assert.Equal(token, File.ReadAllText(tokenPath));
        AssertCurrentUserOnlyAccess(tokenPath);
    }

    [Fact]
    public void Impl_EnsureRuntimeArtifacts_WhenHubJsonMissingAndPortProvided_ShouldRebuildHubRuntimeFile()
    {
        using var dataScope = new EnvironmentVariableScope(RuntimePathOptions.DataDirEnvironmentVariable, _tempDirectory);

        var manager = new HostRuntimeArtifactManager(
            Mock.Of<ILogger<HostRuntimeArtifactManager>>(),
            RuntimePathOptions.Resolve(),
            RuntimeTuningOptions.Default,
            DefaultHubVersion);
        _ = manager.GetToken();

        var hubJsonPath = Path.Combine(_runtimeDirectory, "hub.json");
        if (File.Exists(hubJsonPath))
        {
            File.Delete(hubJsonPath);
        }

        manager.EnsureRuntimeArtifacts(port: 47999);

        Assert.True(File.Exists(hubJsonPath));
        using var document = JsonDocument.Parse(File.ReadAllText(hubJsonPath));
        Assert.Equal(DefaultHubVersion, document.RootElement.GetProperty("hubVersion").GetString());
        Assert.Equal("http://127.0.0.1:47999", document.RootElement.GetProperty("httpBaseUrl").GetString());
        Assert.Equal("ws://127.0.0.1:47999/ws", document.RootElement.GetProperty("wsUrl").GetString());
        AssertCurrentUserOnlyAccess(hubJsonPath);
    }

    [Fact]
    public void Impl_EnsureRuntimeArtifacts_WhenHubJsonExists_ShouldPreserveRuntimeFile()
    {
        using var dataScope = new EnvironmentVariableScope(RuntimePathOptions.DataDirEnvironmentVariable, _tempDirectory);

        var manager = new HostRuntimeArtifactManager(Mock.Of<ILogger<HostRuntimeArtifactManager>>(), RuntimePathOptions.Resolve());
        _ = manager.GetToken();
        manager.WriteHubJson(48000, "v1");

        var hubJsonPath = Path.Combine(_runtimeDirectory, "hub.json");
        var before = File.ReadAllText(hubJsonPath);

        manager.EnsureRuntimeArtifacts();

        var after = File.ReadAllText(hubJsonPath);
        Assert.Equal(before, after);
    }

    [Fact]
    public void Impl_EnsureRuntimeArtifacts_WhenHubJsonRebuilt_ShouldPreserveSessionStartedAtUtc()
    {
        using var dataScope = new EnvironmentVariableScope(RuntimePathOptions.DataDirEnvironmentVariable, _tempDirectory);

        var manager = new HostRuntimeArtifactManager(Mock.Of<ILogger<HostRuntimeArtifactManager>>(), RuntimePathOptions.Resolve());
        _ = manager.GetToken();
        manager.WriteHubJson(48030, "v1");

        var hubJsonPath = Path.Combine(_runtimeDirectory, "hub.json");
        using var beforeDocument = JsonDocument.Parse(File.ReadAllText(hubJsonPath));
        var beforeStartedAtUtc = beforeDocument.RootElement.GetProperty("startedAtUtc").GetString();

        File.Delete(hubJsonPath);

        manager.EnsureRuntimeArtifacts(port: 48031);

        using var afterDocument = JsonDocument.Parse(File.ReadAllText(hubJsonPath));
        var afterRoot = afterDocument.RootElement;
        Assert.Equal(beforeStartedAtUtc, afterRoot.GetProperty("startedAtUtc").GetString());
        Assert.Equal("http://127.0.0.1:48031", afterRoot.GetProperty("httpBaseUrl").GetString());
    }

    [Fact]
    public void Impl_EnsureRuntimeArtifacts_WhenUsingIsolatedRoot_ShouldOnlyCreateRuntimeArtifacts()
    {
        var isolatedRoot = Path.Combine(_tempDirectory, "isolated-root");
        var isolatedRuntime = Path.Combine(isolatedRoot, "runtime");
        var isolatedDefinitions = Path.Combine(isolatedRoot, "apps", "definitions");
        var isolatedInstances = Path.Combine(isolatedRoot, "apps", "instances");
        var isolatedLogs = Path.Combine(isolatedRoot, "logs");
        var isolatedToken = Path.Combine(isolatedRuntime, "token.txt");

        var runtimeOptions = RuntimePathOptions.Create(isolatedRoot);
        var manager = new HostRuntimeArtifactManager(Mock.Of<ILogger<HostRuntimeArtifactManager>>(), runtimeOptions);

        manager.EnsureRuntimeArtifacts();

        Assert.True(Directory.Exists(isolatedRoot));
        Assert.True(Directory.Exists(isolatedRuntime));
        Assert.True(File.Exists(isolatedToken));
        Assert.False(Directory.Exists(isolatedDefinitions));
        Assert.False(Directory.Exists(isolatedInstances));
        Assert.False(Directory.Exists(isolatedLogs));
    }

    [Fact]
    public void Impl_EnsureRuntimeArtifacts_WhenHubJsonMissingAndPortInvalid_ShouldNotCreateHubRuntimeFile()
    {
        using var dataScope = new EnvironmentVariableScope(RuntimePathOptions.DataDirEnvironmentVariable, _tempDirectory);

        var manager = new HostRuntimeArtifactManager(Mock.Of<ILogger<HostRuntimeArtifactManager>>(), RuntimePathOptions.Resolve());
        _ = manager.GetToken();

        var hubJsonPath = Path.Combine(_runtimeDirectory, "hub.json");
        Assert.False(File.Exists(hubJsonPath));

        manager.EnsureRuntimeArtifacts(port: 0);

        Assert.False(File.Exists(hubJsonPath));
    }

    [Fact]
    public void Impl_WriteHubJson_WhenVersionProvided_ShouldPersistVersionAndRuntimeTuning()
    {
        using var dataScope = new EnvironmentVariableScope(RuntimePathOptions.DataDirEnvironmentVariable, _tempDirectory);

        var manager = new HostRuntimeArtifactManager(Mock.Of<ILogger<HostRuntimeArtifactManager>>(), RuntimePathOptions.Resolve());
        var token = manager.GetToken();

        manager.WriteHubJson(48010, "v1.2.3");

        var hubJsonPath = Path.Combine(_runtimeDirectory, "hub.json");
        using var document = JsonDocument.Parse(File.ReadAllText(hubJsonPath));
        var root = document.RootElement;
        Assert.Equal(1, root.GetProperty("protocolVersion").GetInt32());
        Assert.Equal("v1.2.3", root.GetProperty("hubVersion").GetString());
        Assert.Equal($"http://127.0.0.1:48010", root.GetProperty("httpBaseUrl").GetString());
        Assert.Equal($"ws://127.0.0.1:48010/ws", root.GetProperty("wsUrl").GetString());
        Assert.Equal(token, File.ReadAllText(Path.Combine(_runtimeDirectory, "token.txt")));
        Assert.Equal(Path.Combine(_runtimeDirectory, "token.txt"), root.GetProperty("tokenFile").GetString());
        Assert.True(root.TryGetProperty("runtimeTuning", out var runtimeTuning));
        Assert.True(runtimeTuning.TryGetProperty("leaseSeconds", out _));
        Assert.True(runtimeTuning.TryGetProperty("onlineThresholdSeconds", out _));
        Assert.True(runtimeTuning.TryGetProperty("launchDedupeWindowSeconds", out _));
        Assert.True(runtimeTuning.TryGetProperty("launchRegisterTimeoutSeconds", out _));
    }

    [Fact]
    public async System.Threading.Tasks.Task Impl_WriteHubJson_WhenRuntimeFileTemporarilyLocked_ShouldRetryUntilSuccess()
    {
        using var dataScope = new EnvironmentVariableScope(RuntimePathOptions.DataDirEnvironmentVariable, _tempDirectory);

        var manager = new HostRuntimeArtifactManager(Mock.Of<ILogger<HostRuntimeArtifactManager>>(), RuntimePathOptions.Resolve());
        _ = manager.GetToken();
        manager.WriteHubJson(48020, "v1");

        var hubJsonPath = Path.Combine(_runtimeDirectory, "hub.json");
        var lockStream = new FileStream(hubJsonPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var writeTask = System.Threading.Tasks.Task.Run(() => manager.WriteHubJson(48021, "v2"));

        if (OperatingSystem.IsWindows())
        {
            await System.Threading.Tasks.Task.Delay(200);
            Assert.False(writeTask.IsCompleted);
        }

        lockStream.Dispose();

        var completedTask = await System.Threading.Tasks.Task.WhenAny(writeTask, System.Threading.Tasks.Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.Same(writeTask, completedTask);
        await writeTask;

        using var document = JsonDocument.Parse(File.ReadAllText(hubJsonPath));
        Assert.Equal("http://127.0.0.1:48021", document.RootElement.GetProperty("httpBaseUrl").GetString());
        Assert.Equal("ws://127.0.0.1:48021/ws", document.RootElement.GetProperty("wsUrl").GetString());
    }

    [Fact]
    public void Impl_ActivateHubJsonLease_WhenRuntimeFileExists_ShouldAllowReadAndBlockWriteOnWindows()
    {
        using var dataScope = new EnvironmentVariableScope(RuntimePathOptions.DataDirEnvironmentVariable, _tempDirectory);

        using var manager = new HostRuntimeArtifactManager(
            Mock.Of<ILogger<HostRuntimeArtifactManager>>(),
            RuntimePathOptions.Create(_tempDirectory));
        _ = manager.GetToken();
        manager.WriteHubJson(48040, "v1");
        manager.ActivateHubJsonLease();

        var hubJsonPath = Path.Combine(_runtimeDirectory, "hub.json");
        using var document = JsonDocument.Parse(File.ReadAllText(hubJsonPath));
        Assert.Equal("http://127.0.0.1:48040", document.RootElement.GetProperty("httpBaseUrl").GetString());

        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var exception = Record.Exception(() => File.WriteAllText(hubJsonPath, "{}"));
        Assert.NotNull(exception);
        Assert.True(exception is IOException or UnauthorizedAccessException);
    }

    [Fact]
    public void Impl_Cleanup_WhenHubJsonExists_ShouldRotateToPrevHubJsonAndOverwriteHistory()
    {
        using var dataScope = new EnvironmentVariableScope(RuntimePathOptions.DataDirEnvironmentVariable, _tempDirectory);

        using var manager = new HostRuntimeArtifactManager(
            Mock.Of<ILogger<HostRuntimeArtifactManager>>(),
            RuntimePathOptions.Create(_tempDirectory));
        _ = manager.GetToken();
        manager.WriteHubJson(48041, "v1");
        manager.ActivateHubJsonLease();

        var hubJsonPath = Path.Combine(_runtimeDirectory, "hub.json");
        var previousHubJsonPath = Path.Combine(_runtimeDirectory, "prev_hub.json");
        File.WriteAllText(previousHubJsonPath, "legacy-prev");
        var currentContent = File.ReadAllText(hubJsonPath);

        manager.Cleanup();

        Assert.False(File.Exists(hubJsonPath));
        Assert.True(File.Exists(previousHubJsonPath));
        Assert.Equal(currentContent, File.ReadAllText(previousHubJsonPath));
    }

    [Fact]
    public void Impl_EnsureRuntimeArtifacts_WhenLeaseRequestedBeforeHubJsonExists_ShouldCreateAndLeaseHubJson()
    {
        using var dataScope = new EnvironmentVariableScope(RuntimePathOptions.DataDirEnvironmentVariable, _tempDirectory);

        using var manager = new HostRuntimeArtifactManager(
            Mock.Of<ILogger<HostRuntimeArtifactManager>>(),
            RuntimePathOptions.Create(_tempDirectory));
        manager.ActivateHubJsonLease();

        var hubJsonPath = Path.Combine(_runtimeDirectory, "hub.json");
        Assert.False(File.Exists(hubJsonPath));

        manager.EnsureRuntimeArtifacts(port: 48042);

        Assert.True(File.Exists(hubJsonPath));
        using var document = JsonDocument.Parse(File.ReadAllText(hubJsonPath));
        Assert.Equal("http://127.0.0.1:48042", document.RootElement.GetProperty("httpBaseUrl").GetString());

        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var exception = Record.Exception(() => File.WriteAllText(hubJsonPath, "{}"));
        Assert.NotNull(exception);
        Assert.True(exception is IOException or UnauthorizedAccessException);
    }

    [Fact]
    public void Impl_ActivateHubJsonLease_AfterDispose_ShouldThrowObjectDisposedException()
    {
        var manager = new HostRuntimeArtifactManager(
            Mock.Of<ILogger<HostRuntimeArtifactManager>>(),
            RuntimePathOptions.Create(_tempDirectory));

        manager.Dispose();
        manager.Dispose();

        var exception = Record.Exception(() => manager.ActivateHubJsonLease());

        Assert.NotNull(exception);
        Assert.IsType<ObjectDisposedException>(exception);
    }

    [Fact]
    public void Impl_Cleanup_ShouldNotThrow()
    {
        using var manager = new HostRuntimeArtifactManager(
            Mock.Of<ILogger<HostRuntimeArtifactManager>>(),
            RuntimePathOptions.Create(_tempDirectory));

        var exception = Record.Exception(() => manager.Cleanup());

        Assert.Null(exception);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDirectory))
            {
                Directory.Delete(_tempDirectory, recursive: true);
            }
        }
        catch
        {
        }
    }
}
