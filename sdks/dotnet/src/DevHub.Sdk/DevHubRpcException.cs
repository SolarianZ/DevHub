using System.Text.Json;
using DevHub.Sdk.Internal;
using DevHub.Sdk.Models;

namespace DevHub.Sdk;

/// <summary>
/// DevHub RPC 错误异常。
/// </summary>
public sealed class DevHubRpcException : Exception
{
    private readonly JsonElement? _data;

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
        _data = data;
        RequestId = requestId;
    }

    /// <summary>
    /// 错误码。
    /// </summary>
    public int Code { get; }

    /// <summary>
    /// 错误扩展数据。
    /// </summary>
    public new JsonElement? Data => _data;

    /// <summary>
    /// 错误扩展数据的显式别名。
    /// </summary>
    public JsonElement? ErrorData => _data;

    /// <summary>
    /// 若错误码属于规范内已知集合，则返回对应枚举值；否则返回 <see langword="null"/>。
    /// </summary>
    public DevHubRpcErrorCode? KnownCode => TryMapKnownCode(Code, out var knownCode) ? knownCode : null;

    /// <summary>
    /// 当 <c>error.data.reason</c> 为字符串时返回该值，否则返回 <see langword="null"/>。
    /// </summary>
    public string? Reason => TryGetDataString("reason", out var reason) ? reason : null;

    /// <summary>
    /// 当 <c>error.data.invocationId</c> 为字符串时返回该值，否则返回 <see langword="null"/>。
    /// </summary>
    public string? InvocationId => TryGetDataString("invocationId", out var invocationId) ? invocationId : null;

    /// <summary>
    /// 当 <c>error.data.calleeError</c> 可解析为 <see cref="DevHubCalleeError"/> 时返回该值，否则返回 <see langword="null"/>。
    /// </summary>
    public DevHubCalleeError? CalleeError => TryGetCalleeError(out var calleeError) ? calleeError : null;

    /// <summary>
    /// 请求标识。
    /// </summary>
    public string RequestId { get; }

    /// <summary>
    /// 判断当前异常是否匹配指定已知错误码。
    /// </summary>
    /// <param name="errorCode">待匹配的错误码。</param>
    /// <returns>若匹配则返回 <see langword="true"/>。</returns>
    public bool Is(DevHubRpcErrorCode errorCode)
    {
        return Code == (int)errorCode;
    }

    /// <summary>
    /// 尝试读取 <c>error.data</c> 下的指定属性。
    /// </summary>
    /// <param name="propertyName">属性名。</param>
    /// <param name="value">读取到的属性值。</param>
    /// <returns>读取成功时返回 <see langword="true"/>。</returns>
    public bool TryGetDataProperty(string propertyName, out JsonElement value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(propertyName);

        if (_data is { } data &&
            data.ValueKind == JsonValueKind.Object &&
            data.TryGetProperty(propertyName, out var propertyValue))
        {
            value = propertyValue.Clone();
            return true;
        }

        value = default;
        return false;
    }

    /// <summary>
    /// 尝试读取 <c>error.data</c> 下的字符串属性。
    /// </summary>
    /// <param name="propertyName">属性名。</param>
    /// <param name="value">读取到的字符串值。</param>
    /// <returns>读取成功时返回 <see langword="true"/>。</returns>
    public bool TryGetDataString(string propertyName, out string? value)
    {
        if (TryGetDataProperty(propertyName, out var propertyValue) && propertyValue.ValueKind == JsonValueKind.String)
        {
            value = propertyValue.GetString();
            return true;
        }

        value = null;
        return false;
    }

    /// <summary>
    /// 尝试读取并解析 <c>error.data.calleeError</c>。
    /// </summary>
    /// <param name="value">读取到的被调用方错误对象。</param>
    /// <returns>读取并解析成功时返回 <see langword="true"/>。</returns>
    public bool TryGetCalleeError(out DevHubCalleeError? value)
    {
        if (TryGetDataProperty("calleeError", out var propertyValue) && propertyValue.ValueKind == JsonValueKind.Object)
        {
            var calleeError = JsonSerializer.Deserialize<DevHubCalleeError>(propertyValue.GetRawText(), DevHubJson.SerializerOptions);
            if (calleeError is not null && !string.IsNullOrWhiteSpace(calleeError.Message))
            {
                value = calleeError;
                return true;
            }
        }

        value = null;
        return false;
    }

    private static bool TryMapKnownCode(int code, out DevHubRpcErrorCode errorCode)
    {
        switch (code)
        {
            case (int)DevHubRpcErrorCode.ParseError:
            case (int)DevHubRpcErrorCode.InvalidRequest:
            case (int)DevHubRpcErrorCode.MethodNotFound:
            case (int)DevHubRpcErrorCode.InvalidParams:
            case (int)DevHubRpcErrorCode.InternalError:
            case (int)DevHubRpcErrorCode.Unauthorized:
            case (int)DevHubRpcErrorCode.Forbidden:
            case (int)DevHubRpcErrorCode.InstanceNotFound:
            case (int)DevHubRpcErrorCode.InvocationExpired:
            case (int)DevHubRpcErrorCode.InvocationTimeout:
            case (int)DevHubRpcErrorCode.AppDefinitionNotFound:
            case (int)DevHubRpcErrorCode.LaunchFailed:
            case (int)DevHubRpcErrorCode.DeliveryConflict:
            case (int)DevHubRpcErrorCode.RateLimited:
            case (int)DevHubRpcErrorCode.InvocationFailed:
            case (int)DevHubRpcErrorCode.NotSupported:
                errorCode = (DevHubRpcErrorCode)code;
                return true;
            default:
                errorCode = default;
                return false;
        }
    }
}
