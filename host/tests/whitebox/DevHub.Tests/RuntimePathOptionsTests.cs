namespace DevHub.Tests;

using DevHub.Core.Services;

/// <summary>
/// <see cref="RuntimePathOptions"/> 测试。
/// </summary>
[Collection(TestCollections.ProcessEnvironment)]
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
    public void Impl_Resolve_WithDataDirOverride_ShouldUseDerivedStandardLayout()
    {
        var dataDirectory = Path.Combine(_tempRoot, "data-env");

        using var dataScope = new EnvironmentVariableScope(RuntimePathOptions.DataDirEnvironmentVariable, dataDirectory);

        var options = RuntimePathOptions.Resolve();

        Assert.Equal(dataDirectory, options.RootPath);
        Assert.Equal(Path.Combine(dataDirectory, "runtime"), options.RuntimePath);
        Assert.Equal(Path.Combine(dataDirectory, "apps"), options.AppsPath);
        Assert.Equal(Path.Combine(dataDirectory, "apps", "definitions.json"), options.DefinitionsCatalogPath);
        Assert.Equal(Path.Combine(dataDirectory, "logs"), options.LogsPath);
        Assert.Equal(Path.Combine(dataDirectory, "runtime", "token.txt"), options.TokenFilePath);
        Assert.Equal(Path.Combine(dataDirectory, "runtime", "hub.json"), options.HubJsonPath);
        Assert.Equal(Path.Combine(dataDirectory, "runtime", "prev_hub.json"), options.PreviousHubJsonPath);
    }

    [Fact]
    public void Impl_Resolve_WithoutOverrides_ShouldUsePlatformConventionalRoot()
    {
        using var dataScope = new EnvironmentVariableScope(RuntimePathOptions.DataDirEnvironmentVariable, null);

        var options = RuntimePathOptions.Resolve();
        var expectedRoot = GetExpectedDefaultRootPath();

        Assert.Equal(expectedRoot, options.RootPath);
        Assert.Equal(Path.Combine(expectedRoot, "runtime"), options.RuntimePath);
        Assert.Equal(Path.Combine(expectedRoot, "apps"), options.AppsPath);
        Assert.Equal(Path.Combine(expectedRoot, "apps", "definitions.json"), options.DefinitionsCatalogPath);
        Assert.Equal(Path.Combine(expectedRoot, "logs"), options.LogsPath);
        Assert.Equal(Path.Combine(expectedRoot, "runtime", "token.txt"), options.TokenFilePath);
        Assert.Equal(Path.Combine(expectedRoot, "runtime", "hub.json"), options.HubJsonPath);
        Assert.Equal(Path.Combine(expectedRoot, "runtime", "prev_hub.json"), options.PreviousHubJsonPath);
    }

    [Fact]
    public void Impl_Resolve_WithRelativeDataDirOverride_ShouldNormalizeToAbsolutePath()
    {
        var currentDirectory = Directory.GetCurrentDirectory();
        var relativeDataDirectory = Path.Combine(".", "temp", "data-relative");

        using var dataScope = new EnvironmentVariableScope(RuntimePathOptions.DataDirEnvironmentVariable, relativeDataDirectory);
        var options = RuntimePathOptions.Resolve();

        var expectedRootPath = Path.GetFullPath(relativeDataDirectory, currentDirectory);
        Assert.Equal(expectedRootPath, options.RootPath);
        Assert.True(Path.IsPathFullyQualified(options.RuntimePath));
        Assert.True(Path.IsPathFullyQualified(options.AppsPath));
        Assert.True(Path.IsPathFullyQualified(options.DefinitionsCatalogPath));
        Assert.True(Path.IsPathFullyQualified(options.LogsPath));
        Assert.True(Path.IsPathFullyQualified(options.TokenFilePath));
        Assert.True(Path.IsPathFullyQualified(options.HubJsonPath));
        Assert.True(Path.IsPathFullyQualified(options.PreviousHubJsonPath));
        Assert.Equal(Path.Combine(expectedRootPath, "runtime"), options.RuntimePath);
        Assert.Equal(Path.Combine(expectedRootPath, "apps"), options.AppsPath);
        Assert.Equal(Path.Combine(expectedRootPath, "apps", "definitions.json"), options.DefinitionsCatalogPath);
        Assert.Equal(Path.Combine(expectedRootPath, "logs"), options.LogsPath);
        Assert.Equal(Path.Combine(expectedRootPath, "runtime", "token.txt"), options.TokenFilePath);
        Assert.Equal(Path.Combine(expectedRootPath, "runtime", "hub.json"), options.HubJsonPath);
        Assert.Equal(Path.Combine(expectedRootPath, "runtime", "prev_hub.json"), options.PreviousHubJsonPath);
    }

    [Fact]
    public void Impl_Create_WithEquivalentRoot_ShouldMatchResolveAndKeepStandardLayout()
    {
        var root = Path.Combine(_tempRoot, "root");

        using var dataScope = new EnvironmentVariableScope(RuntimePathOptions.DataDirEnvironmentVariable, root);

        var resolved = RuntimePathOptions.Resolve();
        var created = RuntimePathOptions.Create(root);

        Assert.Equal(created.RootPath, resolved.RootPath);
        Assert.Equal(created.RuntimePath, resolved.RuntimePath);
        Assert.Equal(created.AppsPath, resolved.AppsPath);
        Assert.Equal(created.DefinitionsCatalogPath, resolved.DefinitionsCatalogPath);
        Assert.Equal(created.LogsPath, resolved.LogsPath);
        Assert.Equal(created.TokenFilePath, resolved.TokenFilePath);
        Assert.Equal(created.HubJsonPath, resolved.HubJsonPath);
        Assert.Equal(created.PreviousHubJsonPath, resolved.PreviousHubJsonPath);
    }

    [Fact]
    public void Impl_Create_WithBlankRoot_ShouldThrowArgumentException()
    {
        var exception = Record.Exception(() => RuntimePathOptions.Create("  "));

        Assert.NotNull(exception);
        Assert.IsType<ArgumentException>(exception);
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
                GetExpectedUserHomePath(),
                "Library",
                "Application Support",
                "DevHub");
        }

        var xdgDataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        var dataHome = string.IsNullOrWhiteSpace(xdgDataHome)
            ? Path.Combine(GetExpectedUserHomePath(), ".local", "share")
            : xdgDataHome;

        return Path.Combine(dataHome, "DevHub");
    }

    private static string GetExpectedUserHomePath()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            return Path.GetFullPath(userProfile);
        }

        var personal = Environment.GetFolderPath(Environment.SpecialFolder.Personal);
        if (!string.IsNullOrWhiteSpace(personal))
        {
            return Path.GetFullPath(personal);
        }

        var home = Environment.GetEnvironmentVariable("HOME");
        if (!string.IsNullOrWhiteSpace(home))
        {
            return Path.GetFullPath(home);
        }

        throw new InvalidOperationException("测试环境未提供可用的用户主目录。");
    }
}
