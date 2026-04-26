using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DevHub.Sdk;

/// <summary>
/// 创建 <see cref="DevHubEventsClient" /> 时可注入的依赖项。
/// </summary>
public sealed class DevHubEventsClientDependencies
{
    private IDevHubRuntimeResolver _runtimeResolver = new FileSystemDevHubRuntimeResolver();
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
    /// 可选日志工厂。未提供时使用空日志。
    /// </summary>
    public ILoggerFactory LoggerFactory
    {
        get => _loggerFactory;
        init => _loggerFactory = value ?? throw new ArgumentNullException(nameof(value));
    }
}
