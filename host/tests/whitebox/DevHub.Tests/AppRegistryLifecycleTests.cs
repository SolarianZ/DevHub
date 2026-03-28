namespace DevHub.Tests;

using DevHub.Core.Models;
using DevHub.Core.Services;
using DevHub.Core.Services.Abstractions;
using Microsoft.Extensions.Logging;
using Moq;

/// <summary>
/// AppRegistry 生命周期与清理行为测试。
/// </summary>
[Trait("Category", "Impl")]
public sealed class AppRegistryLifecycleTests
{
    [Fact]
    public void Impl_CleanupTimer_ShouldRemoveOnlyExpiredEntries()
    {
        var clock = new MutableClock(DateTime.UtcNow);
        using var registry = new AppRegistry(clock, Mock.Of<ILogger<AppRegistry>>());

        registry.RegisterInstance(CreateInstance("inst-expired", "app.cleanup", null, 101));
        clock.Advance(TimeSpan.FromHours(2));
        registry.RegisterInstance(CreateInstance("inst-active", "app.cleanup", "workspace-A", 102));

        registry.CleanupExpiredInstancesForTesting();

        Assert.Null(registry.GetInstance("inst-expired"));
        Assert.NotNull(registry.GetInstance("inst-active"));
    }

    [Fact]
    public void Impl_HeartbeatAndUnregister_WhenInstanceMissing_ShouldReturnFalse()
    {
        using var registry = new AppRegistry(new SystemClock(), Mock.Of<ILogger<AppRegistry>>());

        Assert.False(registry.Heartbeat("missing-instance", out var heartbeatAt));
        Assert.Equal(DateTime.MinValue, heartbeatAt);
        Assert.False(registry.UnregisterInstance("missing-instance"));
    }

    [Fact]
    public void Impl_RegisterInstance_WhenSameInstanceRegisteredTwice_ShouldUpdateExistingEntry()
    {
        var clock = new MutableClock(DateTime.UtcNow);
        using var registry = new AppRegistry(clock, Mock.Of<ILogger<AppRegistry>>());

        registry.RegisterInstance(CreateInstance("inst-update", "app.original", null, 101));
        clock.Advance(TimeSpan.FromSeconds(5));
        registry.RegisterInstance(CreateInstance("inst-update", "app.updated", "workspace-B", 202));

        var updated = registry.GetInstance("inst-update");
        Assert.NotNull(updated);
        Assert.Equal("app.updated", updated!.AppId);
        Assert.Equal("workspace-B", updated.Scope);
        Assert.Equal(202, updated.Pid);
    }

    [Fact]
    public void Impl_RegisterInstance_WhenSameInstanceRegisteredTwice_ShouldReturnStoredInstanceWithOriginalRegisteredAtUtc()
    {
        var startedAt = DateTime.UtcNow;
        var clock = new MutableClock(startedAt);
        using var registry = new AppRegistry(clock, Mock.Of<ILogger<AppRegistry>>());

        var first = registry.RegisterInstance(CreateInstance("inst-return", "app.first", null, 301));
        clock.Advance(TimeSpan.FromSeconds(10));

        var second = registry.RegisterInstance(CreateInstance("inst-return", "app.second", "workspace-C", 302));
        var stored = registry.GetInstance("inst-return");

        Assert.NotNull(stored);
        Assert.Equal(first.RegisteredAtUtc, second.RegisteredAtUtc);
        Assert.Equal(stored!.RegisteredAtUtc, second.RegisteredAtUtc);
        Assert.Equal(stored.LastSeenUtc, second.LastSeenUtc);
        Assert.Equal("app.second", second.AppId);
        Assert.Equal("workspace-C", second.Scope);
        Assert.Equal(302, second.Pid);
    }

    [Fact]
    public void Impl_Dispose_CalledMultipleTimes_ShouldBeIdempotent()
    {
        var registry = new AppRegistry(new SystemClock(), Mock.Of<ILogger<AppRegistry>>());

        var exception = Record.Exception(() =>
        {
            registry.Dispose();
            registry.Dispose();
        });

        Assert.Null(exception);
    }

    private static AppInstance CreateInstance(string instanceId, string appId, string? scope, int pid)
    {
        return new AppInstance
        {
            InstanceId = instanceId,
            AppId = appId,
            Scope = scope,
            Pid = pid,
            Invoke = new InvokeCapability
            {
                Poll = true,
                Respond = true
            }
        };
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


