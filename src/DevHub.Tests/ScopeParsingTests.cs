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
/// Scope 与 target 解析规则测试。
/// </summary>
public class ScopeParsingTests : IDisposable
{
    private readonly string _tempDirectory;

    /// <summary>
    /// 初始化测试上下文。
    /// </summary>
    public ScopeParsingTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "DevHubScopeParsingTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
    }

    [Fact]
    public async Task AppInstancesHandler_RegisterInstance_WhenScopeGlobal_ShouldReturnInvalidScopeReason()
    {
        var handler = new AppInstancesHandler(new AppRegistry(Mock.Of<ILogger<AppRegistry>>()), Mock.Of<ILogger<AppInstancesHandler>>());

        var response = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "scope-global",
            Method = "hub.apps.registerInstance",
            Params = JsonSerializer.SerializeToElement(new
            {
                instance = new
                {
                    instanceId = "inst-scope-global",
                    appId = "scope-test-app",
                    scope = "global",
                    pid = 12345,
                    invoke = new { poll = true, respond = true }
                }
            })
        }, CancellationToken.None);

        Assert.NotNull(response.Error);
        Assert.Equal(-32602, response.Error.Code);
        Assert.Equal("invalid_params", response.Error.Message);
        var data = JsonSerializer.SerializeToElement(response.Error.Data);
        Assert.Equal("invalid_scope", data.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task AppInstancesHandler_ListInstances_WhenScopeGlobal_ShouldReturnInvalidScopeReason()
    {
        var handler = new AppInstancesHandler(new AppRegistry(Mock.Of<ILogger<AppRegistry>>()), Mock.Of<ILogger<AppInstancesHandler>>());

        var response = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "list-scope-global",
            Method = "hub.apps.listInstances",
            Params = JsonSerializer.SerializeToElement(new
            {
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
    public async Task LaunchHandler_WhenScopeGlobal_ShouldReturnInvalidScopeReason()
    {
        var definitionLoader = new DefinitionLoader(_tempDirectory, Mock.Of<ILogger<DefinitionLoader>>());
        var appRegistry = new AppRegistry(Mock.Of<ILogger<AppRegistry>>());
        var provider = new RuntimeHttpBaseUrlProvider(Mock.Of<ILogger<RuntimeHttpBaseUrlProvider>>());
        var coordinator = new LaunchCoordinator(definitionLoader, appRegistry, provider, Mock.Of<ILogger<LaunchCoordinator>>());
        var handler = new LaunchHandler(coordinator, Mock.Of<ILogger<LaunchHandler>>());

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
    public async Task AppInstancesHandler_RegisterInstance_WhenScopeOmittedOrNull_ShouldTreatBothAsGlobal()
    {
        var appRegistry = new AppRegistry(Mock.Of<ILogger<AppRegistry>>());
        var handler = new AppInstancesHandler(appRegistry, Mock.Of<ILogger<AppInstancesHandler>>());

        var omittedScopeResponse = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "register-scope-omitted",
            Method = "hub.apps.registerInstance",
            Params = JsonSerializer.SerializeToElement(new
            {
                instance = new
                {
                    instanceId = "inst-scope-omitted",
                    appId = "scope-global-equivalent-app",
                    pid = 19001,
                    invoke = new { poll = true, respond = true }
                }
            })
        }, CancellationToken.None);

        var nullScopeResponse = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "register-scope-null",
            Method = "hub.apps.registerInstance",
            Params = JsonSerializer.SerializeToElement(new
            {
                instance = new
                {
                    instanceId = "inst-scope-null",
                    appId = "scope-global-equivalent-app",
                    scope = (string?)null,
                    pid = 19002,
                    invoke = new { poll = true, respond = true }
                }
            })
        }, CancellationToken.None);

        Assert.Null(omittedScopeResponse.Error);
        Assert.Null(nullScopeResponse.Error);

        var omittedInstance = appRegistry.GetInstance("inst-scope-omitted");
        var nullInstance = appRegistry.GetInstance("inst-scope-null");
        Assert.NotNull(omittedInstance);
        Assert.NotNull(nullInstance);
        Assert.Null(omittedInstance!.Scope);
        Assert.Null(nullInstance!.Scope);

        var listResponse = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "list-global-default",
            Method = "hub.apps.listInstances",
            Params = JsonSerializer.SerializeToElement(new
            {
                appId = "scope-global-equivalent-app"
            })
        }, CancellationToken.None);

        Assert.Null(listResponse.Error);
        var result = JsonSerializer.SerializeToElement(listResponse.Result);
        var instances = result.GetProperty("instances").EnumerateArray().ToList();
        Assert.Contains(instances, item => item.GetProperty("instanceId").GetString() == "inst-scope-omitted");
        Assert.Contains(instances, item => item.GetProperty("instanceId").GetString() == "inst-scope-null");
    }

    [Fact]
    public async Task LaunchHandler_WhenScopeOmittedOrNull_ShouldKeepEquivalentBehavior()
    {
        var definitionLoader = new DefinitionLoader(_tempDirectory, Mock.Of<ILogger<DefinitionLoader>>());
        var appRegistry = new AppRegistry(Mock.Of<ILogger<AppRegistry>>());
        var provider = new RuntimeHttpBaseUrlProvider(Mock.Of<ILogger<RuntimeHttpBaseUrlProvider>>());
        var coordinator = new LaunchCoordinator(definitionLoader, appRegistry, provider, Mock.Of<ILogger<LaunchCoordinator>>());
        var handler = new LaunchHandler(coordinator, Mock.Of<ILogger<LaunchHandler>>());

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
    public async Task InvocationHandler_Notify_WhenTargetScopeGlobal_ShouldReturnInvalidTargetScopeReason()
    {
        WriteDefinition("scope-invoke-app", rpcEnabled: true);

        var appRegistry = new AppRegistry(Mock.Of<ILogger<AppRegistry>>());
        var definitionLoader = new DefinitionLoader(_tempDirectory, Mock.Of<ILogger<DefinitionLoader>>());
        definitionLoader.Load();
        var routingService = new InvocationRoutingService(appRegistry, Mock.Of<ILogger<InvocationRoutingService>>());
        var store = new InvocationStore(Mock.Of<ILogger<InvocationStore>>(), routingService);
        var waiter = new InvocationRequestWaiter(Mock.Of<ILogger<InvocationRequestWaiter>>());
        var provider = new RuntimeHttpBaseUrlProvider(Mock.Of<ILogger<RuntimeHttpBaseUrlProvider>>());
        var launchCoordinator = new LaunchCoordinator(definitionLoader, appRegistry, provider, Mock.Of<ILogger<LaunchCoordinator>>());
        var handler = new InvocationHandler(appRegistry, definitionLoader, routingService, store, waiter, launchCoordinator, Mock.Of<ILogger<InvocationHandler>>());

        var response = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "notify-target-scope-global",
            Method = "hub.invoke.notify",
            Params = JsonSerializer.SerializeToElement(new
            {
                appId = "scope-invoke-app",
                target = new { scope = "global", instanceId = (string?)null },
                method = "asset.rebuild",
                args = new { },
                options = new { ttlMs = 60000, queueIfOffline = true, autoLaunch = false }
            })
        }, CancellationToken.None);

        Assert.NotNull(response.Error);
        Assert.Equal(-32602, response.Error.Code);
        Assert.Equal("invalid_params", response.Error.Message);
        var data = JsonSerializer.SerializeToElement(response.Error.Data);
        Assert.Equal("invalid_target_scope", data.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task InvocationHandler_Request_WhenTargetInstanceIdWhitespace_ShouldReturnInvalidTargetInstanceReason()
    {
        WriteDefinition("scope-invoke-app-2", rpcEnabled: true);

        var appRegistry = new AppRegistry(Mock.Of<ILogger<AppRegistry>>());
        var definitionLoader = new DefinitionLoader(_tempDirectory, Mock.Of<ILogger<DefinitionLoader>>());
        definitionLoader.Load();
        var routingService = new InvocationRoutingService(appRegistry, Mock.Of<ILogger<InvocationRoutingService>>());
        var store = new InvocationStore(Mock.Of<ILogger<InvocationStore>>(), routingService);
        var waiter = new InvocationRequestWaiter(Mock.Of<ILogger<InvocationRequestWaiter>>());
        var provider = new RuntimeHttpBaseUrlProvider(Mock.Of<ILogger<RuntimeHttpBaseUrlProvider>>());
        var launchCoordinator = new LaunchCoordinator(definitionLoader, appRegistry, provider, Mock.Of<ILogger<LaunchCoordinator>>());
        var handler = new InvocationHandler(appRegistry, definitionLoader, routingService, store, waiter, launchCoordinator, Mock.Of<ILogger<InvocationHandler>>());

        var response = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "request-target-instance-whitespace",
            Method = "hub.invoke.request",
            Params = JsonSerializer.SerializeToElement(new
            {
                appId = "scope-invoke-app-2",
                target = new { scope = (string?)null, instanceId = "   " },
                method = "asset.build",
                args = new { },
                options = new
                {
                    ttlMs = 3000,
                    waitTimeoutMs = 1000,
                    queueIfOffline = true,
                    autoLaunch = false
                }
            })
        }, CancellationToken.None);

        Assert.NotNull(response.Error);
        Assert.Equal(-32602, response.Error.Code);
        Assert.Equal("invalid_params", response.Error.Message);
        var data = JsonSerializer.SerializeToElement(response.Error.Data);
        Assert.Equal("invalid_target_instance", data.GetProperty("reason").GetString());
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

    private void WriteDefinition(string appId, bool rpcEnabled)
    {
        var filePath = Path.Combine(_tempDirectory, $"{appId}.json");
        File.WriteAllText(filePath, JsonSerializer.Serialize(new
        {
            appId,
            displayName = appId,
            capabilities = new
            {
                rpc = rpcEnabled,
                events = false
            }
        }));
    }
}
