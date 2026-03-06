namespace DevHub.Tests;

using DevHub.Core.Services;

/// <summary>
/// <see cref="RuntimePathOptions"/> 测试。
/// </summary>
[Trait("Category", "Impl")]
public class RuntimePathOptionsTests : IDisposable
{
    private readonly string _tempRoot;

    /// <summary>
    /// 初始化测试上下文。
    /// </summary>
    public RuntimePathOptionsTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "DevHubRuntimePathOptionsTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    [Fact]
    public void Impl_Resolve_WithOverrides_ShouldUseEnvironmentOverrides()
    {
        var runtimeOverride = Path.Combine(_tempRoot, "runtime-env");
        var definitionsOverride = Path.Combine(_tempRoot, "definitions-env");
        var logOverride = Path.Combine(_tempRoot, "logs-env");

        using var runtimeScope = new EnvironmentVariableScope(RuntimePathOptions.RuntimeDirEnvironmentVariable, runtimeOverride);
        using var definitionsScope = new EnvironmentVariableScope(RuntimePathOptions.AppDefinitionsDirEnvironmentVariable, definitionsOverride);
        using var logScope = new EnvironmentVariableScope(RuntimePathOptions.LogDirEnvironmentVariable, logOverride);

        var options = RuntimePathOptions.Resolve();

        Assert.Equal(runtimeOverride, options.RuntimePath);
        Assert.Equal(definitionsOverride, options.DefinitionsPath);
        Assert.Equal(logOverride, options.LogsPath);
        Assert.Equal(Path.Combine(runtimeOverride, "token.txt"), options.TokenFilePath);
        Assert.Equal(Path.Combine(runtimeOverride, "hub.json"), options.HubJsonPath);
    }

    [Fact]
    public void Impl_Resolve_WithDefinitionsOverride_ShouldPreferArgument()
    {
        var definitionsFromEnv = Path.Combine(_tempRoot, "definitions-env");
        var definitionsFromArg = Path.Combine(_tempRoot, "definitions-arg");

        using var definitionsScope = new EnvironmentVariableScope(RuntimePathOptions.AppDefinitionsDirEnvironmentVariable, definitionsFromEnv);
        var options = RuntimePathOptions.Resolve(definitionsFromArg);

        Assert.Equal(definitionsFromArg, options.DefinitionsPath);
    }

    [Fact]
    public void Impl_Resolve_WithoutOverrides_ShouldUsePlatformConventionalRoot()
    {
        using var runtimeScope = new EnvironmentVariableScope(RuntimePathOptions.RuntimeDirEnvironmentVariable, null);
        using var definitionsScope = new EnvironmentVariableScope(RuntimePathOptions.AppDefinitionsDirEnvironmentVariable, null);
        using var logScope = new EnvironmentVariableScope(RuntimePathOptions.LogDirEnvironmentVariable, null);

        var options = RuntimePathOptions.Resolve();
        var expectedRoot = GetExpectedDefaultRootPath();

        Assert.Equal(expectedRoot, options.RootPath);
        Assert.Equal(Path.Combine(expectedRoot, "runtime"), options.RuntimePath);
        Assert.Equal(Path.Combine(expectedRoot, "apps", "definitions"), options.DefinitionsPath);
        Assert.Equal(Path.Combine(expectedRoot, "apps", "instances"), options.InstancesPath);
        Assert.Equal(Path.Combine(expectedRoot, "logs"), options.LogsPath);
        Assert.Equal(Path.Combine(expectedRoot, "runtime", "token.txt"), options.TokenFilePath);
        Assert.Equal(Path.Combine(expectedRoot, "runtime", "hub.json"), options.HubJsonPath);
    }

    [Fact]
    public void Impl_Resolve_WithRelativeRuntimeOverride_ShouldNormalizeToAbsolutePath()
    {
        var currentDirectory = Directory.GetCurrentDirectory();
        var relativeRuntimeOverride = Path.Combine(".", "temp", "runtime-relative");

        using var runtimeScope = new EnvironmentVariableScope(RuntimePathOptions.RuntimeDirEnvironmentVariable, relativeRuntimeOverride);
        var options = RuntimePathOptions.Resolve();

        var expectedRuntimePath = Path.GetFullPath(relativeRuntimeOverride, currentDirectory);
        Assert.Equal(expectedRuntimePath, options.RuntimePath);
        Assert.True(Path.IsPathFullyQualified(options.RuntimePath));
        Assert.True(Path.IsPathFullyQualified(options.TokenFilePath));
        Assert.True(Path.IsPathFullyQualified(options.HubJsonPath));
        Assert.Equal(Path.Combine(expectedRuntimePath, "token.txt"), options.TokenFilePath);
        Assert.Equal(Path.Combine(expectedRuntimePath, "hub.json"), options.HubJsonPath);
    }

    [Fact]
    public void Impl_Create_WithEquivalentInputs_ShouldMatchResolveOverlappingFields_AndKeepIsolatedLayout()
    {
        var root = Path.Combine(_tempRoot, "root");
        var runtime = Path.Combine(root, "runtime-custom");
        var definitions = Path.Combine(root, "definitions-custom");
        var logs = Path.Combine(root, "logs-custom");
        var defaultRoot = GetExpectedDefaultRootPath();

        using var runtimeScope = new EnvironmentVariableScope(RuntimePathOptions.RuntimeDirEnvironmentVariable, runtime);
        using var definitionsScope = new EnvironmentVariableScope(RuntimePathOptions.AppDefinitionsDirEnvironmentVariable, definitions);
        using var logScope = new EnvironmentVariableScope(RuntimePathOptions.LogDirEnvironmentVariable, logs);

        var resolved = RuntimePathOptions.Resolve();
        var created = RuntimePathOptions.Create(
            rootPath: root,
            runtimePath: runtime,
            definitionsPath: definitions,
            logsPath: logs);

        Assert.Equal(created.RuntimePath, resolved.RuntimePath);
        Assert.Equal(created.DefinitionsPath, resolved.DefinitionsPath);
        Assert.Equal(created.LogsPath, resolved.LogsPath);
        Assert.Equal(created.TokenFilePath, resolved.TokenFilePath);
        Assert.Equal(created.HubJsonPath, resolved.HubJsonPath);

        Assert.Equal(Path.Combine(root, "apps", "instances"), created.InstancesPath);
        Assert.Equal(Path.Combine(defaultRoot, "apps", "instances"), resolved.InstancesPath);

        Assert.Equal(Path.Combine(created.RuntimePath, "token.txt"), created.TokenFilePath);
        Assert.Equal(Path.Combine(created.RuntimePath, "hub.json"), created.HubJsonPath);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch
        {
        }
    }

    private static string GetExpectedDefaultRootPath()
    {
        if (OperatingSystem.IsWindows())
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DevHub");
        }

        if (OperatingSystem.IsMacOS())
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Personal),
                "Library",
                "Application Support",
                "DevHub");
        }

        var xdgDataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        var dataHome = string.IsNullOrWhiteSpace(xdgDataHome)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Personal), ".local", "share")
            : xdgDataHome;

        return Path.Combine(dataHome, "DevHub");
    }
}

