namespace DevHub.Sdk;

/// <summary>
/// 为 <see cref="DevHubClient" /> 提供底层 <see cref="HttpClient" /> 的扩展点。
/// </summary>
public interface IDevHubHttpClientProvider
{
    /// <summary>
    /// 基于当前客户端选项与运行时连接信息创建 HTTP 客户端。
    /// SDK 会在对应 <see cref="DevHubClient" /> 释放时一并释放返回的实例。
    /// </summary>
    /// <param name="options">客户端选项。</param>
    /// <param name="connectionInfo">运行时连接信息。</param>
    /// <returns>供当前客户端实例独占使用的 HTTP 客户端。</returns>
    HttpClient CreateClient(DevHubClientOptions options, DevHubRuntimeConnectionInfo connectionInfo);
}

internal sealed class DefaultDevHubHttpClientProvider : IDevHubHttpClientProvider
{
    public HttpClient CreateClient(DevHubClientOptions options, DevHubRuntimeConnectionInfo connectionInfo)
    {
        _ = options ?? throw new ArgumentNullException(nameof(options));
        _ = connectionInfo ?? throw new ArgumentNullException(nameof(connectionInfo));

        return new HttpClient
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
    }
}
