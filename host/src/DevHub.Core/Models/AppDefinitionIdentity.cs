namespace DevHub.Core.Models;

/// <summary>
/// AppDefinition 复合身份。
/// </summary>
public readonly record struct AppDefinitionIdentity(string AppId, string Scope)
{
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
}
