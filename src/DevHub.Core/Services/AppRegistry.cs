using System.Collections.Concurrent;
using DevHub.Core.Models;
using Microsoft.Extensions.Logging;

namespace DevHub.Core.Services;

/// <summary>
/// 应用程序实例注册表
/// </summary>
public class AppRegistry : IDisposable
{
    private readonly ConcurrentDictionary<string, AppInstance> _instances = new();
    private readonly TimeSpan _onlineThreshold = TimeSpan.FromSeconds(30);
    private readonly TimeSpan _cleanupThreshold = TimeSpan.FromHours(1);
    private readonly ILogger<AppRegistry> _logger;
    private readonly Timer _cleanupTimer;
    private bool _disposed = false;

    public AppRegistry(ILogger<AppRegistry> logger)
    {
        _logger = logger;
        // 每60秒执行一次清理
        _cleanupTimer = new Timer(OnCleanupTimer, null, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60));
    }

    /// <summary>
    /// 清理过期实例的定时器回调
    /// </summary>
    /// <param name="state">状态参数</param>
    private void OnCleanupTimer(object? state)
    {
        CleanupExpiredInstances();
    }

    /// <summary>
    /// 清理过期实例（lastSeenUtc 超过 1 小时）
    /// </summary>
    private void CleanupExpiredInstances()
    {
        var now = DateTime.UtcNow;
        var expiredInstanceIds = _instances.Values
            .Where(i => now - i.LastSeenUtc > _cleanupThreshold)
            .Select(i => i.InstanceId)
            .ToList();

        foreach (var instanceId in expiredInstanceIds)
        {
            if (_instances.TryRemove(instanceId, out var removedInstance))
            {
                _logger.LogInformation("已清理过期应用程序实例: {InstanceId} (AppId: {AppId})",
                    instanceId, removedInstance.AppId);
            }
        }

        if (expiredInstanceIds.Count > 0)
        {
            _logger.LogInformation("清理完成，共移除 {Count} 个过期实例", expiredInstanceIds.Count);
        }
    }

    /// <summary>
    /// 注册或更新应用程序实例
    /// </summary>
    public AppInstance RegisterInstance(AppInstance instance)
    {
        var now = DateTime.UtcNow;

        var instanceToRegister = new AppInstance
        {
            InstanceId = instance.InstanceId,
            AppId = instance.AppId,
            Scope = instance.Scope,
            Pid = instance.Pid,
            RegisteredAtUtc = instance.RegisteredAtUtc ?? now,
            LastSeenUtc = now,
            Endpoints = instance.Endpoints,
            Meta = instance.Meta
        };

        _instances.AddOrUpdate(instance.InstanceId, instanceToRegister, (_, existing) =>
        {
            // 更新现有实例
            existing.AppId = instance.AppId;
            existing.Scope = instance.Scope;
            existing.Pid = instance.Pid;
            existing.LastSeenUtc = now;
            existing.Endpoints = instance.Endpoints;
            existing.Meta = instance.Meta;

            _logger.LogInformation("已更新应用程序实例: {InstanceId} (AppId: {AppId})", instance.InstanceId, instance.AppId);
            return existing;
        });

        _logger.LogInformation("已注册应用程序实例: {InstanceId} (AppId: {AppId})", instance.InstanceId, instance.AppId);
        return instanceToRegister;
    }

    /// <summary>
    /// 更新实例的最后更新时间（心跳）
    /// </summary>
    public bool Heartbeat(string instanceId)
    {
        if (_instances.TryGetValue(instanceId, out var instance))
        {
            instance.LastSeenUtc = DateTime.UtcNow;
            _logger.LogDebug("心跳更新: {InstanceId}", instanceId);
            return true;
        }

        _logger.LogWarning("心跳更新失败: 未找到实例 {InstanceId}", instanceId);
        return false;
    }

    /// <summary>
    /// 注销应用程序实例
    /// </summary>
    public bool UnregisterInstance(string instanceId)
    {
        if (_instances.TryRemove(instanceId, out var removedInstance))
        {
            _logger.LogInformation("已注销应用程序实例: {InstanceId} (AppId: {AppId})", instanceId, removedInstance.AppId);
            return true;
        }

        _logger.LogWarning("注销失败: 未找到实例 {InstanceId}", instanceId);
        return false;
    }

    /// <summary>
    /// 列出应用程序实例
    /// </summary>
    public IEnumerable<AppInstance> ListInstances(string? appId = null, string? scope = null, bool includeAllScopes = false)
    {
        var now = DateTime.UtcNow;
        var instances = _instances.Values
            .Where(i => now - i.LastSeenUtc <= _onlineThreshold);

        if (!string.IsNullOrEmpty(appId))
        {
            instances = instances.Where(i => i.AppId == appId);
        }

        if (!includeAllScopes)
        {
            if (scope == null)
            {
                instances = instances.Where(i => i.Scope == null);
            }
            else
            {
                instances = instances.Where(i => i.Scope == scope);
            }
        }

        return instances.ToList();
    }

    /// <summary>
    /// 获取应用程序实例
    /// </summary>
    public AppInstance? GetInstance(string instanceId)
    {
        return _instances.TryGetValue(instanceId, out var instance) ? instance : null;
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
            _cleanupTimer.Dispose();
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
