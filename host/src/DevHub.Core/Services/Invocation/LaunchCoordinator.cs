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
    private const string ProcessExitedBeforeRegisterReason = "process_exited_before_register";
    public const string LaunchIdEnvironmentVariable = "DEVHUB_LAUNCH_ID";

    private readonly object _launchSyncRoot = new();
    private readonly Dictionary<LaunchDedupeIdentity, LaunchRecord> _dedupeRecords = new();
    private readonly Dictionary<string, LaunchRecord> _launchRecordsById = new();
    private readonly IDefinitionProvider _definitionProvider;
    private readonly AppRegistry _appRegistry;
    private readonly IRuntimeHttpBaseUrlProvider _runtimeHttpBaseUrlProvider;
    private readonly IProcessLauncher _processLauncher;
    private readonly IProcessStatusProvider _processStatusProvider;
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
            new ProcessStatusProvider(),
            clock,
            RuntimeTuningOptions.Default,
            logger)
    {
    }

    /// <summary>
    /// 初始化启动协调器。
    /// </summary>
    public LaunchCoordinator(
        IDefinitionProvider definitionProvider,
        AppRegistry appRegistry,
        IRuntimeHttpBaseUrlProvider runtimeHttpBaseUrlProvider,
        IProcessLauncher processLauncher,
        IClock clock,
        RuntimeTuningOptions runtimeTuningOptions,
        ILogger<LaunchCoordinator> logger)
        : this(
            definitionProvider,
            appRegistry,
            runtimeHttpBaseUrlProvider,
            processLauncher,
            new ProcessStatusProvider(),
            clock,
            runtimeTuningOptions,
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
        IProcessStatusProvider processStatusProvider,
        IClock clock,
        RuntimeTuningOptions runtimeTuningOptions,
        ILogger<LaunchCoordinator> logger)
    {
        _definitionProvider = definitionProvider;
        _appRegistry = appRegistry;
        _runtimeHttpBaseUrlProvider = runtimeHttpBaseUrlProvider;
        _processLauncher = processLauncher;
        _processStatusProvider = processStatusProvider;
        _clock = clock;
        _runtimeTuningOptions = runtimeTuningOptions;
        _logger = logger;
    }

    /// <summary>
    /// 执行一次应用启动。
    /// </summary>
    public async Task<LaunchOperationResult> LaunchAsync(
        string appId,
        string scope,
        string? dedupeKey,
        int waitForRegisterMs,
        CancellationToken cancellationToken)
    {
        ProtocolIdentifier.EnsureAppId(appId, nameof(appId));
        ScopeContract.EnsureScopedString(scope, nameof(scope));

        _definitionProvider.Refresh();
        var definition = _definitionProvider.GetDefinition(appId, scope);
        if (definition is null)
        {
            return LaunchOperationResult.CreateError(
                -32014,
                "app_definition_not_found",
                BuildAppDefinitionNotFoundData(appId, scope));
        }

        var onlineInstances = _appRegistry
            .ListInstances(appId, scope, includeOffline: false)
            .ToList();

        if (onlineInstances.Count > 0)
        {
            return LaunchOperationResult.CreateSuccess(
                status: "already_running",
                launchId: null,
                pid: onlineInstances[0].Pid,
                dedupeKey: null,
                instanceId: onlineInstances[0].InstanceId);
        }

        if (!TryValidateLaunchConfig(definition, out var launchConfig, out var configError))
        {
            return configError;
        }

        var httpBaseUrl = _runtimeHttpBaseUrlProvider.GetHttpBaseUrl();
        var resolvedDedupeKey = ResolveDedupeKey(definition, appId, scope, dedupeKey, httpBaseUrl);
        var dedupeIdentity = LaunchDedupeIdentity.Create(appId, scope, resolvedDedupeKey);

        var now = _clock.UtcNow;
        LaunchRecord? existingRecord;
        lock (_launchSyncRoot)
        {
            existingRecord = TryGetActiveDedupeRecord(dedupeIdentity, now);
        }

        if (existingRecord is not null)
        {
            return LaunchOperationResult.CreateSuccess(
                status: "already_running",
                launchId: existingRecord.LaunchId,
                pid: existingRecord.Pid,
                dedupeKey: existingRecord.DedupeKey,
                instanceId: null);
        }

        var launchId = BuildLaunchId();
        lock (_launchSyncRoot)
        {
            var nextNow = _clock.UtcNow;
            if (TryGetActiveDedupeRecord(dedupeIdentity, nextNow) is { } record)
            {
                return LaunchOperationResult.CreateSuccess(
                    status: "already_running",
                    launchId: record.LaunchId,
                    pid: record.Pid,
                    dedupeKey: record.DedupeKey,
                    instanceId: null);
            }

            var launchRecord = new LaunchRecord
            {
                LaunchId = launchId,
                DedupeIdentity = dedupeIdentity,
                DedupeKey = resolvedDedupeKey,
                AppId = appId,
                Scope = scope,
                CreatedAtUtc = nextNow,
                RegisterDeadlineUtc = ComputeRegisterDeadlineUtc(nextNow, waitForRegisterMs),
                State = LaunchRecordState.Starting
            };
            _dedupeRecords[dedupeIdentity] = launchRecord;
            _launchRecordsById[launchId] = launchRecord;
        }

        Process? process;
        try
        {
            process = StartProcess(launchConfig!, appId, scope, httpBaseUrl, launchId);
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
        catch (FormatException ex)
        {
            RemoveLaunchRecordById(launchId);
            _logger.LogWarning(ex, "启动参数模板解析失败，AppId: {AppId}, Scope: {Scope}", appId, scope);
            return LaunchOperationResult.CreateError(
                -32602,
                "invalid_params",
                new
                {
                    reason = "invalid_launch_args_template"
                });
        }
        catch (Exception ex)
        {
            RemoveLaunchRecordById(launchId);
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

        UpdateLaunchRecordPid(launchId, process.Id);
        if (waitForRegisterMs <= 0)
        {
            return LaunchOperationResult.CreateSuccess("started", launchId, process.Id, resolvedDedupeKey, instanceId: null);
        }

        var waitOutcome = await WaitForRegistrationAsync(launchId, waitForRegisterMs, cancellationToken);
        return waitOutcome switch
        {
            LaunchWaitOutcome.Registered => BuildStartedAfterWait(launchId, process.Id, resolvedDedupeKey),
            LaunchWaitOutcome.ScopeMismatch or
            LaunchWaitOutcome.ProcessExitedBeforeRegister => BuildLaunchFailureAfterWait(launchId),
            LaunchWaitOutcome.RegisterTimeout => LaunchOperationResult.CreateError(
                -32020,
                "launch_failed",
                new { reason = "launch_register_timeout", launchId }),
            _ => LaunchOperationResult.CreateSuccess("starting", launchId, process.Id, resolvedDedupeKey, instanceId: null)
        };
    }

    /// <inheritdoc />
    public LaunchRegistrationValidationResult ValidateRegistration(string? launchId, string appId, string scope)
    {
        if (string.IsNullOrWhiteSpace(launchId))
        {
            return new LaunchRegistrationValidationResult(LaunchRegistrationValidationStatus.NotTracked);
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        ScopeContract.EnsureScopedString(scope, nameof(scope));
        lock (_launchSyncRoot)
        {
            CleanupExpiredLaunchRecords(_clock.UtcNow);

            if (!_launchRecordsById.TryGetValue(launchId, out var launchRecord))
            {
                return new LaunchRegistrationValidationResult(LaunchRegistrationValidationStatus.NotTracked);
            }

            if (!string.Equals(launchRecord.AppId, appId, StringComparison.Ordinal)
                || !string.Equals(launchRecord.Scope, scope, StringComparison.Ordinal))
            {
                launchRecord.State = LaunchRecordState.Failed;
                launchRecord.FailureReason = "definition_scope_mismatch";
                launchRecord.FailureData = BuildDefinitionScopeMismatchErrorData(
                    launchId,
                    launchRecord.AppId,
                    launchRecord.Scope,
                    appId,
                    scope);
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
                && string.Equals(launchRecord.Scope, instance.Scope, StringComparison.Ordinal))
            {
                launchRecord.State = LaunchRecordState.Registered;
                launchRecord.RegisteredInstanceId = instance.InstanceId;
                _logger.LogInformation(
                    "启动记录已完成注册绑定，LaunchId: {LaunchId}, AppId: {AppId}, Scope: {Scope}, InstanceId: {InstanceId}",
                    launchRecord.LaunchId,
                    launchRecord.AppId,
                    launchRecord.Scope,
                    instance.InstanceId);
            }
        }
    }

    private static string BuildLaunchId() => $"launch-{Guid.NewGuid():N}";

    private DateTime ComputeRegisterDeadlineUtc(DateTime createdAtUtc, int waitForRegisterMs)
    {
        var baselineDeadlineUtc = createdAtUtc.AddSeconds(_runtimeTuningOptions.LaunchRegisterTimeoutSeconds);
        if (waitForRegisterMs <= 0)
        {
            return baselineDeadlineUtc;
        }

        var requestedDeadlineUtc = createdAtUtc.AddMilliseconds(waitForRegisterMs);
        return requestedDeadlineUtc > baselineDeadlineUtc
            ? requestedDeadlineUtc
            : baselineDeadlineUtc;
    }

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
        string scope,
        string httpBaseUrl,
        string launchId)
    {
        var effectiveLaunchConfig = BuildEffectiveLaunchConfiguration(launchConfig, launchId, appId, scope, httpBaseUrl);
        var arguments = launchConfig.Args is not null
            ? null
            : RenderTemplate(effectiveLaunchConfig.ArgsTemplate, appId, scope, httpBaseUrl);

        return _processLauncher.Start(effectiveLaunchConfig, arguments);
    }

    private async Task<LaunchWaitOutcome> WaitForRegistrationAsync(
        string launchId,
        int waitForRegisterMs,
        CancellationToken cancellationToken)
    {
        DateTime deadline;
        lock (_launchSyncRoot)
        {
            deadline = _launchRecordsById.TryGetValue(launchId, out var launchRecord)
                ? launchRecord.CreatedAtUtc.AddMilliseconds(waitForRegisterMs)
                : _clock.UtcNow.AddMilliseconds(waitForRegisterMs);
        }

        while (true)
        {
            var now = _clock.UtcNow;
            lock (_launchSyncRoot)
            {
                if (_launchRecordsById.TryGetValue(launchId, out var launchRecord))
                {
                    if (launchRecord.State == LaunchRecordState.Failed)
                    {
                        return string.Equals(launchRecord.FailureReason, "launch_register_timeout", StringComparison.Ordinal)
                            ? LaunchWaitOutcome.RegisterTimeout
                            : string.Equals(launchRecord.FailureReason, ProcessExitedBeforeRegisterReason, StringComparison.Ordinal)
                                ? LaunchWaitOutcome.ProcessExitedBeforeRegister
                                : LaunchWaitOutcome.ScopeMismatch;
                    }

                    if (launchRecord.State == LaunchRecordState.Registered)
                    {
                        RemoveLaunchRecordById(launchId);
                        return LaunchWaitOutcome.Registered;
                    }

                    if (!IsLaunchStillInProgress(launchRecord))
                    {
                        MarkLaunchFailed(
                            launchRecord,
                            ProcessExitedBeforeRegisterReason,
                            BuildProcessExitedBeforeRegisterErrorData(launchRecord));
                        return LaunchWaitOutcome.ProcessExitedBeforeRegister;
                    }

                    if (now >= launchRecord.RegisterDeadlineUtc)
                    {
                        MarkLaunchFailed(
                            launchRecord,
                            "launch_register_timeout",
                            new { reason = "launch_register_timeout", launchId = launchRecord.LaunchId });
                        return LaunchWaitOutcome.RegisterTimeout;
                    }
                }
            }

            if (now >= deadline)
            {
                break;
            }

            await Task.Delay(20, cancellationToken);
        }

        return LaunchWaitOutcome.TimedOut;
    }

    private static LaunchOperationResult BuildStartedAfterWait(string launchId, int processId, string dedupeKey)
    {
        return LaunchOperationResult.CreateSuccess("started", launchId, processId, dedupeKey, instanceId: null);
    }

    private LaunchOperationResult BuildLaunchFailureAfterWait(string launchId)
    {
        var errorData = GetLaunchFailureData(launchId);
        RemoveLaunchRecordById(launchId);
        return LaunchOperationResult.CreateError(
            -32020,
            "launch_failed",
            errorData);
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

        return new { reason = "launch_failed", launchId };
    }

    private static string? RenderTemplate(
        string? template,
        string appId,
        string scope,
        string httpBaseUrl)
    {
        ScopeContract.EnsureScopedString(scope, nameof(scope));

        if (string.IsNullOrEmpty(template))
        {
            return template;
        }

        var scopeValue = scope;
        var scopeOrGlobal = ScopeContract.IsGlobal(scope) ? "global" : scope;

        return template
            .Replace("{appId}", appId, StringComparison.Ordinal)
            .Replace("{scope}", scopeValue, StringComparison.Ordinal)
            .Replace("{scopeOrGlobal}", scopeOrGlobal, StringComparison.Ordinal)
            .Replace("{httpBaseUrl}", httpBaseUrl, StringComparison.Ordinal);
    }

    private static IReadOnlyList<string> BuildLaunchArgs(
        LaunchConfiguration launchConfig,
        string appId,
        string scope,
        string httpBaseUrl)
    {
        if (launchConfig.Args is not null)
        {
            return launchConfig.Args
                .Select(argument => RenderTemplate(argument, appId, scope, httpBaseUrl) ?? string.Empty)
                .ToArray();
        }

        return ParseArgsTemplate(launchConfig.ArgsTemplate, appId, scope, httpBaseUrl);
    }

    private static IReadOnlyList<string> ParseArgsTemplate(
        string? argsTemplate,
        string appId,
        string scope,
        string httpBaseUrl)
    {
        var rendered = RenderTemplate(argsTemplate, appId, scope, httpBaseUrl);
        if (string.IsNullOrEmpty(rendered))
        {
            return [];
        }

        var args = new List<string>();
        var current = new System.Text.StringBuilder();
        char? quote = null;
        var escaping = false;
        var tokenStarted = false;

        foreach (var ch in rendered)
        {
            if (escaping)
            {
                current.Append(ch);
                tokenStarted = true;
                escaping = false;
                continue;
            }

            if (ch == '\\')
            {
                escaping = true;
                tokenStarted = true;
                continue;
            }

            if (quote.HasValue)
            {
                if (ch == quote.Value)
                {
                    quote = null;
                    tokenStarted = true;
                    continue;
                }

                current.Append(ch);
                tokenStarted = true;
                continue;
            }

            if (ch is '\'' or '"')
            {
                quote = ch;
                tokenStarted = true;
                continue;
            }

            if (char.IsWhiteSpace(ch))
            {
                if (tokenStarted)
                {
                    args.Add(current.ToString());
                    current.Clear();
                    tokenStarted = false;
                }

                continue;
            }

            current.Append(ch);
            tokenStarted = true;
        }

        if (escaping || quote.HasValue)
        {
            throw new FormatException("launch argsTemplate has invalid quoting or escaping.");
        }

        if (tokenStarted)
        {
            args.Add(current.ToString());
        }

        return args;
    }

    private string ResolveDedupeKey(
        AppDefinition definition,
        string appId,
        string scope,
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
            ? RenderTemplate(DefaultDedupeKeyTemplate, appId, scope, httpBaseUrl) ?? $"{appId}:{(ScopeContract.IsGlobal(scope) ? "global" : scope)}"
            : rendered;
    }

    private LaunchConfiguration BuildEffectiveLaunchConfiguration(
        LaunchConfiguration launchConfig,
        string launchId,
        string appId,
        string scope,
        string httpBaseUrl)
    {
        var environmentVariables = launchConfig.EnvironmentVariables is null
            ? new Dictionary<string, string?>(StringComparer.Ordinal)
            : new Dictionary<string, string?>(launchConfig.EnvironmentVariables, StringComparer.Ordinal);

        environmentVariables[LaunchIdEnvironmentVariable] = launchId;
        var args = BuildLaunchArgs(launchConfig, appId, scope, httpBaseUrl);

        return new LaunchConfiguration
        {
            ExePath = launchConfig.ExePath,
            Args = [.. args],
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
            .Where(record => EvaluateLaunchRecordLifecycle(record, now))
            .Select(record => record.LaunchId)
            .ToList();

        foreach (var launchId in expiredLaunchIds)
        {
            RemoveLaunchRecordById(launchId);
        }
    }

    private LaunchRecord? TryGetActiveDedupeRecord(LaunchDedupeIdentity dedupeIdentity, DateTime now)
    {
        CleanupExpiredLaunchRecords(now);

        if (!_dedupeRecords.TryGetValue(dedupeIdentity, out var record))
        {
            return null;
        }

        if (record.State == LaunchRecordState.Failed)
        {
            DeactivateDedupeRecord(record);
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
        if (_dedupeRecords.TryGetValue(record.DedupeIdentity, out var current) && current.LaunchId == record.LaunchId)
        {
            _dedupeRecords.Remove(record.DedupeIdentity);
        }
    }

    private bool IsLaunchStillInProgress(LaunchRecord record)
    {
        if (!record.Pid.HasValue)
        {
            return true;
        }

        return _processStatusProvider.IsProcessRunning(record.Pid.Value);
    }

    private static object BuildAppDefinitionNotFoundData(string appId, string scope)
    {
        return new AppDefinitionIdentityErrorData
        {
            AppId = appId,
            Scope = scope
        };
    }

    private bool EvaluateLaunchRecordLifecycle(LaunchRecord record, DateTime now)
    {
        if (record.State == LaunchRecordState.Starting)
        {
            if (!IsLaunchStillInProgress(record))
            {
                MarkLaunchFailed(
                    record,
                    ProcessExitedBeforeRegisterReason,
                    BuildProcessExitedBeforeRegisterErrorData(record));
                return false;
            }

            if (now >= record.RegisterDeadlineUtc)
            {
                MarkLaunchFailed(
                    record,
                    "launch_register_timeout",
                    new { reason = "launch_register_timeout", launchId = record.LaunchId });
            }

            return false;
        }

        return now > record.CreatedAtUtc.AddSeconds(_runtimeTuningOptions.LaunchDedupeWindowSeconds);
    }

    private void MarkLaunchFailed(LaunchRecord record, string reason, object failureData)
    {
        if (record.State == LaunchRecordState.Failed
            && string.Equals(record.FailureReason, reason, StringComparison.Ordinal))
        {
            return;
        }

        record.State = LaunchRecordState.Failed;
        record.FailureReason = reason;
        record.FailureData = failureData;
        DeactivateDedupeRecord(record);
        _logger.LogWarning(
            "启动记录进入失败终态，LaunchId: {LaunchId}, AppId: {AppId}, Scope: {Scope}, Pid: {Pid}, Reason: {Reason}",
            record.LaunchId,
            record.AppId,
            record.Scope,
            record.Pid,
            reason);
    }

    private static object BuildDefinitionScopeMismatchErrorData(
        string launchId,
        string expectedAppId,
        string expectedScope,
        string actualAppId,
        string actualScope)
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

    private static object BuildProcessExitedBeforeRegisterErrorData(LaunchRecord record)
    {
        return new
        {
            reason = ProcessExitedBeforeRegisterReason,
            launchId = record.LaunchId,
            pid = record.Pid
        };
    }

    private sealed class LaunchRecord
    {
        public required string LaunchId { get; init; }

        public required LaunchDedupeIdentity DedupeIdentity { get; init; }

        public required string DedupeKey { get; init; }

        public required string AppId { get; init; }

        public required string Scope { get; init; }

        public required DateTime CreatedAtUtc { get; init; }

        public required DateTime RegisterDeadlineUtc { get; init; }

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
        ScopeMismatch,
        RegisterTimeout,
        ProcessExitedBeforeRegister
    }

    private readonly record struct LaunchDedupeIdentity(string AppId, string Scope, string DedupeKey)
    {
        public static LaunchDedupeIdentity Create(string appId, string scope, string dedupeKey)
        {
            ProtocolIdentifier.EnsureAppId(appId, nameof(appId));
            ScopeContract.EnsureScopedString(scope, nameof(scope));
            ArgumentException.ThrowIfNullOrWhiteSpace(dedupeKey);
            return new LaunchDedupeIdentity(appId, scope, dedupeKey);
        }
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
    /// 解析后的启动去重键。
    /// </summary>
    public string? DedupeKey { get; init; }

    /// <summary>
    /// 已在线实例身份。
    /// </summary>
    public string? InstanceId { get; init; }

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
    public static LaunchOperationResult CreateSuccess(string status, string? launchId, int? pid, string? dedupeKey, string? instanceId)
    {
        return new LaunchOperationResult
        {
            Ok = true,
            Status = status,
            LaunchId = launchId,
            Pid = pid,
            DedupeKey = dedupeKey,
            InstanceId = instanceId
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
