namespace DevHub.Tests;

using System.Text.Json;
using DevHub.Core.Services;
using Microsoft.Extensions.Logging;
using Moq;

/// <summary>
/// FileSystemManager 恢复与自愈行为测试。
/// </summary>
[Trait("Category", "Impl")]
public sealed class FileSystemManagerRecoveryTests : IDisposable
{
    private readonly string _tempDirectory;
    private readonly string _runtimeDirectory;
    private readonly string _definitionsDirectory;
    private readonly string _logsDirectory;

    /// <summary>
    /// 初始化测试上下文。
    /// </summary>
    public FileSystemManagerRecoveryTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "DevHubFileSystemManagerRecoveryTests", Guid.NewGuid().ToString("N"));
        _runtimeDirectory = Path.Combine(_tempDirectory, "runtime");
        _definitionsDirectory = Path.Combine(_tempDirectory, "definitions");
        _logsDirectory = Path.Combine(_tempDirectory, "logs");

        Directory.CreateDirectory(_tempDirectory);
        Directory.CreateDirectory(_runtimeDirectory);
        Directory.CreateDirectory(_definitionsDirectory);
        Directory.CreateDirectory(_logsDirectory);
    }

    [Fact]
    public void Impl_GetToken_WhenHistoricalTokenExists_ShouldRotateForNewSession()
    {
        using var runtimeScope = new EnvironmentVariableScope(RuntimePathOptions.RuntimeDirEnvironmentVariable, _runtimeDirectory);
        using var appDefsScope = new EnvironmentVariableScope(RuntimePathOptions.AppDefinitionsDirEnvironmentVariable, _definitionsDirectory);
        using var logScope = new EnvironmentVariableScope(RuntimePathOptions.LogDirEnvironmentVariable, _logsDirectory);

        var tokenPath = Path.Combine(_runtimeDirectory, "token.txt");
        File.WriteAllText(tokenPath, "legacy-token");

        var manager = new FileSystemManager(Mock.Of<ILogger<FileSystemManager>>(), RuntimePathOptions.Resolve(_definitionsDirectory));
        var newToken = manager.GetToken();

        Assert.NotEqual("legacy-token", newToken);
        Assert.Equal(newToken, File.ReadAllText(tokenPath));
    }

    [Fact]
    public void Impl_EnsureRuntimeArtifacts_WhenTokenDeleted_ShouldRestoreCurrentSessionToken()
    {
        using var runtimeScope = new EnvironmentVariableScope(RuntimePathOptions.RuntimeDirEnvironmentVariable, _runtimeDirectory);
        using var appDefsScope = new EnvironmentVariableScope(RuntimePathOptions.AppDefinitionsDirEnvironmentVariable, _definitionsDirectory);
        using var logScope = new EnvironmentVariableScope(RuntimePathOptions.LogDirEnvironmentVariable, _logsDirectory);

        var manager = new FileSystemManager(Mock.Of<ILogger<FileSystemManager>>(), RuntimePathOptions.Resolve(_definitionsDirectory));
        var token = manager.GetToken();

        var tokenPath = Path.Combine(_runtimeDirectory, "token.txt");
        File.Delete(tokenPath);
        Assert.False(File.Exists(tokenPath));

        manager.EnsureRuntimeArtifacts();

        Assert.True(File.Exists(tokenPath));
        Assert.Equal(token, File.ReadAllText(tokenPath));
    }

    [Fact]
    public void Impl_EnsureRuntimeArtifacts_WhenHubJsonMissingAndPortProvided_ShouldRebuildHubRuntimeFile()
    {
        using var runtimeScope = new EnvironmentVariableScope(RuntimePathOptions.RuntimeDirEnvironmentVariable, _runtimeDirectory);
        using var appDefsScope = new EnvironmentVariableScope(RuntimePathOptions.AppDefinitionsDirEnvironmentVariable, _definitionsDirectory);
        using var logScope = new EnvironmentVariableScope(RuntimePathOptions.LogDirEnvironmentVariable, _logsDirectory);

        var manager = new FileSystemManager(Mock.Of<ILogger<FileSystemManager>>(), RuntimePathOptions.Resolve(_definitionsDirectory));
        _ = manager.GetToken();

        var hubJsonPath = Path.Combine(_runtimeDirectory, "hub.json");
        if (File.Exists(hubJsonPath))
        {
            File.Delete(hubJsonPath);
        }

        manager.EnsureRuntimeArtifacts(port: 47999);

        Assert.True(File.Exists(hubJsonPath));
        using var document = JsonDocument.Parse(File.ReadAllText(hubJsonPath));
        Assert.Equal("http://127.0.0.1:47999", document.RootElement.GetProperty("httpBaseUrl").GetString());
        Assert.Equal("ws://127.0.0.1:47999/ws", document.RootElement.GetProperty("wsUrl").GetString());
    }

    [Fact]
    public void Impl_EnsureRuntimeArtifacts_WhenHubJsonExists_ShouldPreserveRuntimeFile()
    {
        using var runtimeScope = new EnvironmentVariableScope(RuntimePathOptions.RuntimeDirEnvironmentVariable, _runtimeDirectory);
        using var appDefsScope = new EnvironmentVariableScope(RuntimePathOptions.AppDefinitionsDirEnvironmentVariable, _definitionsDirectory);
        using var logScope = new EnvironmentVariableScope(RuntimePathOptions.LogDirEnvironmentVariable, _logsDirectory);

        var manager = new FileSystemManager(Mock.Of<ILogger<FileSystemManager>>(), RuntimePathOptions.Resolve(_definitionsDirectory));
        _ = manager.GetToken();
        manager.WriteHubJson(48000, "v1");

        var hubJsonPath = Path.Combine(_runtimeDirectory, "hub.json");
        var before = File.ReadAllText(hubJsonPath);

        manager.EnsureRuntimeArtifacts();

        var after = File.ReadAllText(hubJsonPath);
        Assert.Equal(before, after);
    }

    [Fact]
    public void Impl_InitializeDirectories_WhenUsingIsolatedRoot_ShouldCreateAllFolders()
    {
        var isolatedRoot = Path.Combine(_tempDirectory, "isolated-root");
        var isolatedRuntime = Path.Combine(isolatedRoot, "runtime");
        var isolatedDefinitions = Path.Combine(isolatedRoot, "definitions");
        var isolatedInstances = Path.Combine(isolatedRoot, "instances");
        var isolatedLogs = Path.Combine(isolatedRoot, "logs");

        var runtimeOptions = RuntimePathOptions.Create(
            rootPath: isolatedRoot,
            runtimePath: isolatedRuntime,
            definitionsPath: isolatedDefinitions,
            instancesPath: isolatedInstances,
            logsPath: isolatedLogs);
        var manager = new FileSystemManager(Mock.Of<ILogger<FileSystemManager>>(), runtimeOptions);

        manager.InitializeDirectories();

        Assert.True(Directory.Exists(isolatedRoot));
        Assert.True(Directory.Exists(isolatedRuntime));
        Assert.True(Directory.Exists(isolatedDefinitions));
        Assert.True(Directory.Exists(isolatedInstances));
        Assert.True(Directory.Exists(isolatedLogs));
    }

    [Fact]
    public void Impl_EnsureRuntimeArtifacts_WhenHubJsonMissingAndPortInvalid_ShouldNotCreateHubRuntimeFile()
    {
        using var runtimeScope = new EnvironmentVariableScope(RuntimePathOptions.RuntimeDirEnvironmentVariable, _runtimeDirectory);
        using var appDefsScope = new EnvironmentVariableScope(RuntimePathOptions.AppDefinitionsDirEnvironmentVariable, _definitionsDirectory);
        using var logScope = new EnvironmentVariableScope(RuntimePathOptions.LogDirEnvironmentVariable, _logsDirectory);

        var manager = new FileSystemManager(Mock.Of<ILogger<FileSystemManager>>(), RuntimePathOptions.Resolve(_definitionsDirectory));
        _ = manager.GetToken();

        var hubJsonPath = Path.Combine(_runtimeDirectory, "hub.json");
        Assert.False(File.Exists(hubJsonPath));

        manager.EnsureRuntimeArtifacts(port: 0);

        Assert.False(File.Exists(hubJsonPath));
    }

    [Fact]
    public void Impl_WriteHubJson_WhenVersionProvided_ShouldPersistVersionAndRuntimeTuning()
    {
        using var runtimeScope = new EnvironmentVariableScope(RuntimePathOptions.RuntimeDirEnvironmentVariable, _runtimeDirectory);
        using var appDefsScope = new EnvironmentVariableScope(RuntimePathOptions.AppDefinitionsDirEnvironmentVariable, _definitionsDirectory);
        using var logScope = new EnvironmentVariableScope(RuntimePathOptions.LogDirEnvironmentVariable, _logsDirectory);

        var manager = new FileSystemManager(Mock.Of<ILogger<FileSystemManager>>(), RuntimePathOptions.Resolve(_definitionsDirectory));
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
    }

    [Fact]
    public void Impl_Cleanup_ShouldNotThrow()
    {
        var manager = new FileSystemManager(
            Mock.Of<ILogger<FileSystemManager>>(),
            RuntimePathOptions.Resolve(_definitionsDirectory));

        manager.Cleanup();

        Assert.True(true);
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



