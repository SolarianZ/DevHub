using DevHub.Sdk.Internal;

namespace DevHub.Sdk;

/// <summary>
/// DevHub 运行时发现抽象。
/// </summary>
public interface IDevHubRuntimeResolver
{
    /// <summary>
    /// 根据客户端选项解析运行时连接信息。
    /// </summary>
    /// <param name="options">客户端选项。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>解析出的运行时连接信息。</returns>
    Task<DevHubRuntimeConnectionInfo> ResolveAsync(DevHubClientOptions options, CancellationToken cancellationToken = default);
}

/// <summary>
/// 基于文件系统的默认运行时发现实现。
/// </summary>
public sealed class FileSystemDevHubRuntimeResolver : IDevHubRuntimeResolver
{
    /// <inheritdoc />
    public Task<DevHubRuntimeConnectionInfo> ResolveAsync(DevHubClientOptions options, CancellationToken cancellationToken = default)
    {
        return RuntimeDiscovery.DiscoverAsync(options, cancellationToken);
    }
}
