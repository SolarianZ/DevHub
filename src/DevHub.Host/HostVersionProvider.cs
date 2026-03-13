using System.Reflection;

namespace DevHub.Host;

/// <summary>
/// 解析 Host 进程版本信息。
/// </summary>
internal static class HostVersionProvider
{
    /// <summary>
    /// 解析用于写入 <c>hub.json</c> 的 Hub 版本号。
    /// </summary>
    /// <param name="assembly">目标程序集，未提供时使用当前 Host 程序集。</param>
    /// <returns>可写入发现文件的版本号；若无法解析则返回 <c>null</c>。</returns>
    internal static string? ResolveHubVersion(Assembly? assembly = null)
    {
        assembly ??= typeof(Program).Assembly;

        var informationalVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informationalVersion))
        {
            return informationalVersion.Trim();
        }

        var assemblyVersion = assembly.GetName().Version?.ToString();
        return string.IsNullOrWhiteSpace(assemblyVersion)
            ? null
            : assemblyVersion.Trim();
    }
}
