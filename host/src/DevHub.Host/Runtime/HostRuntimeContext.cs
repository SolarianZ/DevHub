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
        var normalizedHttpBaseUrl = ValidateHttpBaseUrl(httpBaseUrl);
        var normalizedWsUrl = ValidateWsUrl(wsUrl);

        lock (_syncRoot)
        {
            _httpBaseUrl = normalizedHttpBaseUrl;
            _wsUrl = normalizedWsUrl;
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

    private static string ValidateHttpBaseUrl(string httpBaseUrl)
    {
        if (httpBaseUrl.Trim() != httpBaseUrl
            || httpBaseUrl.Contains('?', StringComparison.Ordinal)
            || httpBaseUrl.Contains('#', StringComparison.Ordinal)
            || !Uri.TryCreate(httpBaseUrl, UriKind.Absolute, out var uri)
            || !IsHttpScheme(uri)
            || !IsLoopbackHost(uri)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment)
            || uri.AbsolutePath != "/")
        {
            throw new ArgumentException("httpBaseUrl 必须是回环地址上的 HTTP(S) origin。", nameof(httpBaseUrl));
        }

        return uri.GetLeftPart(UriPartial.Authority);
    }

    private static string ValidateWsUrl(string wsUrl)
    {
        if (wsUrl.Trim() != wsUrl
            || wsUrl.Contains('?', StringComparison.Ordinal)
            || wsUrl.Contains('#', StringComparison.Ordinal)
            || !Uri.TryCreate(wsUrl, UriKind.Absolute, out var uri)
            || !IsWebSocketScheme(uri)
            || !IsLoopbackHost(uri)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment)
            || uri.AbsolutePath != "/ws")
        {
            throw new ArgumentException("wsUrl 必须是回环地址上的固定 /ws WebSocket 端点。", nameof(wsUrl));
        }

        return uri.GetLeftPart(UriPartial.Authority) + "/ws";
    }

    private static bool IsHttpScheme(Uri uri)
    {
        return string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            || string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsWebSocketScheme(Uri uri)
    {
        return string.Equals(uri.Scheme, "ws", StringComparison.OrdinalIgnoreCase)
            || string.Equals(uri.Scheme, "wss", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLoopbackHost(Uri uri)
    {
        return uri.IsLoopback
            || string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase);
    }
}
