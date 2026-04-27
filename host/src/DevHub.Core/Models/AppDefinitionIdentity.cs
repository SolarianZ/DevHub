namespace DevHub.Core.Models;

/// <summary>
/// AppDefinition 复合身份。
/// </summary>
public readonly record struct AppDefinitionIdentity(string AppId, string Scope)
{
    /// <summary>
    /// Global Definition 的文件名后缀。
    /// </summary>
    public const string GlobalScopeFileSegment = "global";

    /// <summary>
    /// 显式 scope 的文件名编码前缀。
    /// </summary>
    public const string ExplicitScopeFileSegmentPrefix = "scope-";

    /// <summary>
    /// 从 appId 与 scope 构造复合身份。
    /// </summary>
    public static AppDefinitionIdentity Create(string appId, string scope)
    {
        ProtocolIdentifier.EnsureAppId(appId, nameof(appId));
        ScopeContract.EnsureScopedString(scope, nameof(scope));
        return new AppDefinitionIdentity(appId, scope);
    }

    /// <summary>
    /// 从定义模型构造复合身份。
    /// </summary>
    public static AppDefinitionIdentity FromDefinition(AppDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return Create(definition.AppId, definition.Scope);
    }

    /// <summary>
    /// 生成定义文件名。
    /// </summary>
    public string GetFileName()
    {
        return $"{AppId}--{EncodeScopeSegment(Scope)}.json";
    }

    /// <summary>
    /// 生成无冲突的 scope 文件名片段。
    /// </summary>
    public static string EncodeScopeSegment(string scope)
    {
        ScopeContract.EnsureScopedString(scope, nameof(scope));

        if (ScopeContract.IsGlobal(scope))
        {
            return GlobalScopeFileSegment;
        }

        return $"{ExplicitScopeFileSegmentPrefix}{scope}";
    }
}
