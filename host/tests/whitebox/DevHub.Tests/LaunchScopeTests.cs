namespace DevHub.Tests;

using System.Text.Json;
using DevHub.Core.Models;
using DevHub.Core.Models.Rpc;
using DevHub.Core.Services;
using DevHub.Core.Services.Abstractions;
using DevHub.Core.Services.Invocation;
using DevHub.Core.Services.Rpc;
using DevHub.Core.Services.Rpc.Handlers;
using Microsoft.Extensions.Logging;
using Moq;

/// <summary>
/// Launch scope 语义专项测试。
/// </summary>
[Trait("Category", "Impl")]
public class LaunchScopeTests : IDisposable
{
    private readonly string _dataDirectory;
    private readonly string _definitionsDirectory;
    private readonly string _runtimeDirectory;
    private readonly EnvironmentVariableScope _dataScope;
    private readonly Mock<ILogger<DefinitionLoader>> _definitionLogger = new();
    private readonly Mock<ILogger<AppRegistry>> _registryLogger = new();
    private readonly Mock<ILogger<LaunchCoordinator>> _launchLogger = new();
    private readonly Mock<ILogger<InvocationRoutingService>> _routingLogger = new();
    private readonly Mock<ILogger<InvocationStore>> _storeLogger = new();
    private readonly Mock<ILogger<InvocationHandler>> _invocationHandlerLogger = new();
    private readonly Mock<ILogger<LaunchHandler>> _launchHandlerLogger = new();

    /// <summary>
    /// 初始化测试上下文。
    /// </summary>
    public LaunchScopeTests()
    {
        _dataDirectory = Path.Combine(Path.GetTempPath(), "DevHubLaunchScopeTests", Guid.NewGuid().ToString("N"));
        _definitionsDirectory = Path.Combine(_dataDirectory, "apps", "definitions");
        _runtimeDirectory = Path.Combine(_dataDirectory, "runtime");
        Directory.CreateDirectory(_dataDirectory);
        Directory.CreateDirectory(_definitionsDirectory);
        Directory.CreateDirectory(_runtimeDirectory);
        _dataScope = new EnvironmentVariableScope(RuntimePathOptions.DataDirEnvironmentVariable, _dataDirectory);
    }

    [Fact]
    public async Task Impl_LaunchHandler_WhenScopeGlobal_ShouldBeTreatedAsExplicitScope()
    {
        var definitionLoader = new DefinitionLoader(DefinitionCatalogTestHelper.GetCatalogPath(_definitionsDirectory), _definitionLogger.Object);
        var definitionProvider = new DefinitionProvider(definitionLoader);
        definitionProvider.Refresh();
        var appRegistry = new AppRegistry(new SystemClock(), _registryLogger.Object);
        var provider = new RuntimeHttpBaseUrlProvider(Mock.Of<ILogger<RuntimeHttpBaseUrlProvider>>(), RuntimePathOptions.Resolve());
        var coordinator = new LaunchCoordinator(definitionProvider, appRegistry, provider, new ProcessLauncher(), new SystemClock(), _launchLogger.Object);
        var handler = new LaunchHandler(coordinator, _launchHandlerLogger.Object);

        var response = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "launch-scope-global",
            Method = "hub.apps.launch",
            Params = JsonSerializer.SerializeToElement(new
            {
                appId = "scope-launch-app",
                scope = "global"
            })
        }, CancellationToken.None);

        Assert.NotNull(response.Error);
        Assert.Equal(-32014, response.Error.Code);
        Assert.Equal("app_definition_not_found", response.Error.Message);
        var data = JsonSerializer.SerializeToElement(response.Error.Data);
        Assert.Equal("scope-launch-app", data.GetProperty("appId").GetString());
    }

    [Fact]
    public async Task Impl_LaunchHandler_WhenScopeEmpty_ShouldBeTreatedAsGlobal()
    {
        var definitionLoader = new DefinitionLoader(DefinitionCatalogTestHelper.GetCatalogPath(_definitionsDirectory), _definitionLogger.Object);
        var definitionProvider = new DefinitionProvider(definitionLoader);
        definitionProvider.Refresh();
        var appRegistry = new AppRegistry(new SystemClock(), _registryLogger.Object);
        var provider = new RuntimeHttpBaseUrlProvider(Mock.Of<ILogger<RuntimeHttpBaseUrlProvider>>(), RuntimePathOptions.Resolve());
        var coordinator = new LaunchCoordinator(definitionProvider, appRegistry, provider, new ProcessLauncher(), new SystemClock(), _launchLogger.Object);
        var handler = new LaunchHandler(coordinator, _launchHandlerLogger.Object);

        var response = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "launch-scope-empty",
            Method = "hub.apps.launch",
            Params = JsonSerializer.SerializeToElement(new
            {
                appId = "scope-launch-app",
                scope = string.Empty
            })
        }, CancellationToken.None);

        Assert.NotNull(response.Error);
        Assert.Equal(-32014, response.Error.Code);
        Assert.Equal("app_definition_not_found", response.Error.Message);
        var emptyData = JsonSerializer.SerializeToElement(response.Error.Data);
        Assert.Equal(ScopeContract.Global, emptyData.GetProperty("scope").GetString());
    }

    [Fact]
    public async Task Impl_LaunchHandler_WhenScopeOmittedOrNull_ShouldReturnInvalidParams()
    {
        var definitionLoader = new DefinitionLoader(DefinitionCatalogTestHelper.GetCatalogPath(_definitionsDirectory), _definitionLogger.Object);
        var definitionProvider = new DefinitionProvider(definitionLoader);
        definitionProvider.Refresh();
        var appRegistry = new AppRegistry(new SystemClock(), _registryLogger.Object);
        var provider = new RuntimeHttpBaseUrlProvider(Mock.Of<ILogger<RuntimeHttpBaseUrlProvider>>(), RuntimePathOptions.Resolve());
        var coordinator = new LaunchCoordinator(definitionProvider, appRegistry, provider, new ProcessLauncher(), new SystemClock(), _launchLogger.Object);
        var handler = new LaunchHandler(coordinator, _launchHandlerLogger.Object);

        var omittedScopeResponse = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "launch-omitted-scope",
            Method = "hub.apps.launch",
            Params = JsonSerializer.SerializeToElement(new
            {
                appId = "missing-scope-equivalent-app"
            })
        }, CancellationToken.None);

        var nullScopeResponse = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "launch-null-scope",
            Method = "hub.apps.launch",
            Params = JsonSerializer.SerializeToElement(new
            {
                appId = "missing-scope-equivalent-app",
                scope = (string?)null
            })
        }, CancellationToken.None);

        Assert.NotNull(omittedScopeResponse.Error);
        Assert.NotNull(nullScopeResponse.Error);
        Assert.Equal(-32602, omittedScopeResponse.Error.Code);
        Assert.Equal(-32602, nullScopeResponse.Error.Code);
        Assert.Equal("invalid_params", omittedScopeResponse.Error.Message);
        Assert.Equal("invalid_params", nullScopeResponse.Error.Message);
        Assert.Equal("invalid_scope", JsonSerializer.SerializeToElement(omittedScopeResponse.Error.Data).GetProperty("reason").GetString());
        Assert.Equal("invalid_scope", JsonSerializer.SerializeToElement(nullScopeResponse.Error.Data).GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Impl_LaunchHandler_WhenWaitForRegisterMsNegative_ShouldReturnInvalidParams()
    {
        var definitionLoader = new DefinitionLoader(DefinitionCatalogTestHelper.GetCatalogPath(_definitionsDirectory), _definitionLogger.Object);
        var definitionProvider = new DefinitionProvider(definitionLoader);
        definitionProvider.Refresh();
        var appRegistry = new AppRegistry(new SystemClock(), _registryLogger.Object);
        var provider = new RuntimeHttpBaseUrlProvider(Mock.Of<ILogger<RuntimeHttpBaseUrlProvider>>(), RuntimePathOptions.Resolve());
        var coordinator = new LaunchCoordinator(definitionProvider, appRegistry, provider, new ProcessLauncher(), new SystemClock(), _launchLogger.Object);
        var handler = new LaunchHandler(coordinator, _launchHandlerLogger.Object);

        var response = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "launch-negative-wait",
            Method = "hub.apps.launch",
            Params = JsonSerializer.SerializeToElement(new
            {
                appId = "scope-launch-app",
                scope = ScopeContract.Global,
                waitForRegisterMs = -1
            })
        }, CancellationToken.None);

        Assert.NotNull(response.Error);
        Assert.Equal(-32602, response.Error.Code);
        Assert.Equal("invalid_params", response.Error.Message);
    }

    [Fact]
    public async Task Impl_LaunchHandler_WhenDedupeKeyTypeInvalid_ShouldReturnInvalidParams()
    {
        WriteDefinition(
            "scope-launch-app",
            rpcEnabled: true,
            includeLaunch: true,
            dedupeKeyTemplate: "{appId}:{scopeOrGlobal}");

        var definitionLoader = new DefinitionLoader(DefinitionCatalogTestHelper.GetCatalogPath(_definitionsDirectory), _definitionLogger.Object);
        var definitionProvider = new DefinitionProvider(definitionLoader);
        definitionProvider.Refresh();
        var appRegistry = new AppRegistry(new SystemClock(), _registryLogger.Object);
        var provider = new RuntimeHttpBaseUrlProvider(Mock.Of<ILogger<RuntimeHttpBaseUrlProvider>>(), RuntimePathOptions.Resolve());
        var coordinator = new LaunchCoordinator(definitionProvider, appRegistry, provider, new ProcessLauncher(), new SystemClock(), _launchLogger.Object);
        var handler = new LaunchHandler(coordinator, _launchHandlerLogger.Object);

        var response = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "launch-invalid-dedupe-key-type",
            Method = "hub.apps.launch",
            Params = JsonSerializer.SerializeToElement(new
            {
                appId = "scope-launch-app",
                scope = ScopeContract.Global,
                dedupeKey = 123,
                waitForRegisterMs = 0
            })
        }, CancellationToken.None);

        Assert.NotNull(response.Error);
        Assert.Equal(-32602, response.Error.Code);
        Assert.Equal("invalid_params", response.Error.Message);
    }

    [Fact]
    public async Task Impl_LaunchAsync_WithExplicitSameDedupeKeyInDifferentScopes_ShouldNotCollide()
    {
        WriteDefinition(
            "launch-scope-isolation.app",
            rpcEnabled: true,
            includeLaunch: true,
            dedupeKeyTemplate: "{appId}:{scopeOrGlobal}",
            definitionScope: "workspace-A");
        WriteDefinition(
            "launch-scope-isolation.app",
            rpcEnabled: true,
            includeLaunch: true,
            dedupeKeyTemplate: "{appId}:{scopeOrGlobal}",
            definitionScope: "workspace-B");

        var processLauncher = new Mock<IProcessLauncher>();
        processLauncher
            .Setup(launcher => launcher.Start(It.IsAny<LaunchConfiguration>(), It.IsAny<string?>()))
            .Returns(System.Diagnostics.Process.GetCurrentProcess());
        var coordinator = CreateCoordinator(processLauncher.Object);

        var scopeA = await coordinator.LaunchAsync(
            appId: "launch-scope-isolation.app",
            scope: "workspace-A",
            dedupeKey: "shared",
            waitForRegisterMs: 0,
            CancellationToken.None);

        var scopeB = await coordinator.LaunchAsync(
            appId: "launch-scope-isolation.app",
            scope: "workspace-B",
            dedupeKey: "shared",
            waitForRegisterMs: 0,
            CancellationToken.None);

        Assert.True(scopeA.Ok);
        Assert.True(scopeB.Ok);
        Assert.Equal("started", scopeA.Status);
        Assert.Equal("started", scopeB.Status);
        Assert.Equal("shared", scopeA.DedupeKey);
        Assert.Equal("shared", scopeB.DedupeKey);
        Assert.NotEqual(scopeA.LaunchId, scopeB.LaunchId);
        processLauncher.Verify(launcher => launcher.Start(It.IsAny<LaunchConfiguration>(), It.IsAny<string?>()), Times.Exactly(2));
    }

    [Fact]
    public async Task Impl_LaunchAsync_WithExplicitSameDedupeKeyInDifferentApps_ShouldNotCollide()
    {
        WriteDefinition(
            "launch-app-isolation-a.app",
            rpcEnabled: true,
            includeLaunch: true);
        WriteDefinition(
            "launch-app-isolation-b.app",
            rpcEnabled: true,
            includeLaunch: true);

        var processLauncher = new Mock<IProcessLauncher>();
        processLauncher
            .Setup(launcher => launcher.Start(It.IsAny<LaunchConfiguration>(), It.IsAny<string?>()))
            .Returns(System.Diagnostics.Process.GetCurrentProcess());
        var coordinator = CreateCoordinator(processLauncher.Object);

        var first = await coordinator.LaunchAsync(
            appId: "launch-app-isolation-a.app",
            scope: ScopeContract.Global,
            dedupeKey: "shared",
            waitForRegisterMs: 0,
            CancellationToken.None);

        var second = await coordinator.LaunchAsync(
            appId: "launch-app-isolation-b.app",
            scope: ScopeContract.Global,
            dedupeKey: "shared",
            waitForRegisterMs: 0,
            CancellationToken.None);

        Assert.True(first.Ok);
        Assert.True(second.Ok);
        Assert.Equal("started", first.Status);
        Assert.Equal("started", second.Status);
        Assert.NotEqual(first.LaunchId, second.LaunchId);
        processLauncher.Verify(launcher => launcher.Start(It.IsAny<LaunchConfiguration>(), It.IsAny<string?>()), Times.Exactly(2));
    }

    [Fact]
    public async Task Impl_InvocationHandler_Notify_AutoLaunch_ShouldPassTargetScopeToLaunch()
    {
        const string appId = "launch-scope-auto-pass.app";
        const string targetScope = "workspace-A";

        WriteDefinition(
            appId,
            rpcEnabled: true,
            includeLaunch: true,
            dedupeKeyTemplate: "{appId}:{scopeOrGlobal}",
            argsTemplate: "--scope {scopeOrGlobal}",
            definitionScope: targetScope);

        var appRegistry = new AppRegistry(new SystemClock(), _registryLogger.Object);
        var definitionLoader = new DefinitionLoader(DefinitionCatalogTestHelper.GetCatalogPath(_definitionsDirectory), _definitionLogger.Object);
        var definitionProvider = new DefinitionProvider(definitionLoader);
        definitionProvider.Refresh();
        var routingService = new InvocationRoutingService(appRegistry, _routingLogger.Object);
        var store = new InvocationStore(_storeLogger.Object, routingService, new SystemClock());
        var waiter = new InvocationRequestWaiter(Mock.Of<ILogger<InvocationRequestWaiter>>());
        var provider = new RuntimeHttpBaseUrlProvider(Mock.Of<ILogger<RuntimeHttpBaseUrlProvider>>(), RuntimePathOptions.Resolve());
        string? launchArguments = null;
        var processLauncher = new Mock<IProcessLauncher>();
        processLauncher
            .Setup(launcher => launcher.Start(It.IsAny<LaunchConfiguration>(), It.IsAny<string?>()))
            .Callback<LaunchConfiguration, string?>((_, args) => launchArguments = args)
            .Returns(System.Diagnostics.Process.GetCurrentProcess());
        var launchCoordinator = new LaunchCoordinator(definitionProvider, appRegistry, provider, processLauncher.Object, new SystemClock(), _launchLogger.Object);
        var invocationHandler = new InvocationHandler(appRegistry, definitionProvider, routingService, store, waiter, launchCoordinator, new SystemClock(), _invocationHandlerLogger.Object);
        var launchHandler = new LaunchHandler(launchCoordinator, _launchHandlerLogger.Object);

        var notifyResponse = await invocationHandler.HandleAsync(new JsonRpcRequest
        {
            Id = "launch-scope-auto-notify",
            Method = "hub.invoke.notify",
            Params = JsonSerializer.SerializeToElement(new
            {
                appId,
                target = new { scope = targetScope, instanceId = (string?)null },
                method = "asset.rebuild",
                args = new { },
                options = new
                {
                    ttlMs = 60000,
                    queueIfOffline = true,
                    autoLaunch = true
                }
            })
        }, CancellationToken.None);

        Assert.Null(notifyResponse.Error);
        var notifyResult = JsonSerializer.SerializeToElement(notifyResponse.Result);
        Assert.False(string.IsNullOrWhiteSpace(notifyResult.GetProperty("invocationId").GetString()));

        var launchResponse = await launchHandler.HandleAsync(new JsonRpcRequest
        {
            Id = "launch-scope-auto-check",
            Method = "hub.apps.launch",
            Params = JsonSerializer.SerializeToElement(new
            {
                appId,
                scope = targetScope,
                waitForRegisterMs = 0
            })
        }, CancellationToken.None);

        Assert.Null(launchResponse.Error);
        var launchResult = JsonSerializer.SerializeToElement(launchResponse.Result);
        Assert.Equal("already_running", launchResult.GetProperty("status").GetString());
        Assert.Equal("--scope workspace-A", launchArguments);
        processLauncher.Verify(launcher => launcher.Start(It.IsAny<LaunchConfiguration>(), It.IsAny<string?>()), Times.Once);
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

    private LaunchCoordinator CreateCoordinator(IProcessLauncher? processLauncher = null)
    {
        var definitionLoader = new DefinitionLoader(DefinitionCatalogTestHelper.GetCatalogPath(_definitionsDirectory), _definitionLogger.Object);
        var definitionProvider = new DefinitionProvider(definitionLoader);
        definitionProvider.Refresh();
        var appRegistry = new AppRegistry(new SystemClock(), _registryLogger.Object);
        var provider = new RuntimeHttpBaseUrlProvider(Mock.Of<ILogger<RuntimeHttpBaseUrlProvider>>(), RuntimePathOptions.Resolve());
        return new LaunchCoordinator(
            definitionProvider,
            appRegistry,
            provider,
            processLauncher ?? new ProcessLauncher(),
            new SystemClock(),
            _launchLogger.Object);
    }

    private void WriteDefinition(
        string appId,
        bool rpcEnabled,
        bool includeLaunch,
        string? dedupeKeyTemplate = null,
        string? argsTemplate = null,
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
                ["rpc"] = rpcEnabled,
                ["events"] = false
            }
        };

        if (includeLaunch)
        {
            var launch = new Dictionary<string, object?>
            {
                ["exePath"] = "dotnet",
                ["argsTemplate"] = argsTemplate ?? "--version"
            };

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
}
