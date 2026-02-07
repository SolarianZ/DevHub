namespace DevHub.Tests;

using System.Text.Json;
using DevHub.Core.Services;
using DevHub.Core.Services.Invocation;
using Microsoft.Extensions.Logging;
using Moq;

/// <summary>
/// LaunchCoordinator 行为测试。
/// </summary>
public class LaunchCoordinatorTests : IDisposable
{
    private readonly string _tempDirectory;
    private readonly Mock<ILogger<DefinitionLoader>> _definitionLogger = new();
    private readonly Mock<ILogger<AppRegistry>> _registryLogger = new();
    private readonly Mock<ILogger<LaunchCoordinator>> _launchLogger = new();

    /// <summary>
    /// 初始化测试上下文。
    /// </summary>
    public LaunchCoordinatorTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "DevHubLaunchCoordinatorTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
    }

    [Fact]
    public async Task LaunchAsync_WhenDefinitionMissing_ShouldReturnAppDefinitionNotFound()
    {
        var definitionLoader = new DefinitionLoader(_tempDirectory, _definitionLogger.Object);
        definitionLoader.Load();
        var appRegistry = new AppRegistry(_registryLogger.Object);
        var coordinator = new LaunchCoordinator(definitionLoader, appRegistry, _launchLogger.Object);

        var result = await coordinator.LaunchAsync(
            appId: "missing.app",
            scope: null,
            dedupeKey: null,
            waitForRegisterMs: 0,
            CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(-32014, result.ErrorCode);
        Assert.Equal("app_definition_not_found", result.ErrorMessage);
        var errorData = JsonSerializer.SerializeToElement(result.ErrorData);
        Assert.Equal("missing.app", errorData.GetProperty("appId").GetString());
    }

    [Fact]
    public async Task LaunchAsync_WhenLaunchConfigMissing_ShouldReturnLaunchFailed()
    {
        WriteDefinition("launch-missing.app", includeLaunch: false);

        var definitionLoader = new DefinitionLoader(_tempDirectory, _definitionLogger.Object);
        definitionLoader.Load();
        var appRegistry = new AppRegistry(_registryLogger.Object);
        var coordinator = new LaunchCoordinator(definitionLoader, appRegistry, _launchLogger.Object);

        var result = await coordinator.LaunchAsync(
            appId: "launch-missing.app",
            scope: null,
            dedupeKey: null,
            waitForRegisterMs: 0,
            CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(-32020, result.ErrorCode);
        Assert.Equal("launch_failed", result.ErrorMessage);
        var errorData = JsonSerializer.SerializeToElement(result.ErrorData);
        Assert.Equal("launch_config_missing", errorData.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task LaunchAsync_WhenProcessStarted_ShouldReturnStartedStatus()
    {
        WriteDefinition("launch-started.app", includeLaunch: true);

        var definitionLoader = new DefinitionLoader(_tempDirectory, _definitionLogger.Object);
        definitionLoader.Load();
        var appRegistry = new AppRegistry(_registryLogger.Object);
        var coordinator = new LaunchCoordinator(definitionLoader, appRegistry, _launchLogger.Object);

        var result = await coordinator.LaunchAsync(
            appId: "launch-started.app",
            scope: null,
            dedupeKey: null,
            waitForRegisterMs: 0,
            CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal("started", result.Status);
        Assert.NotNull(result.Pid);
        Assert.True(result.Pid > 0);
        Assert.False(string.IsNullOrWhiteSpace(result.LaunchId));
    }

    [Fact]
    public async Task LaunchAsync_WhenWaitForRegisterTimeout_ShouldReturnStartingStatus()
    {
        WriteDefinition("launch-starting.app", includeLaunch: true);

        var definitionLoader = new DefinitionLoader(_tempDirectory, _definitionLogger.Object);
        definitionLoader.Load();
        var appRegistry = new AppRegistry(_registryLogger.Object);
        var coordinator = new LaunchCoordinator(definitionLoader, appRegistry, _launchLogger.Object);

        var result = await coordinator.LaunchAsync(
            appId: "launch-starting.app",
            scope: null,
            dedupeKey: null,
            waitForRegisterMs: 120,
            CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal("starting", result.Status);
        Assert.NotNull(result.Pid);
        Assert.True(result.Pid > 0);
        Assert.False(string.IsNullOrWhiteSpace(result.LaunchId));
    }

    /// <summary>
    /// 释放测试资源。
    /// </summary>
    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }

    private void WriteDefinition(string appId, bool includeLaunch)
    {
        var payload = new Dictionary<string, object?>
        {
            ["appId"] = appId,
            ["displayName"] = appId,
            ["scopePolicy"] = "any",
            ["capabilities"] = new Dictionary<string, object?>
            {
                ["rpc"] = true,
                ["events"] = false
            }
        };

        if (includeLaunch)
        {
            payload["launch"] = new Dictionary<string, object?>
            {
                ["exePath"] = "dotnet",
                ["argsTemplate"] = "--version"
            };
        }

        var filePath = Path.Combine(_tempDirectory, $"{appId}.json");
        File.WriteAllText(filePath, JsonSerializer.Serialize(payload));
    }
}
