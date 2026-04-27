namespace DevHub.Sdk.Internal;

internal static class ScopeContract
{
    internal static string EnsureAssignedScope(string? scope, string ownerTypeName)
    {
        if (scope is null)
        {
            throw new InvalidOperationException($"{ownerTypeName}.Scope 必须显式设置；Global 作用域请传入空字符串。");
        }

        return scope;
    }

    internal static string EnsureScopedString(string? scope, string paramName)
    {
        if (!ProtocolIdentifier.IsValidScope(scope))
        {
            throw new ArgumentException(
                "scope 必须为显式字符串：\"\" 表示 Global，其他值必须匹配 ^[A-Za-z0-9_](?:[A-Za-z0-9_.-]*[A-Za-z0-9_])?$。",
                paramName);
        }

        return scope!;
    }

    internal static string? EnsureScopeFilter(string? scope, string paramName)
    {
        if (scope is null)
        {
            return null;
        }

        return EnsureScopedString(scope, paramName);
    }

    internal static bool IsValidScopedString(string? scope)
    {
        return ProtocolIdentifier.IsValidScope(scope);
    }
}
