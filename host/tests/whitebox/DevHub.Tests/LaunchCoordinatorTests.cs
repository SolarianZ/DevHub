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
[Trait("Category", "Impl")]
public class LaunchCoordinatorTests : IDisposable
{
    private readonly string _dataDirectory;
    private readonly string _definitionsDirectory;
    private readonly string _runtimeDirectory;
    private readonly EnvironmentVariableScope _dataScope;
    private readonly Mock<ILogger<DefinitionLoader>> _definitionLogger = new();
    private readonly Mock<ILogger<AppRegistry>> _registryLogger = new();
    private readonly Mock<ILogger<LaunchCoordinator>> _launchLogger = new();

    /// <summary>
    /// 初始化测试上下文。
    /// </summary>
    public LaunchCoordinatorTests()
    {
        _dataDirectory = Path.Combine(Path.GetTempPath(), "DevHubLaunchCoordinatorTests", Guid.NewGuid().ToString("N"));
        _definitionsDirectory = Path.Combine(_dataDirectory, "apps", "definitions");
        _runtimeDirectory = Path.Combine(_dataDirectory, "runtime");
        Directory.CreateDirectory(_dataDirectory);
        Directory.CreateDirectory(_definitionsDirectory);
        Directory.CreateDirectory(_runtimeDirectory);
        _dataScope = new EnvironmentVariableScope(RuntimePathOptions.DataDirEnvironmentVariable, _dataDirectory);
    }

    [Fact]
    public async Task Impl_LaunchAsync_WhenDefinitionMissing_ShouldReturnAppDefinitionNotFound()
    {
        var definitionLoader = new DefinitionLoader(DefinitionCatalogTestHelper.GetCatalogPath(_definitionsDirectory), _definitionLogger.Object);
        var definitionProvider = new DefinitionProvider(definitionLoader);
        definitionProvider.Refresh();
        var appRegistry = new AppRegistry(new SystemClock(), _registryLogger.Object);
        var provider = new RuntimeHttpBaseUrlProvider(Mock.Of<ILogger<RuntimeHttpBaseUrlProvider>>(), RuntimePathOptions.Resolve());
        var coordinator = new LaunchCoordinator(definitionProvider, appRegistry, provider, new ProcessLauncher(), new SystemClock(), _launchLogger.Object);

        var result = await coordinator.LaunchAsync(
            appId: "missing.app",
            scope: ScopeContract.Global,
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
    public async Task Impl_LaunchAsync_WhenLaunchConfigMissing_ShouldReturnLaunchFailed()
    {
        WriteDefinition("launch-missing.app", includeLaunch: false);

        var definitionLoader = new DefinitionLoader(DefinitionCatalogTestHelper.GetCatalogPath(_definitionsDirectory), _definitionLogger.Object);
        var definitionProvider = new DefinitionProvider(definitionLoader);
        definitionProvider.Refresh();
        var appRegistry = new AppRegistry(new SystemClock(), _registryLogger.Object);
        var provider = new RuntimeHttpBaseUrlProvider(Mock.Of<ILogger<RuntimeHttpBaseUrlProvider>>(), RuntimePathOptions.Resolve());
        var coordinator = new LaunchCoordinator(definitionProvider, appRegistry, provider, new ProcessLauncher(), new SystemClock(), _launchLogger.Object);

        var result = await coordinator.LaunchAsync(
            appId: "launch-missing.app",
            scope: ScopeContract.Global,
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
    public async Task Impl_LaunchAsync_WhenProcessStarted_ShouldReturnStartedStatus()
    {
        WriteDefinition("launch-started.app", includeLaunch: true);

        var processLauncher = new Mock<IProcessLauncher>();
        processLauncher
            .Setup(launcher => launcher.Start(It.IsAny<LaunchConfiguration>(), It.IsAny<string?>()))
            .Returns(System.Diagnostics.Process.GetCurrentProcess());
        var coordinator = CreateCoordinator(processLauncher: processLauncher.Object);

        var result = await coordinator.LaunchAsync(
            appId: "launch-started.app",
            scope: ScopeContract.Global,
            dedupeKey: null,
            waitForRegisterMs: 0,
            CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal("started", result.Status);
        Assert.NotNull(result.Pid);
        Assert.True(result.Pid > 0);
        Assert.False(string.IsNullOrWhiteSpace(result.LaunchId));
        Assert.Equal("launch-started.app:global", result.DedupeKey);
        processLauncher.Verify(launcher => launcher.Start(It.IsAny<LaunchConfiguration>(), It.IsAny<string?>()), Times.Once);
    }

    [Fact]
    public async Task Impl_LaunchAsync_WhenWaitForRegisterTimeout_ShouldReturnStartingStatus()
    {
        WriteDefinition("launch-starting.app", includeLaunch: true);

        var processLauncher = new Mock<IProcessLauncher>();
        processLauncher
            .Setup(launcher => launcher.Start(It.IsAny<LaunchConfiguration>(), It.IsAny<string?>()))
            .Returns(System.Diagnostics.Process.GetCurrentProcess());
        var coordinator = CreateCoordinator(processLauncher: processLauncher.Object);

        var result = await coordinator.LaunchAsync(
            appId: "launch-starting.app",
            scope: ScopeContract.Global,
            dedupeKey: null,
            waitForRegisterMs: 120,
            CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal("starting", result.Status);
        Assert.NotNull(result.Pid);
        Assert.True(result.Pid > 0);
        Assert.False(string.IsNullOrWhiteSpace(result.LaunchId));
        Assert.Equal("launch-starting.app:global", result.DedupeKey);
        processLauncher.Verify(launcher => launcher.Start(It.IsAny<LaunchConfiguration>(), It.IsAny<string?>()), Times.Once);
    }

    [Fact]
    public async Task Impl_LaunchAsync_WhenMatchingOnlineInstanceExists_ShouldNotInvokeProcessLauncher()
    {
        WriteDefinition("launch-online-instance.app", includeLaunch: true, definitionScope: "workspace-A");

        var appRegistry = new AppRegistry(new SystemClock(), _registryLogger.Object);
        appRegistry.RegisterInstance(new AppInstance
        {
            InstanceId = "launch-online-instance-1",
            AppId = "launch-online-instance.app",
            Scope = "workspace-A",
            Pid = 6510,
            Invoke = new InvokeCapability { Poll = true, Respond = true }
        });

        var processLauncher = new Mock<IProcessLauncher>();
        var coordinator = CreateCoordinator(processLauncher: processLauncher.Object, appRegistry: appRegistry);

        var result = await coordinator.LaunchAsync(
            appId: "launch-online-instance.app",
            scope: "workspace-A",
            dedupeKey: null,
            waitForRegisterMs: 0,
            CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal("already_running", result.Status);
        Assert.Equal(6510, result.Pid);
        Assert.Null(result.LaunchId);
        Assert.Null(result.DedupeKey);
        Assert.Equal("launch-online-instance-1", result.InstanceId);
        processLauncher.Verify(launcher => launcher.Start(It.IsAny<LaunchConfiguration>(), It.IsAny<string?>()), Times.Never);
    }

    [Fact]
    public async Task Impl_LaunchAsync_WithSameDedupeKeyWithinWindow_ShouldReturnAlreadyRunningAndReuseLaunchId()
    {
        WriteDefinition(
            "launch-dedupe-window.app",
            includeLaunch: true,
            dedupeKeyTemplate: "{appId}:{scopeOrGlobal}");

        var processLauncher = new Mock<IProcessLauncher>();
        processLauncher
            .Setup(launcher => launcher.Start(It.IsAny<LaunchConfiguration>(), It.IsAny<string?>()))
            .Returns(System.Diagnostics.Process.GetCurrentProcess());

        var coordinator = CreateCoordinator(processLauncher: processLauncher.Object);

        var first = await coordinator.LaunchAsync(
            appId: "launch-dedupe-window.app",
            scope: ScopeContract.Global,
            dedupeKey: null,
            waitForRegisterMs: 0,
            CancellationToken.None);

        var second = await coordinator.LaunchAsync(
            appId: "launch-dedupe-window.app",
            scope: ScopeContract.Global,
            dedupeKey: null,
            waitForRegisterMs: 0,
            CancellationToken.None);

        Assert.True(first.Ok);
        Assert.Equal("started", first.Status);
        Assert.True(second.Ok);
        Assert.Equal("already_running", second.Status);
        Assert.Equal(first.LaunchId, second.LaunchId);
        Assert.Equal(first.DedupeKey, second.DedupeKey);
        processLauncher.Verify(launcher => launcher.Start(It.IsAny<LaunchConfiguration>(), It.IsAny<string?>()), Times.Once);
    }

    [Fact]
    public async Task Impl_LaunchAsync_WhenScopeEmptyAndNullHitSameDedupeKey_ShouldInvokeProcessLauncherOnce()
    {
        WriteDefinition(
            "launch-global-scope-dedupe.app",
            includeLaunch: true,
            dedupeKeyTemplate: "{appId}:{scopeOrGlobal}");

        var processLauncher = new Mock<IProcessLauncher>();
        processLauncher
            .Setup(launcher => launcher.Start(It.IsAny<LaunchConfiguration>(), It.IsAny<string?>()))
            .Returns(System.Diagnostics.Process.GetCurrentProcess());

        var coordinator = CreateCoordinator(processLauncher: processLauncher.Object);

        var first = await coordinator.LaunchAsync(
            appId: "launch-global-scope-dedupe.app",
            scope: string.Empty,
            dedupeKey: null,
            waitForRegisterMs: 0,
            CancellationToken.None);

        var second = await coordinator.LaunchAsync(
            appId: "launch-global-scope-dedupe.app",
            scope: ScopeContract.Global,
            dedupeKey: null,
            waitForRegisterMs: 0,
            CancellationToken.None);

        Assert.True(first.Ok);
        Assert.True(second.Ok);
        Assert.Equal("already_running", second.Status);
        Assert.Equal(first.LaunchId, second.LaunchId);
        processLauncher.Verify(launcher => launcher.Start(It.IsAny<LaunchConfiguration>(), It.IsAny<string?>()), Times.Once);
    }

    [Fact]
    public async Task Impl_LaunchAsync_WhenDedupeWindowOverridden_ShouldRespectConfiguredWindow()
    {
        using var dedupeScope = new EnvironmentVariableScope(RuntimeTuningOptions.LaunchDedupeWindowSecondsEnvironmentVariable, "1");
        var tuningOptions = RuntimeTuningOptions.Resolve(Mock.Of<ILogger<RuntimeTuningOptions>>());

        WriteDefinition(
            "launch-dedupe-window-override.app",
            includeLaunch: true,
            dedupeKeyTemplate: "{appId}:{scopeOrGlobal}");

        var clock = new MutableClock(DateTime.UtcNow);
        var definitionLoader = new DefinitionLoader(DefinitionCatalogTestHelper.GetCatalogPath(_definitionsDirectory), _definitionLogger.Object);
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
            scope: ScopeContract.Global,
            dedupeKey: null,
            waitForRegisterMs: 0,
            CancellationToken.None);

        var second = await coordinator.LaunchAsync(
            appId: "launch-dedupe-window-override.app",
            scope: ScopeContract.Global,
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
            scope: ScopeContract.Global,
            dedupeKey: null,
            waitForRegisterMs: 0,
            CancellationToken.None);

        Assert.True(third.Ok);
        Assert.Equal("already_running", third.Status);
        Assert.Equal(first.LaunchId, third.LaunchId);
    }

    [Fact]
    public async Task Impl_LaunchAsync_WithExplicitDedupeKey_ShouldOverrideTemplate()
    {
        WriteDefinition(
            "launch-explicit-dedupe.app",
            includeLaunch: true,
            dedupeKeyTemplate: "{appId}:{httpBaseUrl}");

        var processLauncher = new Mock<IProcessLauncher>();
        processLauncher
            .Setup(launcher => launcher.Start(It.IsAny<LaunchConfiguration>(), It.IsAny<string?>()))
            .Returns(System.Diagnostics.Process.GetCurrentProcess());

        WriteHubRuntime("http://127.0.0.1:61001");
        var coordinator = CreateCoordinator(processLauncher: processLauncher.Object);

        var first = await coordinator.LaunchAsync(
            appId: "launch-explicit-dedupe.app",
            scope: ScopeContract.Global,
            dedupeKey: "manual-key",
            waitForRegisterMs: 0,
            CancellationToken.None);

        WriteHubRuntime("http://127.0.0.1:61002");
        var second = await coordinator.LaunchAsync(
            appId: "launch-explicit-dedupe.app",
            scope: ScopeContract.Global,
            dedupeKey: "manual-key",
            waitForRegisterMs: 0,
            CancellationToken.None);

        Assert.True(first.Ok);
        Assert.Equal("started", first.Status);
        Assert.True(second.Ok);
        Assert.Equal("already_running", second.Status);
        Assert.Equal(first.LaunchId, second.LaunchId);
        processLauncher.Verify(launcher => launcher.Start(It.IsAny<LaunchConfiguration>(), It.IsAny<string?>()), Times.Once);
    }

    [Fact]
    public async Task Impl_LaunchAsync_WhenHttpBaseUrlChanges_ShouldUseDifferentTemplateKey()
    {
        WriteDefinition(
            "launch-httpbaseurl-template.app",
            includeLaunch: true,
            dedupeKeyTemplate: "{appId}:{scopeOrGlobal}:{httpBaseUrl}");

        var processLauncher = new Mock<IProcessLauncher>();
        processLauncher
            .Setup(launcher => launcher.Start(It.IsAny<LaunchConfiguration>(), It.IsAny<string?>()))
            .Returns(System.Diagnostics.Process.GetCurrentProcess());
        var coordinator = CreateCoordinator(processLauncher: processLauncher.Object);

        WriteHubRuntime("http://127.0.0.1:62001");
        var first = await coordinator.LaunchAsync(
            appId: "launch-httpbaseurl-template.app",
            scope: ScopeContract.Global,
            dedupeKey: null,
            waitForRegisterMs: 0,
            CancellationToken.None);

        WriteHubRuntime("http://127.0.0.1:62002");
        var second = await coordinator.LaunchAsync(
            appId: "launch-httpbaseurl-template.app",
            scope: ScopeContract.Global,
            dedupeKey: null,
            waitForRegisterMs: 0,
            CancellationToken.None);

        Assert.True(first.Ok);
        Assert.Equal("started", first.Status);
        Assert.True(second.Ok);
        Assert.Equal("started", second.Status);
        Assert.NotEqual(first.LaunchId, second.LaunchId);
        processLauncher.Verify(launcher => launcher.Start(It.IsAny<LaunchConfiguration>(), It.IsAny<string?>()), Times.Exactly(2));
    }

    [Fact]
    public async Task Impl_LaunchAsync_ArgsTemplate_ShouldRenderSpecPlaceholders()
    {
        WriteDefinition(
            "launch-args-template.app",
            includeLaunch: true,
            exePath: "dotnet",
            argsTemplate: "{appId}|{scope}|{scopeOrGlobal}|{httpBaseUrl}",
            definitionScope: "workspace-A");

        string? capturedArguments = null;
        var processLauncher = new Mock<IProcessLauncher>();
        processLauncher
            .Setup(launcher => launcher.Start(It.IsAny<LaunchConfiguration>(), It.IsAny<string?>()))
            .Callback<LaunchConfiguration, string?>((_, args) => capturedArguments = args)
            .Returns(System.Diagnostics.Process.GetCurrentProcess());

        WriteHubRuntime("http://127.0.0.1:63001");
        var coordinator = CreateCoordinator(processLauncher: processLauncher.Object);

        var result = await coordinator.LaunchAsync(
            appId: "launch-args-template.app",
            scope: "workspace-A",
            dedupeKey: null,
            waitForRegisterMs: 0,
            CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal("started", result.Status);

        const string expected = "launch-args-template.app|workspace-A|workspace-A|http://127.0.0.1:63001";
        Assert.Equal(expected, capturedArguments);
    }

    [Fact]
    public async Task Impl_LaunchAsync_ArgsTemplate_WithNullScope_ShouldRenderEmptyScopeAndGlobalScopeOrGlobal()
    {
        WriteDefinition(
            "launch-args-template-global.app",
            includeLaunch: true,
            exePath: "dotnet",
            argsTemplate: "{appId}|{scope}|{scopeOrGlobal}|{httpBaseUrl}");

        string? capturedArguments = null;
        var processLauncher = new Mock<IProcessLauncher>();
        processLauncher
            .Setup(launcher => launcher.Start(It.IsAny<LaunchConfiguration>(), It.IsAny<string?>()))
            .Callback<LaunchConfiguration, string?>((_, args) => capturedArguments = args)
            .Returns(System.Diagnostics.Process.GetCurrentProcess());

        WriteHubRuntime("http://127.0.0.1:63002");
        var coordinator = CreateCoordinator(processLauncher: processLauncher.Object);

        var result = await coordinator.LaunchAsync(
            appId: "launch-args-template-global.app",
            scope: ScopeContract.Global,
            dedupeKey: null,
            waitForRegisterMs: 0,
            CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal("started", result.Status);

        const string expected = "launch-args-template-global.app||global|http://127.0.0.1:63002";
        Assert.Equal(expected, capturedArguments);
    }

    [Fact]
    public async Task Impl_LaunchAsync_ArgsTemplate_ShouldKeepUndocumentedTokensLiteral()
    {
        WriteDefinition(
            "launch-args-template-literal.app",
            includeLaunch: true,
            exePath: "dotnet",
            argsTemplate: "{dedupeKey}|{appId}|{scopeOrGlobal}",
            definitionScope: "workspace-B");

        string? capturedArguments = null;
        var processLauncher = new Mock<IProcessLauncher>();
        processLauncher
            .Setup(launcher => launcher.Start(It.IsAny<LaunchConfiguration>(), It.IsAny<string?>()))
            .Callback<LaunchConfiguration, string?>((_, args) => capturedArguments = args)
            .Returns(System.Diagnostics.Process.GetCurrentProcess());

        var coordinator = CreateCoordinator(processLauncher: processLauncher.Object);

        var result = await coordinator.LaunchAsync(
            appId: "launch-args-template-literal.app",
            scope: "workspace-B",
            dedupeKey: "manual-key",
            waitForRegisterMs: 0,
            CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal("started", result.Status);
        Assert.Equal("{dedupeKey}|launch-args-template-literal.app|workspace-B", capturedArguments);
    }

    [Fact]
    public async Task Impl_LaunchAsync_StructuredArgs_ShouldRenderEachArgumentAndOverrideArgsTemplate()
    {
        WriteDefinition(
            "launch-structured-args.app",
            includeLaunch: true,
            exePath: "dotnet",
            argsTemplate: "--ignored {appId}",
            args: ["--app", "{appId}", "--scope", "{scopeOrGlobal}", "{unknown}"]);

        LaunchConfiguration? capturedLaunchConfig = null;
        string? capturedArguments = "sentinel";
        var processLauncher = new Mock<IProcessLauncher>();
        processLauncher
            .Setup(launcher => launcher.Start(It.IsAny<LaunchConfiguration>(), It.IsAny<string?>()))
            .Callback<LaunchConfiguration, string?>((launchConfig, args) =>
            {
                capturedLaunchConfig = launchConfig;
                capturedArguments = args;
            })
            .Returns(System.Diagnostics.Process.GetCurrentProcess());

        var coordinator = CreateCoordinator(processLauncher: processLauncher.Object);

        var result = await coordinator.LaunchAsync(
            appId: "launch-structured-args.app",
            scope: ScopeContract.Global,
            dedupeKey: null,
            waitForRegisterMs: 0,
            CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal("started", result.Status);
        Assert.Null(capturedArguments);
        Assert.NotNull(capturedLaunchConfig);
        Assert.Equal(new[] { "--app", "launch-structured-args.app", "--scope", "global", "{unknown}" }, capturedLaunchConfig!.Args);
    }

    [Fact]
    public async Task Impl_LaunchAsync_ArgsTemplate_ShouldSplitQuotedArgumentsForProcessStart()
    {
        WriteDefinition(
            "launch-args-template-split.app",
            includeLaunch: true,
            exePath: "dotnet",
            argsTemplate: "--name \"hello world\" '--literal value' plain\\ value");

        LaunchConfiguration? capturedLaunchConfig = null;
        var processLauncher = new Mock<IProcessLauncher>();
        processLauncher
            .Setup(launcher => launcher.Start(It.IsAny<LaunchConfiguration>(), It.IsAny<string?>()))
            .Callback<LaunchConfiguration, string?>((launchConfig, _) => capturedLaunchConfig = launchConfig)
            .Returns(System.Diagnostics.Process.GetCurrentProcess());

        var coordinator = CreateCoordinator(processLauncher: processLauncher.Object);

        var result = await coordinator.LaunchAsync(
            appId: "launch-args-template-split.app",
            scope: ScopeContract.Global,
            dedupeKey: null,
            waitForRegisterMs: 0,
            CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal(new[] { "--name", "hello world", "--literal value", "plain value" }, capturedLaunchConfig!.Args);
    }

    [Fact]
    public async Task Impl_LaunchAsync_ArgsTemplate_WhenMalformed_ShouldReturnInvalidParams()
    {
        WriteDefinition(
            "launch-args-template-malformed.app",
            includeLaunch: true,
            exePath: "dotnet",
            argsTemplate: "\"unterminated");

        var processLauncher = new Mock<IProcessLauncher>();
        var coordinator = CreateCoordinator(processLauncher: processLauncher.Object);

        var result = await coordinator.LaunchAsync(
            appId: "launch-args-template-malformed.app",
            scope: ScopeContract.Global,
            dedupeKey: null,
            waitForRegisterMs: 0,
            CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(-32602, result.ErrorCode);
        Assert.Equal("invalid_params", result.ErrorMessage);
        var errorData = JsonSerializer.SerializeToElement(result.ErrorData);
        Assert.Equal("invalid_launch_args_template", errorData.GetProperty("reason").GetString());
        processLauncher.Verify(launcher => launcher.Start(It.IsAny<LaunchConfiguration>(), It.IsAny<string?>()), Times.Never);
    }

    [Fact]
    public async Task Impl_LaunchAsync_WhenProcessLauncherReturnsNull_ShouldReturnLaunchFailed()
    {
        WriteDefinition("launch-null-process.app", includeLaunch: true);

        var processLauncher = new Mock<IProcessLauncher>();
        processLauncher
            .Setup(launcher => launcher.Start(It.IsAny<LaunchConfiguration>(), It.IsAny<string?>()))
            .Returns((System.Diagnostics.Process?)null);

        var coordinator = CreateCoordinator(processLauncher: processLauncher.Object);

        var result = await coordinator.LaunchAsync(
            appId: "launch-null-process.app",
            scope: ScopeContract.Global,
            dedupeKey: null,
            waitForRegisterMs: 0,
            CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(-32020, result.ErrorCode);
        Assert.Equal("launch_failed", result.ErrorMessage);
        var errorData = JsonSerializer.SerializeToElement(result.ErrorData);
        Assert.Equal("process_start_failed", errorData.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Impl_LaunchAsync_WhenProcessLauncherThrows_ShouldReturnLaunchFailedWithStderr()
    {
        WriteDefinition("launch-throws-process.app", includeLaunch: true);

        var processLauncher = new Mock<IProcessLauncher>();
        processLauncher
            .Setup(launcher => launcher.Start(It.IsAny<LaunchConfiguration>(), It.IsAny<string?>()))
            .Throws(new InvalidOperationException("mock launcher failed"));

        var coordinator = CreateCoordinator(processLauncher: processLauncher.Object);

        var result = await coordinator.LaunchAsync(
            appId: "launch-throws-process.app",
            scope: ScopeContract.Global,
            dedupeKey: null,
            waitForRegisterMs: 0,
            CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(-32020, result.ErrorCode);
        Assert.Equal("launch_failed", result.ErrorMessage);
        var errorData = JsonSerializer.SerializeToElement(result.ErrorData);
        Assert.Equal("process_start_failed", errorData.GetProperty("reason").GetString());
        Assert.Equal("mock launcher failed", errorData.GetProperty("stderr").GetString());
    }

    [Fact]
    public async Task Impl_LaunchAsync_WhenWaitForRegisterAndTrackedInstanceAppears_ShouldReturnStarted()
    {
        WriteDefinition("launch-wait-register.app", includeLaunch: true);

        var clock = new SystemClock();
        using var appRegistry = new AppRegistry(clock, _registryLogger.Object);
        LaunchCoordinator? coordinator = null;
        var processLauncher = new Mock<IProcessLauncher>();
        processLauncher
            .Setup(launcher => launcher.Start(It.IsAny<LaunchConfiguration>(), It.IsAny<string?>()))
            .Callback<LaunchConfiguration, string?>((launchConfiguration, _) =>
            {
                var launchId = launchConfiguration.EnvironmentVariables![LaunchCoordinator.LaunchIdEnvironmentVariable];
                var validation = coordinator!.ValidateRegistration(launchId, "launch-wait-register.app", ScopeContract.Global);
                Assert.Equal(LaunchRegistrationValidationStatus.Matched, validation.Status);

                var instance = appRegistry.RegisterInstance(new AppInstance
                {
                    InstanceId = "launch-wait-register-instance",
                    AppId = "launch-wait-register.app",
                    Scope = ScopeContract.Global,
                    Pid = 6501,
                    Invoke = new InvokeCapability { Poll = true, Respond = true }
                });

                coordinator.RecordSuccessfulRegistration(launchId, instance);
            })
            .Returns(System.Diagnostics.Process.GetCurrentProcess());

        coordinator = CreateCoordinator(clock, processLauncher.Object, appRegistry);

        var result = await coordinator.LaunchAsync(
            appId: "launch-wait-register.app",
            scope: ScopeContract.Global,
            dedupeKey: null,
            waitForRegisterMs: 600,
            CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal("started", result.Status);
    }

    [Fact]
    public async Task Impl_LaunchAsync_WhenRegisterDeadlineElapsed_ShouldReturnLaunchRegisterTimeoutAndReleaseDedupe()
    {
        WriteDefinition("launch-register-timeout.app", includeLaunch: true);

        var clock = new MutableClock(DateTime.UtcNow);
        var tuningOptions = RuntimeTuningOptions.Create(
            leaseSeconds: 30,
            onlineThresholdSeconds: 30,
            launchDedupeWindowSeconds: 30,
            launchRegisterTimeoutSeconds: 1);
        var processLauncher = new Mock<IProcessLauncher>();
        processLauncher
            .Setup(launcher => launcher.Start(It.IsAny<LaunchConfiguration>(), It.IsAny<string?>()))
            .Returns(System.Diagnostics.Process.GetCurrentProcess());
        var coordinator = CreateCoordinator(clock, processLauncher.Object, runtimeTuningOptions: tuningOptions);

        var launchTask = coordinator.LaunchAsync(
            appId: "launch-register-timeout.app",
            scope: ScopeContract.Global,
            dedupeKey: "register-timeout",
            waitForRegisterMs: 5000,
            CancellationToken.None);

        await Task.Delay(60);
        clock.Advance(TimeSpan.FromSeconds(2));

        var result = await launchTask;
        Assert.False(result.Ok);
        Assert.Equal(-32020, result.ErrorCode);
        Assert.Equal("launch_failed", result.ErrorMessage);
        var errorData = JsonSerializer.SerializeToElement(result.ErrorData);
        Assert.Equal("launch_register_timeout", errorData.GetProperty("reason").GetString());

        var retry = await coordinator.LaunchAsync(
            appId: "launch-register-timeout.app",
            scope: ScopeContract.Global,
            dedupeKey: "register-timeout",
            waitForRegisterMs: 0,
            CancellationToken.None);

        Assert.True(retry.Ok);
        Assert.Equal("started", retry.Status);
        Assert.NotEqual(result.LaunchId, retry.LaunchId);
        processLauncher.Verify(launcher => launcher.Start(It.IsAny<LaunchConfiguration>(), It.IsAny<string?>()), Times.Exactly(2));
    }

    [Fact]
    public async Task Impl_LaunchAsync_WhenArgsTemplateMissing_ShouldPassNullArgumentsToProcessLauncher()
    {
        WriteDefinitionWithoutArgsTemplate("launch-null-args-template.app");

        string? capturedArguments = "sentinel";
        var processLauncher = new Mock<IProcessLauncher>();
        processLauncher
            .Setup(launcher => launcher.Start(It.IsAny<LaunchConfiguration>(), It.IsAny<string?>()))
            .Callback<LaunchConfiguration, string?>((_, args) => capturedArguments = args)
            .Returns(System.Diagnostics.Process.GetCurrentProcess());

        var coordinator = CreateCoordinator(processLauncher: processLauncher.Object);

        var result = await coordinator.LaunchAsync(
            appId: "launch-null-args-template.app",
            scope: ScopeContract.Global,
            dedupeKey: null,
            waitForRegisterMs: 0,
            CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal("started", result.Status);
        Assert.Null(capturedArguments);
    }

    /// <summary>
    /// 释放测试资源。
    /// </summary>
    public void Dispose()
    {
        _dataScope.Dispose();

        if (Directory.Exists(_dataDirectory))
        {
            Directory.Delete(_dataDirectory, recursive: true);
        }
    }

    private LaunchCoordinator CreateCoordinator(
        IClock? clock = null,
        IProcessLauncher? processLauncher = null,
        AppRegistry? appRegistry = null,
        RuntimeTuningOptions? runtimeTuningOptions = null)
    {
        var effectiveClock = clock ?? new SystemClock();
        var definitionLoader = new DefinitionLoader(DefinitionCatalogTestHelper.GetCatalogPath(_definitionsDirectory), _definitionLogger.Object);
        var definitionProvider = new DefinitionProvider(definitionLoader);
        definitionProvider.Refresh();
        var effectiveAppRegistry = appRegistry ?? new AppRegistry(effectiveClock, _registryLogger.Object);
        var provider = new RuntimeHttpBaseUrlProvider(Mock.Of<ILogger<RuntimeHttpBaseUrlProvider>>(), RuntimePathOptions.Resolve());
        return new LaunchCoordinator(
            definitionProvider,
            effectiveAppRegistry,
            provider,
            processLauncher ?? new ProcessLauncher(),
            effectiveClock,
            runtimeTuningOptions ?? RuntimeTuningOptions.Default,
            _launchLogger.Object);
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

    private void WriteDefinition(
        string appId,
        bool includeLaunch,
        string? dedupeKeyTemplate = null,
        string? argsTemplate = null,
        string[]? args = null,
        string? exePath = null,
        string? definitionScope = ScopeContract.Global)
    {
        var normalizedScope = definitionScope ?? ScopeContract.Global;
        var payload = new Dictionary<string, object?>
        {
            ["appId"] = appId,
            ["scope"] = normalizedScope,
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

            if (args is not null)
            {
                launch["args"] = args;
            }

            if (!string.IsNullOrWhiteSpace(dedupeKeyTemplate))
            {
                launch["dedupeKeyTemplate"] = dedupeKeyTemplate;
            }

            payload["launch"] = launch;
        }

        DefinitionCatalogTestHelper.UpsertDefinition(
            DefinitionCatalogTestHelper.GetCatalogPath(_definitionsDirectory),
            JsonSerializer.Serialize(payload));
    }

    private void WriteDefinitionWithoutArgsTemplate(string appId)
    {
        var payload = new Dictionary<string, object?>
        {
            ["appId"] = appId,
            ["scope"] = ScopeContract.Global,
            ["displayName"] = appId,
            ["capabilities"] = new Dictionary<string, object?>
            {
                ["rpc"] = true,
                ["events"] = false
            },
            ["launch"] = new Dictionary<string, object?>
            {
                ["exePath"] = "dotnet"
            }
        };

        DefinitionCatalogTestHelper.UpsertDefinition(
            DefinitionCatalogTestHelper.GetCatalogPath(_definitionsDirectory),
            JsonSerializer.Serialize(payload));
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
