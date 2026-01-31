using DevHub.Core.Models;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;

namespace DevHub.Core.Services;

/// <summary>
/// 文件系统管理器，负责目录创建、token 管理和 hub.json 写入
/// </summary>
public class FileSystemManager
{
    private readonly ILoggerService _logger;
    private readonly string _rootPath;
    private readonly string _runtimePath;
    private readonly string _definitionsPath;
    private readonly string _tokenFilePath;
    private readonly string _hubJsonPath;

    public FileSystemManager(ILoggerService logger)
    {
        _logger = logger;

        // 计算数据目录路径
        _rootPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DevHub");
        _runtimePath = Path.Combine(_rootPath, "runtime");
        _definitionsPath = Path.Combine(_rootPath, "apps", "definitions");
        _tokenFilePath = Path.Combine(_runtimePath, "token.txt");
        _hubJsonPath = Path.Combine(_runtimePath, "hub.json");
    }

    /// <summary>
    /// 初始化文件系统目录结构
    /// </summary>
    public void InitializeDirectories()
    {
        try
        {
            _logger.Debug("开始初始化文件系统目录结构");

            if (!Directory.Exists(_rootPath))
            {
                Directory.CreateDirectory(_rootPath);
                _logger.Information("成功创建根目录: {Path}", _rootPath);
            }
            else
            {
                _logger.Debug("根目录已存在: {Path}", _rootPath);
            }

            if (!Directory.Exists(_runtimePath))
            {
                Directory.CreateDirectory(_runtimePath);
                _logger.Information("成功创建运行时目录: {Path}", _runtimePath);
            }
            else
            {
                _logger.Debug("运行时目录已存在: {Path}", _runtimePath);
            }

            if (!Directory.Exists(_definitionsPath))
            {
                Directory.CreateDirectory(_definitionsPath);
                _logger.Information("成功创建应用程序定义目录: {Path}", _definitionsPath);
            }
            else
            {
                _logger.Debug("应用程序定义目录已存在: {Path}", _definitionsPath);
            }

            var instancesPath = Path.Combine(_rootPath, "apps", "instances");
            if (!Directory.Exists(instancesPath))
            {
                Directory.CreateDirectory(instancesPath);
                _logger.Information("成功创建实例目录: {Path}", instancesPath);
            }
            else
            {
                _logger.Debug("实例目录已存在: {Path}", instancesPath);
            }

            var logsPath = Path.Combine(_rootPath, "logs");
            if (!Directory.Exists(logsPath))
            {
                Directory.CreateDirectory(logsPath);
                _logger.Information("成功创建日志目录: {Path}", logsPath);
            }
            else
            {
                _logger.Debug("日志目录已存在: {Path}", logsPath);
            }

            _logger.Debug("文件系统目录结构初始化完成");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "初始化文件系统目录结构失败");
            throw;
        }
    }

    /// <summary>
    /// 生成或读取 token
    /// </summary>
    public string GetToken()
    {
        try
        {
            _logger.Debug("开始处理 token 请求，文件路径: {FilePath}", _tokenFilePath);

            if (File.Exists(_tokenFilePath))
            {
                _logger.Debug("Token 文件存在，尝试读取现有 token");
                var existingToken = File.ReadAllText(_tokenFilePath).Trim();
                if (!string.IsNullOrEmpty(existingToken))
                {
                    _logger.Information("成功读取现有 token，文件路径: {FilePath}", _tokenFilePath);
                    return existingToken;
                }
                _logger.Warning("Token 文件存在但内容为空，将生成新 token，文件路径: {FilePath}", _tokenFilePath);
            }
            else
            {
                _logger.Debug("Token 文件不存在，将生成新 token，文件路径: {FilePath}", _tokenFilePath);
            }

            _logger.Debug("开始生成新 token");
            var newToken = GenerateNewToken();
            _logger.Debug("成功生成新 token，长度: {TokenLength} 字符", newToken.Length);

            _logger.Debug("开始写入新 token 到文件: {FilePath}", _tokenFilePath);
            File.WriteAllText(_tokenFilePath, newToken);
            _logger.Information("成功生成新 token 并写入文件，文件路径: {FilePath}", _tokenFilePath);

            // 设置仅当前用户可访问的权限（Windows 平台）
            if (OperatingSystem.IsWindows())
            {
                try
                {
                    _logger.Debug("尝试设置 token 文件权限，文件路径: {FilePath}", _tokenFilePath);
                    var fileInfo = new FileInfo(_tokenFilePath);
                    var security = fileInfo.GetAccessControl(AccessControlSections.Access);
                    var currentUser = WindowsIdentity.GetCurrent().Name;
                    var rule = new FileSystemAccessRule(currentUser, FileSystemRights.FullControl, AccessControlType.Allow);
                    security.SetAccessRule(rule);

                    // 移除继承的权限
                    security.SetAccessRuleProtection(true, false);
                    fileInfo.SetAccessControl(security);
                    _logger.Information("已成功设置 token 文件安全权限，仅当前用户可访问，文件路径: {FilePath}", _tokenFilePath);
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "无法设置 token 文件的安全权限，文件路径: {FilePath}", _tokenFilePath);
                }
            }

            return newToken;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "读取或生成 token 失败，文件路径: {FilePath}", _tokenFilePath);
            throw;
        }
    }

    /// <summary>
    /// 生成新的随机 token（使用加密安全的随机数生成器）
    /// </summary>
    private string GenerateNewToken()
    {
        var randomBytes = new byte[32];
        RandomNumberGenerator.Fill(randomBytes);
        return Convert.ToBase64String(randomBytes).TrimEnd('=');
    }

    /// <summary>
    /// 写入 hub.json 文件
    /// </summary>
    /// <param name="port">监听端口</param>
    /// <param name="hubVersion">Hub 版本号（可选）</param>
    public void WriteHubJson(int port, string? hubVersion = null)
    {
        try
        {
            _logger.Debug("开始写入 hub.json 文件，监听端口: {Port}, Hub版本: {HubVersion}", port, hubVersion);

            var hubRuntime = new HubRuntime
            {
                ProtocolVersion = 1,
                HubVersion = hubVersion,
                Pid = Environment.ProcessId,
                HttpBaseUrl = $"http://127.0.0.1:{port}",
                WsUrl = $"ws://127.0.0.1:{port}/ws",
                TokenFile = _tokenFilePath,
                StartedAtUtc = DateTime.UtcNow
            };

            var tempPath = _hubJsonPath + ".tmp";
            File.WriteAllText(tempPath, System.Text.Json.JsonSerializer.Serialize(hubRuntime, new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true
            }));

            // 使用 overwrite = true 实现原子替换（在同一卷上）
            // 注意：跨卷移动通常不是原子的，但 runtime 目录通常在同一卷
            File.Move(tempPath, _hubJsonPath, overwrite: true);
            _logger.Information("成功写入 hub.json 文件: {Path}", _hubJsonPath);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "写入 hub.json 文件失败，文件路径: {Path}", _hubJsonPath);
            throw;
        }
    }

    /// <summary>
    /// 清理资源
    /// </summary>
    public void Cleanup()
    {
        try
        {
            _logger.Debug("开始清理资源");
            // 可以添加一些清理逻辑，比如删除临时文件
            _logger.Information("资源清理完成");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "清理资源失败");
        }
    }
}
