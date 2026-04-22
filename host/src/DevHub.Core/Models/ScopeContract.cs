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
        if (scope is null)
        {
            return false;
        }

        return scope.Length == 0 || (!char.IsWhiteSpace(scope[0]) && !char.IsWhiteSpace(scope[^1]));
    }

    /// <summary>
    /// 判断是否为合法的列表过滤 scope。
    /// </summary>
    public static bool IsValidListFilter(string? scope)
    {
        return scope is null || IsValidScopedString(scope);
    }

    /// <summary>
    /// 要求调用方提供合法的显式字符串 scope。
    /// </summary>
    public static void EnsureScopedString(string? scope, string paramName)
    {
        if (!IsValidScopedString(scope))
        {
            throw new ArgumentException("scope 必须为显式字符串：\"\" 表示 Global，其他值不得包含前后空白。", paramName);
        }
    }

    /// <summary>
    /// 要求调用方提供合法的列表过滤 scope。
    /// </summary>
    public static void EnsureListFilter(string? scope, string paramName)
    {
        if (!IsValidListFilter(scope))
        {
            throw new ArgumentException("scope 过滤器必须为 null 或合法的显式字符串。", paramName);
        }
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
