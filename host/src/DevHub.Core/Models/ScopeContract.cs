namespace DevHub.Core.Models;

/// <summary>
/// 统一的 scope 合约校验工具。
/// </summary>
public static class ScopeContract
{
    /// <summary>
    /// Global 的规范化显式表示。
    /// </summary>
    public const string Global = "";

    /// <summary>
    /// 判断是否为合法的显式字符串 scope。
    /// </summary>
    public static bool IsValidScopedString(string? scope)
    {
        return ProtocolIdentifier.IsValidScope(scope);
    }

    /// <summary>
    /// 判断是否为合法的列表过滤 scope。
    /// </summary>
    public static bool IsValidListFilter(string? scope)
    {
        return ProtocolIdentifier.IsValidListScope(scope);
    }

    /// <summary>
    /// 要求调用方提供合法的显式字符串 scope。
    /// </summary>
    public static void EnsureScopedString(string? scope, string paramName)
    {
        ProtocolIdentifier.EnsureScope(scope, paramName);
    }

    /// <summary>
    /// 要求调用方提供合法的列表过滤 scope。
    /// </summary>
    public static void EnsureListFilter(string? scope, string paramName)
    {
        ProtocolIdentifier.EnsureListScope(scope, paramName);
    }

    /// <summary>
    /// 判断是否为 Global。
    /// </summary>
    public static bool IsGlobal(string scope)
    {
        EnsureScopedString(scope, nameof(scope));
        return scope.Length == 0;
    }
}
