namespace DevHub.Sdk;

/// <summary>
/// 创建 <see cref="DevHubClient" /> 时可注入的依赖项。
/// </summary>
public sealed class DevHubClientDependencies
{
    private IDevHubRuntimeResolver _runtimeResolver = new FileSystemDevHubRuntimeResolver();
    private IDevHubHttpClientProvider _httpClientProvider = new DefaultDevHubHttpClientProvider();

    /// <summary>
    /// Runtime discovery 抽象。
    /// </summary>
    public IDevHubRuntimeResolver RuntimeResolver
    {
        get => _runtimeResolver;
        init => _runtimeResolver = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>
    /// HTTP 客户端提供器。
    /// </summary>
    public IDevHubHttpClientProvider HttpClientProvider
    {
        get => _httpClientProvider;
        init => _httpClientProvider = value ?? throw new ArgumentNullException(nameof(value));
    }
}
