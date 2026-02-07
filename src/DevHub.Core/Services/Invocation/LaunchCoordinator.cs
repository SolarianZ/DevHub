using System.Diagnostics;
using DevHub.Core.Models;
using DevHub.Core.Services;
using Microsoft.Extensions.Logging;

namespace DevHub.Core.Services.Invocation;

/// <summary>
/// 启动协调器，负责 launch 配置校验与进程拉起。
/// </summary>
public class LaunchCoordinator
{
    private readonly DefinitionLoader _definitionLoader;
    private readonly AppRegistry _appRegistry;
    private readonly ILogger<LaunchCoordinator> _logger;

    /// <summary>
    /// 初始化启动协调器。
    /// </summary>
    public LaunchCoordinator(DefinitionLoader definitionLoader, AppRegistry appRegistry, ILogger<LaunchCoordinator> logger)
    {
        _definitionLoader = definitionLoader;
        _appRegistry = appRegistry;
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
        _definitionLoader.Load();
        var definition = _definitionLoader.GetDefinition(appId);
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

        Process? process;
        try
        {
            process = StartProcess(launchConfig!, appId, scope, dedupeKey);
            if (process is null)
            {
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

        var launchId = BuildLaunchId();
        if (waitForRegisterMs <= 0)
        {
            return LaunchOperationResult.CreateSuccess("started", launchId, process.Id);
        }

        var isRegistered = await WaitForRegistrationAsync(appId, scope, waitForRegisterMs, cancellationToken);
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

    private static Process? StartProcess(LaunchConfiguration launchConfig, string appId, string? scope, string? dedupeKey)
    {
        var arguments = RenderTemplate(
            launchConfig.ArgsTemplate,
            appId,
            scope,
            httpBaseUrl: string.Empty,
            dedupeKey);

        var startInfo = new ProcessStartInfo
        {
            FileName = launchConfig.ExePath!,
            Arguments = arguments ?? string.Empty,
            UseShellExecute = false
        };

        if (!string.IsNullOrWhiteSpace(launchConfig.WorkingDirectory))
        {
            startInfo.WorkingDirectory = launchConfig.WorkingDirectory;
        }

        return Process.Start(startInfo);
    }

    private async Task<bool> WaitForRegistrationAsync(string appId, string? scope, int waitForRegisterMs, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(waitForRegisterMs);
        while (DateTime.UtcNow <= deadline)
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
        string httpBaseUrl,
        string? dedupeKey)
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
            .Replace("{httpBaseUrl}", httpBaseUrl, StringComparison.Ordinal)
            .Replace("{dedupeKey}", dedupeKey ?? string.Empty, StringComparison.Ordinal);
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
