using System.Text.RegularExpressions;

namespace DevHub.Sdk.Internal;

internal static class ProtocolIdentifier
{
    internal const int MaxInstanceIdLength = 256;

    private static readonly Regex CanonicalIdentifierPattern = new(
        "^[A-Za-z0-9_](?:[A-Za-z0-9_.-]*[A-Za-z0-9_])?$",
        RegexOptions.Compiled);

    internal static bool IsValidAppId(string? appId)
    {
        return IsValidCanonicalIdentifier(appId);
    }

    internal static bool IsValidInstanceId(string? instanceId)
    {
        return instanceId is not null &&
            instanceId.Length <= MaxInstanceIdLength &&
            IsValidCanonicalIdentifier(instanceId);
    }

    internal static bool IsValidScope(string? scope)
    {
        return scope is not null && (scope.Length == 0 || IsValidCanonicalIdentifier(scope));
    }

    internal static string EnsureAppId(string? appId, string paramName)
    {
        if (!IsValidAppId(appId))
        {
            throw new ArgumentException(
                "appId 必须匹配 ^[A-Za-z0-9_](?:[A-Za-z0-9_.-]*[A-Za-z0-9_])?$，且不能为空字符串。",
                paramName);
        }

        return appId!;
    }

    internal static string? EnsureOptionalAppId(string? appId, string paramName)
    {
        return appId is null ? null : EnsureAppId(appId, paramName);
    }

    internal static string EnsureInstanceId(string? instanceId, string paramName)
    {
        if (!IsValidInstanceId(instanceId))
        {
            throw new ArgumentException(
                "instanceId 必须匹配 ^[A-Za-z0-9_](?:[A-Za-z0-9_.-]*[A-Za-z0-9_])?$，不能为空字符串，且长度不能超过 256。",
                paramName);
        }

        return instanceId!;
    }

    internal static string? EnsureOptionalInstanceId(string? instanceId, string paramName)
    {
        return instanceId is null ? null : EnsureInstanceId(instanceId, paramName);
    }

    private static bool IsValidCanonicalIdentifier(string? value)
    {
        return !string.IsNullOrEmpty(value) && CanonicalIdentifierPattern.IsMatch(value);
    }
}
