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
/// Invocation request 闭环测试。
/// </summary>
public class InvocationRequestFlowTests : IDisposable
{
    private readonly string _tempDirectory;
    private readonly Mock<ILogger<AppRegistry>> _registryLogger = new();
    private readonly Mock<ILogger<DefinitionLoader>> _definitionLogger = new();
    private readonly Mock<ILogger<InvocationStore>> _storeLogger = new();
    private readonly Mock<ILogger<InvocationRoutingService>> _routingLogger = new();
    private readonly Mock<ILogger<InvocationRequestWaiter>> _waiterLogger = new();
    private readonly Mock<ILogger<LaunchCoordinator>> _launchLogger = new();
    private readonly Mock<ILogger<InvocationHandler>> _invocationHandlerLogger = new();

    /// <summary>
    /// 初始化测试上下文。
    /// </summary>
    public InvocationRequestFlowTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "DevHubInvocationRequestFlowTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
    }

    [Fact]
    public async Task Request_Poll_RespondValue_ShouldReturnSuccessWithValue()
    {
        const string appId = "request-success.app";
        const string instanceId = "request-success-instance";
        WriteDefinition(appId, rpcEnabled: true);

        var appRegistry = new AppRegistry(_registryLogger.Object);
        appRegistry.RegisterInstance(new AppInstance
        {
            InstanceId = instanceId,
            AppId = appId,
            Scope = null,
            Pid = 6101,
            Invoke = new InvokeCapability { Poll = true, Respond = true }
        });

        var handler = CreateHandler(appRegistry);

        var requestTask = handler.HandleAsync(new JsonRpcRequest
        {
            Id = "req-success",
            Method = "hub.invoke.request",
            Params = JsonSerializer.SerializeToElement(new
            {
                appId,
                target = new { scope = (string?)null, instanceId = (string?)null },
                method = "asset.build",
                args = new { branch = "main" },
                options = new
                {
                    ttlMs = 10000,
                    waitTimeoutMs = 3000,
                    queueIfOffline = true,
                    autoLaunch = false
                }
            })
        }, CancellationToken.None);

        var pollResponse = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "poll-success",
            Method = "hub.invoke.poll",
            Params = JsonSerializer.SerializeToElement(new
            {
                instanceId,
                maxCount = 1,
                waitMs = 1000
            })
        }, CancellationToken.None);

        Assert.Null(pollResponse.Error);
        var pollResult = JsonSerializer.SerializeToElement(pollResponse.Result);
        var item = pollResult.GetProperty("items").EnumerateArray().Single();
        var invocationId = item.GetProperty("invocationId").GetString();
        Assert.False(string.IsNullOrWhiteSpace(invocationId));

        var respondResponse = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "respond-success",
            Method = "hub.invoke.respond",
            Params = JsonSerializer.SerializeToElement(new
            {
                instanceId,
                invocationId,
                value = new { ok = true, version = 1 }
            })
        }, CancellationToken.None);

        Assert.Null(respondResponse.Error);

        var requestResponse = await requestTask;
        Assert.Null(requestResponse.Error);
        var requestResult = JsonSerializer.SerializeToElement(requestResponse.Result);
        Assert.True(requestResult.GetProperty("ok").GetBoolean());
        Assert.Equal(invocationId, requestResult.GetProperty("invocationId").GetString());

        var value = requestResult.GetProperty("value");
        Assert.True(value.GetProperty("ok").GetBoolean());
        Assert.Equal(1, value.GetProperty("version").GetInt32());
    }

    [Fact]
    public async Task Request_WhenCalleeRespondsError_ShouldReturnInvocationFailed()
    {
        const string appId = "request-failed.app";
        const string instanceId = "request-failed-instance";
        WriteDefinition(appId, rpcEnabled: true);

        var appRegistry = new AppRegistry(_registryLogger.Object);
        appRegistry.RegisterInstance(new AppInstance
        {
            InstanceId = instanceId,
            AppId = appId,
            Scope = null,
            Pid = 6102,
            Invoke = new InvokeCapability { Poll = true, Respond = true }
        });

        var handler = CreateHandler(appRegistry);

        var requestTask = handler.HandleAsync(new JsonRpcRequest
        {
            Id = "req-failed",
            Method = "hub.invoke.request",
            Params = JsonSerializer.SerializeToElement(new
            {
                appId,
                target = new { scope = (string?)null, instanceId = (string?)null },
                method = "asset.build",
                args = new { branch = "dev" },
                options = new
                {
                    ttlMs = 10000,
                    waitTimeoutMs = 3000,
                    queueIfOffline = true,
                    autoLaunch = false
                }
            })
        }, CancellationToken.None);

        var pollResponse = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "poll-failed",
            Method = "hub.invoke.poll",
            Params = JsonSerializer.SerializeToElement(new { instanceId, maxCount = 1, waitMs = 1000 })
        }, CancellationToken.None);

        Assert.Null(pollResponse.Error);
        var pollResult = JsonSerializer.SerializeToElement(pollResponse.Result);
        var invocationId = pollResult.GetProperty("items").EnumerateArray().Single().GetProperty("invocationId").GetString();

        var respondResponse = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "respond-failed",
            Method = "hub.invoke.respond",
            Params = JsonSerializer.SerializeToElement(new
            {
                instanceId,
                invocationId,
                error = new
                {
                    code = 1001,
                    message = "callee_error",
                    data = new { detail = "bad_input" }
                }
            })
        }, CancellationToken.None);

        Assert.Null(respondResponse.Error);

        var requestResponse = await requestTask;
        Assert.NotNull(requestResponse.Error);
        Assert.Equal(-32050, requestResponse.Error.Code);
        Assert.Equal("invocation_failed", requestResponse.Error.Message);

        var errorData = JsonSerializer.SerializeToElement(requestResponse.Error.Data);
        Assert.Equal(invocationId, errorData.GetProperty("invocationId").GetString());

        var calleeError = errorData.GetProperty("calleeError");
        Assert.Equal(1001, calleeError.GetProperty("code").GetInt32());
        Assert.Equal("callee_error", calleeError.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Request_WhenWaitTimeoutElapsed_ShouldReturnTimeout_AndLateRespondShouldBeExpired()
    {
        const string appId = "request-timeout.app";
        const string instanceId = "request-timeout-instance";
        WriteDefinition(appId, rpcEnabled: true);

        var appRegistry = new AppRegistry(_registryLogger.Object);
        appRegistry.RegisterInstance(new AppInstance
        {
            InstanceId = instanceId,
            AppId = appId,
            Scope = null,
            Pid = 6103,
            Invoke = new InvokeCapability { Poll = true, Respond = true }
        });

        var handler = CreateHandler(appRegistry);

        var requestTask = handler.HandleAsync(new JsonRpcRequest
        {
            Id = "req-timeout",
            Method = "hub.invoke.request",
            Params = JsonSerializer.SerializeToElement(new
            {
                appId,
                target = new { scope = (string?)null, instanceId = (string?)null },
                method = "asset.slow",
                args = new { x = 1 },
                options = new
                {
                    ttlMs = 5000,
                    waitTimeoutMs = 300,
                    queueIfOffline = true,
                    autoLaunch = false
                }
            })
        }, CancellationToken.None);

        var pollResponse = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "poll-timeout",
            Method = "hub.invoke.poll",
            Params = JsonSerializer.SerializeToElement(new { instanceId, maxCount = 1, waitMs = 1000 })
        }, CancellationToken.None);

        Assert.Null(pollResponse.Error);
        var pollResult = JsonSerializer.SerializeToElement(pollResponse.Result);
        var invocationId = pollResult.GetProperty("items").EnumerateArray().Single().GetProperty("invocationId").GetString();

        var requestResponse = await requestTask;
        Assert.NotNull(requestResponse.Error);
        Assert.Equal(-32012, requestResponse.Error.Code);
        Assert.Equal("invocation_timeout", requestResponse.Error.Message);

        var timeoutData = JsonSerializer.SerializeToElement(requestResponse.Error.Data);
        Assert.Equal(invocationId, timeoutData.GetProperty("invocationId").GetString());
        Assert.True(timeoutData.GetProperty("elapsedMs").GetInt32() >= 300);

        var lateRespondResponse = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "respond-late",
            Method = "hub.invoke.respond",
            Params = JsonSerializer.SerializeToElement(new
            {
                instanceId,
                invocationId,
                value = new { ok = true }
            })
        }, CancellationToken.None);

        Assert.NotNull(lateRespondResponse.Error);
        Assert.Equal(-32011, lateRespondResponse.Error.Code);
        Assert.Equal("invocation_expired", lateRespondResponse.Error.Message);
    }

    [Fact]
    public async Task Request_WhenWaitTimeoutGreaterThanTtl_ShouldReturnInvalidParams()
    {
        WriteDefinition("request-invalid.app", rpcEnabled: true);
        var handler = CreateHandler(new AppRegistry(_registryLogger.Object));

        var response = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "req-invalid",
            Method = "hub.invoke.request",
            Params = JsonSerializer.SerializeToElement(new
            {
                appId = "request-invalid.app",
                target = new { scope = (string?)null, instanceId = (string?)null },
                method = "asset.build",
                args = new { },
                options = new
                {
                    ttlMs = 1000,
                    waitTimeoutMs = 1001,
                    queueIfOffline = true,
                    autoLaunch = false
                }
            })
        }, CancellationToken.None);

        Assert.NotNull(response.Error);
        Assert.Equal(-32602, response.Error.Code);
        Assert.Equal("invalid_params", response.Error.Message);
    }

    [Fact]
    public async Task Request_WhenOfflineAndQueueIfOfflineFalse_ShouldReturnInstanceNotFound()
    {
        WriteDefinition("request-offline.app", rpcEnabled: true);
        var handler = CreateHandler(new AppRegistry(_registryLogger.Object));

        var response = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "req-offline",
            Method = "hub.invoke.request",
            Params = JsonSerializer.SerializeToElement(new
            {
                appId = "request-offline.app",
                target = new { scope = (string?)null, instanceId = (string?)null },
                method = "asset.build",
                args = new { },
                options = new
                {
                    ttlMs = 3000,
                    waitTimeoutMs = 1000,
                    queueIfOffline = false,
                    autoLaunch = false
                }
            })
        }, CancellationToken.None);

        Assert.NotNull(response.Error);
        Assert.Equal(-32010, response.Error.Code);
        Assert.Equal("instance_not_found", response.Error.Message);
        var errorData = JsonSerializer.SerializeToElement(response.Error.Data);
        Assert.Equal("offline_no_queue", errorData.GetProperty("reason").GetString());
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

    private InvocationHandler CreateHandler(AppRegistry appRegistry)
    {
        var definitionLoader = new DefinitionLoader(_tempDirectory, _definitionLogger.Object);
        definitionLoader.Load();
        var routingService = new InvocationRoutingService(appRegistry, _routingLogger.Object);
        var store = new InvocationStore(_storeLogger.Object, routingService);
        var waiter = new InvocationRequestWaiter(_waiterLogger.Object);
        var runtimeHttpBaseUrlProvider = new RuntimeHttpBaseUrlProvider(Mock.Of<ILogger<RuntimeHttpBaseUrlProvider>>());
        var launchCoordinator = new LaunchCoordinator(definitionLoader, appRegistry, runtimeHttpBaseUrlProvider, _launchLogger.Object);
        return new InvocationHandler(appRegistry, definitionLoader, routingService, store, waiter, launchCoordinator, _invocationHandlerLogger.Object);
    }

    private void WriteDefinition(string appId, bool rpcEnabled)
    {
        var filePath = Path.Combine(_tempDirectory, $"{appId}.json");
        File.WriteAllText(filePath, JsonSerializer.Serialize(new
        {
            appId,
            displayName = appId,
            scopePolicy = "any",
            capabilities = new
            {
                rpc = rpcEnabled,
                events = false
            }
        }));
    }
}
