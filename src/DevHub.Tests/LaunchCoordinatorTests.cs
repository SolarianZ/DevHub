namespace DevHub.Tests;

using System.Text.Json;
using DevHub.Core.Models;
using DevHub.Core.Services;
using DevHub.Core.Services.Abstractions;
using DevHub.Core.Services.Invocation;
using Microsoft.Extensions.Logging;
using Moq;

/// <summary>
/// LaunchCoordinator 行为测试。
/// </summary>
public class LaunchCoordinatorTests : IDisposable
{
    private readonly string _tempDirectory;
    private readonly string _runtimeDirectory;
    private readonly EnvironmentVariableScope _runtimeScope;
    private readonly Mock<ILogger<DefinitionLoader>> _definitionLogger = new();
    private readonly Mock<ILogger<AppRegistry>> _registryLogger = new();
    private readonly Mock<ILogger<LaunchCoordinator>> _launchLogger = new();

    /// <summary>
    /// 初始化测试上下文。
    /// </summary>
    public LaunchCoordinatorTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "DevHubLaunchCoordinatorTests", Guid.NewGuid().ToString("N"));
        _runtimeDirectory = Path.Combine(_tempDirectory, "runtime");
        Directory.CreateDirectory(_tempDirectory);
        Directory.CreateDirectory(_runtimeDirectory);
        _runtimeScope = new EnvironmentVariableScope("DEVHUB_RUNTIME_DIR", _runtimeDirectory);
    }

    [Fact]
    public async Task LaunchAsync_WhenDefinitionMissing_ShouldReturnAppDefinitionNotFound()
    {
        var definitionLoader = new DefinitionLoader(_tempDirectory, _definitionLogger.Object);
        var definitionProvider = new DefinitionProvider(definitionLoader);
        definitionProvider.Refresh();
        var appRegistry = new AppRegistry(new SystemClock(), _registryLogger.Object);
        var provider = new RuntimeHttpBaseUrlProvider(Mock.Of<ILogger<RuntimeHttpBaseUrlProvider>>(), RuntimePathOptions.Resolve());
        var coordinator = new LaunchCoordinator(definitionProvider, appRegistry, provider, new ProcessLauncher(), new SystemClock(), _launchLogger.Object);

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
        var definitionProvider = new DefinitionProvider(definitionLoader);
        definitionProvider.Refresh();
        var appRegistry = new AppRegistry(new SystemClock(), _registryLogger.Object);
        var provider = new RuntimeHttpBaseUrlProvider(Mock.Of<ILogger<RuntimeHttpBaseUrlProvider>>(), RuntimePathOptions.Resolve());
        var coordinator = new LaunchCoordinator(definitionProvider, appRegistry, provider, new ProcessLauncher(), new SystemClock(), _launchLogger.Object);

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
        var definitionProvider = new DefinitionProvider(definitionLoader);
        definitionProvider.Refresh();
        var appRegistry = new AppRegistry(new SystemClock(), _registryLogger.Object);
        var provider = new RuntimeHttpBaseUrlProvider(Mock.Of<ILogger<RuntimeHttpBaseUrlProvider>>(), RuntimePathOptions.Resolve());
        var coordinator = new LaunchCoordinator(definitionProvider, appRegistry, provider, new ProcessLauncher(), new SystemClock(), _launchLogger.Object);

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

        var coordinator = CreateCoordinator();

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

    [Fact]
    public async Task LaunchAsync_WithSameDedupeKeyWithinWindow_ShouldReturnAlreadyRunningAndReuseLaunchId()
    {
        WriteDefinition(
            "launch-dedupe-window.app",
            includeLaunch: true,
            dedupeKeyTemplate: "{appId}:{scopeOrGlobal}");

        var coordinator = CreateCoordinator();

        var first = await coordinator.LaunchAsync(
            appId: "launch-dedupe-window.app",
            scope: null,
            dedupeKey: null,
            waitForRegisterMs: 0,
            CancellationToken.None);

        var second = await coordinator.LaunchAsync(
            appId: "launch-dedupe-window.app",
            scope: null,
            dedupeKey: null,
            waitForRegisterMs: 0,
            CancellationToken.None);

        Assert.True(first.Ok);
        Assert.Equal("started", first.Status);
        Assert.True(second.Ok);
        Assert.Equal("already_running", second.Status);
        Assert.Equal(first.LaunchId, second.LaunchId);
    }

    [Fact]
    public async Task LaunchAsync_WhenDedupeWindowOverridden_ShouldRespectConfiguredWindow()
    {
        using var dedupeScope = new EnvironmentVariableScope(RuntimeTuningOptions.LaunchDedupeWindowSecondsEnvironmentVariable, "1");
        var tuningOptions = RuntimeTuningOptions.Resolve(Mock.Of<ILogger<RuntimeTuningOptions>>());

        WriteDefinition(
            "launch-dedupe-window-override.app",
            includeLaunch: true,
            dedupeKeyTemplate: "{appId}:{scopeOrGlobal}");

        var clock = new MutableClock(DateTime.UtcNow);
        var definitionLoader = new DefinitionLoader(_tempDirectory, _definitionLogger.Object);
        var definitionProvider = new DefinitionProvider(definitionLoader);
        definitionProvider.Refresh();
        var appRegistry = new AppRegistry(clock, _registryLogger.Object, tuningOptions);
        var runtimeProvider = new Mock<IRuntimeHttpBaseUrlProvider>();
        runtimeProvider.Setup(provider => provider.GetHttpBaseUrl()).Returns("http://127.0.0.1:65001");
        var processLauncher = new Mock<IProcessLauncher>();
        processLauncher
            .Setup(launcher => launcher.Start(It.IsAny<LaunchConfiguration>(), It.IsAny<string?>()))
            .Returns(System.Diagnostics.Process.GetCurrentProcess());

        var coordinator = new LaunchCoordinator(
            definitionProvider,
            appRegistry,
            runtimeProvider.Object,
            processLauncher.Object,
            clock,
            tuningOptions,
            _launchLogger.Object);

        var first = await coordinator.LaunchAsync(
            appId: "launch-dedupe-window-override.app",
            scope: null,
            dedupeKey: null,
            waitForRegisterMs: 0,
            CancellationToken.None);

        var second = await coordinator.LaunchAsync(
            appId: "launch-dedupe-window-override.app",
            scope: null,
            dedupeKey: null,
            waitForRegisterMs: 0,
            CancellationToken.None);

        Assert.True(first.Ok);
        Assert.Equal("started", first.Status);
        Assert.True(second.Ok);
        Assert.Equal("already_running", second.Status);
        Assert.Equal(first.LaunchId, second.LaunchId);

        clock.Advance(TimeSpan.FromSeconds(2));

        var third = await coordinator.LaunchAsync(
            appId: "launch-dedupe-window-override.app",
            scope: null,
            dedupeKey: null,
            waitForRegisterMs: 0,
            CancellationToken.None);

        Assert.True(third.Ok);
        Assert.Equal("started", third.Status);
        Assert.NotEqual(first.LaunchId, third.LaunchId);
    }

    [Fact]
    public async Task LaunchAsync_WithExplicitDedupeKey_ShouldOverrideTemplate()
    {
        WriteDefinition(
            "launch-explicit-dedupe.app",
            includeLaunch: true,
            dedupeKeyTemplate: "{appId}:{httpBaseUrl}");

        WriteHubRuntime("http://127.0.0.1:61001");
        var coordinator = CreateCoordinator();

        var first = await coordinator.LaunchAsync(
            appId: "launch-explicit-dedupe.app",
            scope: null,
            dedupeKey: "manual-key",
            waitForRegisterMs: 0,
            CancellationToken.None);

        WriteHubRuntime("http://127.0.0.1:61002");
        var second = await coordinator.LaunchAsync(
            appId: "launch-explicit-dedupe.app",
            scope: null,
            dedupeKey: "manual-key",
            waitForRegisterMs: 0,
            CancellationToken.None);

        Assert.True(first.Ok);
        Assert.Equal("started", first.Status);
        Assert.True(second.Ok);
        Assert.Equal("already_running", second.Status);
        Assert.Equal(first.LaunchId, second.LaunchId);
    }

    [Fact]
    public async Task LaunchAsync_WhenHttpBaseUrlChanges_ShouldUseDifferentTemplateKey()
    {
        WriteDefinition(
            "launch-httpbaseurl-template.app",
            includeLaunch: true,
            dedupeKeyTemplate: "{appId}:{scopeOrGlobal}:{httpBaseUrl}");

        var coordinator = CreateCoordinator();

        WriteHubRuntime("http://127.0.0.1:62001");
        var first = await coordinator.LaunchAsync(
            appId: "launch-httpbaseurl-template.app",
            scope: null,
            dedupeKey: null,
            waitForRegisterMs: 0,
            CancellationToken.None);

        WriteHubRuntime("http://127.0.0.1:62002");
        var second = await coordinator.LaunchAsync(
            appId: "launch-httpbaseurl-template.app",
            scope: null,
            dedupeKey: null,
            waitForRegisterMs: 0,
            CancellationToken.None);

        Assert.True(first.Ok);
        Assert.Equal("started", first.Status);
        Assert.True(second.Ok);
        Assert.Equal("started", second.Status);
        Assert.NotEqual(first.LaunchId, second.LaunchId);
    }

    [Fact]
    public async Task LaunchAsync_ArgsTemplate_ShouldRenderSpecPlaceholders()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var argsOutputPath = Path.Combine(_tempDirectory, "args-output.txt");
        var escapedOutputPath = argsOutputPath.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);

        WriteDefinition(
            "launch-args-template.app",
            includeLaunch: true,
            exePath: "/bin/sh",
            argsTemplate: $"-c \"printf '%s' '{{appId}}|{{scope}}|{{scopeOrGlobal}}|{{httpBaseUrl}}' > \\\"{escapedOutputPath}\\\"\"");

        WriteHubRuntime("http://127.0.0.1:63001");
        var coordinator = CreateCoordinator();

        var result = await coordinator.LaunchAsync(
            appId: "launch-args-template.app",
            scope: "workspace-A",
            dedupeKey: null,
            waitForRegisterMs: 0,
            CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal("started", result.Status);

        await WaitUntilFileExistsAsync(argsOutputPath, TimeSpan.FromSeconds(3));
        var rendered = File.ReadAllText(argsOutputPath);
        Assert.Equal("launch-args-template.app|workspace-A|workspace-A|http://127.0.0.1:63001", rendered);
    }

    [Fact]
    public async Task LaunchAsync_ArgsTemplate_WithNullScope_ShouldRenderEmptyScopeAndGlobalScopeOrGlobal()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var argsOutputPath = Path.Combine(_tempDirectory, "args-output-global.txt");
        var escapedOutputPath = argsOutputPath.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);

        WriteDefinition(
            "launch-args-template-global.app",
            includeLaunch: true,
            exePath: "/bin/sh",
            argsTemplate: $"-c \"printf '%s' '{{appId}}|{{scope}}|{{scopeOrGlobal}}|{{httpBaseUrl}}' > \\\"{escapedOutputPath}\\\"\"");

        WriteHubRuntime("http://127.0.0.1:63002");
        var coordinator = CreateCoordinator();

        var result = await coordinator.LaunchAsync(
            appId: "launch-args-template-global.app",
            scope: null,
            dedupeKey: null,
            waitForRegisterMs: 0,
            CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal("started", result.Status);

        await WaitUntilFileExistsAsync(argsOutputPath, TimeSpan.FromSeconds(3));
        var rendered = File.ReadAllText(argsOutputPath);
        Assert.Equal("launch-args-template-global.app||global|http://127.0.0.1:63002", rendered);
    }

    /// <summary>
    /// 释放测试资源。
    /// </summary>
    public void Dispose()
    {
        _runtimeScope.Dispose();

        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }

    private LaunchCoordinator CreateCoordinator()
    {
        var definitionLoader = new DefinitionLoader(_tempDirectory, _definitionLogger.Object);
        var definitionProvider = new DefinitionProvider(definitionLoader);
        definitionProvider.Refresh();
        var appRegistry = new AppRegistry(new SystemClock(), _registryLogger.Object);
        var provider = new RuntimeHttpBaseUrlProvider(Mock.Of<ILogger<RuntimeHttpBaseUrlProvider>>(), RuntimePathOptions.Resolve());
        return new LaunchCoordinator(definitionProvider, appRegistry, provider, new ProcessLauncher(), new SystemClock(), _launchLogger.Object);
    }

    private void WriteHubRuntime(string httpBaseUrl)
    {
        var tokenFile = Path.Combine(_runtimeDirectory, "token.txt");
        File.WriteAllText(tokenFile, "launch-coordinator-tests-token");

        var payload = new
        {
            protocolVersion = 1,
            hubVersion = "test",
            pid = 12345,
            httpBaseUrl,
            wsUrl = httpBaseUrl.Replace("http://", "ws://", StringComparison.Ordinal) + "/ws",
            tokenFile,
            startedAtUtc = DateTime.UtcNow.ToString("O"),
            runtimeTuning = new
            {
                leaseSeconds = 30,
                onlineThresholdSeconds = 30,
                launchDedupeWindowSeconds = 30
            }
        };

        var hubJsonPath = Path.Combine(_runtimeDirectory, "hub.json");
        File.WriteAllText(hubJsonPath, JsonSerializer.Serialize(payload));
    }

    private void WriteDefinition(string appId, bool includeLaunch, string? dedupeKeyTemplate = null, string? argsTemplate = null, string? exePath = null)
    {
        var payload = new Dictionary<string, object?>
        {
            ["appId"] = appId,
            ["displayName"] = appId,
            ["capabilities"] = new Dictionary<string, object?>
            {
                ["rpc"] = true,
                ["events"] = false
            }
        };

        if (includeLaunch)
        {
            var launch = new Dictionary<string, object?>
            {
                ["exePath"] = exePath ?? "dotnet",
                ["argsTemplate"] = argsTemplate ?? "--version"
            };

            if (!string.IsNullOrWhiteSpace(dedupeKeyTemplate))
            {
                launch["dedupeKeyTemplate"] = dedupeKeyTemplate;
            }

            payload["launch"] = launch;
        }

        var filePath = Path.Combine(_tempDirectory, $"{appId}.json");
        File.WriteAllText(filePath, JsonSerializer.Serialize(payload));
    }

    private static async Task WaitUntilFileExistsAsync(string filePath, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow.Add(timeout);
        while (DateTime.UtcNow <= deadline)
        {
            if (File.Exists(filePath))
            {
                return;
            }

            await Task.Delay(20);
        }

        throw new Xunit.Sdk.XunitException($"等待文件生成超时: {filePath}");
    }

    private sealed class MutableClock : IClock
    {
        public MutableClock(DateTime utcNow)
        {
            UtcNow = utcNow;
        }

        public DateTime UtcNow { get; private set; }

        public void Advance(TimeSpan duration)
        {
            UtcNow = UtcNow.Add(duration);
        }
    }
}
