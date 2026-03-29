namespace DevHub.Host.Tests;

using System.Reflection;
using DevHub.Core.Services;
using HostProgram = DevHub.Host.Program;

/// <summary>
/// Program 入口辅助逻辑测试。
/// </summary>
[Trait("Category", "Impl")]
public sealed class ProgramTests : IDisposable
{
    private readonly string _tempRoot;

    /// <summary>
    /// 初始化测试上下文。
    /// </summary>
    public ProgramTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "DevHubProgramTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    [Fact]
    public void Impl_ResolveContentRootPath_ShouldReturnAppBaseDirectory()
    {
        var contentRootPath = HostProgram.ResolveContentRootPath();

        Assert.Equal(Path.GetFullPath(AppContext.BaseDirectory), contentRootPath);
    }

    [Fact]
    public void Impl_BuildBootstrapConfiguration_ShouldOverrideSerilogFileSinkPath()
    {
        var contentRootPath = Path.Combine(_tempRoot, "content-root");
        var logsPath = Path.Combine(_tempRoot, "logs");
        Directory.CreateDirectory(contentRootPath);
        Directory.CreateDirectory(logsPath);

        File.WriteAllText(
            Path.Combine(contentRootPath, "appsettings.json"),
            """
            {
              "Serilog": {
                "WriteTo": [
                  {
                    "Name": "Console"
                  },
                  {
                    "Name": "File",
                    "Args": {
                      "path": "should-be-overridden.log"
                    }
                  }
                ]
              }
            }
            """);

        var configuration = HostProgram.BuildBootstrapConfiguration(contentRootPath, logsPath);

        Assert.Equal(
            Path.Combine(logsPath, "devhub-.log"),
            configuration["Serilog:WriteTo:1:Args:path"]);
    }

    [Fact]
    public void Impl_BuildSingleInstanceMutexName_WhenRootPathSame_ShouldReturnStableName()
    {
        var runtimePathOptions = RuntimePathOptions.Create(Path.Combine(_tempRoot, "data"));

        var first = InvokePrivateStatic<string>("BuildSingleInstanceMutexName", runtimePathOptions);
        var second = InvokePrivateStatic<string>("BuildSingleInstanceMutexName", runtimePathOptions);

        Assert.Equal(first, second);
        Assert.StartsWith(@"Local\DevHub_", first, StringComparison.Ordinal);
    }

    [Fact]
    public void Impl_BuildSingleInstanceMutexName_WhenRootPathDifferent_ShouldReturnDifferentName()
    {
        var first = InvokePrivateStatic<string>(
            "BuildSingleInstanceMutexName",
            RuntimePathOptions.Create(Path.Combine(_tempRoot, "data-a")));
        var second = InvokePrivateStatic<string>(
            "BuildSingleInstanceMutexName",
            RuntimePathOptions.Create(Path.Combine(_tempRoot, "data-b")));

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void Impl_BuildDataDirectoryKey_ShouldFollowPlatformCaseRules()
    {
        const string upper = @"C:\Temp\DevHub\DataRoot";
        const string lower = @"c:\temp\devhub\dataroot";

        var upperKey = InvokePrivateStatic<string>("BuildDataDirectoryKey", upper);
        var lowerKey = InvokePrivateStatic<string>("BuildDataDirectoryKey", lower);

        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(upperKey, lowerKey);
            return;
        }

        Assert.NotEqual(upperKey, lowerKey);
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

    private static T InvokePrivateStatic<T>(string methodName, params object[] args)
    {
        var method = typeof(HostProgram).GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var result = method.Invoke(null, args);
        return Assert.IsType<T>(result);
    }
}
