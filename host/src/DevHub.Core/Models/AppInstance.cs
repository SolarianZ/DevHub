using System.Text.Json.Serialization;

namespace DevHub.Core.Models;

/// <summary>
/// 应用程序实例模型
/// 符合 Spec §5.2 AppInstance 模式定义
/// </summary>
public class AppInstance
{
    /// <summary>
    /// 实例ID（唯一标识符，最大256字符）
    /// </summary>
    [JsonPropertyName("instanceId")]
    public required string InstanceId { get; set; }

    /// <summary>
    /// 应用程序ID
    /// </summary>
    [JsonPropertyName("appId")]
    public required string AppId { get; set; }

    /// <summary>
    /// 作用域（null 表示全局作用域，非空字符串表示显式作用域）
    /// </summary>
    [JsonPropertyName("scope")]
    public string? Scope { get; set; }

    /// <summary>
    /// 进程ID
    /// </summary>
    [JsonPropertyName("pid")]
    public int Pid { get; set; }

    /// <summary>
    /// 注册时间（UTC，由 Hub 设置）
    /// </summary>
    [JsonPropertyName("registeredAtUtc")]
    public DateTime RegisteredAtUtc { get; set; }

    /// <summary>
    /// 最后活跃时间（UTC，由 Hub 更新）
    /// 用于在线判定：now - lastSeenUtc <= 在线阈值 表示在线
    /// </summary>
    [JsonPropertyName("lastSeenUtc")]
    public DateTime LastSeenUtc { get; set; }

    /// <summary>
    /// 调用能力配置
    /// 指示实例是否支持 poll 和 respond 操作
    /// </summary>
    [JsonPropertyName("invoke")]
    public InvokeCapability Invoke { get; set; } = new();

    /// <summary>
    /// 端点信息
    /// </summary>
    [JsonIgnore]
    [JsonPropertyName("endpoints")]
    public Dictionary<string, string>? Endpoints { get; set; }

    /// <summary>
    /// 元数据
    /// </summary>
    [JsonPropertyName("meta")]
    public Dictionary<string, object?>? Meta { get; set; }
}

/// <summary>
/// 调用能力配置
/// 符合 Spec §5.2 中 invoke 字段的定义
/// </summary>
public class InvokeCapability
{
    /// <summary>
    /// 是否支持 poll 操作（从 Hub 拉取待处理的调用请求）
    /// </summary>
    [JsonPropertyName("poll")]
    public bool Poll { get; set; }

    /// <summary>
    /// 是否支持 respond 操作（向 Hub 发送调用响应）
    /// </summary>
    [JsonPropertyName("respond")]
    public bool Respond { get; set; }
}
