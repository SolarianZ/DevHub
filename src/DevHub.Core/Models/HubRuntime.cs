using System.Text.Json.Serialization;

namespace DevHub.Core.Models;

/// <summary>
/// Hub运行时模型
/// </summary>
public class HubRuntime
{
    /// <summary>
    /// 协议版本
    /// </summary>
    [JsonPropertyName("protocolVersion")]
    public required int ProtocolVersion { get; set; }

    /// <summary>
    /// HTTP基础URL
    /// </summary>
    [JsonPropertyName("httpBaseUrl")]
    public required string HttpBaseUrl { get; set; }

    /// <summary>
    /// WebSocket URL
    /// </summary>
    [JsonPropertyName("wsUrl")]
    public required string WsUrl { get; set; }

    /// <summary>
    /// 启动时间
    /// </summary>
    [JsonPropertyName("startedAtUtc")]
    public required DateTime StartedAtUtc { get; set; }

    /// <summary>
    /// Token文件路径
    /// </summary>
    [JsonPropertyName("tokenFile")]
    public required string TokenFile { get; set; }
}
