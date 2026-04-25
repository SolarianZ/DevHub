using System.Text.Json;
using System.Text.Json.Serialization;
using DevHub.Sdk.Internal;

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
/// Definition 列表请求。
/// </summary>
public sealed class ListDefinitionsRequest
{
    private string? _scope;

    /// <summary>
    /// 应用标识过滤。
    /// </summary>
    public string? AppId { get; set; }

    /// <summary>
    /// 作用域过滤；<see langword="null"/> 表示不按 scope 过滤。
    /// </summary>
    public string? Scope
    {
        get => _scope;
        set
        {
            _scope = ScopeContract.EnsureScopeFilter(value, nameof(Scope));
        }
    }
}

/// <summary>
/// Launch 请求。
/// </summary>
public sealed class LaunchRequest
{
    private string? _scope;

    /// <summary>
    /// 应用标识。
    /// </summary>
    public string AppId { get; set; } = string.Empty;

    /// <summary>
    /// 作用域。空字符串表示 Global Definition。
    /// </summary>
    public string Scope
    {
        get => ScopeContract.EnsureAssignedScope(_scope, nameof(LaunchRequest));
        set
        {
            _scope = ScopeContract.EnsureScopedString(value, nameof(Scope));
        }
    }

    /// <summary>
    /// 启动去重键。
    /// </summary>
    public string? DedupeKey { get; set; }

    /// <summary>
    /// 等待实例注册完成的最长时长，单位毫秒。
    /// </summary>
    public int? WaitForRegisterMs { get; set; }
}

/// <summary>
/// Launch 结果。
/// </summary>
public sealed class LaunchResult
{
    /// <summary>
    /// 是否成功。
    /// </summary>
    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    /// <summary>
    /// 启动状态。
    /// </summary>
    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    /// <summary>
    /// 关联进程标识。
    /// </summary>
    [JsonPropertyName("pid")]
    public int? Pid { get; set; }

    /// <summary>
    /// 启动请求标识。
    /// </summary>
    [JsonPropertyName("launchId")]
    public string LaunchId { get; set; } = string.Empty;
}

/// <summary>
/// ListInstances 请求。
/// </summary>
public sealed class ListInstancesRequest
{
    private string? _scope;

    /// <summary>
    /// 应用标识过滤。
    /// </summary>
    public string? AppId { get; set; }

    /// <summary>
    /// 作用域过滤；<see langword="null"/> 表示不按 scope 过滤。
    /// </summary>
    public string? Scope
    {
        get => _scope;
        set
        {
            _scope = ScopeContract.EnsureScopeFilter(value, nameof(Scope));
        }
    }

    /// <summary>
    /// 是否包含离线实例。
    /// </summary>
    public bool IncludeOffline { get; set; }
}

/// <summary>
/// 调用请求。
/// </summary>
public sealed class InvokeRequest
{
    /// <summary>
    /// 目标应用标识。
    /// </summary>
    public string AppId { get; set; } = string.Empty;

    /// <summary>
    /// 调用目标。
    /// </summary>
    public InvocationTarget? Target { get; set; }

    /// <summary>
    /// 调用方法名。
    /// </summary>
    public string Method { get; set; } = string.Empty;

    /// <summary>
    /// 调用参数。
    /// </summary>
    public object? Args { get; set; }

    /// <summary>
    /// 调用选项。
    /// </summary>
    public InvocationOptions? Options { get; set; }
}

/// <summary>
/// Notify 结果。
/// </summary>
public sealed class NotifyResult
{
    /// <summary>
    /// 是否成功。
    /// </summary>
    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    /// <summary>
    /// 调用标识。
    /// </summary>
    [JsonPropertyName("invocationId")]
    public string InvocationId { get; set; } = string.Empty;
}

/// <summary>
/// Request 结果。
/// </summary>
public sealed class RequestResult
{
    /// <summary>
    /// 是否成功。
    /// </summary>
    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    /// <summary>
    /// 调用标识。
    /// </summary>
    [JsonPropertyName("invocationId")]
    public string InvocationId { get; set; } = string.Empty;

    /// <summary>
    /// 被调用方返回值。
    /// </summary>
    [JsonPropertyName("value")]
    public JsonElement? Value { get; set; }
}

/// <summary>
/// Poll 请求。
/// </summary>
public sealed class PollRequest
{
    /// <summary>
    /// 实例标识。
    /// </summary>
    public string InstanceId { get; set; } = string.Empty;

    /// <summary>
    /// 实例会话令牌。
    /// </summary>
    public string InstanceSessionToken { get; set; } = string.Empty;

    /// <summary>
    /// 单次最多拉取条数。
    /// </summary>
    public int? MaxCount { get; set; }

    /// <summary>
    /// 最长等待时长，单位毫秒。
    /// </summary>
    public int? WaitMs { get; set; }
}

/// <summary>
/// Poll 结果。
/// </summary>
public sealed class PollResult
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
    /// 拉取到的调用条目。
    /// </summary>
    [JsonPropertyName("items")]
    public List<Invocation> Items { get; set; } = [];
}

/// <summary>
/// Respond 请求。
/// </summary>
public sealed class RespondRequest
{
    private object? _value;

    /// <summary>
    /// 实例标识。
    /// </summary>
    public string InstanceId { get; set; } = string.Empty;

    /// <summary>
    /// 实例会话令牌。
    /// </summary>
    public string InstanceSessionToken { get; set; } = string.Empty;

    /// <summary>
    /// 调用标识。
    /// </summary>
    public string InvocationId { get; set; } = string.Empty;

    /// <summary>
    /// 成功返回值。
    /// </summary>
    public object? Value
    {
        get => _value;
        set
        {
            _value = value;
            HasValue = true;
        }
    }

    /// <summary>
    /// 失败错误对象。
    /// </summary>
    public DevHubCalleeError? Error { get; set; }

    internal bool HasValue { get; private set; }
}

/// <summary>
/// 调用对象。
/// </summary>
public sealed class Invocation
{
    /// <summary>
    /// 调用标识。
    /// </summary>
    [JsonPropertyName("invocationId")]
    public string InvocationId { get; set; } = string.Empty;

    /// <summary>
    /// 应用标识。
    /// </summary>
    [JsonPropertyName("appId")]
    public string AppId { get; set; } = string.Empty;

    /// <summary>
    /// 调用目标。
    /// </summary>
    [JsonPropertyName("target")]
    public InvocationTarget? Target { get; set; }

    /// <summary>
    /// 方法名。
    /// </summary>
    [JsonPropertyName("method")]
    public string Method { get; set; } = string.Empty;

    /// <summary>
    /// 调用参数。
    /// </summary>
    [JsonPropertyName("args")]
    public JsonElement? Args { get; set; }

    /// <summary>
    /// 调用类型。
    /// </summary>
    [JsonPropertyName("kind")]
    public InvocationKind Kind { get; set; }

    /// <summary>
    /// 创建时间。
    /// </summary>
    [JsonPropertyName("createdAtUtc")]
    public DateTimeOffset CreatedAtUtc { get; set; }

    /// <summary>
    /// 调用选项。
    /// </summary>
    [JsonPropertyName("options")]
    public InvocationOptions? Options { get; set; }

    /// <summary>
    /// 投递信息。
    /// </summary>
    [JsonPropertyName("delivery")]
    public InvocationDelivery? Delivery { get; set; }

    /// <summary>
    /// 调用方信息。
    /// </summary>
    [JsonPropertyName("caller")]
    public InvocationCaller? Caller { get; set; }
}

/// <summary>
/// 调用目标。
/// </summary>
public sealed class InvocationTarget
{
    private string? _scope;

    /// <summary>
    /// 目标作用域。空字符串表示 Global。
    /// </summary>
    [JsonPropertyName("scope")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string Scope
    {
        get => ScopeContract.EnsureAssignedScope(_scope, nameof(InvocationTarget));
        set
        {
            _scope = ScopeContract.EnsureScopedString(value, nameof(Scope));
        }
    }

    /// <summary>
    /// 目标实例标识。
    /// </summary>
    [JsonPropertyName("instanceId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? InstanceId { get; set; }
}

/// <summary>
/// 调用选项。
/// </summary>
public sealed class InvocationOptions
{
    /// <summary>
    /// 生存时间，单位毫秒。
    /// </summary>
    [JsonPropertyName("ttlMs")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? TtlMs { get; set; }

    /// <summary>
    /// 请求等待超时，单位毫秒。
    /// </summary>
    [JsonPropertyName("waitTimeoutMs")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? WaitTimeoutMs { get; set; }

    /// <summary>
    /// 目标离线时是否允许入队。
    /// </summary>
    [JsonPropertyName("queueIfOffline")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? QueueIfOffline { get; set; }

    /// <summary>
    /// 目标离线时是否允许自动拉起。
    /// </summary>
    [JsonPropertyName("autoLaunch")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? AutoLaunch { get; set; }
}

/// <summary>
/// 投递信息。
/// </summary>
public sealed class InvocationDelivery
{
    /// <summary>
    /// 当前租约时长，单位秒。
    /// </summary>
    [JsonPropertyName("leaseSeconds")]
    public int LeaseSeconds { get; set; }

    /// <summary>
    /// 当前投递尝试次数。
    /// </summary>
    [JsonPropertyName("attempt")]
    public int Attempt { get; set; }
}

/// <summary>
/// 调用方信息。
/// </summary>
public sealed class InvocationCaller
{
    /// <summary>
    /// 调用方客户端标识。
    /// </summary>
    [JsonPropertyName("clientId")]
    public string ClientId { get; set; } = string.Empty;

    /// <summary>
    /// 调用方客户端会话标识。
    /// </summary>
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
    /// <summary>
    /// 订阅标识。
    /// </summary>
    [JsonPropertyName("subscriptionId")]
    public string SubscriptionId { get; set; } = string.Empty;

    /// <summary>
    /// 事件类型。
    /// </summary>
    [JsonPropertyName("type")]
    public DevHubEventType Type { get; set; }

    /// <summary>
    /// 事件发生时间。
    /// </summary>
    [JsonPropertyName("timeUtc")]
    public DateTimeOffset TimeUtc { get; set; }

    /// <summary>
    /// 事件载荷。
    /// </summary>
    [JsonPropertyName("payload")]
    public JsonElement? Payload { get; set; }
}

/// <summary>
/// 被调用方错误对象。
/// </summary>
public sealed class DevHubCalleeError
{
    /// <summary>
    /// 错误码。
    /// </summary>
    [JsonPropertyName("code")]
    public int Code { get; set; }

    /// <summary>
    /// 错误消息。
    /// </summary>
    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// 错误附加数据。
    /// </summary>
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
        JsonElement? serializedData = null;
        if (data is not null)
        {
            var jsonData = JsonSerializer.SerializeToElement(data, DevHubJson.SerializerOptions);
            if (jsonData.ValueKind != JsonValueKind.Object)
            {
                throw new ArgumentException("data 必须序列化为 JSON 对象。", nameof(data));
            }

            serializedData = jsonData;
        }

        return new DevHubCalleeError
        {
            Code = code,
            Message = message,
            Data = serializedData
        };
    }
}
