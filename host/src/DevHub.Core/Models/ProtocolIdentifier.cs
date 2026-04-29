using System.Text.RegularExpressions;

namespace DevHub.Core.Models;

/// <summary>
/// DevHub 协议公开标识符约束。
/// </summary>
public static partial class ProtocolIdentifier
{
    /// <summary>
    /// `appId`、`instanceId` 与非空 `scope` 共享的 canonical 正则。
    /// </summary>
    public const string CanonicalPattern = "^[A-Za-z0-9_](?:[A-Za-z0-9_.-]*[A-Za-z0-9_])?$";

    /// <summary>
    /// `instanceId` 最大长度。
    /// </summary>
    public const int MaxInstanceIdLength = 256;

    private static readonly Regex CanonicalRegex = CanonicalIdentifierRegex();

    /// <summary>
    /// 判断 `appId` 是否满足协议约束。
    /// </summary>
    public static bool IsValidAppId(string? appId)
    {
        return IsValidRequiredIdentifier(appId);
    }

    /// <summary>
    /// 判断 `instanceId` 是否满足协议约束。
    /// </summary>
    public static bool IsValidInstanceId(string? instanceId)
    {
        return IsValidRequiredIdentifier(instanceId) && instanceId!.Length <= MaxInstanceIdLength;
    }

    /// <summary>
    /// 判断显式 `scope` 是否满足协议约束。
    /// </summary>
    public static bool IsValidScope(string? scope)
    {
        return scope is not null && (scope.Length == 0 || CanonicalRegex.IsMatch(scope));
    }

    /// <summary>
    /// 判断列表过滤 `scope` 是否满足协议约束。
    /// </summary>
    public static bool IsValidListScope(string? scope)
    {
        return scope is null || IsValidScope(scope);
    }

    /// <summary>
    /// 要求 `appId` 满足协议约束。
    /// </summary>
    public static void EnsureAppId(string? appId, string paramName)
    {
        if (!IsValidAppId(appId))
        {
            throw new ArgumentException($"appId must match {CanonicalPattern}.", paramName);
        }
    }

    /// <summary>
    /// 要求 `instanceId` 满足协议约束。
    /// </summary>
    public static void EnsureInstanceId(string? instanceId, string paramName)
    {
        if (!IsValidInstanceId(instanceId))
        {
            throw new ArgumentException($"instanceId must match {CanonicalPattern} and be at most {MaxInstanceIdLength} characters.", paramName);
        }
    }

    /// <summary>
    /// 要求显式 `scope` 满足协议约束。
    /// </summary>
    public static void EnsureScope(string? scope, string paramName)
    {
        if (!IsValidScope(scope))
        {
            throw new ArgumentException($"scope must be empty or match {CanonicalPattern}.", paramName);
        }
    }

    /// <summary>
    /// 要求列表过滤 `scope` 满足协议约束。
    /// </summary>
    public static void EnsureListScope(string? scope, string paramName)
    {
        if (!IsValidListScope(scope))
        {
            throw new ArgumentException($"scope must be null, empty, or match {CanonicalPattern}.", paramName);
        }
    }

    private static bool IsValidRequiredIdentifier(string? value)
    {
        return !string.IsNullOrEmpty(value) && CanonicalRegex.IsMatch(value);
    }

    [GeneratedRegex(CanonicalPattern, RegexOptions.CultureInvariant)]
    private static partial Regex CanonicalIdentifierRegex();
}
