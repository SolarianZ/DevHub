using System.Collections.Concurrent;
using DevHub.Core.Models;
using DevHub.Core.Services.Abstractions;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace DevHub.Core.Services;

/// <summary>
/// 应用程序实例注册表
/// </summary>
public class AppRegistry : IDisposable
{
    private readonly ConcurrentDictionary<string, AppInstance> _instances = new();
    private readonly TimeSpan _onlineThreshold;
    private readonly TimeSpan _cleanupThreshold = TimeSpan.FromHours(1);
    private readonly IClock _clock;
    private readonly ILogger<AppRegistry> _logger;
    private readonly Timer _cleanupTimer;
    private bool _disposed = false;

    /// <summary>
    /// 初始化应用程序实例注册表。
    /// </summary>
    /// <param name="clock">系统时钟。</param>
    /// <param name="logger">日志记录器。</param>
    public AppRegistry(IClock clock, ILogger<AppRegistry> logger)
        : this(clock, logger, RuntimeTuningOptions.Default)
    {
    }

    /// <summary>
    /// 初始化应用程序实例注册表。
    /// </summary>
    /// <param name="clock">系统时钟。</param>
    /// <param name="logger">日志记录器。</param>
    /// <param name="runtimeTuningOptions">运行时调优参数。</param>
    public AppRegistry(IClock clock, ILogger<AppRegistry> logger, RuntimeTuningOptions runtimeTuningOptions)
    {
        _clock = clock;
        _logger = logger;
        _onlineThreshold = TimeSpan.FromSeconds(runtimeTuningOptions.OnlineThresholdSeconds);
        // 每60秒执行一次清理
        _cleanupTimer = new Timer(OnCleanupTimer, null, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60));
    }

    /// <summary>
    /// 清理过期实例的定时器回调
    /// </summary>
    /// <param name="state">状态参数</param>
    private void OnCleanupTimer(object? state)
    {
        _logger.LogDebug("开始执行过期实例清理任务");
        CleanupExpiredInstances();
    }

    /// <summary>
    /// 清理过期实例（lastSeenUtc 超过 1 小时）
    /// </summary>
    private void CleanupExpiredInstances()
    {
        var now = _clock.UtcNow;
        _logger.LogDebug("当前实例数量: {Count}", _instances.Count);

        var expiredInstanceIds = _instances.Values
            .Where(i => now - i.LastSeenUtc > _cleanupThreshold)
            .Select(i => i.InstanceId)
            .ToList();

        _logger.LogDebug("发现 {Count} 个过期实例需要清理", expiredInstanceIds.Count);

        foreach (var instanceId in expiredInstanceIds)
        {
            if (_instances.TryRemove(instanceId, out var removedInstance))
            {
                _logger.LogInformation("已清理过期应用程序实例: {InstanceId} (AppId: {AppId}, Scope: {Scope}, PID: {PID}, LastSeen: {LastSeen})",
                    instanceId, removedInstance.AppId, removedInstance.Scope, removedInstance.Pid, removedInstance.LastSeenUtc);
            }
        }

        if (expiredInstanceIds.Count > 0)
        {
            _logger.LogInformation("清理完成，共移除 {Count} 个过期实例", expiredInstanceIds.Count);
        }
        else
        {
            _logger.LogDebug("没有发现过期实例需要清理");
        }
    }

    internal void CleanupExpiredInstancesForTesting()
    {
        CleanupExpiredInstances();
    }

    /// <summary>
    /// 注册或更新应用程序实例
    /// </summary>
    public AppInstance RegisterInstance(AppInstance instance)
    {
        _logger.LogDebug("尝试注册/更新应用程序实例，InstanceId: {InstanceId}, AppId: {AppId}, Scope: {Scope}, PID: {PID}, 详细信息: {InstanceDetails}",
            instance.InstanceId, instance.AppId, instance.Scope, instance.Pid, JsonSerializer.Serialize(instance));

        var now = _clock.UtcNow;
        var registeredAtUtc = instance.RegisteredAtUtc == default ? now : instance.RegisteredAtUtc;
        var invoke = instance.Invoke ?? new InvokeCapability();

        var instanceToRegister = new AppInstance
        {
            InstanceId = instance.InstanceId,
            AppId = instance.AppId,
            Scope = instance.Scope,
            Pid = instance.Pid,
            RegisteredAtUtc = registeredAtUtc,
            LastSeenUtc = now,
            Invoke = invoke,
            Endpoints = instance.Endpoints,
            Meta = instance.Meta
        };

        var registeredInstance = _instances.AddOrUpdate(instance.InstanceId, instanceToRegister, (_, existing) =>
        {
            // 更新现有实例
            existing.AppId = instance.AppId;
            existing.Scope = instance.Scope;
            existing.Pid = instance.Pid;
            existing.LastSeenUtc = now;
            existing.Invoke = invoke;
            existing.Endpoints = instance.Endpoints;
            existing.Meta = instance.Meta;

            _logger.LogInformation("已更新应用程序实例: {InstanceId} (AppId: {AppId}, Scope: {Scope}, PID: {PID})",
                instance.InstanceId, instance.AppId, instance.Scope, instance.Pid);
            return existing;
        });

        _logger.LogInformation("已注册应用程序实例: {InstanceId} (AppId: {AppId}, Scope: {Scope}, PID: {PID})",
            instance.InstanceId, instance.AppId, instance.Scope, instance.Pid);
        return registeredInstance;
    }

    /// <summary>
    /// 更新实例的最后更新时间（心跳）
    /// </summary>
    public bool Heartbeat(string instanceId, out DateTime lastSeenUtc)
    {
        _logger.LogDebug("尝试更新实例心跳: {InstanceId}", instanceId);

        if (_instances.TryGetValue(instanceId, out var instance))
        {
            var now = _clock.UtcNow;
            instance.LastSeenUtc = now;
            lastSeenUtc = now;
            _logger.LogDebug("成功更新实例心跳: {InstanceId}", instanceId);
            return true;
        }

        lastSeenUtc = DateTime.MinValue;
        _logger.LogWarning("心跳更新失败: 未找到实例 {InstanceId}", instanceId);
        return false;
    }

    /// <summary>
    /// 注销应用程序实例
    /// </summary>
    public bool UnregisterInstance(string instanceId)
    {
        _logger.LogDebug("尝试注销应用程序实例: {InstanceId}", instanceId);

        if (_instances.TryRemove(instanceId, out var removedInstance))
        {
            _logger.LogInformation("已成功注销应用程序实例: {InstanceId} (AppId: {AppId}, Scope: {Scope}, PID: {PID})",
                instanceId, removedInstance.AppId, removedInstance.Scope, removedInstance.Pid);
            return true;
        }

        _logger.LogWarning("注销失败: 未找到实例 {InstanceId}", instanceId);
        return false;
    }

    /// <summary>
    /// 列出应用程序实例
    /// </summary>
    public IEnumerable<AppInstance> ListInstances(string? appId = null, string? scope = null, bool includeAllScopes = false, bool includeOffline = false)
    {
        _logger.LogDebug("尝试列出应用程序实例，AppId: {AppId}, Scope: {Scope}, IncludeAllScopes: {IncludeAllScopes}, IncludeOffline: {IncludeOffline}",
            appId, scope, includeAllScopes, includeOffline);

        var now = _clock.UtcNow;
        var instances = _instances.Values.AsEnumerable();

        if (!includeOffline)
        {
            instances = instances.Where(i => now - i.LastSeenUtc <= _onlineThreshold);
        }

        if (!string.IsNullOrEmpty(appId))
        {
            instances = instances.Where(i => i.AppId == appId);
        }

        if (!includeAllScopes)
        {
            if (scope == null)
            {
                // 兼容历史数据：空字符串也视为 Global
                instances = instances.Where(i => i.Scope is null or "");
            }
            else
            {
                instances = instances.Where(i => i.Scope == scope);
            }
        }

        var result = instances.ToList();
        _logger.LogDebug("成功列出 {Count} 个应用程序实例", result.Count);
        return result;
    }

    /// <summary>
    /// 获取应用程序实例
    /// </summary>
    public AppInstance? GetInstance(string instanceId)
    {
        _logger.LogDebug("尝试获取应用程序实例: {InstanceId}", instanceId);

        if (_instances.TryGetValue(instanceId, out var instance))
        {
            _logger.LogDebug("成功获取应用程序实例: {InstanceId} (AppId: {AppId}, Scope: {Scope}, PID: {PID})",
                instanceId, instance.AppId, instance.Scope, instance.Pid);
            return instance;
        }

        _logger.LogDebug("未找到应用程序实例: {InstanceId}", instanceId);
        return null;
    }

    /// <summary>
    /// 释放资源
    /// </summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// 释放资源（内部实现）
    /// </summary>
    /// <param name="disposing">是否正在释放托管资源</param>
    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
            return;

        if (disposing)
        {
            _logger.LogDebug("开始释放 AppRegistry 资源");
            _cleanupTimer.Dispose();
            _logger.LogInformation("AppRegistry 资源释放完成");
        }

        _disposed = true;
    }

    /// <summary>
    /// 析构函数
    /// </summary>
    ~AppRegistry()
    {
        Dispose(false);
    }
}
