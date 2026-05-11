using System.Text.Json.Serialization;
using System.Text.Json;

namespace DevHub.Core.Models;

/// <summary>
/// 调用对象。
/// </summary>
public class Invocation
{
    /// <summary>
    /// 调用 ID。
    /// </summary>
    [JsonPropertyName("invocationId")]
    public required string InvocationId { get; set; }

    /// <summary>
    /// 应用 ID。
    /// </summary>
    [JsonPropertyName("appId")]
    public required string AppId { get; set; }

    /// <summary>
    /// 目标信息。
    /// </summary>
    [JsonPropertyName("target")]
    public required InvocationTarget Target { get; set; }

    /// <summary>
    /// 方法名。
    /// </summary>
    [JsonPropertyName("method")]
    public required string Method { get; set; }

    /// <summary>
    /// 调用参数。
    /// </summary>
    [JsonPropertyName("args")]
    public object? Args { get; set; }

    /// <summary>
    /// 调用类型。
    /// </summary>
    [JsonPropertyName("kind")]
    public InvocationKind Kind { get; set; }

    /// <summary>
    /// 创建时间（UTC）。
    /// </summary>
    [JsonPropertyName("createdAtUtc")]
    public DateTime CreatedAtUtc { get; set; }

    /// <summary>
    /// 选项。
    /// </summary>
    [JsonPropertyName("options")]
    public required InvocationOptions Options { get; set; }

    /// <summary>
    /// 交付信息。
    /// </summary>
    [JsonPropertyName("delivery")]
    public required InvocationDelivery Delivery { get; set; }

    /// <summary>
    /// 调用方信息。
    /// </summary>
    [JsonPropertyName("caller")]
    public required InvocationCaller Caller { get; set; }

    /// <summary>
    /// 当前状态。
    /// </summary>
    [JsonIgnore]
    public InvocationState State { get; set; }

    /// <summary>
    /// 当前租约持有者。
    /// </summary>
    [JsonIgnore]
    public string? LeaseHolderInstanceId { get; set; }

    /// <summary>
    /// 租约到期时间（UTC）。
    /// </summary>
    [JsonIgnore]
    public DateTime? LeaseExpireAtUtc { get; set; }

    /// <summary>
    /// 完成时间（UTC）。
    /// </summary>
    [JsonIgnore]
    public DateTime? CompletedAtUtc { get; set; }

    /// <summary>
    /// 响应成功值（仅内存态）。
    /// </summary>
    [JsonIgnore]
    public object? ResponseValue { get; set; }

    /// <summary>
    /// 响应错误对象（仅内存态）。
    /// </summary>
    [JsonIgnore]
    public object? ResponseError { get; set; }
}

/// <summary>
/// 调用目标。
/// </summary>
public class InvocationTarget
{
    /// <summary>
    /// 目标作用域。
    /// </summary>
    [JsonPropertyName("scope")]
    public required string Scope { get; set; }

    /// <summary>
    /// 指定实例 ID。
    /// </summary>
    [JsonPropertyName("instanceId")]
    public string? InstanceId { get; set; }
}

/// <summary>
/// 调用选项。
/// </summary>
public class InvocationOptions
{
    /// <summary>
    /// TTL（毫秒）。
    /// </summary>
    [JsonPropertyName("ttlMs")]
    public int TtlMs { get; set; }

    /// <summary>
    /// 等待超时（毫秒）。
    /// </summary>
    [JsonPropertyName("waitTimeoutMs")]
    public int? WaitTimeoutMs { get; set; }

    /// <summary>
    /// 离线是否入队。
    /// </summary>
    [JsonPropertyName("queueIfOffline")]
    public bool QueueIfOffline { get; set; }

    /// <summary>
    /// 是否自动拉起。
    /// </summary>
    [JsonPropertyName("autoLaunch")]
    public bool AutoLaunch { get; set; }
}

/// <summary>
/// 交付信息。
/// </summary>
public class InvocationDelivery
{
    /// <summary>
    /// 租约秒数。
    /// </summary>
    [JsonPropertyName("leaseSeconds")]
    public int LeaseSeconds { get; set; }

    /// <summary>
    /// 投递尝试次数。
    /// </summary>
    [JsonPropertyName("attempt")]
    public int Attempt { get; set; }

    /// <summary>
    /// 当前交付租约令牌。
    /// </summary>
    [JsonPropertyName("leaseToken")]
    public string LeaseToken { get; set; } = string.Empty;
}

/// <summary>
/// 调用方元信息。
/// </summary>
public class InvocationCaller
{
    /// <summary>
    /// 客户端 ID。
    /// </summary>
    [JsonPropertyName("clientId")]
    public required string ClientId { get; set; }

    /// <summary>
    /// 客户端会话 ID。
    /// </summary>
    [JsonPropertyName("clientSessionId")]
    public required string ClientSessionId { get; set; }
}

/// <summary>
/// 调用类型。
/// </summary>
[JsonConverter(typeof(InvocationKindJsonConverter))]
public enum InvocationKind
{
    /// <summary>
    /// 请求。
    /// </summary>
    Request,

    /// <summary>
    /// 通知。
    /// </summary>
    Notify
}

/// <summary>
/// InvocationKind JSON 转换器。
/// </summary>
public sealed class InvocationKindJsonConverter : JsonStringEnumConverter<InvocationKind>
{
    /// <summary>
    /// 初始化转换器，按 Spec 使用小写枚举值。
    /// </summary>
    public InvocationKindJsonConverter()
        : base(JsonNamingPolicy.CamelCase, allowIntegerValues: false)
    {
    }
}

/// <summary>
/// 调用状态。
/// </summary>
public enum InvocationState
{
    /// <summary>
    /// 已创建。
    /// </summary>
    Created,

    /// <summary>
    /// 已入队。
    /// </summary>
    Queued,

    /// <summary>
    /// 挂起等待实例上线。
    /// </summary>
    Pending,

    /// <summary>
    /// 已投递（租约生效）。
    /// </summary>
    Delivered,

    /// <summary>
    /// 已完成。
    /// </summary>
    Completed,

    /// <summary>
    /// 已失败。
    /// </summary>
    Failed,

    /// <summary>
    /// 已过期。
    /// </summary>
    Expired,

    /// <summary>
    /// 已超时。
    /// </summary>
    Timeout
}

/// <summary>
/// Respond 执行结果。
/// </summary>
public enum InvocationRespondStatus
{
    /// <summary>
    /// 成功。
    /// </summary>
    Success,

    /// <summary>
    /// 调用不存在。
    /// </summary>
    NotFound,

    /// <summary>
    /// 已过期。
    /// </summary>
    Expired,

    /// <summary>
    /// 投递冲突。
    /// </summary>
    DeliveryConflict
}
