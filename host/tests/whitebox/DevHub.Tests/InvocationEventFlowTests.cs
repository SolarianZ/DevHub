namespace DevHub.Tests;

using System.Text.Json;
using DevHub.Core.Models;
using DevHub.Core.Models.Rpc;
using DevHub.Core.Services;
using DevHub.Core.Services.Events;
using DevHub.Core.Services.Invocation;
using DevHub.Core.Services.Rpc.Handlers;
using Microsoft.Extensions.Logging;
using Moq;

/// <summary>
/// Invocation 生命周期事件发布测试。
/// </summary>
[Trait("Category", "Impl")]
public class InvocationEventFlowTests : IDisposable
{
    private readonly string _tempDirectory;
    private readonly Mock<ILogger<AppRegistry>> _registryLogger = new();
    private readonly Mock<ILogger<DefinitionLoader>> _definitionLogger = new();
    private readonly Mock<ILogger<InvocationStore>> _storeLogger = new();
    private readonly Mock<ILogger<InvocationRoutingService>> _routingLogger = new();
    private readonly Mock<ILogger<InvocationRequestWaiter>> _waiterLogger = new();
    private readonly Mock<ILogger<LaunchCoordinator>> _launchLogger = new();
    private readonly Mock<ILogger<InvocationHandler>> _invocationLogger = new();
    private readonly Mock<ILogger<HubEventBus>> _eventBusLogger = new();

    /// <summary>
    /// 初始化测试上下文。
    /// </summary>
    public InvocationEventFlowTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "DevHubInvocationEventFlowTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
    }

    [Fact]
    public async Task Impl_NotifyPollRespondValue_ShouldPublishQueuedDeliveredCompleted()
    {
        var appRegistry = new AppRegistry(new SystemClock(), _registryLogger.Object);
        appRegistry.RegisterInstance(new AppInstance
        {
            InstanceId = "inst-event-success",
            AppId = "event.invoke.app",
            Scope = ScopeContract.Global,
            Pid = 6101,
            Invoke = new InvokeCapability { Poll = true, Respond = true }
        });
        var instanceSessionToken = GetInstanceSessionToken(appRegistry, "inst-event-success");

        var (handler, eventBus) = CreateHandler(appRegistry);
        PrepareSubscription(eventBus, "conn-invocation-success");

        var notifyResponse = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "notify-success",
            Method = "hub.invoke.notify",
            Params = JsonSerializer.SerializeToElement(new
            {
                appId = "event.invoke.app",
                target = new
                {
                    scope = ScopeContract.Global,
                    instanceId = "inst-event-success"
                },
                method = "demo.notify",
                args = new { value = 1 },
                options = new
                {
                    ttlMs = 60000,
                    queueIfOffline = true,
                    autoLaunch = false
                }
            })
        }, CancellationToken.None);

        Assert.Null(notifyResponse.Error);

        var pollResponse = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "poll-success",
            Method = "hub.invoke.poll",
            Params = JsonSerializer.SerializeToElement(new
            {
                instanceId = "inst-event-success",
                instanceSessionToken,
                maxCount = 1,
                waitMs = 0
            })
        }, CancellationToken.None);

        Assert.Null(pollResponse.Error);
        var pollResult = JsonSerializer.SerializeToElement(pollResponse.Result);
        var items = pollResult.GetProperty("items").EnumerateArray().ToList();
        Assert.Single(items);
        var invocationId = items[0].GetProperty("invocationId").GetString();
        Assert.False(string.IsNullOrWhiteSpace(invocationId));

        var respondResponse = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "respond-success",
            Method = "hub.invoke.respond",
            Params = JsonSerializer.SerializeToElement(new
            {
                instanceId = "inst-event-success",
                instanceSessionToken,
                invocationId,
                value = new { ok = true }
            })
        }, CancellationToken.None);

        Assert.Null(respondResponse.Error);

        var deliveries = eventBus.DrainDeliveries("conn-invocation-success", maxCount: 20);
        var eventTypes = deliveries.Select(d => d.Type).ToList();

        Assert.Contains("invocation.queued", eventTypes);
        Assert.Contains("invocation.delivered", eventTypes);
        Assert.Contains("invocation.completed", eventTypes);

        var completedPayload = JsonSerializer.SerializeToElement(deliveries.First(d => d.Type == "invocation.completed").Payload);
        Assert.Equal(invocationId, completedPayload.GetProperty("invocationId").GetString());
        Assert.Equal("event.invoke.app", completedPayload.GetProperty("appId").GetString());
        Assert.Equal("inst-event-success", completedPayload.GetProperty("instanceId").GetString());
    }

    [Fact]
    public async Task Impl_NotifyPollRespondError_ShouldPublishFailed()
    {
        var appRegistry = new AppRegistry(new SystemClock(), _registryLogger.Object);
        appRegistry.RegisterInstance(new AppInstance
        {
            InstanceId = "inst-event-failed",
            AppId = "event.invoke.fail.app",
            Scope = "workspace-A",
            Pid = 6102,
            Invoke = new InvokeCapability { Poll = true, Respond = true }
        });
        var instanceSessionToken = GetInstanceSessionToken(appRegistry, "inst-event-failed");

        var (handler, eventBus) = CreateHandler(appRegistry);
        PrepareSubscription(eventBus, "conn-invocation-failed");

        var notifyResponse = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "notify-failed",
            Method = "hub.invoke.notify",
            Params = JsonSerializer.SerializeToElement(new
            {
                appId = "event.invoke.fail.app",
                target = new
                {
                    scope = "workspace-A",
                    instanceId = "inst-event-failed"
                },
                method = "demo.notify",
                args = new { value = 2 },
                options = new
                {
                    ttlMs = 60000,
                    queueIfOffline = true,
                    autoLaunch = false
                }
            })
        }, CancellationToken.None);

        Assert.Null(notifyResponse.Error);

        var pollResponse = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "poll-failed",
            Method = "hub.invoke.poll",
            Params = JsonSerializer.SerializeToElement(new
            {
                instanceId = "inst-event-failed",
                instanceSessionToken,
                maxCount = 1,
                waitMs = 0
            })
        }, CancellationToken.None);

        Assert.Null(pollResponse.Error);
        var pollResult = JsonSerializer.SerializeToElement(pollResponse.Result);
        var invocationId = pollResult.GetProperty("items").EnumerateArray().First().GetProperty("invocationId").GetString();

        var respondResponse = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "respond-failed",
            Method = "hub.invoke.respond",
            Params = JsonSerializer.SerializeToElement(new
            {
                instanceId = "inst-event-failed",
                instanceSessionToken,
                invocationId,
                error = new
                {
                    code = 1001,
                    message = "app_error",
                    data = new { reason = "mock" }
                }
            })
        }, CancellationToken.None);

        Assert.Null(respondResponse.Error);

        var deliveries = eventBus.DrainDeliveries("conn-invocation-failed", maxCount: 20);
        var eventTypes = deliveries.Select(d => d.Type).ToList();

        Assert.Contains("invocation.queued", eventTypes);
        Assert.Contains("invocation.delivered", eventTypes);
        Assert.Contains("invocation.failed", eventTypes);

        var failedPayload = JsonSerializer.SerializeToElement(deliveries.First(d => d.Type == "invocation.failed").Payload);
        Assert.Equal(invocationId, failedPayload.GetProperty("invocationId").GetString());
        Assert.Equal("event.invoke.fail.app", failedPayload.GetProperty("appId").GetString());
        Assert.Equal("inst-event-failed", failedPayload.GetProperty("instanceId").GetString());
        Assert.True(failedPayload.TryGetProperty("error", out _));
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

    private (InvocationHandler Handler, HubEventBus EventBus) CreateHandler(AppRegistry appRegistry)
    {
        var definitionLoader = new DefinitionLoader(_tempDirectory, _definitionLogger.Object);
        var definitionProvider = new DefinitionProvider(definitionLoader);
        definitionProvider.Refresh();
        var routingService = new InvocationRoutingService(appRegistry, _routingLogger.Object);
        var eventBus = new HubEventBus(_eventBusLogger.Object);
        var store = new InvocationStore(_storeLogger.Object, routingService, new SystemClock(), eventBus);
        var waiter = new InvocationRequestWaiter(_waiterLogger.Object);
        var runtimeHttpBaseUrlProvider = new RuntimeHttpBaseUrlProvider(Mock.Of<ILogger<RuntimeHttpBaseUrlProvider>>(), RuntimePathOptions.Resolve());
        var launchCoordinator = new LaunchCoordinator(definitionProvider, appRegistry, runtimeHttpBaseUrlProvider, new ProcessLauncher(), new SystemClock(), _launchLogger.Object);
        var handler = new InvocationHandler(appRegistry, definitionProvider, routingService, store, waiter, launchCoordinator, new SystemClock(), _invocationLogger.Object, eventBus);
        return (handler, eventBus);
    }

    private static void PrepareSubscription(HubEventBus eventBus, string connectionId)
    {
        eventBus.RegisterConnection(connectionId);
        Assert.True(eventBus.TryMarkAuthenticated(connectionId, "test-client", Guid.NewGuid().ToString("D")));
        Assert.True(eventBus.TrySubscribe(connectionId, null, out _));
    }

    private static string GetInstanceSessionToken(AppRegistry appRegistry, string instanceId)
    {
        return appRegistry.GetCurrentInstanceSessionToken(instanceId)
               ?? throw new InvalidOperationException($"Instance '{instanceId}' session token was not registered.");
    }
}






