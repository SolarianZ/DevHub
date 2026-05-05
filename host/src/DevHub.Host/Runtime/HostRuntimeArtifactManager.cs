using DevHub.Core.Models;
using Microsoft.Extensions.Logging;
using System.Diagnostics.CodeAnalysis;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using DevHub.Core.Services;

namespace DevHub.Host.Runtime;

/// <summary>
/// Host 运行时产物管理器，负责 runtime 目录下的 token、hub.json 与 lease 生命周期。
/// </summary>
public sealed class HostRuntimeArtifactManager : IDisposable
{
    private const int HubJsonReplaceMaxRetryCount = 40;
    private static readonly TimeSpan HubJsonReplaceRetryDelay = TimeSpan.FromMilliseconds(50);
    private static readonly Encoding RuntimeFileEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    private static readonly JsonSerializerOptions HubJsonSerializerOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
    private readonly ILogger<HostRuntimeArtifactManager> _logger;
    private readonly RuntimeTuningOptions _runtimeTuningOptions;
    private readonly object _tokenSyncRoot = new();
    private readonly object _hubJsonLeaseSyncRoot = new();
    private readonly string _runtimePath;
    private readonly string _tokenFilePath;
    private readonly string _hubJsonPath;
    private readonly string _previousHubJsonPath;
    private readonly DateTime _sessionStartedAtUtc;
    private readonly string? _defaultHubVersion;
    private readonly Action<string>? _enforceCurrentUserOnlyAccessOverride;
    private bool _tokenPermissionEnsured;
    private bool _hubJsonPermissionEnsured;
    private bool _hubJsonLeaseRequested;
    private bool _sessionTokenInitialized;
    private bool _disposed;
    private string? _sessionToken;
    private FileStream? _hubJsonLeaseStream;

    /// <summary>
    /// 使用统一路径选项初始化 Host 运行时产物管理器。
    /// </summary>
    /// <param name="logger">日志记录器。</param>
    /// <param name="runtimePathOptions">运行时路径选项。</param>
    public HostRuntimeArtifactManager(ILogger<HostRuntimeArtifactManager> logger, RuntimePathOptions runtimePathOptions)
        : this(logger, runtimePathOptions, RuntimeTuningOptions.Default, defaultHubVersion: null)
    {
    }

    /// <summary>
    /// 使用统一路径选项和运行时调优参数初始化 Host 运行时产物管理器。
    /// </summary>
    /// <param name="logger">日志记录器。</param>
    /// <param name="runtimePathOptions">运行时路径选项。</param>
    /// <param name="runtimeTuningOptions">运行时调优参数。</param>
    public HostRuntimeArtifactManager(
        ILogger<HostRuntimeArtifactManager> logger,
        RuntimePathOptions runtimePathOptions,
        RuntimeTuningOptions runtimeTuningOptions,
        string? defaultHubVersion = null)
        : this(
            logger,
            runtimePathOptions,
            runtimeTuningOptions,
            defaultHubVersion,
            enforceCurrentUserOnlyAccessOverride: null)
    {
    }

    internal HostRuntimeArtifactManager(
        ILogger<HostRuntimeArtifactManager> logger,
        RuntimePathOptions runtimePathOptions,
        RuntimeTuningOptions runtimeTuningOptions,
        string? defaultHubVersion,
        Action<string>? enforceCurrentUserOnlyAccessOverride)
    {
        _logger = logger;
        _runtimeTuningOptions = runtimeTuningOptions;
        _runtimePath = runtimePathOptions.RuntimePath;
        _tokenFilePath = runtimePathOptions.TokenFilePath;
        _hubJsonPath = runtimePathOptions.HubJsonPath;
        _previousHubJsonPath = runtimePathOptions.PreviousHubJsonPath;
        _sessionStartedAtUtc = DateTime.UtcNow;
        _defaultHubVersion = NormalizeHubVersion(defaultHubVersion);
        _enforceCurrentUserOnlyAccessOverride = enforceCurrentUserOnlyAccessOverride;
    }

    /// <summary>
    /// 确保 runtime 目录存在。
    /// </summary>
    public void EnsureRuntimeDirectory()
    {
        try
        {
            if (!Directory.Exists(_runtimePath))
            {
                Directory.CreateDirectory(_runtimePath);
                _logger.LogInformation("成功创建运行时目录: {Path}", _runtimePath);
            }
            else
            {
                _logger.LogDebug("运行时目录已存在: {Path}", _runtimePath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "初始化运行时目录失败");
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

        EnsureRuntimeDirectory();

        var newToken = GenerateNewToken();
        _logger.LogDebug("成功生成新 token，长度: {TokenLength} 字符", newToken.Length);

        WriteRestrictedAtomicTextFile(_tokenFilePath, newToken);
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
            EnsureRuntimeDirectory();
            WriteRestrictedAtomicTextFile(_tokenFilePath, _sessionToken);
            _tokenPermissionEnsured = true;
        }

        if (!_tokenPermissionEnsured)
        {
            EnsureRequiredCurrentUserOnlyAccess(_tokenFilePath);
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
        string? tempPath = null;
        var effectiveHubVersion = NormalizeHubVersion(hubVersion) ?? _defaultHubVersion;
        try
        {
            _logger.LogDebug("开始写入 hub.json 文件，监听端口: {Port}, Hub版本: {HubVersion}", port, effectiveHubVersion);

            EnsureRuntimeDirectory();
            _ = GetToken();
            ReleaseHubJsonLease();

            var hubRuntime = new HubRuntime
            {
                ProtocolVersion = 1,
                HubVersion = effectiveHubVersion,
                Pid = Environment.ProcessId,
                HttpBaseUrl = $"http://127.0.0.1:{port}",
                WsUrl = $"ws://127.0.0.1:{port}/ws",
                TokenFile = _tokenFilePath,
                StartedAtUtc = _sessionStartedAtUtc,
                RuntimeTuning = new HubRuntimeTuning
                {
                    LeaseSeconds = _runtimeTuningOptions.LeaseSeconds,
                    OnlineThresholdSeconds = _runtimeTuningOptions.OnlineThresholdSeconds,
                    LaunchDedupeWindowSeconds = _runtimeTuningOptions.LaunchDedupeWindowSeconds,
                    LaunchRegisterTimeoutSeconds = _runtimeTuningOptions.LaunchRegisterTimeoutSeconds
                }
            };

            tempPath = WriteRestrictedTempTextFile(
                _hubJsonPath,
                JsonSerializer.Serialize(hubRuntime, HubJsonSerializerOptions));

            ReplaceHubJsonAtomically(tempPath);
            EnsureRequiredCurrentUserOnlyAccess(_hubJsonPath);
            _hubJsonPermissionEnsured = true;
            EnsureHubJsonLeaseIfRequested();
            _logger.LogInformation("成功写入 hub.json 文件: {Path}", _hubJsonPath);
        }
        catch (Exception ex)
        {
            if (tempPath is not null)
            {
                TryDeleteTempFile(tempPath);
            }

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
    /// 激活当前 Host 会话对 <c>hub.json</c> 的只读独占占用。
    /// </summary>
    public void ActivateHubJsonLease()
    {
        ThrowIfDisposed();

        lock (_hubJsonLeaseSyncRoot)
        {
            _hubJsonLeaseRequested = true;
        }

        EnsureHubJsonLeaseIfRequested();
    }

    private static string? NormalizeHubVersion(string? hubVersion)
    {
        return string.IsNullOrWhiteSpace(hubVersion)
            ? null
            : hubVersion.Trim();
    }

    /// <summary>
    /// 以受限临时文件和原子替换方式写入运行时安全文件。
    /// </summary>
    /// <param name="targetPath">目标文件路径。</param>
    /// <param name="content">文件内容。</param>
    private void WriteRestrictedAtomicTextFile(string targetPath, string content)
    {
        var tempPath = WriteRestrictedTempTextFile(targetPath, content);
        try
        {
            File.Move(tempPath, targetPath, overwrite: true);
            EnsureRequiredCurrentUserOnlyAccess(targetPath);
        }
        catch
        {
            TryDeleteTempFile(tempPath);
            throw;
        }
    }

    /// <summary>
    /// 创建受限临时文件，在写入内容前完成权限收敛。
    /// </summary>
    /// <param name="targetPath">目标文件路径。</param>
    /// <param name="content">文件内容。</param>
    /// <returns>已写入内容的临时文件路径。</returns>
    private string WriteRestrictedTempTextFile(string targetPath, string content)
    {
        var tempPath = CreateTempPathForTarget(targetPath);

        try
        {
            CreateEmptyRestrictedFile(tempPath);
            File.WriteAllText(tempPath, content, RuntimeFileEncoding);
            EnsureRequiredCurrentUserOnlyAccess(tempPath);
            return tempPath;
        }
        catch
        {
            TryDeleteTempFile(tempPath);
            throw;
        }
    }

    private string CreateTempPathForTarget(string targetPath)
    {
        var directory = Path.GetDirectoryName(targetPath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            directory = _runtimePath;
        }

        var targetFileName = Path.GetFileName(targetPath);
        return Path.Combine(directory, $".{targetFileName}.{Guid.NewGuid():N}.tmp");
    }

    private void CreateEmptyRestrictedFile(string tempPath)
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None
        };

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        using (new FileStream(tempPath, options))
        {
        }

        EnsureRequiredCurrentUserOnlyAccess(tempPath);
    }

    /// <summary>
    /// 设置文件仅当前用户可访问
    /// </summary>
    private void EnsureRequiredCurrentUserOnlyAccess(string filePath)
    {
        try
        {
            ApplyCurrentUserOnlyAccess(filePath);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"无法将运行时安全文件权限收敛为仅当前用户可访问: {filePath}",
                ex);
        }
    }

    private void TryEnsureCurrentUserOnlyAccess(string filePath)
    {
        try
        {
            ApplyCurrentUserOnlyAccess(filePath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "无法设置文件权限（仅当前用户可访问），文件路径: {FilePath}", filePath);
        }
    }

    private void ApplyCurrentUserOnlyAccess(string filePath)
    {
        if (_enforceCurrentUserOnlyAccessOverride is not null)
        {
            _enforceCurrentUserOnlyAccessOverride(filePath);
            return;
        }

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

    /// <summary>
    /// 在 Windows 平台设置 ACL，仅当前用户可访问
    /// </summary>
    [SupportedOSPlatform("windows")]
    [ExcludeFromCodeCoverage]
    private void EnsureCurrentUserOnlyAccessOnWindows(string filePath)
    {
        _logger.LogDebug("尝试设置 Windows 文件 ACL，文件路径: {FilePath}", filePath);

        var fileInfo = new FileInfo(filePath);
        var security = fileInfo.GetAccessControl(AccessControlSections.Access);
        var currentUserSid = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("无法解析当前 Windows 用户 SID。");

        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

        var explicitRules = security
            .GetAccessRules(includeExplicit: true, includeInherited: false, targetType: typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .ToArray();

        foreach (var explicitRule in explicitRules)
        {
            security.RemoveAccessRuleSpecific(explicitRule);
        }

        var rule = new FileSystemAccessRule(currentUserSid, FileSystemRights.FullControl, AccessControlType.Allow);
        security.AddAccessRule(rule);
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
    /// 轻量确保运行时发现产物可用，避免运行期间文件被删除导致发现失败。
    /// </summary>
    public void EnsureRuntimeArtifacts(int? port = null)
    {
        EnsureRuntimeDirectory();
        GetToken();

        if (!_hubJsonPermissionEnsured && File.Exists(_hubJsonPath))
        {
            EnsureRequiredCurrentUserOnlyAccess(_hubJsonPath);
            _hubJsonPermissionEnsured = true;
        }

        if (port.HasValue && port.Value > 0 && !File.Exists(_hubJsonPath))
        {
            _logger.LogWarning("检测到 hub.json 丢失，尝试按当前端口重建，端口: {Port}", port.Value);
            WriteHubJson(port.Value);
        }

        EnsureHubJsonLeaseIfRequested();
    }

    /// <summary>
    /// 清理资源
    /// </summary>
    public void Cleanup()
    {
        try
        {
            _logger.LogDebug("开始清理资源");
            ReleaseHubJsonLease();

            if (File.Exists(_hubJsonPath))
            {
                RotateHubJsonToPreviousSnapshot();
                TryEnsureCurrentUserOnlyAccess(_previousHubJsonPath);
                _logger.LogInformation("已将 hub.json 迁移为 prev_hub.json: {Path}", _previousHubJsonPath);
            }

            lock (_hubJsonLeaseSyncRoot)
            {
                _hubJsonLeaseRequested = false;
            }

            _logger.LogInformation("资源清理完成");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "清理资源失败");
        }
    }

    private void RotateHubJsonToPreviousSnapshot()
    {
        Exception? lastException = null;

        for (var retryIndex = 0; retryIndex < HubJsonReplaceMaxRetryCount; retryIndex++)
        {
            try
            {
                Directory.CreateDirectory(_runtimePath);
                File.Move(_hubJsonPath, _previousHubJsonPath, overwrite: true);
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
                    "迁移 hub.json 到 prev_hub.json 失败，将重试，次数: {Retry}/{MaxRetry}，文件路径: {Path}",
                    retryIndex + 1,
                    HubJsonReplaceMaxRetryCount,
                    _hubJsonPath);
                System.Threading.Thread.Sleep(HubJsonReplaceRetryDelay);
            }
        }

        throw new IOException($"迁移 hub.json 到 prev_hub.json 失败，已达到最大重试次数: {HubJsonReplaceMaxRetryCount}", lastException);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Cleanup();
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    private void EnsureHubJsonLeaseIfRequested()
    {
        ThrowIfDisposed();

        lock (_hubJsonLeaseSyncRoot)
        {
            if (!_hubJsonLeaseRequested || _hubJsonLeaseStream is not null || !File.Exists(_hubJsonPath))
            {
                return;
            }

            _hubJsonLeaseStream = OpenHubJsonLeaseStream();
        }
    }

    private FileStream OpenHubJsonLeaseStream()
    {
        var leaseStream = new FileStream(
            _hubJsonPath,
            new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.Read
            });

        TryLockHubJsonForSharedReads(leaseStream);
        return leaseStream;
    }

    private void TryLockHubJsonForSharedReads(FileStream leaseStream)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        try
        {
            var length = leaseStream.Length;
            leaseStream.Lock(0, length > 0 ? length : 1);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException or NotSupportedException)
        {
            _logger.LogDebug(ex, "hub.json 共享读锁不可用，将仅保留文件共享模式占用: {Path}", _hubJsonPath);
        }
    }

    private void ReleaseHubJsonLease()
    {
        lock (_hubJsonLeaseSyncRoot)
        {
            if (_hubJsonLeaseStream is null)
            {
                return;
            }

            try
            {
                TryUnlockHubJson(_hubJsonLeaseStream);
                _hubJsonLeaseStream.Dispose();
            }
            finally
            {
                _hubJsonLeaseStream = null;
            }
        }
    }

    private void TryUnlockHubJson(FileStream leaseStream)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        try
        {
            var length = leaseStream.Length;
            leaseStream.Unlock(0, length > 0 ? length : 1);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException or NotSupportedException)
        {
            _logger.LogDebug(ex, "释放 hub.json 共享读锁时出现非阻断异常: {Path}", _hubJsonPath);
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
