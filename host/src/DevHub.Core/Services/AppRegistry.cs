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
    private readonly ConcurrentDictionary<string, SecretState> _passwordStates = new();
    private readonly ConcurrentDictionary<string, SecretState> _sessionStates = new();
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
                    _sessionStates.TryRemove(instanceId, out _);
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
    /// 注册或更新应用程序实例（仅供白盒测试构造运行态）。
    /// </summary>
    internal AppInstance RegisterInstance(AppInstance instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ValidateInstance(instance);
        _logger.LogDebug("尝试注册/更新应用程序实例，InstanceId: {InstanceId}, AppId: {AppId}, Scope: {Scope}, PID: {PID}, 详细信息: {InstanceDetails}",
            instance.InstanceId, instance.AppId, instance.Scope, instance.Pid, JsonSerializer.Serialize(instance));

        lock (_syncRoot)
        {
            var storedInstance = RegisterOrUpdateInstance(instance);
            RotateSessionTokenState(instance.InstanceId);
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
    public bool TryRegisterInstance(
        AppInstance instance,
        string password,
        out AppInstance registeredInstance,
        out string instanceSessionToken,
        out bool passwordMismatch)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentException.ThrowIfNullOrWhiteSpace(password);
        ValidateInstance(instance);
        instanceSessionToken = string.Empty;

        lock (_syncRoot)
        {
            if (_instances.TryGetValue(instance.InstanceId, out var existing))
            {
                if (_passwordStates.TryGetValue(instance.InstanceId, out var passwordState))
                {
                    if (!MatchesSecret(passwordState, password))
                    {
                        registeredInstance = CloneInstance(existing);
                        passwordMismatch = true;
                        return false;
                    }

                    var updatedInstance = RegisterOrUpdateInstance(instance);
                    instanceSessionToken = RotateSessionTokenState(instance.InstanceId);
                    registeredInstance = CloneInstance(updatedInstance);
                    passwordMismatch = false;
                    return true;
                }

                if (_instances.TryRemove(instance.InstanceId, out var removedLegacyInstance))
                {
                    _sessionStates.TryRemove(instance.InstanceId, out _);
                    _logger.LogInformation(
                        "已丢弃无密码状态的应用程序实例: {InstanceId} (AppId: {AppId}, Scope: {Scope}, PID: {PID})",
                        instance.InstanceId,
                        removedLegacyInstance.AppId,
                        removedLegacyInstance.Scope,
                        removedLegacyInstance.Pid);
                }
            }

            _passwordStates[instance.InstanceId] = CreateSecretState(password);

            var storedInstance = RegisterOrUpdateInstance(instance);
            instanceSessionToken = RotateSessionTokenState(instance.InstanceId);
            registeredInstance = CloneInstance(storedInstance);
            passwordMismatch = false;
            return true;
        }
    }

    /// <summary>
    /// 更新实例的最后更新时间（仅供白盒测试构造运行态）。
    /// </summary>
    internal bool Heartbeat(string instanceId, out DateTime lastSeenUtc)
    {
        ProtocolIdentifier.EnsureInstanceId(instanceId, nameof(instanceId));
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
    /// 注销应用程序实例（仅供白盒测试构造运行态）。
    /// </summary>
    internal bool UnregisterInstance(string instanceId)
    {
        ProtocolIdentifier.EnsureInstanceId(instanceId, nameof(instanceId));
        _logger.LogDebug("尝试注销应用程序实例: {InstanceId}", instanceId);

        lock (_syncRoot)
        {
            if (_instances.TryRemove(instanceId, out var removedInstance))
            {
                _passwordStates.TryRemove(instanceId, out _);
                _sessionStates.TryRemove(instanceId, out _);
                _logger.LogInformation("已成功注销应用程序实例: {InstanceId} (AppId: {AppId}, Scope: {Scope}, PID: {PID})",
                    instanceId, removedInstance.AppId, removedInstance.Scope, removedInstance.Pid);
                return true;
            }
        }

        _logger.LogWarning("注销失败: 未找到实例 {InstanceId}", instanceId);
        return false;
    }

    /// <summary>
    /// 使用实例会话凭据刷新实例在线时间。
    /// </summary>
    /// <param name="instanceId">实例标识。</param>
    /// <param name="instanceSessionToken">实例当前持有的会话凭据。</param>
    /// <param name="lastSeenUtc">成功时返回最新在线时间。</param>
    /// <param name="validationStatus">所有权校验结果。</param>
    /// <returns>校验通过并完成刷新时返回 <c>true</c>。</returns>
    public bool TryHeartbeat(
        string instanceId,
        string instanceSessionToken,
        out DateTime lastSeenUtc,
        out InstanceSessionValidationStatus validationStatus)
    {
        ProtocolIdentifier.EnsureInstanceId(instanceId, nameof(instanceId));
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceSessionToken);

        lock (_syncRoot)
        {
            validationStatus = ValidateInstanceSessionTokenUnsafe(instanceId, instanceSessionToken, out var existing);
            if (validationStatus != InstanceSessionValidationStatus.Matched)
            {
                lastSeenUtc = DateTime.MinValue;
                return false;
            }

            var now = _clock.UtcNow;
            existing!.LastSeenUtc = now;
            lastSeenUtc = now;
            _logger.LogDebug("成功更新实例心跳: {InstanceId}", instanceId);
            return true;
        }
    }

    /// <summary>
    /// 使用实例会话凭据读取实例快照。
    /// </summary>
    /// <param name="instanceId">实例标识。</param>
    /// <param name="instanceSessionToken">实例当前持有的会话凭据。</param>
    /// <param name="instance">成功时返回实例快照。</param>
    /// <param name="validationStatus">所有权校验结果。</param>
    /// <returns>校验通过并获取实例快照时返回 <c>true</c>。</returns>
    public bool TryGetOwnedInstance(
        string instanceId,
        string instanceSessionToken,
        out AppInstance? instance,
        out InstanceSessionValidationStatus validationStatus)
    {
        ProtocolIdentifier.EnsureInstanceId(instanceId, nameof(instanceId));
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceSessionToken);

        lock (_syncRoot)
        {
            validationStatus = ValidateInstanceSessionTokenUnsafe(instanceId, instanceSessionToken, out var existing);
            if (validationStatus != InstanceSessionValidationStatus.Matched)
            {
                instance = null;
                return false;
            }

            instance = CloneInstance(existing!);
            return true;
        }
    }

    /// <summary>
    /// 使用实例会话凭据刷新在线时间并返回实例快照。
    /// </summary>
    /// <param name="instanceId">实例标识。</param>
    /// <param name="instanceSessionToken">实例当前持有的会话凭据。</param>
    /// <param name="instance">成功时返回刷新后的实例快照。</param>
    /// <param name="validationStatus">所有权校验结果。</param>
    /// <returns>校验通过并完成刷新时返回 <c>true</c>。</returns>
    public bool TryTouchOwnedInstance(
        string instanceId,
        string instanceSessionToken,
        out AppInstance? instance,
        out InstanceSessionValidationStatus validationStatus)
    {
        ProtocolIdentifier.EnsureInstanceId(instanceId, nameof(instanceId));
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceSessionToken);

        lock (_syncRoot)
        {
            validationStatus = ValidateInstanceSessionTokenUnsafe(instanceId, instanceSessionToken, out var existing);
            if (validationStatus != InstanceSessionValidationStatus.Matched)
            {
                instance = null;
                return false;
            }

            existing!.LastSeenUtc = _clock.UtcNow;
            instance = CloneInstance(existing);
            return true;
        }
    }

    /// <summary>
    /// 使用实例会话凭据注销应用程序实例。
    /// </summary>
    /// <param name="instanceId">实例标识。</param>
    /// <param name="instanceSessionToken">实例当前持有的会话凭据。</param>
    /// <param name="removedInstance">成功移除时返回被删除的实例快照；目标不存在时返回 <c>null</c>。</param>
    /// <param name="validationStatus">所有权校验结果。</param>
    /// <returns>成功注销或目标不存在时返回 <c>true</c>。</returns>
    public bool TryUnregisterInstance(
        string instanceId,
        string instanceSessionToken,
        out AppInstance? removedInstance,
        out InstanceSessionValidationStatus validationStatus)
    {
        ProtocolIdentifier.EnsureInstanceId(instanceId, nameof(instanceId));
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceSessionToken);

        lock (_syncRoot)
        {
            validationStatus = ValidateInstanceSessionTokenUnsafe(instanceId, instanceSessionToken, out var existing);
            if (validationStatus == InstanceSessionValidationStatus.UnknownInstance)
            {
                removedInstance = null;
                return true;
            }

            if (validationStatus == InstanceSessionValidationStatus.TokenMismatch)
            {
                removedInstance = CloneInstance(existing!);
                return false;
            }

            _instances.TryRemove(instanceId, out var removed);
            _passwordStates.TryRemove(instanceId, out _);
            _sessionStates.TryRemove(instanceId, out _);
            removedInstance = removed is null ? null : CloneInstance(removed);
            validationStatus = InstanceSessionValidationStatus.Matched;
            return true;
        }
    }

    /// <summary>
    /// 列出应用程序实例
    /// </summary>
    public IEnumerable<AppInstance> ListInstances(string? appId = null, string? scope = null, bool includeOffline = false)
    {
        if (appId is not null && !ProtocolIdentifier.IsValidAppId(appId))
        {
            throw new ArgumentException($"appId must match {ProtocolIdentifier.CanonicalPattern}.", nameof(appId));
        }

        ScopeContract.EnsureListFilter(scope, nameof(scope));

        _logger.LogDebug("尝试列出应用程序实例，AppId: {AppId}, Scope: {Scope}, IncludeOffline: {IncludeOffline}",
            appId, scope, includeOffline);

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

            if (scope is not null)
            {
                instances = instances.Where(i => i.Scope == scope);
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
        ProtocolIdentifier.EnsureInstanceId(instanceId, nameof(instanceId));
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

    internal string? GetCurrentInstanceSessionToken(string instanceId)
    {
        ProtocolIdentifier.EnsureInstanceId(instanceId, nameof(instanceId));

        lock (_syncRoot)
        {
            if (_sessionStates.TryGetValue(instanceId, out var state))
            {
                return state.CurrentSecret;
            }
        }

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
            existing.Scope = instance.Scope;
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
            Scope = instance.Scope,
            Pid = instance.Pid,
            RegisteredAtUtc = registeredAtUtc,
            LastSeenUtc = now,
            Invoke = CloneInvoke(instance.Invoke ?? new InvokeCapability()),
            Meta = CloneMeta(instance.Meta)
        };
        _instances[instance.InstanceId] = instanceToRegister;
        return instanceToRegister;
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

    private static void ValidateInstance(AppInstance instance)
    {
        ProtocolIdentifier.EnsureInstanceId(instance.InstanceId, nameof(instance.InstanceId));
        ProtocolIdentifier.EnsureAppId(instance.AppId, nameof(instance.AppId));
        ScopeContract.EnsureScopedString(instance.Scope, nameof(instance.Scope));
        ArgumentOutOfRangeException.ThrowIfLessThan(instance.Pid, 1, nameof(instance.Pid));
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

    private InstanceSessionValidationStatus ValidateInstanceSessionTokenUnsafe(
        string instanceId,
        string instanceSessionToken,
        out AppInstance? instance)
    {
        if (!_instances.TryGetValue(instanceId, out instance))
        {
            return InstanceSessionValidationStatus.UnknownInstance;
        }

        return _sessionStates.TryGetValue(instanceId, out var sessionState) && MatchesSecret(sessionState, instanceSessionToken)
            ? InstanceSessionValidationStatus.Matched
            : InstanceSessionValidationStatus.TokenMismatch;
    }

    private string RotateSessionTokenState(string instanceId)
    {
        var token = GenerateSessionToken();
        _sessionStates[instanceId] = CreateSecretState(token);
        return token;
    }

    private static SecretState CreateSecretState(string secret)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var secretBytes = Encoding.UTF8.GetBytes(secret);
        var saltedBytes = new byte[salt.Length + secretBytes.Length];
        Buffer.BlockCopy(salt, 0, saltedBytes, 0, salt.Length);
        Buffer.BlockCopy(secretBytes, 0, saltedBytes, salt.Length, secretBytes.Length);

        return new SecretState
        {
            Salt = salt,
            Hash = SHA256.HashData(saltedBytes),
            CurrentSecret = secret
        };
    }

    private static bool MatchesSecret(SecretState state, string candidate)
    {
        var candidateBytes = Encoding.UTF8.GetBytes(candidate);
        var saltedBytes = new byte[state.Salt.Length + candidateBytes.Length];
        Buffer.BlockCopy(state.Salt, 0, saltedBytes, 0, state.Salt.Length);
        Buffer.BlockCopy(candidateBytes, 0, saltedBytes, state.Salt.Length, candidateBytes.Length);
        var candidateHash = SHA256.HashData(saltedBytes);
        return CryptographicOperations.FixedTimeEquals(state.Hash, candidateHash);
    }

    private static string GenerateSessionToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private sealed class SecretState
    {
        public required byte[] Salt { get; init; }

        public required byte[] Hash { get; init; }

        public required string CurrentSecret { get; init; }
    }
}

/// <summary>
/// 实例会话凭据校验结果。
/// </summary>
public enum InstanceSessionValidationStatus
{
    /// <summary>
    /// 凭据匹配。
    /// </summary>
    Matched,

    /// <summary>
    /// 实例不存在。
    /// </summary>
    UnknownInstance,

    /// <summary>
    /// 凭据不匹配。
    /// </summary>
    TokenMismatch
}
