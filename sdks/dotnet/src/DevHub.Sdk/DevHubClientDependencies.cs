namespace DevHub.Sdk;

/// <summary>
/// 创建 <see cref="DevHubClient" /> 时可注入的依赖项。
/// </summary>
public sealed class DevHubClientDependencies
{
    private IDevHubRuntimeResolver _runtimeResolver = new FileSystemDevHubRuntimeResolver();
    private IDevHubHttpTransportFactory _transportFactory = new JsonRpcHttpTransportFactory();

    /// <summary>
    /// Runtime discovery 抽象。
    /// </summary>
    public IDevHubRuntimeResolver RuntimeResolver
    {
        get => _runtimeResolver;
        init => _runtimeResolver = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>
    /// HTTP transport 工厂。
    /// </summary>
    public IDevHubHttpTransportFactory TransportFactory
    {
        get => _transportFactory;
        init => _transportFactory = value ?? throw new ArgumentNullException(nameof(value));
    }
}
