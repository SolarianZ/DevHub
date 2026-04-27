using DevHub.Sdk.Internal;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DevHub.Sdk.Models;

/// <summary>
/// Ping 结果。
/// </summary>
public sealed class PingResult
{
    /// <summary>
    /// 是否成功。
    /// </summary>
    [JsonProperty("ok")]
    public bool Ok { get; set; }

    /// <summary>
    /// 服务端时间。
    /// </summary>
    [JsonProperty("serverTimeUtc")]
    public DateTimeOffset ServerTimeUtc { get; set; }

    /// <summary>
    /// 回显数据。
    /// </summary>
    [JsonProperty("echo")]
    [JsonConverter(typeof(NullableJTokenJsonConverter))]
    public JToken? Echo { get; set; }
}

/// <summary>
/// Definition 列表请求。
/// </summary>
public sealed class ListDefinitionsRequest
{
    private string? _appId;
    private string? _scope;

    /// <summary>
    /// 应用标识过滤。
    /// </summary>
    public string? AppId
    {
        get => _appId;
        set => _appId = ProtocolIdentifier.EnsureOptionalAppId(value, nameof(AppId));
    }

    /// <summary>
    /// 作用域过滤；<see langword="null"/> 表示不按 scope 过滤。
    /// </summary>
    public string? Scope
    {
        get => _scope;
        set => _scope = ScopeContract.EnsureScopeFilter(value, nameof(Scope));
    }
}

/// <summary>
/// Launch 请求。
/// </summary>
public sealed class LaunchRequest
{
    private string _appId = string.Empty;
    private string _scope = string.Empty;

    /// <summary>
    /// 应用标识。
    /// </summary>
    public string AppId
    {
        get => _appId;
        set => _appId = ProtocolIdentifier.EnsureAppId(value, nameof(AppId));
    }

    /// <summary>
    /// 作用域。空字符串表示 Global Definition。
    /// </summary>
    public string Scope
    {
        get => _scope;
        set => _scope = ScopeContract.EnsureScopedString(value ?? string.Empty, nameof(Scope));
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
    [JsonProperty("ok")]
    public bool Ok { get; set; }

    /// <summary>
    /// 启动状态。
    /// </summary>
    [JsonProperty("status")]
    public string Status { get; set; } = string.Empty;

    /// <summary>
    /// 关联进程标识。
    /// </summary>
    [JsonProperty("pid")]
    public int? Pid { get; set; }

    /// <summary>
    /// 启动请求标识。
    /// </summary>
    [JsonProperty("launchId")]
    public string LaunchId { get; set; } = string.Empty;
}

/// <summary>
/// ListInstances 请求。
/// </summary>
public sealed class ListInstancesRequest
{
    private string? _appId;
    private string? _scope;

    /// <summary>
    /// 应用标识过滤。
    /// </summary>
    public string? AppId
    {
        get => _appId;
        set => _appId = ProtocolIdentifier.EnsureOptionalAppId(value, nameof(AppId));
    }

    /// <summary>
    /// 作用域过滤；<see langword="null"/> 表示不按 scope 过滤。
    /// </summary>
    public string? Scope
    {
        get => _scope;
        set => _scope = ScopeContract.EnsureScopeFilter(value, nameof(Scope));
    }

    /// <summary>
    /// 是否包含离线实例。
    /// </summary>
    public bool IncludeOffline { get; set; }
}

/// <summary>
/// 已放弃请求过滤器。
/// </summary>
public sealed class AbandonedRequestFilter
{
    private string? _appId;

    /// <summary>
    /// 仅匹配已放弃时长达到该阈值的记录。
    /// </summary>
    public TimeSpan? OlderThan { get; set; }

    /// <summary>
    /// 仅匹配已记录到相同应用标识的请求。
    /// </summary>
    public string? AppId
    {
        get => _appId;
        set => _appId = ProtocolIdentifier.EnsureOptionalAppId(value, nameof(AppId));
    }

    /// <summary>
    /// 仅匹配相同 JSON-RPC 方法名的请求。
    /// </summary>
    public string? Method { get; set; }
}

/// <summary>
/// 调用请求。
/// </summary>
public sealed class InvokeRequest
{
    private string _appId = string.Empty;

    /// <summary>
    /// 目标应用标识。
    /// </summary>
    public string AppId
    {
        get => _appId;
        set => _appId = ProtocolIdentifier.EnsureAppId(value, nameof(AppId));
    }

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
    [JsonProperty("ok")]
    public bool Ok { get; set; }

    /// <summary>
    /// 调用标识。
    /// </summary>
    [JsonProperty("invocationId")]
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
    [JsonProperty("ok")]
    public bool Ok { get; set; }

    /// <summary>
    /// 调用标识。
    /// </summary>
    [JsonProperty("invocationId")]
    public string InvocationId { get; set; } = string.Empty;

    /// <summary>
    /// 被调用方返回值。
    /// </summary>
    [JsonProperty("value")]
    [JsonConverter(typeof(NullableJTokenJsonConverter))]
    public JToken? Value { get; set; }
}

/// <summary>
/// Poll 请求。
/// </summary>
public sealed class PollRequest
{
    private string _instanceId = string.Empty;

    /// <summary>
    /// 实例标识。
    /// </summary>
    public string InstanceId
    {
        get => _instanceId;
        set => _instanceId = ProtocolIdentifier.EnsureInstanceId(value, nameof(InstanceId));
    }

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
    [JsonProperty("ok")]
    public bool Ok { get; set; }

    /// <summary>
    /// 服务端时间。
    /// </summary>
    [JsonProperty("serverTimeUtc")]
    public DateTimeOffset ServerTimeUtc { get; set; }

    /// <summary>
    /// 拉取到的调用条目。
    /// </summary>
    [JsonProperty("items")]
    public List<Invocation> Items { get; set; } = [];
}

/// <summary>
/// Respond 请求。
/// </summary>
public sealed class RespondRequest
{
    private string _instanceId = string.Empty;
    private object? _value;

    /// <summary>
    /// 实例标识。
    /// </summary>
    public string InstanceId
    {
        get => _instanceId;
        set => _instanceId = ProtocolIdentifier.EnsureInstanceId(value, nameof(InstanceId));
    }

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
    private string _appId = string.Empty;

    /// <summary>
    /// 调用标识。
    /// </summary>
    [JsonProperty("invocationId")]
    public string InvocationId { get; set; } = string.Empty;

    /// <summary>
    /// 应用标识。
    /// </summary>
    [JsonProperty("appId")]
    public string AppId
    {
        get => _appId;
        set => _appId = ProtocolIdentifier.EnsureAppId(value, nameof(AppId));
    }

    /// <summary>
    /// 调用目标。
    /// </summary>
    [JsonProperty("target")]
    public InvocationTarget? Target { get; set; }

    /// <summary>
    /// 方法名。
    /// </summary>
    [JsonProperty("method")]
    public string Method { get; set; } = string.Empty;

    /// <summary>
    /// 调用参数。
    /// </summary>
    [JsonProperty("args")]
    [JsonConverter(typeof(NullableJTokenJsonConverter))]
    public JToken? Args { get; set; }

    /// <summary>
    /// 调用类型。
    /// </summary>
    [JsonProperty("kind")]
    public InvocationKind Kind { get; set; }

    /// <summary>
    /// 创建时间。
    /// </summary>
    [JsonProperty("createdAtUtc")]
    public DateTimeOffset CreatedAtUtc { get; set; }

    /// <summary>
    /// 调用选项。
    /// </summary>
    [JsonProperty("options")]
    public InvocationOptions? Options { get; set; }

    /// <summary>
    /// 投递信息。
    /// </summary>
    [JsonProperty("delivery")]
    public InvocationDelivery? Delivery { get; set; }

    /// <summary>
    /// 调用方信息。
    /// </summary>
    [JsonProperty("caller")]
    public InvocationCaller? Caller { get; set; }
}

/// <summary>
/// 调用目标。
/// </summary>
public sealed class InvocationTarget
{
    private string _scope = string.Empty;
    private string? _instanceId;

    /// <summary>
    /// 目标作用域。空字符串表示 Global。
    /// </summary>
    [JsonProperty("scope")]
    public string Scope
    {
        get => _scope;
        set => _scope = ScopeContract.EnsureScopedString(value ?? string.Empty, nameof(Scope));
    }

    /// <summary>
    /// 目标实例标识。
    /// </summary>
    [JsonProperty("instanceId", NullValueHandling = NullValueHandling.Ignore)]
    public string? InstanceId
    {
        get => _instanceId;
        set => _instanceId = ProtocolIdentifier.EnsureOptionalInstanceId(value, nameof(InstanceId));
    }
}

/// <summary>
/// 调用选项。
/// </summary>
public sealed class InvocationOptions
{
    /// <summary>
    /// 生存时间，单位毫秒。
    /// </summary>
    [JsonProperty("ttlMs", NullValueHandling = NullValueHandling.Ignore)]
    public int? TtlMs { get; set; }

    /// <summary>
    /// 请求等待超时，单位毫秒。
    /// </summary>
    [JsonProperty("waitTimeoutMs", NullValueHandling = NullValueHandling.Ignore)]
    public int? WaitTimeoutMs { get; set; }

    /// <summary>
    /// 目标离线时是否允许入队。
    /// </summary>
    [JsonProperty("queueIfOffline", NullValueHandling = NullValueHandling.Ignore)]
    public bool? QueueIfOffline { get; set; }

    /// <summary>
    /// 目标离线时是否允许自动拉起。
    /// </summary>
    [JsonProperty("autoLaunch", NullValueHandling = NullValueHandling.Ignore)]
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
    [JsonProperty("leaseSeconds")]
    public int LeaseSeconds { get; set; }

    /// <summary>
    /// 当前投递尝试次数。
    /// </summary>
    [JsonProperty("attempt")]
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
    [JsonProperty("clientId")]
    public string ClientId { get; set; } = string.Empty;

    /// <summary>
    /// 调用方客户端会话标识。
    /// </summary>
    [JsonProperty("clientSessionId")]
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
public sealed class InvocationKindJsonConverter : JsonConverter
{
    /// <summary>
    /// 初始化转换器。
    /// </summary>
    public override bool CanConvert(Type objectType)
    {
        return objectType == typeof(InvocationKind);
    }

    /// <inheritdoc />
    public override object ReadJson(JsonReader reader, Type objectType, object? existingValue, JsonSerializer serializer)
    {
        var value = reader.Value as string;
        return value switch
        {
            "request" => InvocationKind.Request,
            "notify" => InvocationKind.Notify,
            _ => throw new JsonSerializationException("Invocation.kind 必须为 request 或 notify。")
        };
    }

    /// <inheritdoc />
    public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer)
    {
        if (value is not InvocationKind kind)
        {
            throw new JsonSerializationException("Invocation.kind 类型非法。");
        }

        writer.WriteValue(kind == InvocationKind.Request ? "request" : "notify");
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
    [JsonProperty("subscriptionId")]
    public string SubscriptionId { get; set; } = string.Empty;

    /// <summary>
    /// 事件类型。
    /// </summary>
    [JsonProperty("type")]
    public DevHubEventType Type { get; set; }

    /// <summary>
    /// 事件发生时间。
    /// </summary>
    [JsonProperty("timeUtc")]
    public DateTimeOffset TimeUtc { get; set; }

    /// <summary>
    /// 事件载荷。
    /// </summary>
    [JsonProperty("payload")]
    [JsonConverter(typeof(NullableJTokenJsonConverter))]
    public JToken? Payload { get; set; }
}

/// <summary>
/// 被调用方错误对象。
/// </summary>
public sealed class DevHubCalleeError
{
    /// <summary>
    /// 错误码。
    /// </summary>
    [JsonProperty("code")]
    public int Code { get; set; }

    /// <summary>
    /// 错误消息。
    /// </summary>
    [JsonProperty("message")]
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// 错误附加数据。
    /// </summary>
    [JsonProperty("data", NullValueHandling = NullValueHandling.Ignore)]
    [JsonConverter(typeof(NullableJTokenJsonConverter))]
    public JToken? Data { get; set; }

    /// <summary>
    /// 使用任意 JSON 数据构造错误对象。
    /// </summary>
    /// <param name="code">错误码。</param>
    /// <param name="message">错误消息。</param>
    /// <param name="data">错误附加数据。</param>
    /// <returns>错误对象。</returns>
    public static DevHubCalleeError Create(int code, string message, object? data = null)
    {
        JToken? serializedData = null;
        if (data is not null)
        {
            var jsonData = DevHubJson.SerializeToToken(data);
            if (jsonData.Type != JTokenType.Object)
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
