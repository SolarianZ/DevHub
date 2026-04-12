using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
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
    private readonly ConcurrentDictionary<string, InstancePasswordState> _passwordStates = new();
    private readonly TimeSpan _onlineThreshold;
    private readonly TimeSpan _cleanupThreshold = TimeSpan.FromHours(1);
    private readonly IClock _clock;
    private readonly ILogger<AppRegistry> _logger;
    private readonly object _syncRoot = new();
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
    }

    /// <summary>
    /// 清理过期实例（lastSeenUtc 超过 1 小时）
    /// </summary>
    public void CleanupExpiredInstances()
    {
        _logger.LogDebug("开始执行过期实例清理任务");

        lock (_syncRoot)
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
                    _passwordStates.TryRemove(instanceId, out _);
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
        ArgumentNullException.ThrowIfNull(instance);
        _logger.LogDebug("尝试注册/更新应用程序实例，InstanceId: {InstanceId}, AppId: {AppId}, Scope: {Scope}, PID: {PID}, 详细信息: {InstanceDetails}",
            instance.InstanceId, instance.AppId, instance.Scope, instance.Pid, JsonSerializer.Serialize(instance));

        lock (_syncRoot)
        {
            var storedInstance = RegisterOrUpdateInstance(instance);
            _logger.LogInformation("已注册应用程序实例: {InstanceId} (AppId: {AppId}, Scope: {Scope}, PID: {PID})",
                storedInstance.InstanceId, storedInstance.AppId, storedInstance.Scope, storedInstance.Pid);
            return CloneInstance(storedInstance);
        }
    }

    /// <summary>
    /// 使用实例密码注册或更新应用程序实例。
    /// </summary>
    /// <param name="instance">待注册的实例。</param>
    /// <param name="password">实例密码。</param>
    /// <param name="registeredInstance">成功时返回最新实例快照。</param>
    /// <param name="passwordMismatch">密码不匹配时返回 <c>true</c>。</param>
    /// <returns>成功注册或更新返回 <c>true</c>。</returns>
    public bool TryRegisterInstance(AppInstance instance, string password, out AppInstance registeredInstance, out bool passwordMismatch)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentException.ThrowIfNullOrWhiteSpace(password);

        lock (_syncRoot)
        {
            if (_instances.TryGetValue(instance.InstanceId, out var existing))
            {
                if (_passwordStates.TryGetValue(instance.InstanceId, out var passwordState) && !MatchesPassword(passwordState, password))
                {
                    registeredInstance = CloneInstance(existing);
                    passwordMismatch = true;
                    return false;
                }

                if (!_passwordStates.ContainsKey(instance.InstanceId))
                {
                    _passwordStates[instance.InstanceId] = CreatePasswordState(password);
                }
            }
            else
            {
                _passwordStates[instance.InstanceId] = CreatePasswordState(password);
            }

            var storedInstance = RegisterOrUpdateInstance(instance);
            registeredInstance = CloneInstance(storedInstance);
            passwordMismatch = false;
            return true;
        }
    }

    /// <summary>
    /// 更新实例的最后更新时间（心跳）
    /// </summary>
    public bool Heartbeat(string instanceId, out DateTime lastSeenUtc)
    {
        _logger.LogDebug("尝试更新实例心跳: {InstanceId}", instanceId);

        lock (_syncRoot)
        {
            if (_instances.TryGetValue(instanceId, out var instance))
            {
                var now = _clock.UtcNow;
                instance.LastSeenUtc = now;
                lastSeenUtc = now;
                _logger.LogDebug("成功更新实例心跳: {InstanceId}", instanceId);
                return true;
            }
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

        lock (_syncRoot)
        {
            if (_instances.TryRemove(instanceId, out var removedInstance))
            {
                _passwordStates.TryRemove(instanceId, out _);
                _logger.LogInformation("已成功注销应用程序实例: {InstanceId} (AppId: {AppId}, Scope: {Scope}, PID: {PID})",
                    instanceId, removedInstance.AppId, removedInstance.Scope, removedInstance.Pid);
                return true;
            }
        }

        _logger.LogWarning("注销失败: 未找到实例 {InstanceId}", instanceId);
        return false;
    }

    /// <summary>
    /// 使用实例密码注销应用程序实例。
    /// </summary>
    /// <param name="instanceId">实例标识。</param>
    /// <param name="password">实例密码。</param>
    /// <param name="removedInstance">成功移除时返回被删除的实例快照；目标不存在时返回 <c>null</c>。</param>
    /// <param name="passwordMismatch">密码不匹配时返回 <c>true</c>。</param>
    /// <returns>成功注销或目标不存在时返回 <c>true</c>。</returns>
    public bool TryUnregisterInstance(string instanceId, string password, out AppInstance? removedInstance, out bool passwordMismatch)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(password);

        lock (_syncRoot)
        {
            if (!_instances.TryGetValue(instanceId, out var existing))
            {
                removedInstance = null;
                passwordMismatch = false;
                return true;
            }

            if (_passwordStates.TryGetValue(instanceId, out var passwordState) && !MatchesPassword(passwordState, password))
            {
                removedInstance = CloneInstance(existing);
                passwordMismatch = true;
                return false;
            }

            _instances.TryRemove(instanceId, out var removed);
            _passwordStates.TryRemove(instanceId, out _);
            removedInstance = removed is null ? null : CloneInstance(removed);
            passwordMismatch = false;
            return true;
        }
    }

    /// <summary>
    /// 列出应用程序实例
    /// </summary>
    public IEnumerable<AppInstance> ListInstances(string? appId = null, string? scope = null, bool includeAllScopes = false, bool includeOffline = false)
    {
        _logger.LogDebug("尝试列出应用程序实例，AppId: {AppId}, Scope: {Scope}, IncludeAllScopes: {IncludeAllScopes}, IncludeOffline: {IncludeOffline}",
            appId, scope, includeAllScopes, includeOffline);

        lock (_syncRoot)
        {
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

            var result = instances
                .Select(CloneInstance)
                .ToList();
            _logger.LogDebug("成功列出 {Count} 个应用程序实例", result.Count);
            return result;
        }
    }

    /// <summary>
    /// 获取应用程序实例
    /// </summary>
    public AppInstance? GetInstance(string instanceId)
    {
        _logger.LogDebug("尝试获取应用程序实例: {InstanceId}", instanceId);

        lock (_syncRoot)
        {
            if (_instances.TryGetValue(instanceId, out var instance))
            {
                _logger.LogDebug("成功获取应用程序实例: {InstanceId} (AppId: {AppId}, Scope: {Scope}, PID: {PID})",
                    instanceId, instance.AppId, instance.Scope, instance.Pid);
                return CloneInstance(instance);
            }
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

    private AppInstance RegisterOrUpdateInstance(AppInstance instance)
    {
        var now = _clock.UtcNow;
        if (_instances.TryGetValue(instance.InstanceId, out var existing))
        {
            existing.AppId = instance.AppId;
            existing.Scope = NormalizeScope(instance.Scope);
            existing.Pid = instance.Pid;
            existing.LastSeenUtc = now;
            existing.Invoke = CloneInvoke(instance.Invoke ?? new InvokeCapability());
            existing.Meta = CloneMeta(instance.Meta);

            _logger.LogInformation("已更新应用程序实例: {InstanceId} (AppId: {AppId}, Scope: {Scope}, PID: {PID})",
                instance.InstanceId, instance.AppId, existing.Scope, instance.Pid);
            return existing;
        }

        var registeredAtUtc = instance.RegisteredAtUtc == default ? now : instance.RegisteredAtUtc;
        var instanceToRegister = new AppInstance
        {
            InstanceId = instance.InstanceId,
            AppId = instance.AppId,
            Scope = NormalizeScope(instance.Scope),
            Pid = instance.Pid,
            RegisteredAtUtc = registeredAtUtc,
            LastSeenUtc = now,
            Invoke = CloneInvoke(instance.Invoke ?? new InvokeCapability()),
            Meta = CloneMeta(instance.Meta)
        };
        _instances[instance.InstanceId] = instanceToRegister;
        return instanceToRegister;
    }

    private static string? NormalizeScope(string? scope)
    {
        return scope == string.Empty ? null : scope;
    }

    private static AppInstance CloneInstance(AppInstance instance)
    {
        return new AppInstance
        {
            InstanceId = instance.InstanceId,
            AppId = instance.AppId,
            Scope = instance.Scope,
            Pid = instance.Pid,
            RegisteredAtUtc = instance.RegisteredAtUtc,
            LastSeenUtc = instance.LastSeenUtc,
            Invoke = CloneInvoke(instance.Invoke),
            Meta = CloneMeta(instance.Meta)
        };
    }

    private static InvokeCapability CloneInvoke(InvokeCapability invoke)
    {
        return new InvokeCapability
        {
            Poll = invoke.Poll,
            Respond = invoke.Respond
        };
    }

    private static Dictionary<string, object?>? CloneMeta(Dictionary<string, object?>? meta)
    {
        if (meta is null)
        {
            return null;
        }

        return JsonSerializer.Deserialize<Dictionary<string, object?>>(
            JsonSerializer.Serialize(meta));
    }

    private static InstancePasswordState CreatePasswordState(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var passwordBytes = Encoding.UTF8.GetBytes(password);
        var saltedBytes = new byte[salt.Length + passwordBytes.Length];
        Buffer.BlockCopy(salt, 0, saltedBytes, 0, salt.Length);
        Buffer.BlockCopy(passwordBytes, 0, saltedBytes, salt.Length, passwordBytes.Length);

        return new InstancePasswordState
        {
            Salt = salt,
            Hash = SHA256.HashData(saltedBytes)
        };
    }

    private static bool MatchesPassword(InstancePasswordState state, string password)
    {
        var passwordBytes = Encoding.UTF8.GetBytes(password);
        var saltedBytes = new byte[state.Salt.Length + passwordBytes.Length];
        Buffer.BlockCopy(state.Salt, 0, saltedBytes, 0, state.Salt.Length);
        Buffer.BlockCopy(passwordBytes, 0, saltedBytes, state.Salt.Length, passwordBytes.Length);
        var candidateHash = SHA256.HashData(saltedBytes);
        return CryptographicOperations.FixedTimeEquals(state.Hash, candidateHash);
    }

    private sealed class InstancePasswordState
    {
        public required byte[] Salt { get; init; }

        public required byte[] Hash { get; init; }
    }
}
