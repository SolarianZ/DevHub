namespace DevHub.Host.Tests;

using DevHub.Core.Services;
using DevHub.Host.Runtime;
using Microsoft.Extensions.Logging;
using Moq;

/// <summary>
/// HostDataDirectoryInitializer 测试。
/// </summary>
[Trait("Category", "Impl")]
public sealed class HostDataDirectoryInitializerTests : IDisposable
{
    private readonly string _tempDirectory;

    /// <summary>
    /// 初始化测试上下文。
    /// </summary>
    public HostDataDirectoryInitializerTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "DevHubHostDataDirectoryInitializerTests", Guid.NewGuid().ToString("N"));
    }

    [Fact]
    public void Impl_InitializeDirectories_ShouldCreateHostDataDirectoryLayout()
    {
        var runtimeOptions = RuntimePathOptions.Create(_tempDirectory);
        var initializer = new HostDataDirectoryInitializer(
            Mock.Of<ILogger<HostDataDirectoryInitializer>>(),
            runtimeOptions);

        initializer.InitializeDirectories();

        Assert.True(Directory.Exists(runtimeOptions.RootPath));
        Assert.True(Directory.Exists(runtimeOptions.RuntimePath));
        Assert.True(Directory.Exists(runtimeOptions.DefinitionsPath));
        Assert.True(Directory.Exists(runtimeOptions.InstancesPath));
        Assert.True(Directory.Exists(runtimeOptions.LogsPath));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDirectory))
            {
                Directory.Delete(_tempDirectory, recursive: true);
            }
        }
        catch
        {
        }
    }
}
