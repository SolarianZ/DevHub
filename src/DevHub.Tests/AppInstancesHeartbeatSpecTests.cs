namespace DevHub.Tests;

using System.Globalization;
using System.Text.Json;
using DevHub.Core.Models;
using DevHub.Core.Models.Rpc;
using DevHub.Core.Services;
using DevHub.Core.Services.Rpc.Handlers;
using Microsoft.Extensions.Logging;
using Moq;

/// <summary>
/// hub.apps.heartbeat 规范白盒测试。
/// </summary>
[Trait("Category", "Spec")]
public class AppInstancesHeartbeatSpecTests
{
    private readonly Mock<ILogger<AppRegistry>> _registryLogger = new();
    private readonly Mock<ILogger<AppInstancesHandler>> _handlerLogger = new();

    [Fact]
    [Trait("SpecRef", "6.3.6")]
    public async Task Spec_6_3_6_Heartbeat_ShouldReturnOkAndRefreshLastSeenUtc()
    {
        var appRegistry = new AppRegistry(new SystemClock(), _registryLogger.Object);
        var initialLastSeen = DateTime.UtcNow.AddSeconds(-10);

        appRegistry.RegisterInstance(new AppInstance
        {
            InstanceId = "heartbeat-spec-inst",
            AppId = "heartbeat-spec.app",
            Scope = null,
            Pid = 7011,
            RegisteredAtUtc = DateTime.UtcNow.AddSeconds(-20),
            LastSeenUtc = initialLastSeen,
            Invoke = new InvokeCapability { Poll = true, Respond = true }
        });

        var handler = new AppInstancesHandler(appRegistry, new SystemClock(), _handlerLogger.Object);

        var firstResponse = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "heartbeat-first",
            Method = "hub.apps.heartbeat",
            Params = JsonSerializer.SerializeToElement(new
            {
                instanceId = "heartbeat-spec-inst"
            })
        }, CancellationToken.None);

        Assert.Null(firstResponse.Error);
        var firstResult = JsonSerializer.SerializeToElement(firstResponse.Result);
        Assert.True(firstResult.GetProperty("ok").GetBoolean());
        Assert.True(firstResult.TryGetProperty("lastSeenUtc", out var firstLastSeenProperty));
        Assert.True(DateTime.TryParse(firstLastSeenProperty.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var firstLastSeenUtc));
        Assert.True(firstLastSeenUtc > initialLastSeen);

        await Task.Delay(10);

        var secondResponse = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "heartbeat-second",
            Method = "hub.apps.heartbeat",
            Params = JsonSerializer.SerializeToElement(new
            {
                instanceId = "heartbeat-spec-inst"
            })
        }, CancellationToken.None);

        Assert.Null(secondResponse.Error);
        var secondResult = JsonSerializer.SerializeToElement(secondResponse.Result);
        Assert.True(secondResult.GetProperty("ok").GetBoolean());
        Assert.True(secondResult.TryGetProperty("lastSeenUtc", out var secondLastSeenProperty));
        Assert.True(DateTime.TryParse(secondLastSeenProperty.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var secondLastSeenUtc));
        Assert.True(secondLastSeenUtc >= firstLastSeenUtc);

        var currentInstance = appRegistry.GetInstance("heartbeat-spec-inst");
        Assert.NotNull(currentInstance);
        Assert.True(currentInstance!.LastSeenUtc >= secondLastSeenUtc.ToUniversalTime());
    }

    [Fact]
    [Trait("SpecRef", "6.3.6")]
    public async Task Spec_6_3_6_Heartbeat_UnknownInstance_ShouldReturnInstanceNotFoundWithUnknownReason()
    {
        var appRegistry = new AppRegistry(new SystemClock(), _registryLogger.Object);
        var handler = new AppInstancesHandler(appRegistry, new SystemClock(), _handlerLogger.Object);

        var response = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "heartbeat-unknown",
            Method = "hub.apps.heartbeat",
            Params = JsonSerializer.SerializeToElement(new
            {
                instanceId = "missing-inst"
            })
        }, CancellationToken.None);

        Assert.NotNull(response.Error);
        Assert.Equal(-32010, response.Error.Code);
        Assert.Equal("instance_not_found", response.Error.Message);

        var errorData = JsonSerializer.SerializeToElement(response.Error.Data);
        Assert.Equal("unknown_instance", errorData.GetProperty("reason").GetString());
        Assert.Equal("missing-inst", errorData.GetProperty("instanceId").GetString());
    }
}



