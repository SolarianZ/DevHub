namespace DevHub.Core.Services;

/// <summary>
/// DevHub 运行时路径选项。
/// </summary>
/// <remarks>
/// 用于统一解析 Runtime 根目录、定义目录、日志目录等路径，避免多处重复解析环境变量与默认值。
/// </remarks>
public sealed class RuntimePathOptions
{
    /// <summary>
    /// 运行时目录环境变量名。
    /// </summary>
    public const string RuntimeDirEnvironmentVariable = "DEVHUB_RUNTIME_DIR";

    /// <summary>
    /// 应用定义目录环境变量名。
    /// </summary>
    public const string AppDefinitionsDirEnvironmentVariable = "DEVHUB_APPDEFS_DIR";

    /// <summary>
    /// 日志目录环境变量名。
    /// </summary>
    public const string LogDirEnvironmentVariable = "DEVHUB_LOG_DIR";

    private RuntimePathOptions(
        string rootPath,
        string runtimePath,
        string definitionsPath,
        string instancesPath,
        string logsPath)
    {
        RootPath = rootPath;
        RuntimePath = runtimePath;
        DefinitionsPath = definitionsPath;
        InstancesPath = instancesPath;
        LogsPath = logsPath;
        TokenFilePath = Path.Combine(RuntimePath, "token.txt");
        HubJsonPath = Path.Combine(RuntimePath, "hub.json");
    }

    /// <summary>
    /// DevHub 数据根目录。
    /// </summary>
    public string RootPath { get; }

    /// <summary>
    /// 运行时目录（存放 <c>hub.json</c> 与 <c>token.txt</c>）。
    /// </summary>
    public string RuntimePath { get; }

    /// <summary>
    /// 应用定义目录。
    /// </summary>
    public string DefinitionsPath { get; }

    /// <summary>
    /// 实例目录。
    /// </summary>
    public string InstancesPath { get; }

    /// <summary>
    /// 日志目录。
    /// </summary>
    public string LogsPath { get; }

    /// <summary>
    /// 访问令牌文件路径。
    /// </summary>
    public string TokenFilePath { get; }

    /// <summary>
    /// 发现文件 <c>hub.json</c> 路径。
    /// </summary>
    public string HubJsonPath { get; }

    /// <summary>
    /// 解析当前进程的路径选项。
    /// </summary>
    /// <param name="definitionsPathOverride">可选的定义目录显式覆盖（优先级最高）。</param>
    /// <returns>解析后的路径配置。</returns>
    public static RuntimePathOptions Resolve(string? definitionsPathOverride = null)
    {
        var defaultRootPath = GetDefaultRootPath();

        var runtimeOverride = Environment.GetEnvironmentVariable(RuntimeDirEnvironmentVariable);
        var runtimePath = NormalizePath(string.IsNullOrWhiteSpace(runtimeOverride)
            ? Path.Combine(defaultRootPath, "runtime")
            : runtimeOverride);

        string definitionsPath;
        if (!string.IsNullOrWhiteSpace(definitionsPathOverride))
        {
            definitionsPath = NormalizePath(definitionsPathOverride);
        }
        else
        {
            var definitionsOverride = Environment.GetEnvironmentVariable(AppDefinitionsDirEnvironmentVariable);
            definitionsPath = NormalizePath(string.IsNullOrWhiteSpace(definitionsOverride)
                ? Path.Combine(defaultRootPath, "apps", "definitions")
                : definitionsOverride);
        }

        var logOverride = Environment.GetEnvironmentVariable(LogDirEnvironmentVariable);
        var logsPath = NormalizePath(string.IsNullOrWhiteSpace(logOverride)
            ? Path.Combine(defaultRootPath, "logs")
            : logOverride);

        return new RuntimePathOptions(
            rootPath: defaultRootPath,
            runtimePath: runtimePath,
            definitionsPath: definitionsPath,
            instancesPath: Path.Combine(defaultRootPath, "apps", "instances"),
            logsPath: logsPath);
    }

    /// <summary>
    /// 使用显式路径创建路径选项（仅测试场景）。
    /// </summary>
    /// <remarks>
    /// 该方法仅用于测试工程构造完全隔离的运行时目录，
    /// 生产代码应统一通过 <see cref="Resolve(string?)"/> 解析路径，避免规则分叉。
    /// </remarks>
    /// <param name="rootPath">DevHub 数据根目录。</param>
    /// <param name="runtimePath">运行时目录。</param>
    /// <param name="definitionsPath">应用定义目录。</param>
    /// <param name="instancesPath">实例目录（可选，未提供时默认使用 root/apps/instances）。</param>
    /// <param name="logsPath">日志目录（可选，未提供时默认使用 root/logs）。</param>
    /// <returns>解析后的路径配置。</returns>
    internal static RuntimePathOptions Create(
        string rootPath,
        string runtimePath,
        string definitionsPath,
        string? instancesPath = null,
        string? logsPath = null)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            throw new ArgumentException("根目录不能为空。", nameof(rootPath));
        }

        if (string.IsNullOrWhiteSpace(runtimePath))
        {
            throw new ArgumentException("运行时目录不能为空。", nameof(runtimePath));
        }

        if (string.IsNullOrWhiteSpace(definitionsPath))
        {
            throw new ArgumentException("应用定义目录不能为空。", nameof(definitionsPath));
        }

        return new RuntimePathOptions(
            rootPath: NormalizePath(rootPath),
            runtimePath: NormalizePath(runtimePath),
            definitionsPath: NormalizePath(definitionsPath),
            instancesPath: string.IsNullOrWhiteSpace(instancesPath)
                ? Path.Combine(NormalizePath(rootPath), "apps", "instances")
                : NormalizePath(instancesPath),
            logsPath: string.IsNullOrWhiteSpace(logsPath)
                ? Path.Combine(NormalizePath(rootPath), "logs")
                : NormalizePath(logsPath));
    }

    private static string GetDefaultRootPath()
    {
        if (OperatingSystem.IsWindows())
        {
            return NormalizePath(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DevHub"));
        }

        if (OperatingSystem.IsMacOS())
        {
            return NormalizePath(Path.Combine(
                GetUserHomePath(),
                "Library",
                "Application Support",
                "DevHub"));
        }

        var xdgDataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        var dataHome = string.IsNullOrWhiteSpace(xdgDataHome)
            ? Path.Combine(GetUserHomePath(), ".local", "share")
            : xdgDataHome;

        return NormalizePath(Path.Combine(dataHome, "DevHub"));
    }

    private static string GetUserHomePath()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            return NormalizePath(userProfile);
        }

        var personal = Environment.GetFolderPath(Environment.SpecialFolder.Personal);
        if (!string.IsNullOrWhiteSpace(personal))
        {
            return NormalizePath(personal);
        }

        var home = Environment.GetEnvironmentVariable("HOME");
        if (!string.IsNullOrWhiteSpace(home))
        {
            return NormalizePath(home);
        }

        throw new InvalidOperationException("无法解析当前用户主目录。");
    }

    private static string NormalizePath(string path)
    {
        return Path.GetFullPath(path);
    }
}
