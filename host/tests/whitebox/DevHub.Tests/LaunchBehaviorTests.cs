namespace DevHub.Tests;

using System.Text.Json;
using DevHub.Core.Models;
using DevHub.Core.Models.Rpc;
using DevHub.Core.Services;
using DevHub.Core.Services.Invocation;
using DevHub.Core.Services.Rpc.Handlers;
using Microsoft.Extensions.Logging;
using Moq;

/// <summary>
/// Launch 实现行为白盒测试。
/// </summary>
[Trait("Category", "Impl")]
public class LaunchBehaviorTests : IDisposable
{
    private readonly string _tempDirectory;

    public LaunchBehaviorTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "DevHubLaunchBehaviorTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
    }

    [Fact]
    public async Task Impl_6_3_12_Launch_WhenWaitForRegisterMsNegative_ShouldReturnInvalidParams()
    {
        var handler = CreateLaunchHandler();

        var response = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "spec-6.3.9-negative-wait",
            Method = "hub.apps.launch",
            Params = JsonSerializer.SerializeToElement(new
            {
                appId = "spec-6.3.9-negative-wait",
                scope = ScopeContract.Global,
                waitForRegisterMs = -1
            })
        }, CancellationToken.None);

        AssertError(response, -32602, "invalid_params");
    }

    [Fact]
    public async Task Impl_6_3_12_Launch_WhenScopeEmpty_ShouldResolveGlobalDefinition()
    {
        const string appId = "spec-6.3.9-empty-scope";
        WriteDefinition(appId, includeLaunch: true);

        var handler = CreateLaunchHandler();

        var response = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "spec-6.3.9-empty-scope",
            Method = "hub.apps.launch",
            Params = JsonSerializer.SerializeToElement(new
            {
                appId,
                scope = string.Empty,
                waitForRegisterMs = 0
            })
        }, CancellationToken.None);

        AssertSuccess(response);
        var result = JsonSerializer.SerializeToElement(response.Result);
        Assert.Equal("started", result.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Impl_6_3_12_Launch_WhenDefinitionMissing_ShouldReturnAppDefinitionNotFound()
    {
        var handler = CreateLaunchHandler();

        var response = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "spec-6.3.9-missing-definition",
            Method = "hub.apps.launch",
            Params = JsonSerializer.SerializeToElement(new
            {
                appId = "spec-6.3.9-missing-definition",
                scope = ScopeContract.Global
            })
        }, CancellationToken.None);

        AssertError(response, -32014, "app_definition_not_found");
        var data = JsonSerializer.SerializeToElement(response.Error!.Data);
        Assert.Equal("spec-6.3.9-missing-definition", data.GetProperty("appId").GetString());
    }

    [Fact]
    public async Task Impl_6_3_12_Launch_WhenLaunchConfigMissing_ShouldReturnLaunchFailedWithLaunchConfigMissing()
    {
        const string appId = "spec-6.3.9-launch-config-missing";
        WriteDefinition(appId, includeLaunch: false);

        var handler = CreateLaunchHandler();

        var response = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "spec-6.3.9-launch-config-missing",
            Method = "hub.apps.launch",
            Params = JsonSerializer.SerializeToElement(new
            {
                appId,
                scope = ScopeContract.Global,
                waitForRegisterMs = 0
            })
        }, CancellationToken.None);

        AssertError(response, -32020, "launch_failed");
        var data = JsonSerializer.SerializeToElement(response.Error!.Data);
        Assert.Equal("launch_config_missing", data.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Impl_6_3_12_Launch_WhenProcessCreationFails_ShouldReturnLaunchFailedWithProcessStartFailed()
    {
        const string appId = "spec-6.3.9-process-failed";
        WriteDefinition(appId, includeLaunch: true);

        var processLauncher = new Mock<IProcessLauncher>();
        processLauncher
            .Setup(launcher => launcher.Start(It.IsAny<LaunchConfiguration>(), It.IsAny<string?>()))
            .Returns((System.Diagnostics.Process?)null);

        var handler = CreateLaunchHandler(processLauncher: processLauncher.Object);

        var response = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "spec-6.3.9-process-failed",
            Method = "hub.apps.launch",
            Params = JsonSerializer.SerializeToElement(new
            {
                appId,
                scope = ScopeContract.Global,
                waitForRegisterMs = 0
            })
        }, CancellationToken.None);

        AssertError(response, -32020, "launch_failed");
        var data = JsonSerializer.SerializeToElement(response.Error!.Data);
        Assert.Equal("process_start_failed", data.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Impl_6_3_12_Launch_WhenWaitForRegisterMsPositiveAndRegisterTimeout_ShouldReturnStarting()
    {
        const string appId = "spec-6.3.9-starting";
        WriteDefinition(appId, includeLaunch: true);

        var processLauncher = new Mock<IProcessLauncher>();
        processLauncher
            .Setup(launcher => launcher.Start(It.IsAny<LaunchConfiguration>(), It.IsAny<string?>()))
            .Returns(System.Diagnostics.Process.GetCurrentProcess());

        var handler = CreateLaunchHandler(processLauncher: processLauncher.Object);

        var response = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "spec-6.3.9-starting",
            Method = "hub.apps.launch",
            Params = JsonSerializer.SerializeToElement(new
            {
                appId,
                scope = ScopeContract.Global,
                waitForRegisterMs = 80
            })
        }, CancellationToken.None);

        AssertSuccess(response);
        var result = JsonSerializer.SerializeToElement(response.Result);
        Assert.Equal("starting", result.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Impl_6_3_12_Launch_WhenProcessStarted_ShouldReturnStarted()
    {
        const string appId = "spec-6.3.9-started";
        WriteDefinition(appId, includeLaunch: true);

        var startedProcess = System.Diagnostics.Process.GetCurrentProcess();
        var processLauncher = new Mock<IProcessLauncher>();
        processLauncher
            .Setup(launcher => launcher.Start(It.IsAny<LaunchConfiguration>(), It.IsAny<string?>()))
            .Returns(startedProcess);

        var handler = CreateLaunchHandler(processLauncher: processLauncher.Object);

        var response = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "spec-6.3.9-started",
            Method = "hub.apps.launch",
            Params = JsonSerializer.SerializeToElement(new
            {
                appId,
                scope = ScopeContract.Global,
                waitForRegisterMs = 0
            })
        }, CancellationToken.None);

        AssertSuccess(response);
        var result = JsonSerializer.SerializeToElement(response.Result);
        Assert.Equal("started", result.GetProperty("status").GetString());
        Assert.Equal(startedProcess.Id, result.GetProperty("pid").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(result.GetProperty("launchId").GetString()));
        Assert.Equal($"{appId}:global", result.GetProperty("dedupeKey").GetString());
    }

    [Fact]
    public async Task Impl_6_3_12_Launch_WhenMatchingOnlineInstanceExists_ShouldReturnAlreadyRunning()
    {
        const string appId = "spec-6.3.9-online-instance";
        const string scope = "workspace-A";
        WriteDefinition(appId, includeLaunch: true, definitionScope: scope);

        using var appRegistry = new AppRegistry(new SystemClock(), Mock.Of<ILogger<AppRegistry>>());
        appRegistry.RegisterInstance(new AppInstance
        {
            InstanceId = "spec-6.3.9-online-instance-id",
            AppId = appId,
            Scope = scope,
            Pid = 7890
        });

        var processLauncher = new Mock<IProcessLauncher>();
        var handler = CreateLaunchHandler(processLauncher: processLauncher.Object, appRegistry: appRegistry);

        var response = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "spec-6.3.9-online-instance",
            Method = "hub.apps.launch",
            Params = JsonSerializer.SerializeToElement(new
            {
                appId,
                scope,
                waitForRegisterMs = 0
            })
        }, CancellationToken.None);

        AssertSuccess(response);
        var result = JsonSerializer.SerializeToElement(response.Result);
        Assert.Equal("already_running", result.GetProperty("status").GetString());
        Assert.Equal(7890, result.GetProperty("pid").GetInt32());
        Assert.Equal("spec-6.3.9-online-instance-id", result.GetProperty("instanceId").GetString());
        Assert.False(result.TryGetProperty("launchId", out _));
        Assert.False(result.TryGetProperty("dedupeKey", out _));
    }

    [Fact]
    public async Task Impl_6_3_12_Launch_WhenDedupeHitsWithinWindow_ShouldReturnAlreadyRunningAndReuseLaunchId()
    {
        const string appId = "spec-6.3.9-dedupe-hit";
        WriteDefinition(appId, includeLaunch: true, dedupeKeyTemplate: "{appId}:{scopeOrGlobal}", definitionScope: "workspace-A");

        var processLauncher = new Mock<IProcessLauncher>();
        processLauncher
            .Setup(launcher => launcher.Start(It.IsAny<LaunchConfiguration>(), It.IsAny<string?>()))
            .Returns(System.Diagnostics.Process.GetCurrentProcess());

        var handler = CreateLaunchHandler(processLauncher: processLauncher.Object);

        var first = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "spec-6.3.9-dedupe-first",
            Method = "hub.apps.launch",
            Params = JsonSerializer.SerializeToElement(new
            {
                appId,
                scope = "workspace-A",
                waitForRegisterMs = 0
            })
        }, CancellationToken.None);

        var second = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "spec-6.3.9-dedupe-second",
            Method = "hub.apps.launch",
            Params = JsonSerializer.SerializeToElement(new
            {
                appId,
                scope = "workspace-A",
                waitForRegisterMs = 0
            })
        }, CancellationToken.None);

        AssertSuccess(first);
        AssertSuccess(second);

        var firstResult = JsonSerializer.SerializeToElement(first.Result);
        var secondResult = JsonSerializer.SerializeToElement(second.Result);

        Assert.Equal("already_running", secondResult.GetProperty("status").GetString());
        Assert.Equal(firstResult.GetProperty("launchId").GetString(), secondResult.GetProperty("launchId").GetString());
        Assert.Equal(firstResult.GetProperty("dedupeKey").GetString(), secondResult.GetProperty("dedupeKey").GetString());
    }

    [Fact]
    public async Task Impl_6_3_12_Launch_WhenDedupeTemplateMissing_ShouldUseDefaultTemplate()
    {
        const string appId = "spec-6.3.9-default-dedupe-template";
        WriteDefinition(appId, includeLaunch: true, dedupeKeyTemplate: null);

        var processLauncher = new Mock<IProcessLauncher>();
        processLauncher
            .Setup(launcher => launcher.Start(It.IsAny<LaunchConfiguration>(), It.IsAny<string?>()))
            .Returns(System.Diagnostics.Process.GetCurrentProcess());

        var handler = CreateLaunchHandler(processLauncher: processLauncher.Object);

        var first = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "spec-6.3.9-default-dedupe-first",
            Method = "hub.apps.launch",
            Params = JsonSerializer.SerializeToElement(new
            {
                appId,
                scope = ScopeContract.Global,
                waitForRegisterMs = 0
            })
        }, CancellationToken.None);

        var second = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "spec-6.3.9-default-dedupe-second",
            Method = "hub.apps.launch",
            Params = JsonSerializer.SerializeToElement(new
            {
                appId,
                scope = ScopeContract.Global,
                waitForRegisterMs = 0
            })
        }, CancellationToken.None);

        AssertSuccess(first);
        AssertSuccess(second);

        var firstResult = JsonSerializer.SerializeToElement(first.Result);
        var secondResult = JsonSerializer.SerializeToElement(second.Result);

        Assert.Equal("already_running", secondResult.GetProperty("status").GetString());
        Assert.Equal(firstResult.GetProperty("launchId").GetString(), secondResult.GetProperty("launchId").GetString());
        Assert.Equal($"{appId}:global", secondResult.GetProperty("dedupeKey").GetString());
    }

    [Fact]
    public async Task Impl_6_3_12_Launch_WhenTemplatesContainSpecPlaceholders_ShouldRenderArgsAndDedupeByRenderedKey()
    {
        const string appId = "spec-6.3.9-template-placeholders";
        const string httpBaseUrl = "http://127.0.0.1:7361";
        const string scope = "workspace-A";

        WriteDefinition(
            appId,
            includeLaunch: true,
            dedupeKeyTemplate: "{appId}:{scope}:{scopeOrGlobal}:{httpBaseUrl}",
            argsTemplate: "{appId}|{scope}|{scopeOrGlobal}|{httpBaseUrl}",
            definitionScope: scope);

        string? renderedArguments = null;
        var processLauncher = new Mock<IProcessLauncher>();
        processLauncher
            .Setup(launcher => launcher.Start(It.IsAny<LaunchConfiguration>(), It.IsAny<string?>()))
            .Callback<LaunchConfiguration, string?>((_, args) => renderedArguments = args)
            .Returns(System.Diagnostics.Process.GetCurrentProcess());

        var handler = CreateLaunchHandler(processLauncher: processLauncher.Object, httpBaseUrl: httpBaseUrl);

        var first = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "spec-6.3.9-template-first",
            Method = "hub.apps.launch",
            Params = JsonSerializer.SerializeToElement(new
            {
                appId,
                scope,
                waitForRegisterMs = 0
            })
        }, CancellationToken.None);

        var second = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "spec-6.3.9-template-second",
            Method = "hub.apps.launch",
            Params = JsonSerializer.SerializeToElement(new
            {
                appId,
                scope,
                waitForRegisterMs = 0
            })
        }, CancellationToken.None);

        AssertSuccess(first);
        AssertSuccess(second);

        Assert.Equal($"{appId}|{scope}|{scope}|{httpBaseUrl}", renderedArguments);

        var firstResult = JsonSerializer.SerializeToElement(first.Result);
        var secondResult = JsonSerializer.SerializeToElement(second.Result);

        Assert.Equal("already_running", secondResult.GetProperty("status").GetString());
        Assert.Equal(firstResult.GetProperty("launchId").GetString(), secondResult.GetProperty("launchId").GetString());
        Assert.Equal($"{appId}:{scope}:{scope}:{httpBaseUrl}", secondResult.GetProperty("dedupeKey").GetString());
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }

    private LaunchHandler CreateLaunchHandler(
        IClock? clock = null,
        IProcessLauncher? processLauncher = null,
        AppRegistry? appRegistry = null,
        string httpBaseUrl = "http://127.0.0.1:7301")
    {
        var effectiveClock = clock ?? new SystemClock();
        var definitionLoader = new DefinitionLoader(DefinitionCatalogTestHelper.GetCatalogPath(_tempDirectory), Mock.Of<ILogger<DefinitionLoader>>());
        var definitionProvider = new DefinitionProvider(definitionLoader);
        definitionProvider.Refresh();
        var effectiveAppRegistry = appRegistry ?? new AppRegistry(effectiveClock, Mock.Of<ILogger<AppRegistry>>());

        var runtimeProvider = new Mock<IRuntimeHttpBaseUrlProvider>();
        runtimeProvider
            .Setup(provider => provider.GetHttpBaseUrl())
            .Returns(httpBaseUrl);

        var coordinator = new LaunchCoordinator(
            definitionProvider,
            effectiveAppRegistry,
            runtimeProvider.Object,
            processLauncher ?? new ProcessLauncher(),
            effectiveClock,
            Mock.Of<ILogger<LaunchCoordinator>>());

        return new LaunchHandler(coordinator, Mock.Of<ILogger<LaunchHandler>>());
    }

    private void WriteDefinition(
        string appId,
        bool includeLaunch,
        string? dedupeKeyTemplate = null,
        string? argsTemplate = "--version",
        string? definitionScope = null)
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
                ["exePath"] = "dotnet",
                ["argsTemplate"] = argsTemplate
            };

            if (dedupeKeyTemplate is not null)
            {
                launch["dedupeKeyTemplate"] = dedupeKeyTemplate;
            }

            payload["launch"] = launch;
        }

        DefinitionCatalogTestHelper.UpsertDefinition(
            DefinitionCatalogTestHelper.GetCatalogPath(_tempDirectory),
            JsonSerializer.Serialize(payload));
    }

    private static void AssertSuccess(JsonRpcResponse response)
    {
        Assert.Null(response.Error);
        Assert.NotNull(response.Result);
        var result = JsonSerializer.SerializeToElement(response.Result);
        Assert.True(result.GetProperty("ok").GetBoolean());
    }

    private static void AssertError(JsonRpcResponse response, int expectedCode, string expectedMessage)
    {
        Assert.NotNull(response.Error);
        Assert.Equal(expectedCode, response.Error!.Code);
        Assert.Equal(expectedMessage, response.Error.Message);
    }
}
