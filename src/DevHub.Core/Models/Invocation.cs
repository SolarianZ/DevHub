using System.Text.Json.Serialization;

namespace DevHub.Core.Models;

/// <summary>
/// 调用模型
/// </summary>
public class Invocation
{
    /// <summary>
    /// 调用ID
    /// </summary>
    [JsonPropertyName("invocationId")]
    public required string InvocationId { get; set; }

    /// <summary>
    /// 应用程序ID
    /// </summary>
    [JsonPropertyName("appId")]
    public string? AppId { get; set; }

    /// <summary>
    /// 目标
    /// </summary>
    [JsonPropertyName("target")]
    public string? Target { get; set; }

    /// <summary>
    /// 方法
    /// </summary>
    [JsonPropertyName("method")]
    public required string Method { get; set; }

    /// <summary>
    /// 调用类型
    /// </summary>
    [JsonPropertyName("kind")]
    public string? Kind { get; set; }

    /// <summary>
    /// 参数
    /// </summary>
    [JsonPropertyName("params")]
    public object? Params { get; set; }

    /// <summary>
    /// 过期时间（毫秒）
    /// </summary>
    [JsonPropertyName("ttlMs")]
    public long? TtlMs { get; set; }

    /// <summary>
    /// 创建时间
    /// </summary>
    [JsonPropertyName("createdAtUtc")]
    public DateTime? CreatedAtUtc { get; set; }
}
