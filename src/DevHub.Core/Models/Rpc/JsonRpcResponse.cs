using System.Text.Json.Serialization;

namespace DevHub.Core.Models.Rpc;

/// <summary>
/// JSON-RPC响应模型
/// </summary>
public class JsonRpcResponse
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
    /// 结果
    /// </summary>
    [JsonPropertyName("result")]
    public object? Result { get; set; }

    /// <summary>
    /// 错误信息
    /// </summary>
    [JsonPropertyName("error")]
    public JsonRpcError? Error { get; set; }
}

/// <summary>
/// JSON-RPC错误模型
/// </summary>
public class JsonRpcError
{
    /// <summary>
    /// 错误码
    /// </summary>
    [JsonPropertyName("code")]
    public required int Code { get; set; }

    /// <summary>
    /// 错误消息
    /// </summary>
    [JsonPropertyName("message")]
    public required string Message { get; set; }

    /// <summary>
    /// 错误数据
    /// </summary>
    [JsonPropertyName("data")]
    public object? Data { get; set; }
}
