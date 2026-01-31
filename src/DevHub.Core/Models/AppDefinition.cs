using System.Text.Json.Serialization;

namespace DevHub.Core.Models;

/// <summary>
/// 应用程序定义模型
/// </summary>
public class AppDefinition
{
    /// <summary>
    /// 应用程序ID
    /// </summary>
    [JsonPropertyName("appId")]
    public required string AppId { get; set; }

    /// <summary>
    /// 显示名称
    /// </summary>
    [JsonPropertyName("displayName")]
    public string? DisplayName { get; set; }

    /// <summary>
    /// 范围策略
    /// </summary>
    [JsonPropertyName("scopePolicy")]
    public string? ScopePolicy { get; set; }

    /// <summary>
    /// 启动配置
    /// </summary>
    [JsonPropertyName("launch")]
    public LaunchConfiguration? Launch { get; set; }
}

/// <summary>
/// 启动配置
/// </summary>
public class LaunchConfiguration
{
    /// <summary>
    /// 命令
    /// </summary>
    [JsonPropertyName("command")]
    public string? Command { get; set; }

    /// <summary>
    /// 参数
    /// </summary>
    [JsonPropertyName("arguments")]
    public string? Arguments { get; set; }
}
