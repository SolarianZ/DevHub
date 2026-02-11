using Microsoft.Extensions.Logging;

namespace DevHub.Core.Services;

/// <summary>
/// DevHub 运行时调优参数。
/// </summary>
/// <remarks>
/// v1 默认值与 Spec 保持一致，但允许通过环境变量覆盖，便于不同机器与测试场景调优。
/// </remarks>
public sealed class RuntimeTuningOptions
{
    /// <summary>
    /// 租约秒数环境变量名。
    /// </summary>
    public const string LeaseSecondsEnvironmentVariable = "DEVHUB_LEASE_SECONDS";

    /// <summary>
    /// 在线阈值秒数环境变量名。
    /// </summary>
    public const string OnlineThresholdSecondsEnvironmentVariable = "DEVHUB_ONLINE_THRESHOLD_SECONDS";

    /// <summary>
    /// 启动去重窗口秒数环境变量名。
    /// </summary>
    public const string LaunchDedupeWindowSecondsEnvironmentVariable = "DEVHUB_LAUNCH_DEDUPE_WINDOW_SECONDS";

    /// <summary>
    /// 租约秒数默认值。
    /// </summary>
    public const int DefaultLeaseSeconds = 30;

    /// <summary>
    /// 在线阈值秒数默认值。
    /// </summary>
    public const int DefaultOnlineThresholdSeconds = 30;

    /// <summary>
    /// 启动去重窗口秒数默认值。
    /// </summary>
    public const int DefaultLaunchDedupeWindowSeconds = 30;

    private RuntimeTuningOptions(int leaseSeconds, int onlineThresholdSeconds, int launchDedupeWindowSeconds)
    {
        LeaseSeconds = leaseSeconds;
        OnlineThresholdSeconds = onlineThresholdSeconds;
        LaunchDedupeWindowSeconds = launchDedupeWindowSeconds;
    }

    /// <summary>
    /// 默认运行时调优参数。
    /// </summary>
    public static RuntimeTuningOptions Default { get; } = new(
        DefaultLeaseSeconds,
        DefaultOnlineThresholdSeconds,
        DefaultLaunchDedupeWindowSeconds);

    /// <summary>
    /// 调用租约秒数。
    /// </summary>
    public int LeaseSeconds { get; }

    /// <summary>
    /// 实例在线判定阈值秒数。
    /// </summary>
    public int OnlineThresholdSeconds { get; }

    /// <summary>
    /// 启动去重窗口秒数。
    /// </summary>
    public int LaunchDedupeWindowSeconds { get; }

    /// <summary>
    /// 从环境变量解析运行时调优参数。
    /// </summary>
    /// <param name="logger">日志记录器。</param>
    /// <returns>解析后的调优参数。</returns>
    public static RuntimeTuningOptions Resolve(ILogger? logger = null)
    {
        var leaseSeconds = ReadPositiveIntOrDefault(
            LeaseSecondsEnvironmentVariable,
            DefaultLeaseSeconds,
            logger);
        var onlineThresholdSeconds = ReadPositiveIntOrDefault(
            OnlineThresholdSecondsEnvironmentVariable,
            DefaultOnlineThresholdSeconds,
            logger);
        var launchDedupeWindowSeconds = ReadPositiveIntOrDefault(
            LaunchDedupeWindowSecondsEnvironmentVariable,
            DefaultLaunchDedupeWindowSeconds,
            logger);

        return new RuntimeTuningOptions(
            leaseSeconds,
            onlineThresholdSeconds,
            launchDedupeWindowSeconds);
    }

    private static int ReadPositiveIntOrDefault(string environmentVariable, int defaultValue, ILogger? logger)
    {
        var raw = Environment.GetEnvironmentVariable(environmentVariable);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return defaultValue;
        }

        if (int.TryParse(raw, out var parsed) && parsed >= 1)
        {
            return parsed;
        }

        logger?.LogWarning(
            "运行时调优参数无效，已回退默认值。Env: {EnvName}, Raw: {RawValue}, Default: {DefaultValue}",
            environmentVariable,
            raw,
            defaultValue);
        return defaultValue;
    }
}
