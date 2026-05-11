namespace DevHub.Host.Tests;

using System.Reflection;
using DevHub.Core.Services;
using Microsoft.Extensions.Logging;
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
        var expectedPrefix = OperatingSystem.IsWindows() ? @"Global\DevHub_" : @"Local\DevHub_";
        Assert.StartsWith(expectedPrefix, first, StringComparison.Ordinal);
    }

    [Fact]
    public void Impl_BuildSingleInstanceMutexName_OnWindows_ShouldUseCrossSessionScope()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var runtimePathOptions = RuntimePathOptions.Create(Path.Combine(_tempRoot, "data"));
        var mutexName = InvokePrivateStatic<string>("BuildSingleInstanceMutexName", runtimePathOptions);

        Assert.StartsWith(@"Global\DevHub_", mutexName, StringComparison.Ordinal);
        Assert.False(mutexName.StartsWith(@"Local\", StringComparison.Ordinal));
    }

    [Fact]
    public void Impl_BuildSingleInstanceMutexName_WhenUsedForMutex_ShouldRejectCompetingInstance()
    {
        var runtimePathOptions = RuntimePathOptions.Create(Path.Combine(_tempRoot, "data"));
        var mutexName = InvokePrivateStatic<string>("BuildSingleInstanceMutexName", runtimePathOptions);

        using var firstMutex = new Mutex(initiallyOwned: true, mutexName, out var firstCreated);
        using var secondMutex = new Mutex(initiallyOwned: true, mutexName, out var secondCreated);

        Assert.True(firstCreated);
        Assert.False(secondCreated);
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

    [Fact]
    public void Impl_PersistHubRuntimeOrStop_WhenPersistSucceeds_ShouldKeepRunning()
    {
        var logger = new CapturingLogger();
        var stopped = false;

        InvokePrivateStaticVoid(
            "PersistHubRuntimeOrStop",
            (Func<bool>)(() => true),
            (Action)(() => stopped = true),
            logger);

        Assert.False(stopped);
        Assert.Empty(logger.Entries);
    }

    [Fact]
    public void Impl_PersistHubRuntimeOrStop_WhenPersistFails_ShouldLogCriticalAndStopApplication()
    {
        var logger = new CapturingLogger();
        var stopped = false;

        InvokePrivateStaticVoid(
            "PersistHubRuntimeOrStop",
            (Func<bool>)(() => false),
            (Action)(() => stopped = true),
            logger);

        Assert.True(stopped);
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Critical, entry.Level);
        Assert.Contains("Hub 运行时发现文件持久化失败", entry.Message, StringComparison.Ordinal);
        Assert.Null(entry.Exception);
    }

    [Fact]
    public void Impl_PersistHubRuntimeOrStop_WhenPersistThrows_ShouldLogCriticalAndStopApplication()
    {
        var logger = new CapturingLogger();
        var stopped = false;
        var expectedException = new InvalidOperationException("persist failed");

        InvokePrivateStaticVoid(
            "PersistHubRuntimeOrStop",
            (Func<bool>)(() => throw expectedException),
            (Action)(() => stopped = true),
            logger);

        Assert.True(stopped);
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Critical, entry.Level);
        Assert.Same(expectedException, entry.Exception);
        Assert.Contains("Hub 运行时发现文件持久化失败", entry.Message, StringComparison.Ordinal);
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

    private static void InvokePrivateStaticVoid(string methodName, params object[] args)
    {
        var method = typeof(HostProgram).GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        _ = method.Invoke(null, args);
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<LogEntry> Entries { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull
        {
            return NullScope.Instance;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add(new LogEntry(logLevel, formatter(state, exception), exception));
        }
    }

    private sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);

    private sealed class NullScope : IDisposable
    {
        public static NullScope Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
