using System.Diagnostics;
using DevHub.Core.Models;
using DevHub.Core.Models.Rpc;
using DevHub.Core.Services;
using DevHub.Core.Services.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DevHub.Core.Services.Invocation;

/// <summary>
/// 启动协调器，负责 launch 配置校验、进程拉起与启动绑定跟踪。
/// </summary>
public class LaunchCoordinator : ILaunchRegistrationTracker
{
    private const string DefaultDedupeKeyTemplate = "{appId}:{scopeOrGlobal}";
    public const string LaunchIdEnvironmentVariable = "DEVHUB_LAUNCH_ID";
    public const string LaunchIdMetaKey = "launchId";

    private readonly object _launchSyncRoot = new();
    private readonly Dictionary<string, LaunchRecord> _dedupeRecords = new();
    private readonly Dictionary<string, LaunchRecord> _launchRecordsById = new();
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
        var normalizedScope = AppDefinitionIdentity.NormalizeScope(scope);

        _definitionProvider.Refresh();
        var definition = _definitionProvider.GetDefinition(appId, normalizedScope);
        if (definition is null)
        {
            return LaunchOperationResult.CreateError(
                -32014,
                "app_definition_not_found",
                BuildAppDefinitionNotFoundData(appId, normalizedScope));
        }

        var onlineInstances = _appRegistry
            .ListInstances(appId, normalizedScope, includeAllScopes: false, includeOffline: false)
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
        var resolvedDedupeKey = ResolveDedupeKey(definition, appId, normalizedScope, dedupeKey, httpBaseUrl);

        var now = _clock.UtcNow;
        LaunchRecord? existingRecord;
        lock (_launchSyncRoot)
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
        lock (_launchSyncRoot)
        {
            var nextNow = _clock.UtcNow;
            if (TryGetActiveDedupeRecord(resolvedDedupeKey, nextNow) is { } record)
            {
                return LaunchOperationResult.CreateSuccess(
                    status: "already_running",
                    launchId: record.LaunchId,
                    pid: record.Pid);
            }

            var launchRecord = new LaunchRecord
            {
                LaunchId = launchId,
                DedupeKey = resolvedDedupeKey,
                AppId = appId,
                Scope = normalizedScope,
                CreatedAtUtc = nextNow,
                State = LaunchRecordState.Starting
            };
            _dedupeRecords[resolvedDedupeKey] = launchRecord;
            _launchRecordsById[launchId] = launchRecord;
        }

        Process? process;
        try
        {
            process = StartProcess(launchConfig!, appId, normalizedScope, httpBaseUrl, launchId);
            if (process is null)
            {
                RemoveLaunchRecordById(launchId);
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
            RemoveLaunchRecordById(launchId);
            _logger.LogError(ex, "启动进程失败，AppId: {AppId}, Scope: {Scope}", appId, normalizedScope);
            return LaunchOperationResult.CreateError(
                -32020,
                "launch_failed",
                new
                {
                    reason = "process_start_failed",
                    stderr = ex.Message
                });
        }

        UpdateLaunchRecordPid(launchId, process.Id);
        if (waitForRegisterMs <= 0)
        {
            return LaunchOperationResult.CreateSuccess("started", launchId, process.Id);
        }

        var waitOutcome = await WaitForRegistrationAsync(launchId, waitForRegisterMs, cancellationToken);
        return waitOutcome switch
        {
            LaunchWaitOutcome.Registered => BuildStartedAfterWait(launchId, process.Id),
            LaunchWaitOutcome.ScopeMismatch => LaunchOperationResult.CreateError(
                -32020,
                "launch_failed",
                GetLaunchFailureData(launchId)),
            _ => LaunchOperationResult.CreateSuccess("starting", launchId, process.Id)
        };
    }

    /// <inheritdoc />
    public LaunchRegistrationValidationResult ValidateRegistration(string? launchId, string appId, string? scope)
    {
        if (string.IsNullOrWhiteSpace(launchId))
        {
            return new LaunchRegistrationValidationResult(LaunchRegistrationValidationStatus.NotTracked);
        }

        var normalizedScope = AppDefinitionIdentity.NormalizeScope(scope);
        lock (_launchSyncRoot)
        {
            CleanupExpiredLaunchRecords(_clock.UtcNow);

            if (!_launchRecordsById.TryGetValue(launchId, out var launchRecord))
            {
                return new LaunchRegistrationValidationResult(LaunchRegistrationValidationStatus.NotTracked);
            }

            if (!string.Equals(launchRecord.AppId, appId, StringComparison.Ordinal)
                || !string.Equals(launchRecord.Scope, normalizedScope, StringComparison.Ordinal))
            {
                launchRecord.State = LaunchRecordState.Failed;
                launchRecord.FailureReason = "definition_scope_mismatch";
                launchRecord.FailureData = BuildDefinitionScopeMismatchErrorData(
                    launchId,
                    launchRecord.AppId,
                    launchRecord.Scope,
                    appId,
                    normalizedScope);
                DeactivateDedupeRecord(launchRecord);

                return new LaunchRegistrationValidationResult(
                    LaunchRegistrationValidationStatus.Mismatched,
                    launchRecord.FailureData);
            }

            return new LaunchRegistrationValidationResult(LaunchRegistrationValidationStatus.Matched);
        }
    }

    /// <inheritdoc />
    public void RecordSuccessfulRegistration(string? launchId, AppInstance instance)
    {
        ArgumentNullException.ThrowIfNull(instance);

        if (string.IsNullOrWhiteSpace(launchId))
        {
            return;
        }

        lock (_launchSyncRoot)
        {
            CleanupExpiredLaunchRecords(_clock.UtcNow);

            if (_launchRecordsById.TryGetValue(launchId, out var launchRecord)
                && string.Equals(launchRecord.AppId, instance.AppId, StringComparison.Ordinal)
                && string.Equals(launchRecord.Scope, AppDefinitionIdentity.NormalizeScope(instance.Scope), StringComparison.Ordinal))
            {
                launchRecord.State = LaunchRecordState.Registered;
                launchRecord.RegisteredInstanceId = instance.InstanceId;
                DeactivateDedupeRecord(launchRecord);
            }
        }
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

    private Process? StartProcess(
        LaunchConfiguration launchConfig,
        string appId,
        string? scope,
        string httpBaseUrl,
        string launchId)
    {
        var effectiveLaunchConfig = BuildEffectiveLaunchConfiguration(launchConfig, launchId);
        var arguments = RenderTemplate(
            effectiveLaunchConfig.ArgsTemplate,
            appId,
            scope,
            httpBaseUrl);

        return _processLauncher.Start(effectiveLaunchConfig, arguments);
    }

    private async Task<LaunchWaitOutcome> WaitForRegistrationAsync(
        string launchId,
        int waitForRegisterMs,
        CancellationToken cancellationToken)
    {
        var deadline = _clock.UtcNow.AddMilliseconds(waitForRegisterMs);
        while (_clock.UtcNow <= deadline)
        {
            lock (_launchSyncRoot)
            {
                CleanupExpiredLaunchRecords(_clock.UtcNow);

                if (_launchRecordsById.TryGetValue(launchId, out var launchRecord))
                {
                    if (launchRecord.State == LaunchRecordState.Failed
                        && string.Equals(launchRecord.FailureReason, "definition_scope_mismatch", StringComparison.Ordinal))
                    {
                        RemoveLaunchRecordById(launchId);
                        return LaunchWaitOutcome.ScopeMismatch;
                    }

                    if (launchRecord.State == LaunchRecordState.Registered)
                    {
                        RemoveLaunchRecordById(launchId);
                        return LaunchWaitOutcome.Registered;
                    }
                }
            }

            await Task.Delay(20, cancellationToken);
        }

        return LaunchWaitOutcome.TimedOut;
    }

    private static LaunchOperationResult BuildStartedAfterWait(string launchId, int processId)
    {
        return LaunchOperationResult.CreateSuccess("started", launchId, processId);
    }

    private object GetLaunchFailureData(string launchId)
    {
        lock (_launchSyncRoot)
        {
            if (_launchRecordsById.TryGetValue(launchId, out var launchRecord) && launchRecord.FailureData is not null)
            {
                return launchRecord.FailureData;
            }
        }

        return new { reason = "definition_scope_mismatch", launchId };
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

    private LaunchConfiguration BuildEffectiveLaunchConfiguration(LaunchConfiguration launchConfig, string launchId)
    {
        var environmentVariables = launchConfig.EnvironmentVariables is null
            ? new Dictionary<string, string?>(StringComparer.Ordinal)
            : new Dictionary<string, string?>(launchConfig.EnvironmentVariables, StringComparer.Ordinal);

        environmentVariables[LaunchIdEnvironmentVariable] = launchId;

        return new LaunchConfiguration
        {
            ExePath = launchConfig.ExePath,
            ArgsTemplate = launchConfig.ArgsTemplate,
            WorkingDirectory = launchConfig.WorkingDirectory,
            DedupeKeyTemplate = launchConfig.DedupeKeyTemplate,
            EnvironmentVariables = environmentVariables
        };
    }

    private void UpdateLaunchRecordPid(string launchId, int pid)
    {
        lock (_launchSyncRoot)
        {
            if (_launchRecordsById.TryGetValue(launchId, out var record))
            {
                record.Pid = pid;
            }
        }
    }

    private void CleanupExpiredLaunchRecords(DateTime now)
    {
        var expiredLaunchIds = _launchRecordsById.Values
            .Where(record => now > record.CreatedAtUtc.AddSeconds(_runtimeTuningOptions.LaunchDedupeWindowSeconds))
            .Select(record => record.LaunchId)
            .ToList();

        foreach (var launchId in expiredLaunchIds)
        {
            RemoveLaunchRecordById(launchId);
        }
    }

    private LaunchRecord? TryGetActiveDedupeRecord(string dedupeKey, DateTime now)
    {
        CleanupExpiredLaunchRecords(now);

        if (!_dedupeRecords.TryGetValue(dedupeKey, out var record))
        {
            return null;
        }

        if (record.State != LaunchRecordState.Starting)
        {
            DeactivateDedupeRecord(record);
            return null;
        }

        if (!IsLaunchStillInProgress(record))
        {
            RemoveLaunchRecordById(record.LaunchId);
            return null;
        }

        return record;
    }

    private void RemoveLaunchRecordById(string launchId)
    {
        lock (_launchSyncRoot)
        {
            if (_launchRecordsById.TryGetValue(launchId, out var record))
            {
                DeactivateDedupeRecord(record);
                _launchRecordsById.Remove(launchId);
            }
        }
    }

    private void DeactivateDedupeRecord(LaunchRecord record)
    {
        if (_dedupeRecords.TryGetValue(record.DedupeKey, out var current) && current.LaunchId == record.LaunchId)
        {
            _dedupeRecords.Remove(record.DedupeKey);
        }
    }

    private static bool IsLaunchStillInProgress(LaunchRecord record)
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

    private static object BuildAppDefinitionNotFoundData(string appId, string? scope)
    {
        return new AppDefinitionIdentityErrorData
        {
            AppId = appId,
            Scope = scope
        };
    }

    private static object BuildDefinitionScopeMismatchErrorData(
        string launchId,
        string expectedAppId,
        string? expectedScope,
        string actualAppId,
        string? actualScope)
    {
        return new
        {
            reason = "definition_scope_mismatch",
            launchId,
            appId = actualAppId,
            scope = actualScope,
            expectedAppId,
            expectedScope
        };
    }

    private sealed class LaunchRecord
    {
        public required string LaunchId { get; init; }

        public required string DedupeKey { get; init; }

        public required string AppId { get; init; }

        public required string? Scope { get; init; }

        public required DateTime CreatedAtUtc { get; init; }

        public required LaunchRecordState State { get; set; }

        public string? FailureReason { get; set; }

        public object? FailureData { get; set; }

        public string? RegisteredInstanceId { get; set; }

        public int? Pid { get; set; }
    }

    private enum LaunchRecordState
    {
        Starting,
        Registered,
        Failed
    }

    private enum LaunchWaitOutcome
    {
        Registered,
        TimedOut,
        ScopeMismatch
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
