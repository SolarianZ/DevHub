namespace DevHub.Tests;

using System.Globalization;
using System.Text.Json;
using DevHub.Core.Models;
using DevHub.Core.Models.Rpc;
using DevHub.Core.Services;
using DevHub.Core.Services.Abstractions;
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
        var clock = new MutableClock(DateTime.UtcNow);
        var appRegistry = new AppRegistry(clock, _registryLogger.Object);
        var initialLastSeen = clock.UtcNow.AddSeconds(-10);

        var registered = appRegistry.TryRegisterInstance(
            new AppInstance
            {
                InstanceId = "heartbeat-spec-inst",
                AppId = "heartbeat-spec.app",
                Scope = ScopeContract.Global,
                Pid = 7011,
                RegisteredAtUtc = clock.UtcNow.AddSeconds(-20),
                LastSeenUtc = initialLastSeen,
                Invoke = new InvokeCapability { Poll = true, Respond = true }
            },
            "heartbeat-spec-password",
            out _,
            out var instanceSessionToken,
            out _);
        Assert.True(registered);

        var handler = new AppInstancesHandler(appRegistry, clock, _handlerLogger.Object);

        var firstResponse = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "heartbeat-first",
            Method = "hub.apps.heartbeat",
            Params = JsonSerializer.SerializeToElement(new
            {
                instanceId = "heartbeat-spec-inst",
                instanceSessionToken
            })
        }, CancellationToken.None);

        Assert.Null(firstResponse.Error);
        var firstResult = JsonSerializer.SerializeToElement(firstResponse.Result);
        Assert.True(firstResult.GetProperty("ok").GetBoolean());
        Assert.True(firstResult.TryGetProperty("lastSeenUtc", out var firstLastSeenProperty));
        Assert.True(DateTime.TryParse(firstLastSeenProperty.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var firstLastSeenUtc));
        Assert.True(firstLastSeenUtc > initialLastSeen);

        clock.Advance(TimeSpan.FromSeconds(1));

        var secondResponse = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "heartbeat-second",
            Method = "hub.apps.heartbeat",
            Params = JsonSerializer.SerializeToElement(new
            {
                instanceId = "heartbeat-spec-inst",
                instanceSessionToken
            })
        }, CancellationToken.None);

        Assert.Null(secondResponse.Error);
        var secondResult = JsonSerializer.SerializeToElement(secondResponse.Result);
        Assert.True(secondResult.GetProperty("ok").GetBoolean());
        Assert.True(secondResult.TryGetProperty("lastSeenUtc", out var secondLastSeenProperty));
        Assert.True(DateTime.TryParse(secondLastSeenProperty.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var secondLastSeenUtc));
        Assert.True(secondLastSeenUtc >= firstLastSeenUtc);
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
                instanceId = "missing-inst",
                instanceSessionToken = "missing-inst-token"
            })
        }, CancellationToken.None);

        Assert.NotNull(response.Error);
        Assert.Equal(-32010, response.Error.Code);
        Assert.Equal("instance_not_found", response.Error.Message);

        var errorData = JsonSerializer.SerializeToElement(response.Error.Data);
        Assert.Equal("unknown_instance", errorData.GetProperty("reason").GetString());
        Assert.Equal("missing-inst", errorData.GetProperty("instanceId").GetString());
    }

    private sealed class MutableClock : IClock
    {
        public MutableClock(DateTime utcNow)
        {
            UtcNow = utcNow;
        }

        public DateTime UtcNow { get; private set; }

        public void Advance(TimeSpan duration)
        {
            UtcNow = UtcNow.Add(duration);
        }
    }
}


