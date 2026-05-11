using DevHub.Sdk.Internal;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DevHub.Sdk.Models;

/// <summary>
/// 应用定义。
/// </summary>
public sealed class AppDefinition
{
    private string _appId = string.Empty;
    private string _scope = string.Empty;
    private string? _displayName;

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
    /// Definition 作用域。空字符串表示 Global Definition。
    /// </summary>
    [JsonProperty("scope")]
    public string Scope
    {
        get => _scope;
        set => _scope = ScopeContract.EnsureScopedString(value ?? string.Empty, nameof(Scope));
    }

    /// <summary>
    /// 显示名称。
    /// </summary>
    [JsonProperty("displayName")]
    public string DisplayName
    {
        get => EnsureNonWhitespaceString(_displayName, nameof(DisplayName));
        set => _displayName = EnsureNonWhitespaceString(value, nameof(DisplayName));
    }

    /// <summary>
    /// 应用描述。
    /// </summary>
    [JsonProperty("description", NullValueHandling = NullValueHandling.Ignore)]
    public string? Description { get; set; }

    /// <summary>
    /// 能力声明。
    /// </summary>
    [JsonProperty("capabilities", NullValueHandling = NullValueHandling.Ignore)]
    public AppCapabilities? Capabilities { get; set; }

    /// <summary>
    /// 启动配置。
    /// </summary>
    [JsonProperty("launch", NullValueHandling = NullValueHandling.Ignore)]
    public LaunchConfiguration? Launch { get; set; }

    private static string EnsureNonWhitespaceString(string? value, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"{propertyName} 不能为空白字符串。", propertyName);
        }

        return value;
    }
}

/// <summary>
/// 应用能力声明。
/// </summary>
public sealed class AppCapabilities
{
    /// <summary>
    /// 是否允许 RPC。
    /// </summary>
    [JsonProperty("rpc", NullValueHandling = NullValueHandling.Ignore)]
    public bool? Rpc { get; set; }

    /// <summary>
    /// 是否声明事件能力。
    /// </summary>
    [JsonProperty("events", NullValueHandling = NullValueHandling.Ignore)]
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
    [JsonProperty("exePath", NullValueHandling = NullValueHandling.Ignore)]
    public string? ExePath { get; set; }

    /// <summary>
    /// 结构化启动参数。
    /// </summary>
    [JsonProperty("args", NullValueHandling = NullValueHandling.Ignore)]
    public List<string>? Args { get; set; }

    /// <summary>
    /// 参数模板。
    /// </summary>
    [JsonProperty("argsTemplate", NullValueHandling = NullValueHandling.Ignore)]
    public string? ArgsTemplate { get; set; }

    /// <summary>
    /// 工作目录。
    /// </summary>
    [JsonProperty("workingDirectory", NullValueHandling = NullValueHandling.Ignore)]
    public string? WorkingDirectory { get; set; }

    /// <summary>
    /// 去重键模板。
    /// </summary>
    [JsonProperty("dedupeKeyTemplate", NullValueHandling = NullValueHandling.Ignore)]
    public string? DedupeKeyTemplate { get; set; }
}

/// <summary>
/// 定义校验问题。
/// </summary>
public sealed class ValidationIssue
{
    /// <summary>
    /// 出错字段路径。
    /// </summary>
    [JsonProperty("path")]
    public string Path { get; set; } = string.Empty;

    /// <summary>
    /// 机器可读错误码。
    /// </summary>
    [JsonProperty("code")]
    public string Code { get; set; } = string.Empty;

    /// <summary>
    /// 人类可读错误消息。
    /// </summary>
    [JsonProperty("message")]
    public string Message { get; set; } = string.Empty;
}

/// <summary>
/// 定义校验结果。
/// </summary>
public sealed class DefinitionValidationResult
{
    /// <summary>
    /// 是否成功执行校验请求。
    /// </summary>
    [JsonProperty("ok")]
    public bool Ok { get; set; }

    /// <summary>
    /// 定义是否有效。
    /// </summary>
    [JsonProperty("valid")]
    public bool Valid { get; set; }

    /// <summary>
    /// 字段级校验问题。
    /// </summary>
    [JsonProperty("errors")]
    public List<ValidationIssue> Errors { get; set; } = [];
}

/// <summary>
/// 应用实例。
/// </summary>
public sealed class AppInstance
{
    private string _instanceId = string.Empty;
    private string _appId = string.Empty;
    private string _scope = string.Empty;

    /// <summary>
    /// Hub 注册表内全局唯一的实例标识，长度不超过 256。
    /// </summary>
    [JsonProperty("instanceId")]
    public string InstanceId
    {
        get => _instanceId;
        set => _instanceId = ProtocolIdentifier.EnsureInstanceId(value, nameof(InstanceId));
    }

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
    /// 作用域。空字符串表示 Global 实例。
    /// </summary>
    [JsonProperty("scope")]
    public string Scope
    {
        get => _scope;
        set => _scope = ScopeContract.EnsureScopedString(value ?? string.Empty, nameof(Scope));
    }

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
/// 实例注册结果。
/// </summary>
public sealed class RegisterInstanceResult
{
    /// <summary>
    /// 注册后的实例快照。
    /// </summary>
    [JsonProperty("instance")]
    public AppInstance Instance { get; set; } = new();

    /// <summary>
    /// 实例所有权会话令牌。
    /// </summary>
    [JsonProperty("instanceSessionToken")]
    public string InstanceSessionToken { get; set; } = string.Empty;

    /// <summary>
    /// 实例标识的兼容快捷访问器。
    /// </summary>
    [JsonIgnore]
    public string InstanceId => Instance.InstanceId;

    /// <summary>
    /// 应用标识的兼容快捷访问器。
    /// </summary>
    [JsonIgnore]
    public string AppId => Instance.AppId;

    /// <summary>
    /// 作用域的兼容快捷访问器。
    /// </summary>
    [JsonIgnore]
    public string Scope => Instance.Scope;

    /// <summary>
    /// 进程标识的兼容快捷访问器。
    /// </summary>
    [JsonIgnore]
    public int? Pid => Instance.Pid;

    /// <summary>
    /// 调用能力的兼容快捷访问器。
    /// </summary>
    [JsonIgnore]
    public InvokeCapability? Invoke => Instance.Invoke;

    /// <summary>
    /// 注册时间的兼容快捷访问器。
    /// </summary>
    [JsonIgnore]
    public DateTimeOffset? RegisteredAtUtc => Instance.RegisteredAtUtc;

    /// <summary>
    /// 最后在线时间的兼容快捷访问器。
    /// </summary>
    [JsonIgnore]
    public DateTimeOffset? LastSeenUtc => Instance.LastSeenUtc;

    /// <summary>
    /// 元数据的兼容快捷访问器。
    /// </summary>
    [JsonIgnore]
    public JToken? Meta => Instance.Meta;
}

/// <summary>
/// 实例注册载荷。
/// </summary>
public sealed class AppInstanceRegistration
{
    private string _instanceId = string.Empty;
    private string _appId = string.Empty;
    private string _scope = string.Empty;

    /// <summary>
    /// Hub 注册表内全局唯一的实例标识，长度不超过 256。同一 instanceId 重注册时必须保持相同 appId 与 scope。
    /// </summary>
    [JsonProperty("instanceId")]
    public string InstanceId
    {
        get => _instanceId;
        set => _instanceId = ProtocolIdentifier.EnsureInstanceId(value, nameof(InstanceId));
    }

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
    /// 作用域。空字符串表示 Global 实例。
    /// </summary>
    [JsonProperty("scope")]
    public string Scope
    {
        get => _scope;
        set => _scope = ScopeContract.EnsureScopedString(value ?? string.Empty, nameof(Scope));
    }

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

    /// <summary>
    /// 启动注册截止时间秒数。
    /// </summary>
    [JsonProperty("launchRegisterTimeoutSeconds")]
    public int LaunchRegisterTimeoutSeconds { get; set; }
}
