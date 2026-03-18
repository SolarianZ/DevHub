namespace DevHub.Host.Tests;

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using DevHub.Core.Services;
using Xunit.Sdk;

/// <summary>
/// Program 进程级回归测试。
/// </summary>
[Trait("Category", "Impl")]
public sealed class ProgramProcessTests : IDisposable
{
    private readonly string _tempRoot;

    /// <summary>
    /// 初始化测试上下文。
    /// </summary>
    public ProgramProcessTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "DevHubProgramProcessTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    [Fact]
    public async Task Impl_HostProcess_WhenStartedFromDifferentWorkingDirectory_ShouldCreateIsolatedArtifacts()
    {
        using var context = CreateProcessContext();
        using var hostProcess = StartHostProcess(context);

        await WaitForHubJsonAsync(hostProcess, context.HubJsonPath, TimeSpan.FromSeconds(30));

        Assert.False(hostProcess.Process.HasExited, hostProcess.GetFailureMessage("Host 在生成 hub.json 后提前退出。"));
        Assert.True(Directory.Exists(context.RuntimeDirectory));
        Assert.True(Directory.Exists(context.DefinitionsDirectory));
        Assert.True(Directory.Exists(context.InstancesDirectory));
        Assert.True(Directory.Exists(context.LogsDirectory));
        Assert.True(File.Exists(Path.Combine(context.RuntimeDirectory, "token.txt")));

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(context.HubJsonPath));
        Assert.Equal(
            HostVersionProvider.ResolveHubVersion(typeof(Program).Assembly),
            document.RootElement.GetProperty("hubVersion").GetString());
        Assert.Equal(
            Path.Combine(context.RuntimeDirectory, "token.txt"),
            document.RootElement.GetProperty("tokenFile").GetString());
        Assert.StartsWith(
            "http://127.0.0.1:",
            document.RootElement.GetProperty("httpBaseUrl").GetString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Impl_HostProcess_WhenSameDataDirectoryStartedTwice_ShouldBlockSecondProcess()
    {
        using var firstContext = CreateProcessContext();
        using var secondContext = CreateProcessContext(dataDirectory: firstContext.DataDirectory);
        using var firstHostProcess = StartHostProcess(firstContext);

        await WaitForHubJsonAsync(firstHostProcess, firstContext.HubJsonPath, TimeSpan.FromSeconds(30));

        using var secondHostProcess = StartHostProcess(secondContext);
        await secondHostProcess.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));

        Assert.False(firstHostProcess.Process.HasExited, firstHostProcess.GetFailureMessage("首个 Host 在校验单实例时意外退出。"));
        Assert.Equal(0, secondHostProcess.Process.ExitCode);
        Assert.Contains("DevHub 已在运行中", secondHostProcess.GetCombinedOutput(), StringComparison.Ordinal);
        Assert.True(File.Exists(firstContext.HubJsonPath));
        Assert.False(File.Exists(secondContext.HubJsonPath) && !PathsReferToSameLocation(secondContext.HubJsonPath, firstContext.HubJsonPath));
    }

    [Fact]
    public async Task Impl_HostProcess_WhenDifferentDataDirectoriesStartedConcurrently_ShouldAllowParallelExecution()
    {
        using var firstContext = CreateProcessContext();
        using var secondContext = CreateProcessContext();
        using var firstHostProcess = StartHostProcess(firstContext);
        using var secondHostProcess = StartHostProcess(secondContext);

        await WaitForHubJsonAsync(firstHostProcess, firstContext.HubJsonPath, TimeSpan.FromSeconds(30));
        await WaitForHubJsonAsync(secondHostProcess, secondContext.HubJsonPath, TimeSpan.FromSeconds(30));

        Assert.False(firstHostProcess.Process.HasExited, firstHostProcess.GetFailureMessage("首个 Host 在异数据根并行启动时意外退出。"));
        Assert.False(secondHostProcess.Process.HasExited, secondHostProcess.GetFailureMessage("第二个 Host 在异数据根并行启动时意外退出。"));

        using var firstDocument = JsonDocument.Parse(await File.ReadAllTextAsync(firstContext.HubJsonPath));
        using var secondDocument = JsonDocument.Parse(await File.ReadAllTextAsync(secondContext.HubJsonPath));

        var firstRoot = firstDocument.RootElement;
        var secondRoot = secondDocument.RootElement;

        Assert.Equal(Path.Combine(firstContext.RuntimeDirectory, "token.txt"), firstRoot.GetProperty("tokenFile").GetString());
        Assert.Equal(Path.Combine(secondContext.RuntimeDirectory, "token.txt"), secondRoot.GetProperty("tokenFile").GetString());
        Assert.NotEqual(firstRoot.GetProperty("pid").GetInt32(), secondRoot.GetProperty("pid").GetInt32());
        Assert.NotEqual(firstRoot.GetProperty("httpBaseUrl").GetString(), secondRoot.GetProperty("httpBaseUrl").GetString());
    }

    [Fact]
    public async Task Impl_HostProcess_WhenStartupFails_ShouldReturnNonZeroExitCode()
    {
        using var context = CreateProcessContext();
        var invalidDataDirectory = Path.Combine(_tempRoot, $"data-blocker-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(invalidDataDirectory, "blocked");

        context.EnvironmentVariables[RuntimePathOptions.DataDirEnvironmentVariable] = invalidDataDirectory;

        using var hostProcess = StartHostProcess(context);
        await hostProcess.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));

        Assert.NotEqual(0, hostProcess.Process.ExitCode);
        Assert.Contains("DevHub 启动过程中发生致命错误", hostProcess.GetCombinedOutput(), StringComparison.Ordinal);
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

    private ProcessTestContext CreateProcessContext(string? dataDirectory = null)
    {
        var contextId = Guid.NewGuid().ToString("N");
        var workingDirectory = Path.Combine(_tempRoot, "work", contextId);
        var effectiveDataDirectory = dataDirectory ?? Path.Combine(_tempRoot, "data", contextId);
        var runtimeDirectory = Path.Combine(effectiveDataDirectory, "runtime");
        var definitionsDirectory = Path.Combine(effectiveDataDirectory, "apps", "definitions");
        var instancesDirectory = Path.Combine(effectiveDataDirectory, "apps", "instances");
        var logsDirectory = Path.Combine(effectiveDataDirectory, "logs");

        Directory.CreateDirectory(workingDirectory);

        return new ProcessTestContext(
            typeof(Program).Assembly.Location,
            workingDirectory,
            effectiveDataDirectory,
            runtimeDirectory,
            definitionsDirectory,
            instancesDirectory,
            logsDirectory,
            Path.Combine(runtimeDirectory, "hub.json"),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [RuntimePathOptions.DataDirEnvironmentVariable] = effectiveDataDirectory
            });
    }

    private static HostProcessHandle StartHostProcess(ProcessTestContext context)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = context.WorkingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        startInfo.ArgumentList.Add(context.HostAssemblyPath);

        foreach (var (key, value) in context.EnvironmentVariables)
        {
            startInfo.Environment[key] = value;
        }

        var process = new Process
        {
            StartInfo = startInfo
        };

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var syncRoot = new object();

        process.OutputDataReceived += (_, args) =>
        {
            if (args.Data is null)
            {
                return;
            }

            lock (syncRoot)
            {
                stdout.AppendLine(args.Data);
            }
        };

        process.ErrorDataReceived += (_, args) =>
        {
            if (args.Data is null)
            {
                return;
            }

            lock (syncRoot)
            {
                stderr.AppendLine(args.Data);
            }
        };

        Assert.True(process.Start(), "Host 进程启动失败。");
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        return new HostProcessHandle(process, stdout, stderr, syncRoot);
    }

    private static async Task WaitForHubJsonAsync(HostProcessHandle hostProcess, string hubJsonPath, TimeSpan timeout)
    {
        var deadlineUtc = DateTime.UtcNow.Add(timeout);
        while (DateTime.UtcNow < deadlineUtc)
        {
            if (File.Exists(hubJsonPath))
            {
                return;
            }

            if (hostProcess.Process.HasExited)
            {
                throw new XunitException(hostProcess.GetFailureMessage($"Host 在生成 hub.json 前提前退出: {hubJsonPath}"));
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250));
        }

        throw new XunitException(hostProcess.GetFailureMessage($"等待 hub.json 超时: {hubJsonPath}"));
    }

    private static bool PathsReferToSameLocation(string left, string right)
    {
        try
        {
            return File.Exists(left) && File.Exists(right) && string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private sealed record ProcessTestContext(
        string HostAssemblyPath,
        string WorkingDirectory,
        string DataDirectory,
        string RuntimeDirectory,
        string DefinitionsDirectory,
        string InstancesDirectory,
        string LogsDirectory,
        string HubJsonPath,
        IDictionary<string, string> EnvironmentVariables)
        : IDisposable
    {
        public void Dispose()
        {
        }
    }

    private sealed class HostProcessHandle : IDisposable
    {
        private readonly StringBuilder _stdout;
        private readonly StringBuilder _stderr;
        private readonly object _syncRoot;

        public HostProcessHandle(Process process, StringBuilder stdout, StringBuilder stderr, object syncRoot)
        {
            Process = process;
            _stdout = stdout;
            _stderr = stderr;
            _syncRoot = syncRoot;
        }

        public Process Process { get; }

        public string GetCombinedOutput()
        {
            lock (_syncRoot)
            {
                return _stdout.ToString() + _stderr.ToString();
            }
        }

        public string GetFailureMessage(string message)
        {
            return $"{message}{Environment.NewLine}ExitCode: {(Process.HasExited ? Process.ExitCode : null)}{Environment.NewLine}{GetCombinedOutput()}";
        }

        public void Dispose()
        {
            try
            {
                if (!Process.HasExited)
                {
                    Process.Kill(entireProcessTree: true);
                    Process.WaitForExit(10000);
                }
            }
            catch
            {
            }
            finally
            {
                Process.Dispose();
            }
        }
    }
}
