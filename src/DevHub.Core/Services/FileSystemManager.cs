using DevHub.Core.Models;
using Microsoft.Extensions.Logging;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace DevHub.Core.Services;

/// <summary>
/// 文件系统管理器，负责目录创建、token 管理和 hub.json 写入
/// </summary>
public class FileSystemManager
{
    private const int HubJsonReplaceMaxRetryCount = 40;
    private static readonly TimeSpan HubJsonReplaceRetryDelay = TimeSpan.FromMilliseconds(50);
    private readonly ILogger<FileSystemManager> _logger;
    private readonly RuntimeTuningOptions _runtimeTuningOptions;
    private readonly object _tokenSyncRoot = new();
    private readonly string _rootPath;
    private readonly string _runtimePath;
    private readonly string _definitionsPath;
    private readonly string _instancesPath;
    private readonly string _logsPath;
    private readonly string _tokenFilePath;
    private readonly string _hubJsonPath;
    private readonly DateTime _sessionStartedAtUtc;
    private bool _tokenPermissionEnsured;
    private bool _hubJsonPermissionEnsured;
    private bool _sessionTokenInitialized;
    private string? _sessionToken;

    /// <summary>
    /// 使用统一路径选项初始化文件系统管理器。
    /// </summary>
    /// <param name="logger">日志记录器。</param>
    /// <param name="runtimePathOptions">运行时路径选项。</param>
    public FileSystemManager(ILogger<FileSystemManager> logger, RuntimePathOptions runtimePathOptions)
        : this(logger, runtimePathOptions, RuntimeTuningOptions.Default)
    {
    }

    /// <summary>
    /// 使用统一路径选项和运行时调优参数初始化文件系统管理器。
    /// </summary>
    /// <param name="logger">日志记录器。</param>
    /// <param name="runtimePathOptions">运行时路径选项。</param>
    /// <param name="runtimeTuningOptions">运行时调优参数。</param>
    public FileSystemManager(
        ILogger<FileSystemManager> logger,
        RuntimePathOptions runtimePathOptions,
        RuntimeTuningOptions runtimeTuningOptions)
    {
        _logger = logger;
        _runtimeTuningOptions = runtimeTuningOptions;
        _rootPath = runtimePathOptions.RootPath;
        _runtimePath = runtimePathOptions.RuntimePath;
        _definitionsPath = runtimePathOptions.DefinitionsPath;
        _instancesPath = runtimePathOptions.InstancesPath;
        _logsPath = runtimePathOptions.LogsPath;
        _tokenFilePath = runtimePathOptions.TokenFilePath;
        _hubJsonPath = runtimePathOptions.HubJsonPath;
        _sessionStartedAtUtc = DateTime.UtcNow;
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

            if (!Directory.Exists(_instancesPath))
            {
                Directory.CreateDirectory(_instancesPath);
                _logger.LogInformation("成功创建实例目录: {Path}", _instancesPath);
            }
            else
            {
                _logger.LogDebug("实例目录已存在: {Path}", _instancesPath);
            }

            if (!Directory.Exists(_logsPath))
            {
                Directory.CreateDirectory(_logsPath);
                _logger.LogInformation("成功创建日志目录: {Path}", _logsPath);
            }
            else
            {
                _logger.LogDebug("日志目录已存在: {Path}", _logsPath);
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
    /// 获取当前 Hub 会话 token。
    /// 首次调用时会按 Spec 要求轮换 token（每个 Hub 会话一个 token）。
    /// </summary>
    public string GetToken()
    {
        lock (_tokenSyncRoot)
        {
            try
            {
                _logger.LogDebug("开始处理 token 请求，文件路径: {FilePath}", _tokenFilePath);

                if (!_sessionTokenInitialized)
                {
                    _sessionToken = CreateAndPersistNewSessionToken();
                    _sessionTokenInitialized = true;
                }

                EnsureSessionTokenFileExists();
                return _sessionToken!;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "读取或生成 token 失败，文件路径: {FilePath}", _tokenFilePath);
                throw;
            }
        }
    }

    /// <summary>
    /// 生成并持久化当前 Hub 会话 token。
    /// </summary>
    private string CreateAndPersistNewSessionToken()
    {
        if (File.Exists(_tokenFilePath))
        {
            _logger.LogInformation("检测到历史 token，将按 Spec 在 Hub 启动时轮换 token，文件路径: {FilePath}", _tokenFilePath);
        }
        else
        {
            _logger.LogDebug("Token 文件不存在，将为当前 Hub 会话生成新 token，文件路径: {FilePath}", _tokenFilePath);
        }

        Directory.CreateDirectory(_runtimePath);

        var newToken = GenerateNewToken();
        _logger.LogDebug("成功生成新 token，长度: {TokenLength} 字符", newToken.Length);

        File.WriteAllText(_tokenFilePath, newToken);
        EnsureCurrentUserOnlyAccess(_tokenFilePath);
        _tokenPermissionEnsured = true;

        _logger.LogInformation("成功生成并写入当前 Hub 会话 token，文件路径: {FilePath}", _tokenFilePath);
        return newToken;
    }

    /// <summary>
    /// 确保 token 文件存在并与当前 Hub 会话 token 一致。
    /// </summary>
    private void EnsureSessionTokenFileExists()
    {
        if (string.IsNullOrEmpty(_sessionToken))
        {
            throw new InvalidOperationException("Hub 会话 token 尚未初始化。");
        }

        if (!File.Exists(_tokenFilePath))
        {
            _logger.LogWarning("检测到 token 文件丢失，正在恢复当前 Hub 会话 token，文件路径: {FilePath}", _tokenFilePath);
            Directory.CreateDirectory(_runtimePath);
            File.WriteAllText(_tokenFilePath, _sessionToken);
            _tokenPermissionEnsured = false;
        }

        if (!_tokenPermissionEnsured)
        {
            EnsureCurrentUserOnlyAccess(_tokenFilePath);
            _tokenPermissionEnsured = true;
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
        var tempPath = _hubJsonPath + ".tmp";
        try
        {
            _logger.LogDebug("开始写入 hub.json 文件，监听端口: {Port}, Hub版本: {HubVersion}", port, hubVersion);

            Directory.CreateDirectory(_runtimePath);
            _ = GetToken();

            var hubRuntime = new HubRuntime
            {
                ProtocolVersion = 1,
                HubVersion = hubVersion,
                Pid = Environment.ProcessId,
                HttpBaseUrl = $"http://127.0.0.1:{port}",
                WsUrl = $"ws://127.0.0.1:{port}/ws",
                TokenFile = _tokenFilePath,
                StartedAtUtc = _sessionStartedAtUtc,
                RuntimeTuning = new HubRuntimeTuning
                {
                    LeaseSeconds = _runtimeTuningOptions.LeaseSeconds,
                    OnlineThresholdSeconds = _runtimeTuningOptions.OnlineThresholdSeconds,
                    LaunchDedupeWindowSeconds = _runtimeTuningOptions.LaunchDedupeWindowSeconds
                }
            };

            File.WriteAllText(tempPath, System.Text.Json.JsonSerializer.Serialize(hubRuntime, new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true
            }));

            ReplaceHubJsonAtomically(tempPath);
            EnsureCurrentUserOnlyAccess(_hubJsonPath);
            _hubJsonPermissionEnsured = true;
            _logger.LogInformation("成功写入 hub.json 文件: {Path}", _hubJsonPath);
        }
        catch (Exception ex)
        {
            TryDeleteTempFile(tempPath);
            _logger.LogError(ex, "写入 hub.json 文件失败，文件路径: {Path}", _hubJsonPath);
            throw;
        }
    }

    /// <summary>
    /// 以原子方式替换 hub.json，并对临时文件占用场景执行短时重试。
    /// </summary>
    /// <param name="tempPath">临时文件路径。</param>
    private void ReplaceHubJsonAtomically(string tempPath)
    {
        Exception? lastException = null;

        for (var retryIndex = 0; retryIndex < HubJsonReplaceMaxRetryCount; retryIndex++)
        {
            try
            {
                File.Move(tempPath, _hubJsonPath, overwrite: true);
                return;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException || ex is IOException)
            {
                lastException = ex;
                if (retryIndex >= HubJsonReplaceMaxRetryCount - 1)
                {
                    break;
                }

                _logger.LogWarning(
                    ex,
                    "hub.json 原子替换失败，将重试，次数: {Retry}/{MaxRetry}，文件路径: {Path}",
                    retryIndex + 1,
                    HubJsonReplaceMaxRetryCount,
                    _hubJsonPath);
                System.Threading.Thread.Sleep(HubJsonReplaceRetryDelay);
            }
        }

        throw new IOException($"hub.json 原子替换失败，已达到最大重试次数: {HubJsonReplaceMaxRetryCount}", lastException);
    }

    /// <summary>
    /// 尝试删除临时文件，避免异常流程中残留脏数据。
    /// </summary>
    /// <param name="tempPath">临时文件路径。</param>
    private void TryDeleteTempFile(string tempPath)
    {
        try
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
        catch (Exception cleanupException)
        {
            _logger.LogWarning(cleanupException, "删除临时 hub.json 文件失败: {Path}", tempPath);
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
    [SupportedOSPlatform("windows")]
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
