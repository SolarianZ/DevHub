namespace DevHub.Core.Services;

/// <summary>
/// DevHub 运行时路径选项。
/// </summary>
/// <remarks>
/// 用于统一解析数据根目录及其固定派生子目录，避免多处重复解析环境变量与默认值。
/// </remarks>
public sealed class RuntimePathOptions
{
    /// <summary>
    /// 数据根目录环境变量名。
    /// </summary>
    public const string DataDirEnvironmentVariable = "DEVHUB_DATA_DIR";

    private RuntimePathOptions(string rootPath)
    {
        RootPath = rootPath;
        RuntimePath = Path.Combine(rootPath, "runtime");
        AppsPath = Path.Combine(rootPath, "apps");
        DefinitionsCatalogPath = Path.Combine(AppsPath, "definitions.json");
        LogsPath = Path.Combine(rootPath, "logs");
        TokenFilePath = Path.Combine(RuntimePath, "token.txt");
        HubJsonPath = Path.Combine(RuntimePath, "hub.json");
        PreviousHubJsonPath = Path.Combine(RuntimePath, "prev_hub.json");
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
    /// 应用运行时目录。
    /// </summary>
    public string AppsPath { get; }

    /// <summary>
    /// 应用定义目录索引文件路径。
    /// </summary>
    public string DefinitionsCatalogPath { get; }

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
    /// 上一次 Hub 退出时保留的发现文件 <c>prev_hub.json</c> 路径。
    /// </summary>
    public string PreviousHubJsonPath { get; }

    /// <summary>
    /// 解析当前进程的路径选项。
    /// </summary>
    /// <returns>解析后的路径配置。</returns>
    public static RuntimePathOptions Resolve()
    {
        var configuredDataDir = Environment.GetEnvironmentVariable(DataDirEnvironmentVariable);
        var rootPath = NormalizePath(string.IsNullOrWhiteSpace(configuredDataDir)
            ? GetDefaultRootPath()
            : configuredDataDir);

        return new RuntimePathOptions(rootPath);
    }

    /// <summary>
    /// 使用显式路径创建路径选项（仅测试场景）。
    /// </summary>
    /// <remarks>
    /// 该方法仅用于测试工程构造完全隔离的数据根目录，
    /// 生产代码应统一通过 <see cref="Resolve"/> 解析路径，避免规则分叉。
    /// </remarks>
    /// <param name="rootPath">DevHub 数据根目录。</param>
    /// <returns>解析后的路径配置。</returns>
    internal static RuntimePathOptions Create(string rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            throw new ArgumentException("根目录不能为空。", nameof(rootPath));
        }

        return new RuntimePathOptions(NormalizePath(rootPath));
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
        var fullPath = Path.GetFullPath(path);
        return Path.TrimEndingDirectorySeparator(fullPath);
    }
}
