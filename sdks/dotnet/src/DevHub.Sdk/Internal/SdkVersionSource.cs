using System.Globalization;
using System.Reflection;

namespace DevHub.Sdk.Internal;

internal static class SdkVersionSource
{
    private static readonly Lazy<string?> CurrentVersionValue = new(ResolveCurrentVersion);

    internal static string? CurrentVersion => CurrentVersionValue.Value;

    private static string? ResolveCurrentVersion()
    {
        var assembly = typeof(SdkAssemblyMarker).Assembly;
        var informationalVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informationalVersion))
        {
            return informationalVersion.Trim();
        }

        var assemblyVersion = assembly.GetName().Version;
        if (assemblyVersion is null)
        {
            return null;
        }

        var patch = assemblyVersion.Build >= 0 ? assemblyVersion.Build : 0;
        return string.Format(
            CultureInfo.InvariantCulture,
            "{0}.{1}.{2}",
            assemblyVersion.Major,
            assemblyVersion.Minor,
            patch);
    }
}
