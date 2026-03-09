using System.Text.Json;

namespace DevHub.Sdk;

/// <summary>
/// DevHub RPC 错误异常。
/// </summary>
public sealed class DevHubRpcException : Exception
{
    /// <summary>
    /// 初始化异常。
    /// </summary>
    /// <param name="code">错误码。</param>
    /// <param name="message">错误消息。</param>
    /// <param name="data">错误扩展数据。</param>
    /// <param name="requestId">请求标识。</param>
    internal DevHubRpcException(int code, string message, JsonElement? data, string requestId)
        : base(message)
    {
        Code = code;
        Data = data;
        RequestId = requestId;
    }

    /// <summary>
    /// 错误码。
    /// </summary>
    public int Code { get; }

    /// <summary>
    /// 错误扩展数据。
    /// </summary>
    public new JsonElement? Data { get; }

    /// <summary>
    /// 请求标识。
    /// </summary>
    public string RequestId { get; }
}
