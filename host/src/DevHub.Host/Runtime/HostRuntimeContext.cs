namespace DevHub.Host.Runtime;

/// <summary>
/// Host 当前运行时地址上下文。
/// </summary>
public sealed class HostRuntimeContext
{
    private readonly object _syncRoot = new();
    private string _httpBaseUrl = string.Empty;
    private string _wsUrl = string.Empty;

    /// <summary>
    /// 更新当前运行时地址。
    /// </summary>
    /// <param name="httpBaseUrl">当前 HTTP 基础地址。</param>
    /// <param name="wsUrl">当前 WebSocket 地址。</param>
    public void SetUrls(string httpBaseUrl, string wsUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(httpBaseUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(wsUrl);

        lock (_syncRoot)
        {
            _httpBaseUrl = httpBaseUrl.TrimEnd('/');
            _wsUrl = wsUrl.TrimEnd('/');
        }
    }

    /// <summary>
    /// 获取当前 HTTP 基础地址。
    /// </summary>
    /// <returns>未就绪时返回空字符串。</returns>
    public string GetHttpBaseUrl()
    {
        lock (_syncRoot)
        {
            return _httpBaseUrl;
        }
    }

    /// <summary>
    /// 获取当前 WebSocket 地址。
    /// </summary>
    /// <returns>未就绪时返回空字符串。</returns>
    public string GetWsUrl()
    {
        lock (_syncRoot)
        {
            return _wsUrl;
        }
    }

    /// <summary>
    /// 清空当前运行时地址。
    /// </summary>
    public void Clear()
    {
        lock (_syncRoot)
        {
            _httpBaseUrl = string.Empty;
            _wsUrl = string.Empty;
        }
    }
}
