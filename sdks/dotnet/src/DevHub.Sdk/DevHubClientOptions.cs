namespace DevHub.Sdk;

/// <summary>
/// DevHub 客户端选项。
/// </summary>
public sealed class DevHubClientOptions
{
    /// <summary>
    /// 逻辑客户端标识。
    /// </summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>
    /// 客户端会话标识。
    /// </summary>
    public Guid ClientSessionId { get; set; } = Guid.NewGuid();

    /// <summary>
    /// 运行时目录覆盖。
    /// </summary>
    public string? RuntimeDir { get; set; }

    /// <summary>
    /// 可选的客户端请求超时。
    /// </summary>
    public TimeSpan? RequestTimeout { get; set; }

    /// <summary>
    /// 协议版本。
    /// </summary>
    public int ProtocolVersion { get; set; } = 1;

    internal DevHubClientOptions Clone()
    {
        return new DevHubClientOptions
        {
            ClientId = ClientId,
            ClientSessionId = ClientSessionId,
            RuntimeDir = RuntimeDir,
            RequestTimeout = RequestTimeout,
            ProtocolVersion = ProtocolVersion
        };
    }

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(ClientId))
        {
            throw new ArgumentException("ClientId 不能为空。", nameof(ClientId));
        }

        if (ClientSessionId == Guid.Empty)
        {
            throw new ArgumentException("ClientSessionId 不能为空 GUID。", nameof(ClientSessionId));
        }

        if (ProtocolVersion != 1)
        {
            throw new ArgumentOutOfRangeException(nameof(ProtocolVersion), ProtocolVersion, "当前仅支持协议版本 1。");
        }

        if (RequestTimeout is { } requestTimeout && requestTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(RequestTimeout), requestTimeout, "RequestTimeout 必须大于 0。");
        }
    }
}
