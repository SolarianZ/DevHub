using System.Text.Json.Serialization;

namespace DevHub.Core.Models;

/// <summary>
/// 应用程序定义模型
/// 符合 Spec §5.1 AppDefinition 模式定义
/// </summary>
public class AppDefinition
{
    /// <summary>
    /// 应用程序ID（格式：^[a-z0-9][a-z0-9.-]*$）
    /// </summary>
    [JsonPropertyName("appId")]
    public required string AppId { get; set; }

    /// <summary>
    /// Definition 作用域（空字符串表示 Global Definition）。
    /// </summary>
    [JsonPropertyName("scope")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public required string Scope { get; set; }

    /// <summary>
    /// 显示名称
    /// </summary>
    [JsonPropertyName("displayName")]
    public string? DisplayName { get; set; }

    /// <summary>
    /// 应用描述
    /// </summary>
    [JsonPropertyName("description")]
    public string? Description { get; set; }

    /// <summary>
    /// 启动配置
    /// </summary>
    [JsonPropertyName("launch")]
    public LaunchConfiguration? Launch { get; set; }

    /// <summary>
    /// 能力声明配置。
    /// </summary>
    [JsonPropertyName("capabilities")]
    public AppCapabilities? Capabilities { get; set; }
}

/// <summary>
/// 启动配置
/// 符合 Spec §5.1 中 launch 字段的定义
/// </summary>
public class LaunchConfiguration
{
    /// <summary>
    /// 可执行文件路径
    /// </summary>
    [JsonPropertyName("exePath")]
    public string? ExePath { get; set; }

    /// <summary>
    /// 参数模板（支持占位符：{appId}, {scope}, {scopeOrGlobal}, {httpBaseUrl}）
    /// </summary>
    [JsonPropertyName("argsTemplate")]
    public string? ArgsTemplate { get; set; }

    /// <summary>
    /// 工作目录
    /// </summary>
    [JsonPropertyName("workingDirectory")]
    public string? WorkingDirectory { get; set; }

    /// <summary>
    /// 去重键模板（用于防止重复启动）
    /// 默认模板：{appId}:{scopeOrGlobal}
    /// </summary>
    [JsonPropertyName("dedupeKeyTemplate")]
    public string? DedupeKeyTemplate { get; set; }

    /// <summary>
    /// 进程启动时附加的环境变量。
    /// </summary>
    [JsonIgnore]
    public Dictionary<string, string?>? EnvironmentVariables { get; set; }
}

/// <summary>
/// 应用能力声明。
/// </summary>
public class AppCapabilities
{
    /// <summary>
    /// 是否允许 RPC 调用。
    /// </summary>
    [JsonPropertyName("rpc")]
    public bool? Rpc { get; set; }

    /// <summary>
    /// 是否允许事件能力。
    /// </summary>
    [JsonPropertyName("events")]
    public bool? Events { get; set; }
}
