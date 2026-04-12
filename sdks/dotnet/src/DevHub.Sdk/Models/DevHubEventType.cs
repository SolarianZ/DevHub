using Newtonsoft.Json;

namespace DevHub.Sdk.Models;

/// <summary>
/// DevHub 协议定义的闭集事件类型。
/// </summary>
[JsonConverter(typeof(DevHubEventTypeJsonConverter))]
public readonly struct DevHubEventType : IEquatable<DevHubEventType>
{
    private static readonly IReadOnlyDictionary<string, DevHubEventType> KnownTypes = new Dictionary<string, DevHubEventType>(StringComparer.Ordinal)
    {
        ["app.definition.upserted"] = new("app.definition.upserted"),
        ["app.definition.deleted"] = new("app.definition.deleted"),
        ["app.instance.registered"] = new("app.instance.registered"),
        ["app.instance.unregistered"] = new("app.instance.unregistered"),
        ["invocation.queued"] = new("invocation.queued"),
        ["invocation.delivered"] = new("invocation.delivered"),
        ["invocation.completed"] = new("invocation.completed"),
        ["invocation.failed"] = new("invocation.failed")
    };

    private readonly string? _value;

    private DevHubEventType(string value)
    {
        _value = value;
    }

    /// <summary>
    /// 应用定义已新增或更新。
    /// </summary>
    public static DevHubEventType AppDefinitionUpserted => KnownTypes["app.definition.upserted"];

    /// <summary>
    /// 应用定义已删除。
    /// </summary>
    public static DevHubEventType AppDefinitionDeleted => KnownTypes["app.definition.deleted"];

    /// <summary>
    /// 应用实例已注册。
    /// </summary>
    public static DevHubEventType AppInstanceRegistered => KnownTypes["app.instance.registered"];

    /// <summary>
    /// 应用实例已注销。
    /// </summary>
    public static DevHubEventType AppInstanceUnregistered => KnownTypes["app.instance.unregistered"];

    /// <summary>
    /// 调用已入队。
    /// </summary>
    public static DevHubEventType InvocationQueued => KnownTypes["invocation.queued"];

    /// <summary>
    /// 调用已投递。
    /// </summary>
    public static DevHubEventType InvocationDelivered => KnownTypes["invocation.delivered"];

    /// <summary>
    /// 调用已完成。
    /// </summary>
    public static DevHubEventType InvocationCompleted => KnownTypes["invocation.completed"];

    /// <summary>
    /// 调用已失败。
    /// </summary>
    public static DevHubEventType InvocationFailed => KnownTypes["invocation.failed"];

    /// <summary>
    /// 所有受支持事件类型。
    /// </summary>
    public static IReadOnlyList<DevHubEventType> All { get; } =
    [
        AppDefinitionUpserted,
        AppDefinitionDeleted,
        AppInstanceRegistered,
        AppInstanceUnregistered,
        InvocationQueued,
        InvocationDelivered,
        InvocationCompleted,
        InvocationFailed
    ];

    /// <summary>
    /// 事件类型字符串值。
    /// </summary>
    public string Value => _value ?? string.Empty;

    /// <summary>
    /// 当前值是否为受支持事件类型。
    /// </summary>
    public bool IsSupported => !string.IsNullOrWhiteSpace(_value) && KnownTypes.ContainsKey(_value!);

    /// <summary>
    /// 解析事件类型。
    /// </summary>
    /// <param name="value">事件类型字符串。</param>
    /// <returns>事件类型值对象。</returns>
    public static DevHubEventType Parse(string value)
    {
        if (TryParse(value, out var eventType))
        {
            return eventType;
        }

        throw new ArgumentException("value 必须是受支持的 DevHub 事件类型。", nameof(value));
    }

    /// <summary>
    /// 尝试解析事件类型。
    /// </summary>
    /// <param name="value">事件类型字符串。</param>
    /// <param name="eventType">解析结果。</param>
    /// <returns>是否解析成功。</returns>
    public static bool TryParse(string? value, out DevHubEventType eventType)
    {
        if (value is not null && KnownTypes.TryGetValue(value, out eventType))
        {
            return true;
        }

        eventType = default;
        return false;
    }

    /// <inheritdoc />
    public override string ToString()
    {
        return Value;
    }

    /// <inheritdoc />
    public bool Equals(DevHubEventType other)
    {
        return string.Equals(Value, other.Value, StringComparison.Ordinal);
    }

    /// <inheritdoc />
    public override bool Equals(object? obj)
    {
        return obj is DevHubEventType other && Equals(other);
    }

    /// <inheritdoc />
    public override int GetHashCode()
    {
        return StringComparer.Ordinal.GetHashCode(Value);
    }

    /// <summary>
    /// 将事件类型转换为协议字符串值。
    /// </summary>
    /// <param name="eventType">事件类型。</param>
    public static implicit operator string(DevHubEventType eventType)
    {
        return eventType.Value;
    }

    /// <summary>
    /// 比较两个事件类型是否相等。
    /// </summary>
    public static bool operator ==(DevHubEventType left, DevHubEventType right)
    {
        return left.Equals(right);
    }

    /// <summary>
    /// 比较两个事件类型是否不相等。
    /// </summary>
    public static bool operator !=(DevHubEventType left, DevHubEventType right)
    {
        return !left.Equals(right);
    }
}

/// <summary>
/// <see cref="DevHubEventType" /> 的 JSON 转换器。
/// </summary>
public sealed class DevHubEventTypeJsonConverter : JsonConverter
{
    /// <inheritdoc />
    public override bool CanConvert(Type objectType)
    {
        return objectType == typeof(DevHubEventType);
    }

    /// <inheritdoc />
    public override object ReadJson(JsonReader reader, Type objectType, object? existingValue, JsonSerializer serializer)
    {
        var value = reader.Value as string;
        if (DevHubEventType.TryParse(value, out var eventType))
        {
            return eventType;
        }

        throw new JsonSerializationException("hub.event.params.type 必须是受支持的 DevHub 事件类型。");
    }

    /// <inheritdoc />
    public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer)
    {
        if (value is not DevHubEventType eventType || !eventType.IsSupported)
        {
            throw new JsonSerializationException("事件类型必须是受支持的 DevHub 事件类型。");
        }

        writer.WriteValue(eventType.Value);
    }
}
