using System.Text.Json;
using System.Text.Json.Serialization;

namespace DevHub.Sdk.Models;

/// <summary>
/// Ping 结果。
/// </summary>
public sealed class PingResult
{
    /// <summary>
    /// 是否成功。
    /// </summary>
    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    /// <summary>
    /// 服务端时间。
    /// </summary>
    [JsonPropertyName("serverTimeUtc")]
    public DateTimeOffset ServerTimeUtc { get; set; }

    /// <summary>
    /// 回显数据。
    /// </summary>
    [JsonPropertyName("echo")]
    public JsonElement? Echo { get; set; }
}

/// <summary>
/// Launch 请求。
/// </summary>
public sealed class LaunchRequest
{
    /// <summary>
    /// 应用标识。
    /// </summary>
    public string AppId { get; set; } = string.Empty;

    /// <summary>
    /// 作用域。
    /// </summary>
    public string? Scope { get; set; }

    /// <summary>
    /// 去重键。
    /// </summary>
    public string? DedupeKey { get; set; }

    /// <summary>
    /// 等待注册时长。
    /// </summary>
    public int? WaitForRegisterMs { get; set; }
}

/// <summary>
/// Launch 结果。
/// </summary>
public sealed class LaunchResult
{
    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("pid")]
    public int? Pid { get; set; }

    [JsonPropertyName("launchId")]
    public string LaunchId { get; set; } = string.Empty;
}

/// <summary>
/// ListInstances 请求。
/// </summary>
public sealed class ListInstancesRequest
{
    public string? AppId { get; set; }

    public string? Scope { get; set; }

    public bool IncludeOffline { get; set; }

    public bool IncludeAllScopes { get; set; }
}

/// <summary>
/// 调用请求。
/// </summary>
public sealed class InvokeRequest
{
    public string AppId { get; set; } = string.Empty;

    public InvocationTarget? Target { get; set; }

    public string Method { get; set; } = string.Empty;

    public object? Args { get; set; }

    public InvocationOptions? Options { get; set; }
}

/// <summary>
/// Notify 结果。
/// </summary>
public sealed class NotifyResult
{
    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    [JsonPropertyName("invocationId")]
    public string InvocationId { get; set; } = string.Empty;
}

/// <summary>
/// Request 结果。
/// </summary>
public sealed class RequestResult
{
    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    [JsonPropertyName("invocationId")]
    public string InvocationId { get; set; } = string.Empty;

    [JsonPropertyName("value")]
    public JsonElement? Value { get; set; }
}

/// <summary>
/// Poll 请求。
/// </summary>
public sealed class PollRequest
{
    public string InstanceId { get; set; } = string.Empty;

    public int? MaxCount { get; set; }

    public int? WaitMs { get; set; }
}

/// <summary>
/// Poll 结果。
/// </summary>
public sealed class PollResult
{
    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    [JsonPropertyName("serverTimeUtc")]
    public DateTimeOffset ServerTimeUtc { get; set; }

    [JsonPropertyName("items")]
    public List<Invocation> Items { get; set; } = [];
}

/// <summary>
/// Respond 请求。
/// </summary>
public sealed class RespondRequest
{
    public string InstanceId { get; set; } = string.Empty;

    public string InvocationId { get; set; } = string.Empty;

    public object? Value { get; set; }

    public DevHubCalleeError? Error { get; set; }
}

/// <summary>
/// 调用对象。
/// </summary>
public sealed class Invocation
{
    [JsonPropertyName("invocationId")]
    public string InvocationId { get; set; } = string.Empty;

    [JsonPropertyName("appId")]
    public string AppId { get; set; } = string.Empty;

    [JsonPropertyName("target")]
    public InvocationTarget? Target { get; set; }

    [JsonPropertyName("method")]
    public string Method { get; set; } = string.Empty;

    [JsonPropertyName("args")]
    public JsonElement? Args { get; set; }

    [JsonPropertyName("kind")]
    public InvocationKind Kind { get; set; }

    [JsonPropertyName("createdAtUtc")]
    public DateTimeOffset CreatedAtUtc { get; set; }

    [JsonPropertyName("options")]
    public InvocationOptions? Options { get; set; }

    [JsonPropertyName("delivery")]
    public InvocationDelivery? Delivery { get; set; }

    [JsonPropertyName("caller")]
    public InvocationCaller? Caller { get; set; }
}

/// <summary>
/// 调用目标。
/// </summary>
public sealed class InvocationTarget
{
    [JsonPropertyName("scope")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? Scope { get; set; }

    [JsonPropertyName("instanceId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? InstanceId { get; set; }
}

/// <summary>
/// 调用选项。
/// </summary>
public sealed class InvocationOptions
{
    [JsonPropertyName("ttlMs")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? TtlMs { get; set; }

    [JsonPropertyName("waitTimeoutMs")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? WaitTimeoutMs { get; set; }

    [JsonPropertyName("queueIfOffline")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? QueueIfOffline { get; set; }

    [JsonPropertyName("autoLaunch")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? AutoLaunch { get; set; }
}

/// <summary>
/// 投递信息。
/// </summary>
public sealed class InvocationDelivery
{
    [JsonPropertyName("leaseSeconds")]
    public int LeaseSeconds { get; set; }

    [JsonPropertyName("attempt")]
    public int Attempt { get; set; }
}

/// <summary>
/// 调用方信息。
/// </summary>
public sealed class InvocationCaller
{
    [JsonPropertyName("clientId")]
    public string ClientId { get; set; } = string.Empty;

    [JsonPropertyName("clientSessionId")]
    public string ClientSessionId { get; set; } = string.Empty;
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
/// 调用类型 JSON 转换器。
/// </summary>
public sealed class InvocationKindJsonConverter : JsonStringEnumConverter<InvocationKind>
{
    /// <summary>
    /// 初始化转换器。
    /// </summary>
    public InvocationKindJsonConverter()
        : base(JsonNamingPolicy.CamelCase, allowIntegerValues: false)
    {
    }
}

/// <summary>
/// DevHub 事件。
/// </summary>
public sealed class DevHubEvent
{
    [JsonPropertyName("subscriptionId")]
    public string SubscriptionId { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("timeUtc")]
    public DateTimeOffset TimeUtc { get; set; }

    [JsonPropertyName("payload")]
    public JsonElement? Payload { get; set; }
}

/// <summary>
/// 被调用方错误对象。
/// </summary>
public sealed class DevHubCalleeError
{
    [JsonPropertyName("code")]
    public int Code { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("data")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Data { get; set; }

    /// <summary>
    /// 使用任意 JSON 数据构造错误对象。
    /// </summary>
    /// <param name="code">错误码。</param>
    /// <param name="message">错误消息。</param>
    /// <param name="data">错误附加数据。</param>
    /// <returns>错误对象。</returns>
    public static DevHubCalleeError Create(int code, string message, object? data = null)
    {
        return new DevHubCalleeError
        {
            Code = code,
            Message = message,
            Data = data is null ? null : JsonSerializer.SerializeToElement(data)
        };
    }
}
