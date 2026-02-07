namespace DevHub.Tests;

using System.Text.Json;
using DevHub.Core.Models;
using DevHub.Core.Models.Rpc;
using DevHub.Core.Services;
using DevHub.Core.Services.Invocation;
using DevHub.Core.Services.Rpc;
using DevHub.Core.Services.Rpc.Handlers;
using Microsoft.Extensions.Logging;
using Moq;

/// <summary>
/// Invocation 路由与门禁相关测试。
/// </summary>
public class InvocationRoutingTests : IDisposable
{
    private readonly string _tempDirectory;
    private readonly Mock<ILogger<AppRegistry>> _registryLogger = new();
    private readonly Mock<ILogger<DefinitionLoader>> _definitionLogger = new();
    private readonly Mock<ILogger<InvocationStore>> _storeLogger = new();
    private readonly Mock<ILogger<InvocationRoutingService>> _routingLogger = new();
    private readonly Mock<ILogger<InvocationHandler>> _invocationHandlerLogger = new();
    private readonly Mock<ILogger<LaunchHandler>> _launchHandlerLogger = new();

    /// <summary>
    /// 初始化测试上下文。
    /// </summary>
    public InvocationRoutingTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "DevHubInvocationRoutingTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
    }

    [Fact]
    public void RoutingService_WithTargetInstanceId_ShouldNotFallbackToOtherInstances()
    {
        var appRegistry = new AppRegistry(_registryLogger.Object);
        appRegistry.RegisterInstance(new AppInstance
        {
            InstanceId = "inst-a",
            AppId = "route.app",
            Scope = null,
            Pid = 1001,
            Invoke = new InvokeCapability { Poll = true, Respond = true }
        });
        appRegistry.RegisterInstance(new AppInstance
        {
            InstanceId = "inst-b",
            AppId = "route.app",
            Scope = null,
            Pid = 1002,
            Invoke = new InvokeCapability { Poll = true, Respond = true }
        });

        var service = new InvocationRoutingService(appRegistry, _routingLogger.Object);

        var candidates = service.GetOnlineCandidates(
            "route.app",
            new InvocationTarget { Scope = null, InstanceId = "inst-b" });

        Assert.Single(candidates);
        Assert.Equal("inst-b", candidates[0].InstanceId);
    }

    [Fact]
    public void RoutingService_WithTargetScope_ShouldNotFallbackToGlobal()
    {
        var appRegistry = new AppRegistry(_registryLogger.Object);
        appRegistry.RegisterInstance(new AppInstance
        {
            InstanceId = "global-inst",
            AppId = "scope.app",
            Scope = null,
            Pid = 2001,
            Invoke = new InvokeCapability { Poll = true, Respond = true }
        });
        appRegistry.RegisterInstance(new AppInstance
        {
            InstanceId = "scoped-inst",
            AppId = "scope.app",
            Scope = "workspace://a",
            Pid = 2002,
            Invoke = new InvokeCapability { Poll = true, Respond = true }
        });

        var service = new InvocationRoutingService(appRegistry, _routingLogger.Object);

        var candidates = service.GetOnlineCandidates(
            "scope.app",
            new InvocationTarget { Scope = "workspace://a", InstanceId = null });

        Assert.Single(candidates);
        Assert.Equal("scoped-inst", candidates[0].InstanceId);
    }

    [Fact]
    public async Task InvocationHandler_Notify_WithRpcDisabledDefinition_ShouldReturnForbidden()
    {
        WriteDefinition("disabled-app", scopePolicy: "any", rpcEnabled: false);

        var appRegistry = new AppRegistry(_registryLogger.Object);
        var definitionLoader = new DefinitionLoader(_tempDirectory, _definitionLogger.Object);
        definitionLoader.Load();
        var routingService = new InvocationRoutingService(appRegistry, _routingLogger.Object);
        var store = new InvocationStore(_storeLogger.Object, routingService);
        var waiter = new InvocationRequestWaiter(Mock.Of<ILogger<InvocationRequestWaiter>>());
        var handler = new InvocationHandler(appRegistry, definitionLoader, routingService, store, waiter, _invocationHandlerLogger.Object);

        var request = new JsonRpcRequest
        {
            Id = "notify-rpc-disabled",
            Method = "hub.invoke.notify",
            Params = JsonSerializer.SerializeToElement(new
            {
                appId = "disabled-app",
                target = new { scope = (string?)null, instanceId = (string?)null },
                method = "disabled.call",
                args = new { },
                options = new { queueIfOffline = true, autoLaunch = false, ttlMs = 60000 }
            })
        };

        var response = await handler.HandleAsync(request, CancellationToken.None);

        Assert.NotNull(response.Error);
        Assert.Equal(-32002, response.Error.Code);
        Assert.Equal("forbidden", response.Error.Message);
        var data = JsonSerializer.SerializeToElement(response.Error.Data);
        Assert.Equal("rpc_disabled", data.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task InvocationHandler_Poll_WithPollDisabledInstance_ShouldReturnForbidden()
    {
        var appRegistry = new AppRegistry(_registryLogger.Object);
        appRegistry.RegisterInstance(new AppInstance
        {
            InstanceId = "poll-disabled",
            AppId = "poll.app",
            Scope = null,
            Pid = 3011,
            Invoke = new InvokeCapability { Poll = false, Respond = true }
        });

        var definitionLoader = new DefinitionLoader(_tempDirectory, _definitionLogger.Object);
        var routingService = new InvocationRoutingService(appRegistry, _routingLogger.Object);
        var store = new InvocationStore(_storeLogger.Object, routingService);
        var waiter = new InvocationRequestWaiter(Mock.Of<ILogger<InvocationRequestWaiter>>());
        var handler = new InvocationHandler(appRegistry, definitionLoader, routingService, store, waiter, _invocationHandlerLogger.Object);

        var request = new JsonRpcRequest
        {
            Id = "poll-disabled",
            Method = "hub.invoke.poll",
            Params = JsonSerializer.SerializeToElement(new { instanceId = "poll-disabled", maxCount = 10, waitMs = 0 })
        };

        var response = await handler.HandleAsync(request, CancellationToken.None);

        Assert.NotNull(response.Error);
        Assert.Equal(-32002, response.Error.Code);
        Assert.Equal("forbidden", response.Error.Message);
        var data = JsonSerializer.SerializeToElement(response.Error.Data);
        Assert.Equal("poll_not_enabled", data.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task InvocationHandler_Respond_WithRespondDisabledInstance_ShouldReturnForbidden()
    {
        var appRegistry = new AppRegistry(_registryLogger.Object);
        appRegistry.RegisterInstance(new AppInstance
        {
            InstanceId = "respond-disabled",
            AppId = "respond.app",
            Scope = null,
            Pid = 3012,
            Invoke = new InvokeCapability { Poll = true, Respond = false }
        });

        var definitionLoader = new DefinitionLoader(_tempDirectory, _definitionLogger.Object);
        var routingService = new InvocationRoutingService(appRegistry, _routingLogger.Object);
        var store = new InvocationStore(_storeLogger.Object, routingService);
        var waiter = new InvocationRequestWaiter(Mock.Of<ILogger<InvocationRequestWaiter>>());
        var handler = new InvocationHandler(appRegistry, definitionLoader, routingService, store, waiter, _invocationHandlerLogger.Object);

        var request = new JsonRpcRequest
        {
            Id = "respond-disabled",
            Method = "hub.invoke.respond",
            Params = JsonSerializer.SerializeToElement(new
            {
                instanceId = "respond-disabled",
                invocationId = "invk-non-existent",
                value = new { ok = true }
            })
        };

        var response = await handler.HandleAsync(request, CancellationToken.None);

        Assert.NotNull(response.Error);
        Assert.Equal(-32002, response.Error.Code);
        Assert.Equal("forbidden", response.Error.Message);
        var data = JsonSerializer.SerializeToElement(response.Error.Data);
        Assert.Equal("respond_not_enabled", data.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task InvocationAndLaunchDeferredMethods_ShouldReturnNotSupported()
    {
        var appRegistry = new AppRegistry(_registryLogger.Object);
        var definitionLoader = new DefinitionLoader(_tempDirectory, _definitionLogger.Object);
        var routingService = new InvocationRoutingService(appRegistry, _routingLogger.Object);
        var store = new InvocationStore(_storeLogger.Object, routingService);
        var waiter = new InvocationRequestWaiter(Mock.Of<ILogger<InvocationRequestWaiter>>());
        var invocationHandler = new InvocationHandler(appRegistry, definitionLoader, routingService, store, waiter, _invocationHandlerLogger.Object);
        var launchHandler = new LaunchHandler(_launchHandlerLogger.Object);

        var requestResponse = await invocationHandler.HandleAsync(new JsonRpcRequest
        {
            Id = "request-runtime-check",
            Method = "hub.invoke.request",
            Params = JsonSerializer.SerializeToElement(new
            {
                appId = "test.app",
                target = new { scope = (string?)null, instanceId = (string?)null },
                method = "test.m",
                args = new { },
                options = new
                {
                    ttlMs = 1000,
                    waitTimeoutMs = 1000,
                    queueIfOffline = false,
                    autoLaunch = false
                }
            })
        }, CancellationToken.None);

        Assert.NotNull(requestResponse.Error);
        Assert.Equal(-32010, requestResponse.Error.Code);
        Assert.Equal("instance_not_found", requestResponse.Error.Message);
        var requestData = JsonSerializer.SerializeToElement(requestResponse.Error.Data);
        Assert.Equal("offline_no_queue", requestData.GetProperty("reason").GetString());

        var launchResponse = await launchHandler.HandleAsync(new JsonRpcRequest
        {
            Id = "launch-deferred",
            Method = "hub.apps.launch",
            Params = JsonSerializer.SerializeToElement(new { appId = "launch.app" })
        }, CancellationToken.None);

        Assert.NotNull(launchResponse.Error);
        Assert.Equal(-32099, launchResponse.Error.Code);
        Assert.Equal("not_supported", launchResponse.Error.Message);
        var launchData = JsonSerializer.SerializeToElement(launchResponse.Error.Data);
        Assert.Equal("launch_deferred", launchData.GetProperty("reason").GetString());
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

    private void WriteDefinition(string appId, string scopePolicy, bool rpcEnabled)
    {
        var filePath = Path.Combine(_tempDirectory, $"{appId}.json");
        File.WriteAllText(filePath, JsonSerializer.Serialize(new
        {
            appId,
            displayName = appId,
            scopePolicy,
            capabilities = new
            {
                rpc = rpcEnabled,
                events = false
            }
        }));
    }
}
