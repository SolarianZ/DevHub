using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DevHub.Sdk;

/// <summary>
/// 创建 <see cref="DevHubClient" /> 时可注入的依赖项。
/// </summary>
public sealed class DevHubClientDependencies
{
    private IDevHubRuntimeResolver _runtimeResolver = new FileSystemDevHubRuntimeResolver();
    private IDevHubHttpClientProvider _httpClientProvider = new DefaultDevHubHttpClientProvider();
    private ILoggerFactory _loggerFactory = NullLoggerFactory.Instance;

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

    /// <summary>
    /// 可选日志工厂。未提供时使用空日志。
    /// </summary>
    public ILoggerFactory LoggerFactory
    {
        get => _loggerFactory;
        init => _loggerFactory = value ?? throw new ArgumentNullException(nameof(value));
    }
}
