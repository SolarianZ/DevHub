namespace DevHub.Sdk;

/// <summary>
/// 创建 <see cref="DevHubEventsClient" /> 时可注入的依赖项。
/// </summary>
public sealed class DevHubEventsClientDependencies
{
    private IDevHubRuntimeResolver _runtimeResolver = new FileSystemDevHubRuntimeResolver();

    /// <summary>
    /// Runtime discovery 抽象。
    /// </summary>
    public IDevHubRuntimeResolver RuntimeResolver
    {
        get => _runtimeResolver;
        init => _runtimeResolver = value ?? throw new ArgumentNullException(nameof(value));
    }
}
