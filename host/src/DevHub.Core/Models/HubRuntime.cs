using System.Text.Json.Serialization;

namespace DevHub.Core.Models;

/// <summary>
/// Hub运行时模型，用于生成 hub.json 发现文件
/// 符合 Spec §5.4 HubRuntime 模式定义
/// </summary>
public class HubRuntime
{
    /// <summary>
    /// 协议版本（必须为 1）
    /// </summary>
    [JsonPropertyName("protocolVersion")]
    public required int ProtocolVersion { get; set; }

    /// <summary>
    /// Hub 版本号
    /// </summary>
    [JsonPropertyName("hubVersion")]
    public string? HubVersion { get; set; }

    /// <summary>
    /// Hub 进程 ID
    /// </summary>
    [JsonPropertyName("pid")]
    public required int Pid { get; set; }

    /// <summary>
    /// HTTP基础URL（不含尾部斜杠）
    /// </summary>
    [JsonPropertyName("httpBaseUrl")]
    public required string HttpBaseUrl { get; set; }

    /// <summary>
    /// WebSocket URL（不含尾部斜杠）
    /// </summary>
    [JsonPropertyName("wsUrl")]
    public required string WsUrl { get; set; }

    /// <summary>
    /// Token文件的绝对路径
    /// </summary>
    [JsonPropertyName("tokenFile")]
    public required string TokenFile { get; set; }

    /// <summary>
    /// Hub 启动时间（UTC）
    /// </summary>
    [JsonPropertyName("startedAtUtc")]
    public required DateTime StartedAtUtc { get; set; }

    /// <summary>
    /// 运行时调优参数（当前生效值）。
    /// </summary>
    [JsonPropertyName("runtimeTuning")]
    public required HubRuntimeTuning RuntimeTuning { get; set; }
}

/// <summary>
/// hub.json 中的运行时调优参数。
/// </summary>
public class HubRuntimeTuning
{
    /// <summary>
    /// 调用租约秒数。
    /// </summary>
    [JsonPropertyName("leaseSeconds")]
    public required int LeaseSeconds { get; set; }

    /// <summary>
    /// 在线阈值秒数。
    /// </summary>
    [JsonPropertyName("onlineThresholdSeconds")]
    public required int OnlineThresholdSeconds { get; set; }

    /// <summary>
    /// 启动去重窗口秒数。
    /// </summary>
    [JsonPropertyName("launchDedupeWindowSeconds")]
    public required int LaunchDedupeWindowSeconds { get; set; }
}
