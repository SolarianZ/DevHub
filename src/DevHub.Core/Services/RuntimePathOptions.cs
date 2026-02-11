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
        var defaultRootPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DevHub");

        var runtimeOverride = Environment.GetEnvironmentVariable(RuntimeDirEnvironmentVariable);
        var runtimePath = string.IsNullOrWhiteSpace(runtimeOverride)
            ? Path.Combine(defaultRootPath, "runtime")
            : runtimeOverride;

        string definitionsPath;
        if (!string.IsNullOrWhiteSpace(definitionsPathOverride))
        {
            definitionsPath = definitionsPathOverride;
        }
        else
        {
            var definitionsOverride = Environment.GetEnvironmentVariable(AppDefinitionsDirEnvironmentVariable);
            definitionsPath = string.IsNullOrWhiteSpace(definitionsOverride)
                ? Path.Combine(defaultRootPath, "apps", "definitions")
                : definitionsOverride;
        }

        var logOverride = Environment.GetEnvironmentVariable(LogDirEnvironmentVariable);
        var logsPath = string.IsNullOrWhiteSpace(logOverride)
            ? Path.Combine(defaultRootPath, "logs")
            : logOverride;

        return new RuntimePathOptions(
            rootPath: defaultRootPath,
            runtimePath: runtimePath,
            definitionsPath: definitionsPath,
            instancesPath: Path.Combine(defaultRootPath, "apps", "instances"),
            logsPath: logsPath);
    }
}
