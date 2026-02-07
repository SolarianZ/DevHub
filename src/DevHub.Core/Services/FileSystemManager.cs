using DevHub.Core.Models;
using Microsoft.Extensions.Logging;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Runtime.InteropServices;

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
    private bool _tokenPermissionEnsured;
    private bool _hubJsonPermissionEnsured;

    // 新增：接受自定义 definitionsPath 的构造函数
    public FileSystemManager(ILogger<FileSystemManager> logger, string? definitionsPath = null)
    {
        _logger = logger;

        // 计算数据目录路径
        var defaultRootPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DevHub");
        _rootPath = defaultRootPath;

        // DEVHUB_RUNTIME_DIR 仅覆盖 runtime 目录（Spec 约束）
        var runtimeOverride = Environment.GetEnvironmentVariable("DEVHUB_RUNTIME_DIR");
        _runtimePath = string.IsNullOrWhiteSpace(runtimeOverride)
            ? Path.Combine(defaultRootPath, "runtime")
            : runtimeOverride;

        // 优先使用参数，然后检查环境变量，最后使用默认路径
        if (!string.IsNullOrEmpty(definitionsPath))
        {
            _definitionsPath = definitionsPath;
        }
        else if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DEVHUB_APPDEFS_DIR")))
        {
            _definitionsPath = Environment.GetEnvironmentVariable("DEVHUB_APPDEFS_DIR")!;
        }
        else
        {
            _definitionsPath = Path.Combine(defaultRootPath, "apps", "definitions");
        }

        _tokenFilePath = Path.Combine(_runtimePath, "token.txt");
        _hubJsonPath = Path.Combine(_runtimePath, "hub.json");
    }

    // 保留默认构造函数以保持兼容性
    public FileSystemManager(ILogger<FileSystemManager> logger)
        : this(logger, null)
    {
    }

    /// <summary>
    /// 初始化文件系统目录结构
    /// </summary>
    public void InitializeDirectories()
    {
        try
        {
            _logger.LogDebug("开始初始化文件系统目录结构");

            if (!Directory.Exists(_rootPath))
            {
                Directory.CreateDirectory(_rootPath);
                _logger.LogInformation("成功创建根目录: {Path}", _rootPath);
            }
            else
            {
                _logger.LogDebug("根目录已存在: {Path}", _rootPath);
            }

            if (!Directory.Exists(_runtimePath))
            {
                Directory.CreateDirectory(_runtimePath);
                _logger.LogInformation("成功创建运行时目录: {Path}", _runtimePath);
            }
            else
            {
                _logger.LogDebug("运行时目录已存在: {Path}", _runtimePath);
            }

            if (!Directory.Exists(_definitionsPath))
            {
                Directory.CreateDirectory(_definitionsPath);
                _logger.LogInformation("成功创建应用程序定义目录: {Path}", _definitionsPath);
            }
            else
            {
                _logger.LogDebug("应用程序定义目录已存在: {Path}", _definitionsPath);
            }

            var instancesPath = Path.Combine(_rootPath, "apps", "instances");
            if (!Directory.Exists(instancesPath))
            {
                Directory.CreateDirectory(instancesPath);
                _logger.LogInformation("成功创建实例目录: {Path}", instancesPath);
            }
            else
            {
                _logger.LogDebug("实例目录已存在: {Path}", instancesPath);
            }

            var logsPath = Path.Combine(_rootPath, "logs");
            if (!Directory.Exists(logsPath))
            {
                Directory.CreateDirectory(logsPath);
                _logger.LogInformation("成功创建日志目录: {Path}", logsPath);
            }
            else
            {
                _logger.LogDebug("日志目录已存在: {Path}", logsPath);
            }

            _logger.LogDebug("文件系统目录结构初始化完成");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "初始化文件系统目录结构失败");
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
            _logger.LogDebug("开始处理 token 请求，文件路径: {FilePath}", _tokenFilePath);

            if (File.Exists(_tokenFilePath))
            {
                if (!_tokenPermissionEnsured)
                {
                    EnsureCurrentUserOnlyAccess(_tokenFilePath);
                    _tokenPermissionEnsured = true;
                }

                _logger.LogDebug("Token 文件存在，尝试读取现有 token");
                var existingToken = File.ReadAllText(_tokenFilePath).Trim();
                if (!string.IsNullOrEmpty(existingToken))
                {
                    _logger.LogInformation("成功读取现有 token，文件路径: {FilePath}", _tokenFilePath);
                    return existingToken;
                }
                _logger.LogWarning("Token 文件存在但内容为空，将生成新 token，文件路径: {FilePath}", _tokenFilePath);
            }
            else
            {
                _logger.LogDebug("Token 文件不存在，将生成新 token，文件路径: {FilePath}", _tokenFilePath);
            }

            _logger.LogDebug("开始生成新 token");
            var newToken = GenerateNewToken();
            _logger.LogDebug("成功生成新 token，长度: {TokenLength} 字符", newToken.Length);

            _logger.LogDebug("开始写入新 token 到文件: {FilePath}", _tokenFilePath);
            File.WriteAllText(_tokenFilePath, newToken);
            EnsureCurrentUserOnlyAccess(_tokenFilePath);
            _tokenPermissionEnsured = true;
            _logger.LogInformation("成功生成新 token 并写入文件，文件路径: {FilePath}", _tokenFilePath);

            return newToken;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "读取或生成 token 失败，文件路径: {FilePath}", _tokenFilePath);
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
            _logger.LogDebug("开始写入 hub.json 文件，监听端口: {Port}, Hub版本: {HubVersion}", port, hubVersion);

            Directory.CreateDirectory(_runtimePath);

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
            EnsureCurrentUserOnlyAccess(_hubJsonPath);
            _hubJsonPermissionEnsured = true;
            _logger.LogInformation("成功写入 hub.json 文件: {Path}", _hubJsonPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "写入 hub.json 文件失败，文件路径: {Path}", _hubJsonPath);
            throw;
        }
    }

    /// <summary>
    /// 设置文件仅当前用户可访问
    /// </summary>
    private void EnsureCurrentUserOnlyAccess(string filePath)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                EnsureCurrentUserOnlyAccessOnWindows(filePath);
                return;
            }

            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            {
                EnsureCurrentUserOnlyAccessOnUnix(filePath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "无法设置文件权限（仅当前用户可访问），文件路径: {FilePath}", filePath);
        }
    }

    /// <summary>
    /// 在 Windows 平台设置 ACL，仅当前用户可访问
    /// </summary>
    private void EnsureCurrentUserOnlyAccessOnWindows(string filePath)
    {
        _logger.LogDebug("尝试设置 Windows 文件 ACL，文件路径: {FilePath}", filePath);

        var fileInfo = new FileInfo(filePath);
        var security = fileInfo.GetAccessControl(AccessControlSections.Access);
        var currentUser = WindowsIdentity.GetCurrent().Name;
        var rule = new FileSystemAccessRule(currentUser, FileSystemRights.FullControl, AccessControlType.Allow);

        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.ResetAccessRule(rule);
        fileInfo.SetAccessControl(security);

        _logger.LogInformation("已成功设置 Windows 文件 ACL（仅当前用户可访问），文件路径: {FilePath}", filePath);
    }

    /// <summary>
    /// 在 Unix 平台设置权限为 0600
    /// </summary>
    private void EnsureCurrentUserOnlyAccessOnUnix(string filePath)
    {
        _logger.LogDebug("尝试设置 Unix 文件权限为 0600，文件路径: {FilePath}", filePath);

        const int OwnerReadWrite = 0x180; // 0600
        var result = Chmod(filePath, OwnerReadWrite);
        if (result != 0)
        {
            var errorCode = Marshal.GetLastWin32Error();
            throw new InvalidOperationException($"chmod 失败，错误码: {errorCode}");
        }

        _logger.LogInformation("已成功设置 Unix 文件权限为 0600，文件路径: {FilePath}", filePath);
    }

    [DllImport("libc", SetLastError = true, EntryPoint = "chmod")]
    private static extern int Chmod(string path, int mode);

    /// <summary>
    /// 轻量确保运行时文件可用，避免运行期间文件被删除导致发现失败
    /// </summary>
    public void EnsureRuntimeArtifacts(int? port = null)
    {
        InitializeDirectories();
        GetToken();

        if (!_hubJsonPermissionEnsured && File.Exists(_hubJsonPath))
        {
            EnsureCurrentUserOnlyAccess(_hubJsonPath);
            _hubJsonPermissionEnsured = true;
        }

        if (port.HasValue && port.Value > 0 && !File.Exists(_hubJsonPath))
        {
            _logger.LogWarning("检测到 hub.json 丢失，尝试按当前端口重建，端口: {Port}", port.Value);
            WriteHubJson(port.Value);
        }
    }

    /// <summary>
    /// 清理资源
    /// </summary>
    public void Cleanup()
    {
        try
        {
            _logger.LogDebug("开始清理资源");
            // 可以添加一些清理逻辑，比如删除临时文件
            _logger.LogInformation("资源清理完成");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "清理资源失败");
        }
    }
}
