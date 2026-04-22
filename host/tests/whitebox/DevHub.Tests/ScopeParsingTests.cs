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
[Trait("Category", "Impl")]
public class ScopeParsingTests : IDisposable
{
    private const string InstancePassword = "scope-tests-password";
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
    public async Task Impl_AppInstancesHandler_RegisterInstance_WhenGlobalLiteral_ShouldRemainDistinctFromGlobal()
    {
        var appRegistry = new AppRegistry(new SystemClock(), Mock.Of<ILogger<AppRegistry>>());
        var handler = new AppInstancesHandler(appRegistry, new SystemClock(), Mock.Of<ILogger<AppInstancesHandler>>());

        var scopedGlobalResponse = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "scope-global",
            Method = "hub.apps.registerInstance",
            Params = JsonSerializer.SerializeToElement(new
            {
                password = InstancePassword,
                instance = new
                {
                    instanceId = "inst-scope-literal",
                    appId = "scope-test-app",
                    scope = "global",
                    pid = 12345,
                    invoke = new { poll = true, respond = true }
                }
            })
        }, CancellationToken.None);

        var globalResponse = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "scope-global",
            Method = "hub.apps.registerInstance",
            Params = JsonSerializer.SerializeToElement(new
            {
                password = InstancePassword,
                instance = new
                {
                    instanceId = "inst-scope-global",
                    appId = "scope-test-app",
                    scope = ScopeContract.Global,
                    pid = 12346,
                    invoke = new { poll = true, respond = true }
                }
            })
        }, CancellationToken.None);

        Assert.Null(scopedGlobalResponse.Error);
        Assert.Null(globalResponse.Error);

        var scopedGlobalInstance = appRegistry.GetInstance("inst-scope-literal");
        var globalInstance = appRegistry.GetInstance("inst-scope-global");
        Assert.NotNull(scopedGlobalInstance);
        Assert.NotNull(globalInstance);
        Assert.Equal("global", scopedGlobalInstance!.Scope);
        Assert.Equal(ScopeContract.Global, globalInstance!.Scope);

        var defaultListResponse = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "list-default",
            Method = "hub.apps.listInstances",
            Params = JsonSerializer.SerializeToElement(new
            {
                appId = "scope-test-app",
                scope = ScopeContract.Global
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

        Assert.Contains("inst-scope-global", defaultListInstances);
        Assert.DoesNotContain("inst-scope-literal", defaultListInstances);
        Assert.Contains("inst-scope-literal", scopedListInstances);
        Assert.DoesNotContain("inst-scope-global", scopedListInstances);
    }

    [Fact]
    public async Task Impl_AppInstancesHandler_ListInstances_WhenScopeEmpty_ShouldTreatAsGlobal()
    {
        var appRegistry = new AppRegistry(new SystemClock(), Mock.Of<ILogger<AppRegistry>>());
        var handler = new AppInstancesHandler(appRegistry, new SystemClock(), Mock.Of<ILogger<AppInstancesHandler>>());

        var registerResponse = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "register-for-empty-scope",
            Method = "hub.apps.registerInstance",
            Params = JsonSerializer.SerializeToElement(new
            {
                password = InstancePassword,
                instance = new
                {
                    instanceId = "inst-empty-scope-global",
                    appId = "scope-empty-list-app",
                    scope = ScopeContract.Global,
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
        Assert.Equal(ScopeContract.Global, scopeElement.GetString());
    }


    [Fact]
    public async Task Impl_AppInstancesHandler_RegisterInstance_WhenScopeOmittedOrNull_ShouldReturnInvalidParams()
    {
        var appRegistry = new AppRegistry(new SystemClock(), Mock.Of<ILogger<AppRegistry>>());
        var handler = new AppInstancesHandler(appRegistry, new SystemClock(), Mock.Of<ILogger<AppInstancesHandler>>());

        var omittedScopeResponse = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "register-scope-omitted",
            Method = "hub.apps.registerInstance",
            Params = JsonSerializer.SerializeToElement(new
            {
                password = InstancePassword,
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
                password = InstancePassword,
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

        Assert.NotNull(omittedScopeResponse.Error);
        Assert.NotNull(nullScopeResponse.Error);
        Assert.Equal(-32602, omittedScopeResponse.Error.Code);
        Assert.Equal(-32602, nullScopeResponse.Error.Code);
        Assert.Equal("invalid_scope", JsonSerializer.SerializeToElement(omittedScopeResponse.Error.Data).GetProperty("reason").GetString());
        Assert.Equal("invalid_scope", JsonSerializer.SerializeToElement(nullScopeResponse.Error.Data).GetProperty("reason").GetString());
    }


    [Fact]
    public async Task Impl_InvocationHandler_Notify_WhenTargetScopeGlobal_ShouldRouteToExplicitGlobalScope()
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

        var globalInstance = appRegistry.RegisterInstance(new AppInstance
        {
            InstanceId = "scope-invoke-app-global",
            AppId = "scope-invoke-app",
            Scope = ScopeContract.Global,
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

        var globalPoll = await store.PollAsync(globalInstance, maxCount: 10, waitMs: 0, CancellationToken.None);
        Assert.DoesNotContain(globalPoll, item => item.InvocationId == invocationId);
    }

    [Fact]
    public async Task Impl_InvocationHandler_Notify_WhenTargetScopeEmpty_ShouldRouteToDefaultGlobal()
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

        var globalInstance = appRegistry.RegisterInstance(new AppInstance
        {
            InstanceId = "scope-invoke-app-empty-global",
            AppId = "scope-invoke-app-empty",
            Scope = ScopeContract.Global,
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

        var globalPoll = await store.PollAsync(globalInstance, maxCount: 10, waitMs: 0, CancellationToken.None);
        Assert.Contains(globalPoll, item => item.InvocationId == invocationId);

        var explicitGlobalPoll = await store.PollAsync(explicitGlobal, maxCount: 10, waitMs: 0, CancellationToken.None);
        Assert.DoesNotContain(explicitGlobalPoll, item => item.InvocationId == invocationId);
    }

    [Fact]
    public async Task Impl_InvocationHandler_Request_WhenTargetInstanceIdWhitespace_ShouldReturnInvalidTargetInstanceReason()
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
                target = new { scope = ScopeContract.Global, instanceId = "   " },
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
        var filePath = Path.Combine(_tempDirectory, AppDefinitionIdentity.Create(appId, ScopeContract.Global).GetFileName());
        File.WriteAllText(filePath, JsonSerializer.Serialize(new
        {
            appId,
            scope = ScopeContract.Global,
            displayName = appId,
            capabilities = new
            {
                rpc = rpcEnabled,
                events = false
            }
        }));
    }
}
