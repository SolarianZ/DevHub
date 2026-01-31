using System.Text.Json.Serialization;

namespace DevHub.Core.Models;

/// <summary>
/// 应用程序实例模型
/// </summary>
public class AppInstance
{
    /// <summary>
    /// 实例ID
    /// </summary>
    [JsonPropertyName("instanceId")]
    public required string InstanceId { get; set; }

    /// <summary>
    /// 应用程序ID
    /// </summary>
    [JsonPropertyName("appId")]
    public required string AppId { get; set; }

    /// <summary>
    /// 范围
    /// </summary>
    [JsonPropertyName("scope")]
    public string? Scope { get; set; }

    /// <summary>
    /// 进程ID
    /// </summary>
    [JsonPropertyName("pid")]
    public int? Pid { get; set; }

    /// <summary>
    /// 注册时间
    /// </summary>
    [JsonPropertyName("registeredAtUtc")]
    public DateTime? RegisteredAtUtc { get; set; }

    /// <summary>
    /// 最后更新时间
    /// </summary>
    [JsonPropertyName("lastSeenUtc")]
    public DateTime? LastSeenUtc { get; set; }

    /// <summary>
    /// 端点信息
    /// </summary>
    [JsonPropertyName("endpoints")]
    public Dictionary<string, string>? Endpoints { get; set; }

    /// <summary>
    /// 元数据
    /// </summary>
    [JsonPropertyName("meta")]
    public Dictionary<string, object>? Meta { get; set; }
}
