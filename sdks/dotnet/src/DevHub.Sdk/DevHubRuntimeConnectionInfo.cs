using DevHub.Sdk.Models;

namespace DevHub.Sdk;

/// <summary>
/// DevHub 运行时连接信息。
/// </summary>
public sealed class DevHubRuntimeConnectionInfo
{
    /// <summary>
    /// 初始化运行时连接信息。
    /// </summary>
    /// <param name="runtimeDirectory">运行时目录。</param>
    /// <param name="token">访问令牌。</param>
    /// <param name="runtime">Hub 运行时发现信息。</param>
    public DevHubRuntimeConnectionInfo(string runtimeDirectory, string token, HubRuntime runtime)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        ArgumentNullException.ThrowIfNull(runtime);

        RuntimeDirectory = runtimeDirectory;
        Token = token;
        Runtime = runtime;
        RpcEndpoint = new Uri($"{runtime.HttpBaseUrl}/rpc", UriKind.Absolute);
        WebSocketEndpoint = new Uri(runtime.WsUrl, UriKind.Absolute);
    }

    /// <summary>
    /// 运行时目录。
    /// </summary>
    public string RuntimeDirectory { get; }

    /// <summary>
    /// 访问令牌。
    /// </summary>
    public string Token { get; }

    /// <summary>
    /// Hub 运行时发现信息。
    /// </summary>
    public HubRuntime Runtime { get; }

    /// <summary>
    /// HTTP JSON-RPC 端点。
    /// </summary>
    public Uri RpcEndpoint { get; }

    /// <summary>
    /// WebSocket 端点。
    /// </summary>
    public Uri WebSocketEndpoint { get; }
}
