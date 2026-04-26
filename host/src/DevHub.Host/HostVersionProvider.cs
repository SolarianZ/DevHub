using System.Reflection;
using System.Text.RegularExpressions;

namespace DevHub.Host;

/// <summary>
/// 解析 Host 进程版本信息。
/// </summary>
internal static class HostVersionProvider
{
    private static readonly Regex SemVerPattern = new(
        "^(0|[1-9]\\d*)\\.(0|[1-9]\\d*)\\.(0|[1-9]\\d*)(?:-((?:0|[1-9]\\d*|\\d*[A-Za-z-][0-9A-Za-z-]*)(?:\\.(?:0|[1-9]\\d*|\\d*[A-Za-z-][0-9A-Za-z-]*))*))?(?:\\+([0-9A-Za-z-]+(?:\\.[0-9A-Za-z-]+)*))?$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

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

    /// <summary>
    /// 解析用于 <c>hub.getVersion</c> 的规范 SemVer 版本号。
    /// </summary>
    /// <param name="preferredVersion">优先使用的原始版本值。</param>
    /// <param name="assembly">目标程序集，未提供时使用当前 Host 程序集。</param>
    /// <returns>规范化后的 SemVer 版本；若无法解析则返回 <c>null</c>。</returns>
    internal static string? ResolveRuntimeVersion(string? preferredVersion = null, Assembly? assembly = null)
    {
        if (TryNormalizeSemVer(preferredVersion, out var normalizedPreferredVersion))
        {
            return normalizedPreferredVersion;
        }

        assembly ??= typeof(Program).Assembly;

        var informationalVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (TryNormalizeSemVer(informationalVersion, out var normalizedInformationalVersion))
        {
            return normalizedInformationalVersion;
        }

        var assemblyVersion = assembly.GetName().Version;
        if (assemblyVersion is null)
        {
            return null;
        }

        var patch = assemblyVersion.Build >= 0 ? assemblyVersion.Build : 0;
        return $"{assemblyVersion.Major}.{assemblyVersion.Minor}.{patch}";
    }

    private static bool TryNormalizeSemVer(string? rawVersion, out string normalizedVersion)
    {
        normalizedVersion = string.Empty;

        if (string.IsNullOrWhiteSpace(rawVersion))
        {
            return false;
        }

        var trimmedVersion = rawVersion.Trim();
        if (!SemVerPattern.IsMatch(trimmedVersion))
        {
            return false;
        }

        normalizedVersion = trimmedVersion;
        return true;
    }
}
