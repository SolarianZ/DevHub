using DevHub.Sdk.Internal;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DevHub.Sdk.Models;

/// <summary>
/// 应用定义。
/// </summary>
public sealed class AppDefinition
{
    /// <summary>
    /// 应用标识。
    /// </summary>
    [JsonProperty("appId")]
    public string AppId { get; set; } = string.Empty;

    /// <summary>
    /// 显示名称。
    /// </summary>
    [JsonProperty("displayName")]
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// 应用描述。
    /// </summary>
    [JsonProperty("description")]
    public string? Description { get; set; }

    /// <summary>
    /// 能力声明。
    /// </summary>
    [JsonProperty("capabilities")]
    public AppCapabilities? Capabilities { get; set; }

    /// <summary>
    /// 启动配置。
    /// </summary>
    [JsonProperty("launch")]
    public LaunchConfiguration? Launch { get; set; }
}

/// <summary>
/// 应用能力声明。
/// </summary>
public sealed class AppCapabilities
{
    /// <summary>
    /// 是否允许 RPC。
    /// </summary>
    [JsonProperty("rpc")]
    public bool? Rpc { get; set; }

    /// <summary>
    /// 是否声明事件能力。
    /// </summary>
    [JsonProperty("events")]
    public bool? Events { get; set; }
}

/// <summary>
/// 启动配置。
/// </summary>
public sealed class LaunchConfiguration
{
    /// <summary>
    /// 可执行文件路径。
    /// </summary>
    [JsonProperty("exePath")]
    public string? ExePath { get; set; }

    /// <summary>
    /// 参数模板。
    /// </summary>
    [JsonProperty("argsTemplate")]
    public string? ArgsTemplate { get; set; }

    /// <summary>
    /// 工作目录。
    /// </summary>
    [JsonProperty("workingDirectory")]
    public string? WorkingDirectory { get; set; }

    /// <summary>
    /// 去重键模板。
    /// </summary>
    [JsonProperty("dedupeKeyTemplate")]
    public string? DedupeKeyTemplate { get; set; }
}

/// <summary>
/// 应用实例。
/// </summary>
public sealed class AppInstance
{
    /// <summary>
    /// 实例标识。
    /// </summary>
    [JsonProperty("instanceId")]
    public string InstanceId { get; set; } = string.Empty;

    /// <summary>
    /// 应用标识。
    /// </summary>
    [JsonProperty("appId")]
    public string AppId { get; set; } = string.Empty;

    /// <summary>
    /// 作用域。
    /// </summary>
    [JsonProperty("scope")]
    public string? Scope { get; set; }

    /// <summary>
    /// 进程标识。
    /// </summary>
    [JsonProperty("pid")]
    public int? Pid { get; set; }

    /// <summary>
    /// 注册时间。
    /// </summary>
    [JsonProperty("registeredAtUtc")]
    public DateTimeOffset? RegisteredAtUtc { get; set; }

    /// <summary>
    /// 最后在线时间。
    /// </summary>
    [JsonProperty("lastSeenUtc")]
    public DateTimeOffset? LastSeenUtc { get; set; }

    /// <summary>
    /// 调用能力。
    /// </summary>
    [JsonProperty("invoke")]
    public InvokeCapability? Invoke { get; set; }

    /// <summary>
    /// 元数据。
    /// </summary>
    [JsonProperty("meta")]
    [JsonConverter(typeof(NullableJTokenJsonConverter))]
    public JToken? Meta { get; set; }
}

/// <summary>
/// 实例注册载荷。
/// </summary>
public sealed class AppInstanceRegistration
{
    /// <summary>
    /// 实例标识。
    /// </summary>
    [JsonProperty("instanceId")]
    public string InstanceId { get; set; } = string.Empty;

    /// <summary>
    /// 应用标识。
    /// </summary>
    [JsonProperty("appId")]
    public string AppId { get; set; } = string.Empty;

    /// <summary>
    /// 作用域。
    /// </summary>
    [JsonProperty("scope")]
    public string? Scope { get; set; }

    /// <summary>
    /// 进程标识。
    /// </summary>
    [JsonProperty("pid")]
    public int Pid { get; set; }

    /// <summary>
    /// 调用能力。
    /// </summary>
    [JsonProperty("invoke")]
    public InvokeCapability Invoke { get; set; } = new();

    /// <summary>
    /// 元数据。
    /// </summary>
    [JsonProperty("meta", NullValueHandling = NullValueHandling.Ignore)]
    public object? Meta { get; set; }
}

/// <summary>
/// 调用能力。
/// </summary>
public sealed class InvokeCapability
{
    /// <summary>
    /// 是否支持轮询。
    /// </summary>
    [JsonProperty("poll")]
    public bool Poll { get; set; }

    /// <summary>
    /// 是否支持响应。
    /// </summary>
    [JsonProperty("respond")]
    public bool Respond { get; set; }
}

/// <summary>
/// Hub 运行时发现文件。
/// </summary>
public sealed class HubRuntime
{
    /// <summary>
    /// 协议版本。
    /// </summary>
    [JsonProperty("protocolVersion")]
    public int ProtocolVersion { get; set; }

    /// <summary>
    /// Hub 版本。
    /// </summary>
    [JsonProperty("hubVersion")]
    public string? HubVersion { get; set; }

    /// <summary>
    /// 进程标识。
    /// </summary>
    [JsonProperty("pid")]
    public int Pid { get; set; }

    /// <summary>
    /// HTTP 基础地址。
    /// </summary>
    [JsonProperty("httpBaseUrl")]
    public string HttpBaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// WebSocket 地址。
    /// </summary>
    [JsonProperty("wsUrl")]
    public string WsUrl { get; set; } = string.Empty;

    /// <summary>
    /// 令牌文件路径。
    /// </summary>
    [JsonProperty("tokenFile")]
    public string TokenFile { get; set; } = string.Empty;

    /// <summary>
    /// 启动时间。
    /// </summary>
    [JsonProperty("startedAtUtc")]
    public DateTimeOffset StartedAtUtc { get; set; }

    /// <summary>
    /// 运行时调优参数。
    /// </summary>
    [JsonProperty("runtimeTuning")]
    public HubRuntimeTuning RuntimeTuning { get; set; } = new();
}

/// <summary>
/// Hub 运行时调优参数。
/// </summary>
public sealed class HubRuntimeTuning
{
    /// <summary>
    /// 调用租约秒数。
    /// </summary>
    [JsonProperty("leaseSeconds")]
    public int LeaseSeconds { get; set; }

    /// <summary>
    /// 在线阈值秒数。
    /// </summary>
    [JsonProperty("onlineThresholdSeconds")]
    public int OnlineThresholdSeconds { get; set; }

    /// <summary>
    /// 启动去重窗口秒数。
    /// </summary>
    [JsonProperty("launchDedupeWindowSeconds")]
    public int LaunchDedupeWindowSeconds { get; set; }
}
