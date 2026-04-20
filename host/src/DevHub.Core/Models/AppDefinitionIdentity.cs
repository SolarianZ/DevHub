using System.Text;

namespace DevHub.Core.Models;

/// <summary>
/// AppDefinition 复合身份。
/// </summary>
public readonly record struct AppDefinitionIdentity(string AppId, string? Scope)
{
    /// <summary>
    /// Global Definition 的文件名后缀。
    /// </summary>
    public const string GlobalScopeFileSegment = "global";

    /// <summary>
    /// 从 appId 与 scope 构造复合身份。
    /// </summary>
    public static AppDefinitionIdentity Create(string appId, string? scope)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        return new AppDefinitionIdentity(appId, NormalizeScope(scope));
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
    /// 规范化 scope。
    /// </summary>
    public static string? NormalizeScope(string? scope)
    {
        return string.IsNullOrEmpty(scope) ? null : scope;
    }

    /// <summary>
    /// 生成定义文件名。
    /// </summary>
    public string GetFileName()
    {
        return $"{AppId}--{EncodeScopeSegment(Scope)}.json";
    }

    /// <summary>
    /// 生成文件名安全的 scope 片段。
    /// </summary>
    public static string EncodeScopeSegment(string? scope)
    {
        if (scope is null)
        {
            return GlobalScopeFileSegment;
        }

        return Convert.ToHexString(Encoding.UTF8.GetBytes(scope));
    }
}
