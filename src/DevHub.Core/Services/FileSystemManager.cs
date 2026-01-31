using DevHub.Core.Models;
using Microsoft.Extensions.Logging;
using System.Security.AccessControl;
using System.Security.Principal;

namespace DevHub.Core.Services;

/// <summary>
/// 文件系统管理器，负责目录创建、token 管理和 hub.json 写入
/// </summary>
public class FileSystemManager
{
    private readonly ILogger<FileSystemManager> _logger;
    private readonly string _rootPath;
    private readonly string _runtimePath;
    private readonly string _definitionsPath;
    private readonly string _tokenFilePath;
    private readonly string _hubJsonPath;

    public FileSystemManager(ILogger<FileSystemManager> logger)
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
            if (!Directory.Exists(_rootPath))
            {
                Directory.CreateDirectory(_rootPath);
                _logger.LogInformation("创建根目录: {Path}", _rootPath);
            }

            if (!Directory.Exists(_runtimePath))
            {
                Directory.CreateDirectory(_runtimePath);
                _logger.LogInformation("创建运行时目录: {Path}", _runtimePath);
            }

            if (!Directory.Exists(_definitionsPath))
            {
                Directory.CreateDirectory(_definitionsPath);
                _logger.LogInformation("创建应用程序定义目录: {Path}", _definitionsPath);
            }

            var instancesPath = Path.Combine(_rootPath, "apps", "instances");
            if (!Directory.Exists(instancesPath))
            {
                Directory.CreateDirectory(instancesPath);
                _logger.LogInformation("创建实例目录: {Path}", instancesPath);
            }

            var logsPath = Path.Combine(_rootPath, "logs");
            if (!Directory.Exists(logsPath))
            {
                Directory.CreateDirectory(logsPath);
                _logger.LogInformation("创建日志目录: {Path}", logsPath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "初始化目录结构失败");
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
            if (File.Exists(_tokenFilePath))
            {
                var existingToken = File.ReadAllText(_tokenFilePath).Trim();
                if (!string.IsNullOrEmpty(existingToken))
                {
                    _logger.LogInformation("读取现有 token");
                    return existingToken;
                }
            }

            var newToken = GenerateNewToken();
            File.WriteAllText(_tokenFilePath, newToken);
            _logger.LogInformation("生成新 token: {Token}", newToken);

            // 设置仅当前用户可访问的权限（Windows 平台）
            // TODO: 实现 token 文件安全权限设置（需要检查 .NET 10 API）
            /*
            if (OperatingSystem.IsWindows())
            {
                try
                {
                    var security = new FileSecurity(_tokenFilePath, AccessControlSections.Access);
                    var currentUser = WindowsIdentity.GetCurrent().Name;
                    var rule = new FileSystemAccessRule(currentUser, FileSystemRights.FullControl, AccessControlType.Allow);
                    security.SetAccessRule(rule);

                    // 移除继承的权限
                    security.SetAccessRuleProtection(true, false);
                    File.SetAccessControl(_tokenFilePath, security);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "无法设置 token 文件的安全权限");
                }
            }
            */

            return newToken;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "读取或生成 token 失败");
            throw;
        }
    }

    /// <summary>
    /// 生成新的随机 token
    /// </summary>
    private string GenerateNewToken()
    {
        var randomBytes = new byte[32];
        Random.Shared.NextBytes(randomBytes);
        return Convert.ToBase64String(randomBytes).TrimEnd('=');
    }

    /// <summary>
    /// 写入 hub.json 文件
    /// </summary>
    public void WriteHubJson(int port)
    {
        try
        {
            var hubRuntime = new HubRuntime
            {
                ProtocolVersion = 1,
                HttpBaseUrl = $"http://127.0.0.1:{port}",
                WsUrl = $"ws://127.0.0.1:{port}/ws",
                StartedAtUtc = DateTime.UtcNow
            };

            var tempPath = _hubJsonPath + ".tmp";
            File.WriteAllText(tempPath, System.Text.Json.JsonSerializer.Serialize(hubRuntime, new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true
            }));

            if (File.Exists(_hubJsonPath))
            {
                File.Delete(_hubJsonPath);
            }

            File.Move(tempPath, _hubJsonPath);
            _logger.LogInformation("写入 hub.json 文件: {Path}", _hubJsonPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "写入 hub.json 文件失败");
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
            // 可以添加一些清理逻辑，比如删除临时文件
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "清理资源失败");
        }
    }
}
