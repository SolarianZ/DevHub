using DevHub.Core.Models;
using DevHub.Core.Services;
using DevHub.Core.Services.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace DevHub.Core.Services.Invocation;

/// <summary>
/// 启动协调器，负责 launch 配置校验与进程拉起。
/// </summary>
public class LaunchCoordinator
{
    private const string DefaultDedupeKeyTemplate = "{appId}:{scopeOrGlobal}";

    private readonly object _dedupeSyncRoot = new();
    private readonly Dictionary<string, DedupeLaunchRecord> _dedupeRecords = new();
    private readonly IDefinitionProvider _definitionProvider;
    private readonly AppRegistry _appRegistry;
    private readonly IRuntimeHttpBaseUrlProvider _runtimeHttpBaseUrlProvider;
    private readonly IProcessLauncher _processLauncher;
    private readonly RuntimeTuningOptions _runtimeTuningOptions;
    private readonly IClock _clock;
    private readonly ILogger<LaunchCoordinator> _logger;

    /// <summary>
    /// 初始化启动协调器。
    /// </summary>
    public LaunchCoordinator(
        IDefinitionProvider definitionProvider,
        AppRegistry appRegistry,
        IRuntimeHttpBaseUrlProvider runtimeHttpBaseUrlProvider,
        IProcessLauncher processLauncher,
        IClock clock,
        ILogger<LaunchCoordinator> logger)
        : this(
            definitionProvider,
            appRegistry,
            runtimeHttpBaseUrlProvider,
            processLauncher,
            clock,
            RuntimeTuningOptions.Default,
            logger)
    {
    }

    /// <summary>
    /// 初始化启动协调器。
    /// </summary>
    [ActivatorUtilitiesConstructor]
    public LaunchCoordinator(
        IDefinitionProvider definitionProvider,
        AppRegistry appRegistry,
        IRuntimeHttpBaseUrlProvider runtimeHttpBaseUrlProvider,
        IProcessLauncher processLauncher,
        IClock clock,
        RuntimeTuningOptions runtimeTuningOptions,
        ILogger<LaunchCoordinator> logger)
    {
        _definitionProvider = definitionProvider;
        _appRegistry = appRegistry;
        _runtimeHttpBaseUrlProvider = runtimeHttpBaseUrlProvider;
        _processLauncher = processLauncher;
        _clock = clock;
        _runtimeTuningOptions = runtimeTuningOptions;
        _logger = logger;
    }

    /// <summary>
    /// 执行一次应用启动。
    /// </summary>
    public async Task<LaunchOperationResult> LaunchAsync(
        string appId,
        string? scope,
        string? dedupeKey,
        int waitForRegisterMs,
        CancellationToken cancellationToken)
    {
        _definitionProvider.Refresh();
        var definition = _definitionProvider.GetDefinition(appId);
        if (definition is null)
        {
            return LaunchOperationResult.CreateError(
                -32014,
                "app_definition_not_found",
                new { appId });
        }

        var onlineInstances = _appRegistry
            .ListInstances(appId, scope, includeAllScopes: false, includeOffline: false)
            .ToList();

        if (onlineInstances.Count > 0)
        {
            return LaunchOperationResult.CreateSuccess(
                status: "already_running",
                launchId: BuildLaunchId(),
                pid: onlineInstances[0].Pid);
        }

        if (!TryValidateLaunchConfig(definition, out var launchConfig, out var configError))
        {
            return configError;
        }

        var httpBaseUrl = _runtimeHttpBaseUrlProvider.GetHttpBaseUrl();
        var resolvedDedupeKey = ResolveDedupeKey(definition, appId, scope, dedupeKey, httpBaseUrl);

        var now = _clock.UtcNow;
        DedupeLaunchRecord? existingRecord;
        lock (_dedupeSyncRoot)
        {
            existingRecord = TryGetActiveDedupeRecord(resolvedDedupeKey, now);
        }

        if (existingRecord is not null)
        {
            return LaunchOperationResult.CreateSuccess(
                status: "already_running",
                launchId: existingRecord.LaunchId,
                pid: existingRecord.Pid);
        }

        var launchId = BuildLaunchId();
        lock (_dedupeSyncRoot)
        {
            var nextNow = _clock.UtcNow;
            if (TryGetActiveDedupeRecord(resolvedDedupeKey, nextNow) is { } record)
            {
                return LaunchOperationResult.CreateSuccess(
                    status: "already_running",
                    launchId: record.LaunchId,
                    pid: record.Pid);
            }

            _dedupeRecords[resolvedDedupeKey] = new DedupeLaunchRecord
            {
                LaunchId = launchId,
                AppId = appId,
                Scope = scope,
                CreatedAtUtc = nextNow,
                Pid = null
            };
        }

        System.Diagnostics.Process? process;
        try
        {
            process = StartProcess(launchConfig!, appId, scope, httpBaseUrl);
            if (process is null)
            {
                RemoveDedupeRecord(resolvedDedupeKey, launchId);
                return LaunchOperationResult.CreateError(
                    -32020,
                    "launch_failed",
                    new
                    {
                        reason = "process_start_failed"
                    });
            }
        }
        catch (Exception ex)
        {
            RemoveDedupeRecord(resolvedDedupeKey, launchId);
            _logger.LogError(ex, "启动进程失败，AppId: {AppId}, Scope: {Scope}", appId, scope);
            return LaunchOperationResult.CreateError(
                -32020,
                "launch_failed",
                new
                {
                    reason = "process_start_failed",
                    stderr = ex.Message
                });
        }

        UpdateDedupeRecordPid(resolvedDedupeKey, launchId, process.Id);
        if (waitForRegisterMs <= 0)
        {
            return LaunchOperationResult.CreateSuccess("started", launchId, process.Id);
        }

        var isRegistered = await WaitForRegistrationAsync(appId, scope, waitForRegisterMs, cancellationToken);
        if (isRegistered)
        {
            RemoveDedupeRecord(resolvedDedupeKey, launchId);
        }

        var status = isRegistered ? "started" : "starting";
        return LaunchOperationResult.CreateSuccess(status, launchId, process.Id);
    }

    private static string BuildLaunchId() => $"launch-{Guid.NewGuid():N}";

    private static bool TryValidateLaunchConfig(
        AppDefinition definition,
        out LaunchConfiguration? launchConfiguration,
        out LaunchOperationResult error)
    {
        launchConfiguration = definition.Launch;
        if (launchConfiguration is null || string.IsNullOrWhiteSpace(launchConfiguration.ExePath))
        {
            error = LaunchOperationResult.CreateError(
                -32020,
                "launch_failed",
                new { reason = "launch_config_missing" });
            return false;
        }

        error = null!;
        return true;
    }

    private System.Diagnostics.Process? StartProcess(
        LaunchConfiguration launchConfig,
        string appId,
        string? scope,
        string httpBaseUrl)
    {
        var arguments = RenderTemplate(
            launchConfig.ArgsTemplate,
            appId,
            scope,
            httpBaseUrl);

        return _processLauncher.Start(launchConfig, arguments);
    }

    private async Task<bool> WaitForRegistrationAsync(string appId, string? scope, int waitForRegisterMs, CancellationToken cancellationToken)
    {
        var deadline = _clock.UtcNow.AddMilliseconds(waitForRegisterMs);
        while (_clock.UtcNow <= deadline)
        {
            var hasOnline = _appRegistry
                .ListInstances(appId, scope, includeAllScopes: false, includeOffline: false)
                .Any();
            if (hasOnline)
            {
                return true;
            }

            await Task.Delay(20, cancellationToken);
        }

        return false;
    }

    private static string? RenderTemplate(
        string? template,
        string appId,
        string? scope,
        string httpBaseUrl)
    {
        if (string.IsNullOrEmpty(template))
        {
            return template;
        }

        var scopeValue = scope ?? string.Empty;
        var scopeOrGlobal = string.IsNullOrEmpty(scope) ? "global" : scope;

        return template
            .Replace("{appId}", appId, StringComparison.Ordinal)
            .Replace("{scope}", scopeValue, StringComparison.Ordinal)
            .Replace("{scopeOrGlobal}", scopeOrGlobal, StringComparison.Ordinal)
            .Replace("{httpBaseUrl}", httpBaseUrl, StringComparison.Ordinal);
    }

    private string ResolveDedupeKey(
        AppDefinition definition,
        string appId,
        string? scope,
        string? explicitDedupeKey,
        string httpBaseUrl)
    {
        if (!string.IsNullOrWhiteSpace(explicitDedupeKey))
        {
            return explicitDedupeKey;
        }

        var template = definition.Launch?.DedupeKeyTemplate;
        if (string.IsNullOrWhiteSpace(template))
        {
            template = DefaultDedupeKeyTemplate;
        }

        var rendered = RenderTemplate(template, appId, scope, httpBaseUrl);
        return string.IsNullOrWhiteSpace(rendered)
            ? RenderTemplate(DefaultDedupeKeyTemplate, appId, scope, httpBaseUrl) ?? $"{appId}:{scope ?? "global"}"
            : rendered;
    }

    private void UpdateDedupeRecordPid(string dedupeKey, string launchId, int pid)
    {
        lock (_dedupeSyncRoot)
        {
            if (_dedupeRecords.TryGetValue(dedupeKey, out var record) && record.LaunchId == launchId)
            {
                record.Pid = pid;
            }
        }
    }

    private void RemoveDedupeRecord(string dedupeKey, string launchId)
    {
        lock (_dedupeSyncRoot)
        {
            if (_dedupeRecords.TryGetValue(dedupeKey, out var record) && record.LaunchId == launchId)
            {
                _dedupeRecords.Remove(dedupeKey);
            }
        }
    }

    private void CleanupExpiredDedupeRecords(DateTime now)
    {
        var expiredKeys = _dedupeRecords
            .Where(entry => now > entry.Value.CreatedAtUtc.AddSeconds(_runtimeTuningOptions.LaunchDedupeWindowSeconds))
            .Select(entry => entry.Key)
            .ToList();

        foreach (var key in expiredKeys)
        {
            _dedupeRecords.Remove(key);
        }
    }

    private DedupeLaunchRecord? TryGetActiveDedupeRecord(string dedupeKey, DateTime now)
    {
        CleanupExpiredDedupeRecords(now);

        if (!_dedupeRecords.TryGetValue(dedupeKey, out var record))
        {
            return null;
        }

        if (HasObservedRegistration(record) || !IsLaunchStillInProgress(record))
        {
            _dedupeRecords.Remove(dedupeKey);
            return null;
        }

        return record;
    }

    private bool HasObservedRegistration(DedupeLaunchRecord record)
    {
        return _appRegistry
            .ListInstances(record.AppId, record.Scope, includeAllScopes: false, includeOffline: true)
            .Any(instance => instance.RegisteredAtUtc >= record.CreatedAtUtc);
    }

    private static bool IsLaunchStillInProgress(DedupeLaunchRecord record)
    {
        if (!record.Pid.HasValue)
        {
            return true;
        }

        try
        {
            using var process = Process.GetProcessById(record.Pid.Value);
            return !process.HasExited;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    private sealed class DedupeLaunchRecord
    {
        public required string LaunchId { get; init; }

        public required string AppId { get; init; }

        public required string? Scope { get; init; }

        public required DateTime CreatedAtUtc { get; set; }

        public int? Pid { get; set; }
    }
}

/// <summary>
/// 启动执行结果。
/// </summary>
public sealed class LaunchOperationResult
{
    /// <summary>
    /// 是否成功。
    /// </summary>
    public required bool Ok { get; init; }

    /// <summary>
    /// 启动状态。
    /// </summary>
    public string? Status { get; init; }

    /// <summary>
    /// 启动进程 PID。
    /// </summary>
    public int? Pid { get; init; }

    /// <summary>
    /// 启动流水号。
    /// </summary>
    public string? LaunchId { get; init; }

    /// <summary>
    /// 错误码。
    /// </summary>
    public int? ErrorCode { get; init; }

    /// <summary>
    /// 错误消息。
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// 错误附加数据。
    /// </summary>
    public object? ErrorData { get; init; }

    /// <summary>
    /// 创建成功结果。
    /// </summary>
    public static LaunchOperationResult CreateSuccess(string status, string launchId, int? pid)
    {
        return new LaunchOperationResult
        {
            Ok = true,
            Status = status,
            LaunchId = launchId,
            Pid = pid
        };
    }

    /// <summary>
    /// 创建失败结果。
    /// </summary>
    public static LaunchOperationResult CreateError(int code, string message, object? data)
    {
        return new LaunchOperationResult
        {
            Ok = false,
            ErrorCode = code,
            ErrorMessage = message,
            ErrorData = data
        };
    }
}
