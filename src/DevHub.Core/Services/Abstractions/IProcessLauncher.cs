using System.Diagnostics;
using DevHub.Core.Models;

namespace DevHub.Core.Services.Abstractions;

/// <summary>
/// 进程拉起抽象。
/// </summary>
public interface IProcessLauncher
{
    /// <summary>
    /// 按启动配置拉起目标进程。
    /// </summary>
    /// <param name="launchConfig">启动配置。</param>
    /// <param name="arguments">最终参数字符串。</param>
    /// <returns>启动后的进程对象。</returns>
    Process? Start(LaunchConfiguration launchConfig, string? arguments);
}

/// <summary>
/// 默认进程拉起实现。
/// </summary>
public sealed class ProcessLauncher : IProcessLauncher
{
    /// <inheritdoc />
    public Process? Start(LaunchConfiguration launchConfig, string? arguments)
    {
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
}
