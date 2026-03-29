using DevHub.Core.Services;
using Microsoft.Extensions.Logging;

namespace DevHub.Host.Runtime;

/// <summary>
/// Host 数据目录骨架初始化器。
/// </summary>
/// <remarks>
/// 仅负责创建 Host 运行所需的稳定目录布局，不处理 token、hub.json 或 lease 生命周期。
/// </remarks>
public sealed class HostDataDirectoryInitializer
{
    private readonly ILogger<HostDataDirectoryInitializer> _logger;
    private readonly string _rootPath;
    private readonly string _runtimePath;
    private readonly string _definitionsPath;
    private readonly string _instancesPath;
    private readonly string _logsPath;

    /// <summary>
    /// 初始化 Host 数据目录骨架初始化器。
    /// </summary>
    /// <param name="logger">日志记录器。</param>
    /// <param name="runtimePathOptions">运行时路径选项。</param>
    public HostDataDirectoryInitializer(
        ILogger<HostDataDirectoryInitializer> logger,
        RuntimePathOptions runtimePathOptions)
    {
        _logger = logger;
        _rootPath = runtimePathOptions.RootPath;
        _runtimePath = runtimePathOptions.RuntimePath;
        _definitionsPath = runtimePathOptions.DefinitionsPath;
        _instancesPath = runtimePathOptions.InstancesPath;
        _logsPath = runtimePathOptions.LogsPath;
    }

    /// <summary>
    /// 确保 Host 数据目录骨架存在。
    /// </summary>
    public void InitializeDirectories()
    {
        try
        {
            EnsureDirectory(_rootPath, "根目录");
            EnsureDirectory(_runtimePath, "运行时目录");
            EnsureDirectory(_definitionsPath, "应用程序定义目录");
            EnsureDirectory(_instancesPath, "实例目录");
            EnsureDirectory(_logsPath, "日志目录");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "初始化 Host 数据目录骨架失败");
            throw;
        }
    }

    private void EnsureDirectory(string path, string description)
    {
        if (!Directory.Exists(path))
        {
            Directory.CreateDirectory(path);
            _logger.LogInformation("成功创建{Description}: {Path}", description, path);
            return;
        }

        _logger.LogDebug("{Description}已存在: {Path}", description, path);
    }
}
