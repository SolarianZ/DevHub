namespace DevHub.Tests;

using DevHub.Core.Models;
using DevHub.Core.Services;
using DevHub.Core.Services.Abstractions;
using Microsoft.Extensions.Logging;
using Moq;

/// <summary>
/// AppRegistry 生命周期与清理行为测试。
/// </summary>
public sealed class AppRegistryLifecycleTests
{
    [Fact]
    public async Task CleanupTimer_ShouldRemoveOnlyExpiredEntries()
    {
        var clock = new MutableClock(DateTime.UtcNow);
        using var registry = new AppRegistry(clock, Mock.Of<ILogger<AppRegistry>>());

        registry.RegisterInstance(CreateInstance("inst-expired", "app.cleanup", null, 101));
        registry.RegisterInstance(CreateInstance("inst-active", "app.cleanup", "workspace-A", 102));

        registry.GetInstance("inst-expired")!.LastSeenUtc = clock.UtcNow.AddHours(-2);
        registry.GetInstance("inst-active")!.LastSeenUtc = clock.UtcNow;

        await WaitUntilAsync(() => registry.GetInstance("inst-expired") is null, TimeSpan.FromSeconds(70));

        Assert.Null(registry.GetInstance("inst-expired"));
        Assert.NotNull(registry.GetInstance("inst-active"));
    }

    [Fact]
    public void HeartbeatAndUnregister_WhenInstanceMissing_ShouldReturnFalse()
    {
        using var registry = new AppRegistry(new SystemClock(), Mock.Of<ILogger<AppRegistry>>());

        Assert.False(registry.Heartbeat("missing-instance", out var heartbeatAt));
        Assert.Equal(DateTime.MinValue, heartbeatAt);
        Assert.False(registry.UnregisterInstance("missing-instance"));
    }

    [Fact]
    public void RegisterInstance_WhenSameInstanceRegisteredTwice_ShouldUpdateExistingEntry()
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
    public void Dispose_CalledMultipleTimes_ShouldBeIdempotent()
    {
        var registry = new AppRegistry(new SystemClock(), Mock.Of<ILogger<AppRegistry>>());

        registry.Dispose();
        registry.Dispose();

        Assert.True(true);
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

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow.Add(timeout);
        while (DateTime.UtcNow <= deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(100);
        }

        throw new Xunit.Sdk.XunitException("等待 AppRegistry 定时清理超时。");
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
