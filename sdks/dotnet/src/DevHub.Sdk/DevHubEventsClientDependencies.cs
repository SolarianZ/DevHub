namespace DevHub.Sdk;

/// <summary>
/// 创建 <see cref="DevHubEventsClient" /> 时可注入的依赖项。
/// </summary>
public sealed class DevHubEventsClientDependencies
{
    private IDevHubRuntimeResolver _runtimeResolver = new FileSystemDevHubRuntimeResolver();
    private IDevHubWebSocketSessionFactory _sessionFactory = new JsonRpcWebSocketSessionFactory();

    /// <summary>
    /// Runtime discovery 抽象。
    /// </summary>
    public IDevHubRuntimeResolver RuntimeResolver
    {
        get => _runtimeResolver;
        set => _runtimeResolver = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>
    /// WebSocket session 工厂。
    /// </summary>
    public IDevHubWebSocketSessionFactory SessionFactory
    {
        get => _sessionFactory;
        set => _sessionFactory = value ?? throw new ArgumentNullException(nameof(value));
    }
}
