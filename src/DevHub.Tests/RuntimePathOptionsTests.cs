namespace DevHub.Tests;

using DevHub.Core.Services;

/// <summary>
/// <see cref="RuntimePathOptions"/> 测试。
/// </summary>
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
    public void Resolve_WithOverrides_ShouldUseEnvironmentOverrides()
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
    public void Resolve_WithDefinitionsOverride_ShouldPreferArgument()
    {
        var definitionsFromEnv = Path.Combine(_tempRoot, "definitions-env");
        var definitionsFromArg = Path.Combine(_tempRoot, "definitions-arg");

        using var definitionsScope = new EnvironmentVariableScope(RuntimePathOptions.AppDefinitionsDirEnvironmentVariable, definitionsFromEnv);
        var options = RuntimePathOptions.Resolve(definitionsFromArg);

        Assert.Equal(definitionsFromArg, options.DefinitionsPath);
    }

    [Fact]
    public void Create_WithEquivalentInputs_ShouldMatchResolveOverlappingFields_AndKeepIsolatedLayout()
    {
        var root = Path.Combine(_tempRoot, "root");
        var runtime = Path.Combine(root, "runtime-custom");
        var definitions = Path.Combine(root, "definitions-custom");
        var logs = Path.Combine(root, "logs-custom");
        var defaultRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DevHub");

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
}
