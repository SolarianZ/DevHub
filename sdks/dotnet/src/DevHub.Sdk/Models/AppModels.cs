using System.Text.Json;
using System.Text.Json.Serialization;
using DevHub.Sdk.Internal;

namespace DevHub.Sdk.Models;

/// <summary>
/// 应用定义。
/// </summary>
public sealed class AppDefinition
{
    private string _appId = string.Empty;
    private string? _scope;

    /// <summary>
    /// 应用标识。
    /// </summary>
    [JsonPropertyName("appId")]
    public string AppId
    {
        get => _appId;
        set => _appId = ProtocolIdentifier.EnsureAppId(value, nameof(AppId));
    }

    /// <summary>
    /// Definition 作用域。空字符串表示 Global Definition。
    /// </summary>
    [JsonPropertyName("scope")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string Scope
    {
        get => ScopeContract.EnsureAssignedScope(_scope, nameof(AppDefinition));
        set
        {
            _scope = ScopeContract.EnsureScopedString(value, nameof(Scope));
        }
    }

    /// <summary>
    /// 显示名称。
    /// </summary>
    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// 应用描述。
    /// </summary>
    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; set; }

    /// <summary>
    /// 能力声明。
    /// </summary>
    [JsonPropertyName("capabilities")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AppCapabilities? Capabilities { get; set; }

    /// <summary>
    /// 启动配置。
    /// </summary>
    [JsonPropertyName("launch")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
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
    [JsonPropertyName("rpc")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Rpc { get; set; }

    /// <summary>
    /// 是否声明事件能力。
    /// </summary>
    [JsonPropertyName("events")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
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
    [JsonPropertyName("exePath")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ExePath { get; set; }

    /// <summary>
    /// 结构化启动参数。
    /// </summary>
    [JsonPropertyName("args")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Args { get; set; }

    /// <summary>
    /// 参数模板。
    /// </summary>
    [JsonPropertyName("argsTemplate")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ArgsTemplate { get; set; }

    /// <summary>
    /// 工作目录。
    /// </summary>
    [JsonPropertyName("workingDirectory")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? WorkingDirectory { get; set; }

    /// <summary>
    /// 去重键模板。
    /// </summary>
    [JsonPropertyName("dedupeKeyTemplate")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
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
    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    /// <summary>
    /// 机器可读错误码。
    /// </summary>
    [JsonPropertyName("code")]
    public string Code { get; set; } = string.Empty;

    /// <summary>
    /// 人类可读错误消息。
    /// </summary>
    [JsonPropertyName("message")]
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
    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    /// <summary>
    /// 定义是否有效。
    /// </summary>
    [JsonPropertyName("valid")]
    public bool Valid { get; set; }

    /// <summary>
    /// 字段级校验问题。
    /// </summary>
    [JsonPropertyName("errors")]
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
    /// 实例标识。
    /// </summary>
    [JsonPropertyName("instanceId")]
    public string InstanceId
    {
        get => _instanceId;
        set => _instanceId = ProtocolIdentifier.EnsureInstanceId(value, nameof(InstanceId));
    }

    /// <summary>
    /// 应用标识。
    /// </summary>
    [JsonPropertyName("appId")]
    public string AppId
    {
        get => _appId;
        set => _appId = ProtocolIdentifier.EnsureAppId(value, nameof(AppId));
    }

    /// <summary>
    /// 作用域。空字符串表示 Global 实例。
    /// </summary>
    [JsonPropertyName("scope")]
    public string Scope
    {
        get => _scope;
        set
        {
            _scope = ScopeContract.EnsureScopedString(value, nameof(Scope));
        }
    }

    /// <summary>
    /// 进程标识。
    /// </summary>
    [JsonPropertyName("pid")]
    public int? Pid { get; set; }

    /// <summary>
    /// 注册时间。
    /// </summary>
    [JsonPropertyName("registeredAtUtc")]
    public DateTimeOffset? RegisteredAtUtc { get; set; }

    /// <summary>
    /// 最后在线时间。
    /// </summary>
    [JsonPropertyName("lastSeenUtc")]
    public DateTimeOffset? LastSeenUtc { get; set; }

    /// <summary>
    /// 调用能力。
    /// </summary>
    [JsonPropertyName("invoke")]
    public InvokeCapability? Invoke { get; set; }

    /// <summary>
    /// 元数据。
    /// </summary>
    [JsonPropertyName("meta")]
    public JsonElement? Meta { get; set; }
}

/// <summary>
/// 实例注册结果。
/// </summary>
public sealed class RegisterInstanceResult
{
    private AppInstance _instance = new();
    private string _instanceSessionToken = string.Empty;

    /// <summary>
    /// 注册后的实例快照。
    /// </summary>
    [JsonPropertyName("instance")]
    public AppInstance Instance
    {
        get => _instance;
        set => _instance = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>
    /// 实例所有权会话令牌。
    /// </summary>
    [JsonPropertyName("instanceSessionToken")]
    public string InstanceSessionToken
    {
        get => _instanceSessionToken;
        set => _instanceSessionToken = string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("InstanceSessionToken 不能为空白字符串。", nameof(InstanceSessionToken))
            : value;
    }
}

/// <summary>
/// 实例注册载荷。
/// </summary>
public sealed class AppInstanceRegistration
{
    private string _instanceId = string.Empty;
    private string _appId = string.Empty;
    private string? _scope;

    /// <summary>
    /// 实例标识。
    /// </summary>
    [JsonPropertyName("instanceId")]
    public string InstanceId
    {
        get => _instanceId;
        set => _instanceId = ProtocolIdentifier.EnsureInstanceId(value, nameof(InstanceId));
    }

    /// <summary>
    /// 应用标识。
    /// </summary>
    [JsonPropertyName("appId")]
    public string AppId
    {
        get => _appId;
        set => _appId = ProtocolIdentifier.EnsureAppId(value, nameof(AppId));
    }

    /// <summary>
    /// 作用域。空字符串表示 Global 实例。
    /// </summary>
    [JsonPropertyName("scope")]
    public string Scope
    {
        get => ScopeContract.EnsureAssignedScope(_scope, nameof(AppInstanceRegistration));
        set
        {
            _scope = ScopeContract.EnsureScopedString(value, nameof(Scope));
        }
    }

    /// <summary>
    /// 进程标识。
    /// </summary>
    [JsonPropertyName("pid")]
    public int Pid { get; set; }

    /// <summary>
    /// 调用能力。
    /// </summary>
    [JsonPropertyName("invoke")]
    public InvokeCapability Invoke { get; set; } = new();

    /// <summary>
    /// 元数据。
    /// </summary>
    [JsonPropertyName("meta")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
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
    [JsonPropertyName("poll")]
    public bool Poll { get; set; }

    /// <summary>
    /// 是否支持响应。
    /// </summary>
    [JsonPropertyName("respond")]
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
    [JsonPropertyName("protocolVersion")]
    public int ProtocolVersion { get; set; }

    /// <summary>
    /// Hub 版本。
    /// </summary>
    [JsonPropertyName("hubVersion")]
    public string? HubVersion { get; set; }

    /// <summary>
    /// 进程标识。
    /// </summary>
    [JsonPropertyName("pid")]
    public int Pid { get; set; }

    /// <summary>
    /// HTTP 基础地址。
    /// </summary>
    [JsonPropertyName("httpBaseUrl")]
    public string HttpBaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// WebSocket 地址。
    /// </summary>
    [JsonPropertyName("wsUrl")]
    public string WsUrl { get; set; } = string.Empty;

    /// <summary>
    /// 令牌文件路径。
    /// </summary>
    [JsonPropertyName("tokenFile")]
    public string TokenFile { get; set; } = string.Empty;

    /// <summary>
    /// 启动时间。
    /// </summary>
    [JsonPropertyName("startedAtUtc")]
    public DateTimeOffset StartedAtUtc { get; set; }

    /// <summary>
    /// 运行时调优参数。
    /// </summary>
    [JsonPropertyName("runtimeTuning")]
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
    [JsonPropertyName("leaseSeconds")]
    public int LeaseSeconds { get; set; }

    /// <summary>
    /// 在线阈值秒数。
    /// </summary>
    [JsonPropertyName("onlineThresholdSeconds")]
    public int OnlineThresholdSeconds { get; set; }

    /// <summary>
    /// 启动去重窗口秒数。
    /// </summary>
    [JsonPropertyName("launchDedupeWindowSeconds")]
    public int LaunchDedupeWindowSeconds { get; set; }
}
