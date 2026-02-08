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
    private readonly Mock<ILogger<LaunchCoordinator>> _launchLogger = new();
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
    public async Task InvocationHandler_Notify_WithRpcDisabledDefinition_ShouldReturnForbidden()
    {
        WriteDefinition("disabled-app", rpcEnabled: false);

        var appRegistry = new AppRegistry(_registryLogger.Object);
        var definitionLoader = new DefinitionLoader(_tempDirectory, _definitionLogger.Object);
        definitionLoader.Load();
        var routingService = new InvocationRoutingService(appRegistry, _routingLogger.Object);
        var store = new InvocationStore(_storeLogger.Object, routingService);
        var waiter = new InvocationRequestWaiter(Mock.Of<ILogger<InvocationRequestWaiter>>());
        var runtimeHttpBaseUrlProvider = new RuntimeHttpBaseUrlProvider(Mock.Of<ILogger<RuntimeHttpBaseUrlProvider>>());
        var launchCoordinator = new LaunchCoordinator(definitionLoader, appRegistry, runtimeHttpBaseUrlProvider, _launchLogger.Object);
        var handler = new InvocationHandler(appRegistry, definitionLoader, routingService, store, waiter, launchCoordinator, _invocationHandlerLogger.Object);

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
        var runtimeHttpBaseUrlProvider = new RuntimeHttpBaseUrlProvider(Mock.Of<ILogger<RuntimeHttpBaseUrlProvider>>());
        var launchCoordinator = new LaunchCoordinator(definitionLoader, appRegistry, runtimeHttpBaseUrlProvider, _launchLogger.Object);
        var handler = new InvocationHandler(appRegistry, definitionLoader, routingService, store, waiter, launchCoordinator, _invocationHandlerLogger.Object);

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
        var runtimeHttpBaseUrlProvider = new RuntimeHttpBaseUrlProvider(Mock.Of<ILogger<RuntimeHttpBaseUrlProvider>>());
        var launchCoordinator = new LaunchCoordinator(definitionLoader, appRegistry, runtimeHttpBaseUrlProvider, _launchLogger.Object);
        var handler = new InvocationHandler(appRegistry, definitionLoader, routingService, store, waiter, launchCoordinator, _invocationHandlerLogger.Object);

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
    public async Task InvocationAndLaunchHandlers_ShouldReturnExpectedLaunchErrors()
    {
        var appRegistry = new AppRegistry(_registryLogger.Object);
        var definitionLoader = new DefinitionLoader(_tempDirectory, _definitionLogger.Object);
        definitionLoader.Load();
        var routingService = new InvocationRoutingService(appRegistry, _routingLogger.Object);
        var store = new InvocationStore(_storeLogger.Object, routingService);
        var waiter = new InvocationRequestWaiter(Mock.Of<ILogger<InvocationRequestWaiter>>());
        var runtimeHttpBaseUrlProvider = new RuntimeHttpBaseUrlProvider(Mock.Of<ILogger<RuntimeHttpBaseUrlProvider>>());
        var launchCoordinator = new LaunchCoordinator(definitionLoader, appRegistry, runtimeHttpBaseUrlProvider, _launchLogger.Object);
        var invocationHandler = new InvocationHandler(appRegistry, definitionLoader, routingService, store, waiter, launchCoordinator, _invocationHandlerLogger.Object);
        var launchHandler = new LaunchHandler(launchCoordinator, _launchHandlerLogger.Object);

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
                    queueIfOffline = true,
                    autoLaunch = true
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
        Assert.Equal(-32014, launchResponse.Error.Code);
        Assert.Equal("app_definition_not_found", launchResponse.Error.Message);
        var launchData = JsonSerializer.SerializeToElement(launchResponse.Error.Data);
        Assert.Equal("launch.app", launchData.GetProperty("appId").GetString());
    }

    [Fact]
    public async Task InvocationHandler_Notify_AutoLaunchWithoutLaunchConfig_ShouldReturnLaunchFailed()
    {
        WriteDefinition("notify-launch-missing", rpcEnabled: true);

        var appRegistry = new AppRegistry(_registryLogger.Object);
        var definitionLoader = new DefinitionLoader(_tempDirectory, _definitionLogger.Object);
        definitionLoader.Load();
        var routingService = new InvocationRoutingService(appRegistry, _routingLogger.Object);
        var store = new InvocationStore(_storeLogger.Object, routingService);
        var waiter = new InvocationRequestWaiter(Mock.Of<ILogger<InvocationRequestWaiter>>());
        var runtimeHttpBaseUrlProvider = new RuntimeHttpBaseUrlProvider(Mock.Of<ILogger<RuntimeHttpBaseUrlProvider>>());
        var launchCoordinator = new LaunchCoordinator(definitionLoader, appRegistry, runtimeHttpBaseUrlProvider, _launchLogger.Object);
        var handler = new InvocationHandler(appRegistry, definitionLoader, routingService, store, waiter, launchCoordinator, _invocationHandlerLogger.Object);

        var request = new JsonRpcRequest
        {
            Id = "notify-launch-missing",
            Method = "hub.invoke.notify",
            Params = JsonSerializer.SerializeToElement(new
            {
                appId = "notify-launch-missing",
                target = new { scope = (string?)null, instanceId = (string?)null },
                method = "task.sync",
                args = new { },
                options = new
                {
                    ttlMs = 60000,
                    queueIfOffline = true,
                    autoLaunch = true
                }
            })
        };

        var response = await handler.HandleAsync(request, CancellationToken.None);

        Assert.NotNull(response.Error);
        Assert.Equal(-32020, response.Error.Code);
        Assert.Equal("launch_failed", response.Error.Message);
        var data = JsonSerializer.SerializeToElement(response.Error.Data);
        Assert.Equal("launch_config_missing", data.GetProperty("reason").GetString());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public async Task InvocationHandler_Poll_MaxCountOutOfRange_ShouldReturnInvalidParams(int maxCount)
    {
        var handler = CreateInvocationHandler(new AppRegistry(_registryLogger.Object));

        var response = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = $"poll-maxCount-{maxCount}",
            Method = "hub.invoke.poll",
            Params = JsonSerializer.SerializeToElement(new
            {
                instanceId = "poll-instance",
                maxCount,
                waitMs = 0
            })
        }, CancellationToken.None);

        Assert.NotNull(response.Error);
        Assert.Equal(-32602, response.Error.Code);
        Assert.Equal("invalid_params", response.Error.Message);
    }

    [Fact]
    public async Task InvocationHandler_Respond_WithValueAndError_ShouldReturnInvalidParams()
    {
        var handler = CreateInvocationHandler(new AppRegistry(_registryLogger.Object));

        var response = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "respond-both",
            Method = "hub.invoke.respond",
            Params = JsonSerializer.SerializeToElement(new
            {
                instanceId = "respond-instance",
                invocationId = "invk-both",
                value = new { ok = true },
                error = new { code = 1001, message = "app_error" }
            })
        }, CancellationToken.None);

        Assert.NotNull(response.Error);
        Assert.Equal(-32602, response.Error.Code);
        Assert.Equal("invalid_params", response.Error.Message);
    }

    [Fact]
    public async Task InvocationHandler_Respond_WithoutValueAndError_ShouldReturnInvalidParams()
    {
        var handler = CreateInvocationHandler(new AppRegistry(_registryLogger.Object));

        var response = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "respond-none",
            Method = "hub.invoke.respond",
            Params = JsonSerializer.SerializeToElement(new
            {
                instanceId = "respond-instance",
                invocationId = "invk-none"
            })
        }, CancellationToken.None);

        Assert.NotNull(response.Error);
        Assert.Equal(-32602, response.Error.Code);
        Assert.Equal("invalid_params", response.Error.Message);
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

    private InvocationHandler CreateInvocationHandler(AppRegistry appRegistry)
    {
        var definitionLoader = new DefinitionLoader(_tempDirectory, _definitionLogger.Object);
        definitionLoader.Load();
        var routingService = new InvocationRoutingService(appRegistry, _routingLogger.Object);
        var store = new InvocationStore(_storeLogger.Object, routingService);
        var waiter = new InvocationRequestWaiter(Mock.Of<ILogger<InvocationRequestWaiter>>());
        var runtimeHttpBaseUrlProvider = new RuntimeHttpBaseUrlProvider(Mock.Of<ILogger<RuntimeHttpBaseUrlProvider>>());
        var launchCoordinator = new LaunchCoordinator(definitionLoader, appRegistry, runtimeHttpBaseUrlProvider, _launchLogger.Object);
        return new InvocationHandler(appRegistry, definitionLoader, routingService, store, waiter, launchCoordinator, _invocationHandlerLogger.Object);
    }

    private void WriteDefinition(string appId, bool rpcEnabled, bool includeLaunch = false, string? dedupeKeyTemplate = null)
    {
        var filePath = Path.Combine(_tempDirectory, $"{appId}.json");
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

        File.WriteAllText(filePath, JsonSerializer.Serialize(payload));
    }
}
