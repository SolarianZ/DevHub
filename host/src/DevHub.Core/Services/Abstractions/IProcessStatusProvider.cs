using System.Diagnostics;

namespace DevHub.Core.Services.Abstractions;

/// <summary>
/// 进程状态查询抽象。
/// </summary>
public interface IProcessStatusProvider
{
    /// <summary>
    /// 判断指定进程是否仍在运行。
    /// </summary>
    /// <param name="pid">进程 ID。</param>
    /// <returns>进程仍在运行时返回 <see langword="true" />。</returns>
    bool IsProcessRunning(int pid);
}

/// <summary>
/// 基于操作系统进程表的进程状态查询实现。
/// </summary>
public sealed class ProcessStatusProvider : IProcessStatusProvider
{
    /// <inheritdoc />
    public bool IsProcessRunning(int pid)
    {
        if (pid <= 0)
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }
}
