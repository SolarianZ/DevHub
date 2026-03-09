namespace DevHub.Sdk;

/// <summary>
/// DevHub 已知 RPC 错误码。
/// </summary>
public enum DevHubRpcErrorCode
{
    /// <summary>
    /// JSON 文本无法解析。
    /// </summary>
    ParseError = -32700,

    /// <summary>
    /// JSON-RPC 请求结构非法。
    /// </summary>
    InvalidRequest = -32600,

    /// <summary>
    /// 方法不存在。
    /// </summary>
    MethodNotFound = -32601,

    /// <summary>
    /// 参数非法。
    /// </summary>
    InvalidParams = -32602,

    /// <summary>
    /// 服务端内部错误。
    /// </summary>
    InternalError = -32603,

    /// <summary>
    /// 鉴权失败。
    /// </summary>
    Unauthorized = -32001,

    /// <summary>
    /// 操作被禁止。
    /// </summary>
    Forbidden = -32002,

    /// <summary>
    /// 目标实例不存在。
    /// </summary>
    InstanceNotFound = -32010,

    /// <summary>
    /// 调用已过期。
    /// </summary>
    InvocationExpired = -32011,

    /// <summary>
    /// 调用等待超时。
    /// </summary>
    InvocationTimeout = -32012,

    /// <summary>
    /// 应用定义不存在。
    /// </summary>
    AppDefinitionNotFound = -32014,

    /// <summary>
    /// 启动失败。
    /// </summary>
    LaunchFailed = -32020,

    /// <summary>
    /// 投递冲突。
    /// </summary>
    DeliveryConflict = -32030,

    /// <summary>
    /// 触发限流。
    /// </summary>
    RateLimited = -32040,

    /// <summary>
    /// 被调用方返回了应用级错误。
    /// </summary>
    InvocationFailed = -32050,

    /// <summary>
    /// 当前协议或传输不受支持。
    /// </summary>
    NotSupported = -32099
}
