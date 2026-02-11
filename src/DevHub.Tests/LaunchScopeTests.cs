namespace DevHub.Tests;

using System.Text.Json;
using DevHub.Core.Models.Rpc;
using DevHub.Core.Services;
using DevHub.Core.Services.Invocation;
using DevHub.Core.Services.Rpc;
using DevHub.Core.Services.Rpc.Handlers;
using Microsoft.Extensions.Logging;
using Moq;

/// <summary>
/// Launch scope 语义专项测试。
/// </summary>
public class LaunchScopeTests : IDisposable
{
    private readonly string _tempDirectory;
    private readonly string _runtimeDirectory;
    private readonly EnvironmentVariableScope _runtimeScope;
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
        _tempDirectory = Path.Combine(Path.GetTempPath(), "DevHubLaunchScopeTests", Guid.NewGuid().ToString("N"));
        _runtimeDirectory = Path.Combine(_tempDirectory, "runtime");
        Directory.CreateDirectory(_tempDirectory);
        Directory.CreateDirectory(_runtimeDirectory);
        _runtimeScope = new EnvironmentVariableScope("DEVHUB_RUNTIME_DIR", _runtimeDirectory);
    }

    [Fact]
    public async Task LaunchHandler_WhenScopeGlobal_ShouldReturnInvalidScopeReason()
    {
        var definitionLoader = new DefinitionLoader(_tempDirectory, _definitionLogger.Object);
        var definitionProvider = new DefinitionProvider(definitionLoader);
        definitionProvider.Refresh();
        var appRegistry = new AppRegistry(_registryLogger.Object);
        var provider = new RuntimeHttpBaseUrlProvider(Mock.Of<ILogger<RuntimeHttpBaseUrlProvider>>());
        var coordinator = new LaunchCoordinator(definitionProvider, appRegistry, provider, _launchLogger.Object);
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
        Assert.Equal(-32602, response.Error.Code);
        Assert.Equal("invalid_params", response.Error.Message);
        var data = JsonSerializer.SerializeToElement(response.Error.Data);
        Assert.Equal("invalid_scope", data.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task LaunchHandler_WhenScopeEmpty_ShouldReturnInvalidScopeReason()
    {
        var definitionLoader = new DefinitionLoader(_tempDirectory, _definitionLogger.Object);
        var definitionProvider = new DefinitionProvider(definitionLoader);
        definitionProvider.Refresh();
        var appRegistry = new AppRegistry(_registryLogger.Object);
        var provider = new RuntimeHttpBaseUrlProvider(Mock.Of<ILogger<RuntimeHttpBaseUrlProvider>>());
        var coordinator = new LaunchCoordinator(definitionProvider, appRegistry, provider, _launchLogger.Object);
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
        Assert.Equal(-32602, response.Error.Code);
        Assert.Equal("invalid_params", response.Error.Message);
        var data = JsonSerializer.SerializeToElement(response.Error.Data);
        Assert.Equal("invalid_scope", data.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task LaunchHandler_WhenScopeOmittedOrNull_ShouldKeepEquivalentBehavior()
    {
        var definitionLoader = new DefinitionLoader(_tempDirectory, _definitionLogger.Object);
        var definitionProvider = new DefinitionProvider(definitionLoader);
        definitionProvider.Refresh();
        var appRegistry = new AppRegistry(_registryLogger.Object);
        var provider = new RuntimeHttpBaseUrlProvider(Mock.Of<ILogger<RuntimeHttpBaseUrlProvider>>());
        var coordinator = new LaunchCoordinator(definitionProvider, appRegistry, provider, _launchLogger.Object);
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
        Assert.Equal(-32014, omittedScopeResponse.Error.Code);
        Assert.Equal(-32014, nullScopeResponse.Error.Code);
        Assert.Equal("app_definition_not_found", omittedScopeResponse.Error.Message);
        Assert.Equal("app_definition_not_found", nullScopeResponse.Error.Message);

        var omittedData = JsonSerializer.SerializeToElement(omittedScopeResponse.Error.Data);
        var nullData = JsonSerializer.SerializeToElement(nullScopeResponse.Error.Data);
        Assert.Equal("missing-scope-equivalent-app", omittedData.GetProperty("appId").GetString());
        Assert.Equal("missing-scope-equivalent-app", nullData.GetProperty("appId").GetString());
    }

    [Fact]
    public async Task LaunchHandler_WhenWaitForRegisterMsNegative_ShouldReturnInvalidParams()
    {
        var definitionLoader = new DefinitionLoader(_tempDirectory, _definitionLogger.Object);
        var definitionProvider = new DefinitionProvider(definitionLoader);
        definitionProvider.Refresh();
        var appRegistry = new AppRegistry(_registryLogger.Object);
        var provider = new RuntimeHttpBaseUrlProvider(Mock.Of<ILogger<RuntimeHttpBaseUrlProvider>>());
        var coordinator = new LaunchCoordinator(definitionProvider, appRegistry, provider, _launchLogger.Object);
        var handler = new LaunchHandler(coordinator, _launchHandlerLogger.Object);

        var response = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "launch-negative-wait",
            Method = "hub.apps.launch",
            Params = JsonSerializer.SerializeToElement(new
            {
                appId = "scope-launch-app",
                waitForRegisterMs = -1
            })
        }, CancellationToken.None);

        Assert.NotNull(response.Error);
        Assert.Equal(-32602, response.Error.Code);
        Assert.Equal("invalid_params", response.Error.Message);
    }

    [Fact]
    public async Task LaunchAsync_WithDifferentScopes_ShouldUseDifferentDedupeKeys()
    {
        WriteDefinition(
            "launch-scope-isolation.app",
            rpcEnabled: true,
            includeLaunch: true,
            dedupeKeyTemplate: "{appId}:{scopeOrGlobal}");

        var coordinator = CreateCoordinator();

        var scopeA = await coordinator.LaunchAsync(
            appId: "launch-scope-isolation.app",
            scope: "workspace-A",
            dedupeKey: null,
            waitForRegisterMs: 0,
            CancellationToken.None);

        var scopeB = await coordinator.LaunchAsync(
            appId: "launch-scope-isolation.app",
            scope: "workspace-B",
            dedupeKey: null,
            waitForRegisterMs: 0,
            CancellationToken.None);

        Assert.True(scopeA.Ok);
        Assert.True(scopeB.Ok);
        Assert.Equal("started", scopeA.Status);
        Assert.Equal("started", scopeB.Status);
        Assert.NotEqual(scopeA.LaunchId, scopeB.LaunchId);
    }

    [Fact]
    public async Task InvocationHandler_Notify_AutoLaunch_ShouldPassTargetScopeToLaunch()
    {
        const string appId = "launch-scope-auto-pass.app";
        const string targetScope = "workspace-A";

        WriteDefinition(
            appId,
            rpcEnabled: true,
            includeLaunch: true,
            dedupeKeyTemplate: "{appId}:{scopeOrGlobal}");

        var appRegistry = new AppRegistry(_registryLogger.Object);
        var definitionLoader = new DefinitionLoader(_tempDirectory, _definitionLogger.Object);
        var definitionProvider = new DefinitionProvider(definitionLoader);
        definitionProvider.Refresh();
        var routingService = new InvocationRoutingService(appRegistry, _routingLogger.Object);
        var store = new InvocationStore(_storeLogger.Object, routingService);
        var waiter = new InvocationRequestWaiter(Mock.Of<ILogger<InvocationRequestWaiter>>());
        var provider = new RuntimeHttpBaseUrlProvider(Mock.Of<ILogger<RuntimeHttpBaseUrlProvider>>());
        var launchCoordinator = new LaunchCoordinator(definitionProvider, appRegistry, provider, _launchLogger.Object);
        var invocationHandler = new InvocationHandler(appRegistry, definitionProvider, routingService, store, waiter, launchCoordinator, _invocationHandlerLogger.Object);
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
        var appRegistry = new AppRegistry(_registryLogger.Object);
        var provider = new RuntimeHttpBaseUrlProvider(Mock.Of<ILogger<RuntimeHttpBaseUrlProvider>>());
        return new LaunchCoordinator(definitionProvider, appRegistry, provider, _launchLogger.Object);
    }

    private void WriteDefinition(string appId, bool rpcEnabled, bool includeLaunch, string? dedupeKeyTemplate = null)
    {
        var payload = new Dictionary<string, object?>
        {
            ["appId"] = appId,
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
                ["argsTemplate"] = "--version"
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
}



