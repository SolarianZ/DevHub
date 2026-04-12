using DevHub.Core.Services;
using DevHub.Host.Runtime;

namespace DevHub.Host;

/// <summary>
/// Host 启动编排器。
/// </summary>
/// <remarks>
/// 负责运行时目录初始化、定义快照刷新与启动后发现文件写入。
/// </remarks>
public class HostBootstrapper
{
    private readonly HostDataDirectoryInitializer _dataDirectoryInitializer;
    private readonly HostRuntimeArtifactManager _runtimeArtifactManager;
    private readonly HostRuntimeContext _runtimeContext;
    private readonly IDefinitionProvider _definitionProvider;
    private readonly ILogger<HostBootstrapper> _logger;

    /// <summary>
    /// 初始化 Host 启动编排器。
    /// </summary>
    /// <param name="dataDirectoryInitializer">Host 数据目录骨架初始化器。</param>
    /// <param name="runtimeArtifactManager">Host 运行时产物管理器。</param>
    /// <param name="definitionProvider">定义提供器。</param>
    /// <param name="invocationTimeoutWorker">调用超时工作器。</param>
    /// <param name="logger">日志记录器。</param>
    public HostBootstrapper(
        HostDataDirectoryInitializer dataDirectoryInitializer,
        HostRuntimeArtifactManager runtimeArtifactManager,
        HostRuntimeContext runtimeContext,
        IDefinitionProvider definitionProvider,
        ILogger<HostBootstrapper> logger)
    {
        _dataDirectoryInitializer = dataDirectoryInitializer;
        _runtimeArtifactManager = runtimeArtifactManager;
        _runtimeContext = runtimeContext;
        _definitionProvider = definitionProvider;
        _logger = logger;
    }

    /// <summary>
    /// 执行启动前初始化。
    /// </summary>
    public void Initialize()
    {
        _logger.LogDebug("初始化 Host 数据目录骨架...");
        _dataDirectoryInitializer.InitializeDirectories();
        _logger.LogInformation("Host 数据目录初始化完成");

        _logger.LogDebug("确保 token 文件存在...");
        _runtimeArtifactManager.GetToken();
        _logger.LogInformation("Token 文件准备完成");

        _logger.LogDebug("刷新应用程序定义快照...");
        _definitionProvider.Refresh();
        _logger.LogInformation("应用程序定义加载完成");
    }

    /// <summary>
    /// 在应用启动后解析端口并写入 <c>hub.json</c>。
    /// </summary>
    /// <param name="addresses">监听地址集合。</param>
    /// <param name="port">解析出的监听端口。</param>
    /// <returns>成功写入返回 <c>true</c>。</returns>
    public bool TryPersistHubRuntime(IEnumerable<string> addresses, out int port)
    {
        port = 0;
        var addressList = addresses.ToArray();

        foreach (var address in addressList)
        {
            if (!TryParseLoopbackPort(address, out var parsedPort))
            {
                continue;
            }

            port = parsedPort;
            _runtimeContext.SetUrls(
                $"http://127.0.0.1:{parsedPort}",
                $"ws://127.0.0.1:{parsedPort}/ws");
            _logger.LogInformation("服务器成功启动，监听地址: {Address}", address);
            _logger.LogDebug("写入 hub.json 文件...");
            _runtimeArtifactManager.WriteHubJson(parsedPort);
            _runtimeArtifactManager.ActivateHubJsonLease();
            _logger.LogInformation("DevHub 启动成功，监听端口: {Port}", parsedPort);
            _logger.LogInformation("HTTP 地址: http://127.0.0.1:{Port}", parsedPort);
            return true;
        }

        _logger.LogWarning(
            "未找到可用于持久化 hub.json 的回环 HTTP 地址。Addresses: {Addresses}",
            string.Join(", ", addressList));
        return false;
    }

    /// <summary>
    /// 执行 Host 停止后的运行时清理。
    /// </summary>
    public void Cleanup()
    {
        _runtimeContext.Clear();
        _runtimeArtifactManager.Cleanup();
    }

    private static bool TryParseLoopbackPort(string address, out int port)
    {
        port = 0;

        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (!string.Equals(uri.Scheme, "http", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.Equals(uri.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (uri.Port <= 0)
        {
            return false;
        }

        port = uri.Port;
        return true;
    }
}
