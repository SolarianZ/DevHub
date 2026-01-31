using System.Text.Json.Serialization;

namespace DevHub.Core.Models.Rpc;

/// <summary>
/// JSON-RPC请求模型
/// </summary>
public class JsonRpcRequest
{
    /// <summary>
    /// JSON-RPC版本
    /// </summary>
    [JsonPropertyName("jsonrpc")]
    public string JsonRpc { get; set; } = "2.0";

    /// <summary>
    /// 请求ID
    /// </summary>
    [JsonPropertyName("id")]
    public object? Id { get; set; }

    /// <summary>
    /// 方法名
    /// </summary>
    [JsonPropertyName("method")]
    public required string Method { get; set; }

    /// <summary>
    /// 参数
    /// </summary>
    [JsonPropertyName("params")]
    public object? Params { get; set; }
}
