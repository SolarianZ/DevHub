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
    public async Task AppInstancesHandler_RegisterInstance_WhenScopeGlobal_ShouldTreatAsExplicitScope()
    {
        var appRegistry = new AppRegistry(new SystemClock(), Mock.Of<ILogger<AppRegistry>>());
        var handler = new AppInstancesHandler(appRegistry, new SystemClock(), Mock.Of<ILogger<AppInstancesHandler>>());

        var scopedGlobalResponse = await handler.HandleAsync(new JsonRpcRequest
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

        var nullGlobalResponse = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "scope-null",
            Method = "hub.apps.registerInstance",
            Params = JsonSerializer.SerializeToElement(new
            {
                instance = new
                {
                    instanceId = "inst-scope-null",
                    appId = "scope-test-app",
                    scope = (string?)null,
                    pid = 12346,
                    invoke = new { poll = true, respond = true }
                }
            })
        }, CancellationToken.None);

        Assert.Null(scopedGlobalResponse.Error);
        Assert.Null(nullGlobalResponse.Error);

        var scopedGlobalInstance = appRegistry.GetInstance("inst-scope-global");
        var nullGlobalInstance = appRegistry.GetInstance("inst-scope-null");
        Assert.NotNull(scopedGlobalInstance);
        Assert.NotNull(nullGlobalInstance);
        Assert.Equal("global", scopedGlobalInstance!.Scope);
        Assert.Null(nullGlobalInstance!.Scope);

        var defaultListResponse = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "list-default",
            Method = "hub.apps.listInstances",
            Params = JsonSerializer.SerializeToElement(new
            {
                appId = "scope-test-app"
            })
        }, CancellationToken.None);
        Assert.Null(defaultListResponse.Error);

        var scopedListResponse = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "list-global-scope",
            Method = "hub.apps.listInstances",
            Params = JsonSerializer.SerializeToElement(new
            {
                appId = "scope-test-app",
                scope = "global"
            })
        }, CancellationToken.None);
        Assert.Null(scopedListResponse.Error);

        var defaultListInstances = JsonSerializer.SerializeToElement(defaultListResponse.Result)
            .GetProperty("instances")
            .EnumerateArray()
            .Select(item => item.GetProperty("instanceId").GetString())
            .ToHashSet(StringComparer.Ordinal);
        var scopedListInstances = JsonSerializer.SerializeToElement(scopedListResponse.Result)
            .GetProperty("instances")
            .EnumerateArray()
            .Select(item => item.GetProperty("instanceId").GetString())
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains("inst-scope-null", defaultListInstances);
        Assert.DoesNotContain("inst-scope-global", defaultListInstances);
        Assert.Contains("inst-scope-global", scopedListInstances);
        Assert.DoesNotContain("inst-scope-null", scopedListInstances);
    }

    [Fact]
    public async Task AppInstancesHandler_ListInstances_WhenScopeEmpty_ShouldTreatAsGlobal()
    {
        var appRegistry = new AppRegistry(new SystemClock(), Mock.Of<ILogger<AppRegistry>>());
        var handler = new AppInstancesHandler(appRegistry, new SystemClock(), Mock.Of<ILogger<AppInstancesHandler>>());

        var registerResponse = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "register-for-empty-scope",
            Method = "hub.apps.registerInstance",
            Params = JsonSerializer.SerializeToElement(new
            {
                instance = new
                {
                    instanceId = "inst-empty-scope-global",
                    appId = "scope-empty-list-app",
                    scope = (string?)null,
                    pid = 12347,
                    invoke = new { poll = true, respond = true }
                }
            })
        }, CancellationToken.None);
        Assert.Null(registerResponse.Error);

        var response = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "list-scope-empty",
            Method = "hub.apps.listInstances",
            Params = JsonSerializer.SerializeToElement(new
            {
                appId = "scope-empty-list-app",
                scope = string.Empty
            })
        }, CancellationToken.None);

        Assert.Null(response.Error);
        var result = JsonSerializer.SerializeToElement(response.Result);
        var instances = result.GetProperty("instances").EnumerateArray().ToList();
        Assert.Single(instances);
        Assert.Equal("inst-empty-scope-global", instances[0].GetProperty("instanceId").GetString());
        Assert.True(instances[0].TryGetProperty("scope", out var scopeElement));
        Assert.Equal(JsonValueKind.Null, scopeElement.ValueKind);
    }


    [Fact]
    public async Task AppInstancesHandler_RegisterInstance_WhenScopeOmittedOrNull_ShouldTreatBothAsGlobal()
    {
        var appRegistry = new AppRegistry(new SystemClock(), Mock.Of<ILogger<AppRegistry>>());
        var handler = new AppInstancesHandler(appRegistry, new SystemClock(), Mock.Of<ILogger<AppInstancesHandler>>());

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
    public async Task InvocationHandler_Notify_WhenTargetScopeGlobal_ShouldRouteToExplicitGlobalScope()
    {
        WriteDefinition("scope-invoke-app", rpcEnabled: true);

        var appRegistry = new AppRegistry(new SystemClock(), Mock.Of<ILogger<AppRegistry>>());
        var definitionLoader = new DefinitionLoader(_tempDirectory, Mock.Of<ILogger<DefinitionLoader>>());
        var definitionProvider = new DefinitionProvider(definitionLoader);
        definitionProvider.Refresh();
        var routingService = new InvocationRoutingService(appRegistry, Mock.Of<ILogger<InvocationRoutingService>>());
        var store = new InvocationStore(Mock.Of<ILogger<InvocationStore>>(), routingService, new SystemClock());
        var waiter = new InvocationRequestWaiter(Mock.Of<ILogger<InvocationRequestWaiter>>());
        var provider = new RuntimeHttpBaseUrlProvider(Mock.Of<ILogger<RuntimeHttpBaseUrlProvider>>(), RuntimePathOptions.Resolve());
        var launchCoordinator = new LaunchCoordinator(definitionProvider, appRegistry, provider, new ProcessLauncher(), new SystemClock(), Mock.Of<ILogger<LaunchCoordinator>>());
        var handler = new InvocationHandler(appRegistry, definitionProvider, routingService, store, waiter, launchCoordinator, new SystemClock(), Mock.Of<ILogger<InvocationHandler>>());

        var nullGlobal = appRegistry.RegisterInstance(new AppInstance
        {
            InstanceId = "scope-invoke-app-global-null",
            AppId = "scope-invoke-app",
            Scope = null,
            Pid = 22001,
            Invoke = new InvokeCapability { Poll = true, Respond = true }
        });
        var explicitGlobal = appRegistry.RegisterInstance(new AppInstance
        {
            InstanceId = "scope-invoke-app-global-explicit",
            AppId = "scope-invoke-app",
            Scope = "global",
            Pid = 22002,
            Invoke = new InvokeCapability { Poll = true, Respond = true }
        });

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

        Assert.Null(response.Error);
        var result = JsonSerializer.SerializeToElement(response.Result);
        var invocationId = result.GetProperty("invocationId").GetString();
        Assert.False(string.IsNullOrWhiteSpace(invocationId));

        var explicitGlobalPoll = await store.PollAsync(explicitGlobal, maxCount: 10, waitMs: 0, CancellationToken.None);
        Assert.Contains(explicitGlobalPoll, item => item.InvocationId == invocationId);

        var nullGlobalPoll = await store.PollAsync(nullGlobal, maxCount: 10, waitMs: 0, CancellationToken.None);
        Assert.DoesNotContain(nullGlobalPoll, item => item.InvocationId == invocationId);
    }

    [Fact]
    public async Task InvocationHandler_Notify_WhenTargetScopeEmpty_ShouldRouteToDefaultGlobal()
    {
        WriteDefinition("scope-invoke-app-empty", rpcEnabled: true);

        var appRegistry = new AppRegistry(new SystemClock(), Mock.Of<ILogger<AppRegistry>>());
        var definitionLoader = new DefinitionLoader(_tempDirectory, Mock.Of<ILogger<DefinitionLoader>>());
        var definitionProvider = new DefinitionProvider(definitionLoader);
        definitionProvider.Refresh();
        var routingService = new InvocationRoutingService(appRegistry, Mock.Of<ILogger<InvocationRoutingService>>());
        var store = new InvocationStore(Mock.Of<ILogger<InvocationStore>>(), routingService, new SystemClock());
        var waiter = new InvocationRequestWaiter(Mock.Of<ILogger<InvocationRequestWaiter>>());
        var provider = new RuntimeHttpBaseUrlProvider(Mock.Of<ILogger<RuntimeHttpBaseUrlProvider>>(), RuntimePathOptions.Resolve());
        var launchCoordinator = new LaunchCoordinator(definitionProvider, appRegistry, provider, new ProcessLauncher(), new SystemClock(), Mock.Of<ILogger<LaunchCoordinator>>());
        var handler = new InvocationHandler(appRegistry, definitionProvider, routingService, store, waiter, launchCoordinator, new SystemClock(), Mock.Of<ILogger<InvocationHandler>>());

        var nullGlobal = appRegistry.RegisterInstance(new AppInstance
        {
            InstanceId = "scope-invoke-app-empty-global-null",
            AppId = "scope-invoke-app-empty",
            Scope = null,
            Pid = 22003,
            Invoke = new InvokeCapability { Poll = true, Respond = true }
        });
        var explicitGlobal = appRegistry.RegisterInstance(new AppInstance
        {
            InstanceId = "scope-invoke-app-empty-global-explicit",
            AppId = "scope-invoke-app-empty",
            Scope = "global",
            Pid = 22004,
            Invoke = new InvokeCapability { Poll = true, Respond = true }
        });

        var response = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "notify-target-scope-empty",
            Method = "hub.invoke.notify",
            Params = JsonSerializer.SerializeToElement(new
            {
                appId = "scope-invoke-app-empty",
                target = new { scope = string.Empty, instanceId = (string?)null },
                method = "asset.rebuild",
                args = new { },
                options = new { ttlMs = 60000, queueIfOffline = true, autoLaunch = false }
            })
        }, CancellationToken.None);

        Assert.Null(response.Error);
        var result = JsonSerializer.SerializeToElement(response.Result);
        var invocationId = result.GetProperty("invocationId").GetString();
        Assert.False(string.IsNullOrWhiteSpace(invocationId));

        var nullGlobalPoll = await store.PollAsync(nullGlobal, maxCount: 10, waitMs: 0, CancellationToken.None);
        Assert.Contains(nullGlobalPoll, item => item.InvocationId == invocationId);

        var explicitGlobalPoll = await store.PollAsync(explicitGlobal, maxCount: 10, waitMs: 0, CancellationToken.None);
        Assert.DoesNotContain(explicitGlobalPoll, item => item.InvocationId == invocationId);
    }

    [Fact]
    public async Task InvocationHandler_Request_WhenTargetInstanceIdWhitespace_ShouldReturnInvalidTargetInstanceReason()
    {
        WriteDefinition("scope-invoke-app-2", rpcEnabled: true);

        var appRegistry = new AppRegistry(new SystemClock(), Mock.Of<ILogger<AppRegistry>>());
        var definitionLoader = new DefinitionLoader(_tempDirectory, Mock.Of<ILogger<DefinitionLoader>>());
        var definitionProvider = new DefinitionProvider(definitionLoader);
        definitionProvider.Refresh();
        var routingService = new InvocationRoutingService(appRegistry, Mock.Of<ILogger<InvocationRoutingService>>());
        var store = new InvocationStore(Mock.Of<ILogger<InvocationStore>>(), routingService, new SystemClock());
        var waiter = new InvocationRequestWaiter(Mock.Of<ILogger<InvocationRequestWaiter>>());
        var provider = new RuntimeHttpBaseUrlProvider(Mock.Of<ILogger<RuntimeHttpBaseUrlProvider>>(), RuntimePathOptions.Resolve());
        var launchCoordinator = new LaunchCoordinator(definitionProvider, appRegistry, provider, new ProcessLauncher(), new SystemClock(), Mock.Of<ILogger<LaunchCoordinator>>());
        var handler = new InvocationHandler(appRegistry, definitionProvider, routingService, store, waiter, launchCoordinator, new SystemClock(), Mock.Of<ILogger<InvocationHandler>>());

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

