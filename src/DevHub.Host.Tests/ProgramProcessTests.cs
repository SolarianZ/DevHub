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

        var hubJsonPath = Path.Combine(context.RuntimeDirectory, "hub.json");
        await WaitForHubJsonAsync(hostProcess, hubJsonPath, TimeSpan.FromSeconds(30));

        Assert.False(hostProcess.Process.HasExited, hostProcess.GetFailureMessage("Host 在生成 hub.json 后提前退出。"));
        Assert.True(Directory.Exists(context.RuntimeDirectory));
        Assert.True(Directory.Exists(context.DefinitionsDirectory));
        Assert.True(Directory.Exists(context.InstancesDirectory));
        Assert.True(Directory.Exists(context.LogsDirectory));
        Assert.True(File.Exists(Path.Combine(context.RuntimeDirectory, "token.txt")));

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(hubJsonPath));
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
    public async Task Impl_HostProcess_WhenStartupFails_ShouldReturnNonZeroExitCode()
    {
        using var context = CreateProcessContext();
        var invalidRuntimePath = Path.Combine(_tempRoot, $"runtime-blocker-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(invalidRuntimePath, "blocked");

        context.EnvironmentVariables[RuntimePathOptions.RuntimeDirEnvironmentVariable] = invalidRuntimePath;

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

    private ProcessTestContext CreateProcessContext()
    {
        var contextId = Guid.NewGuid().ToString("N");
        var workingDirectory = Path.Combine(_tempRoot, "work", contextId);
        var runtimeDirectory = Path.Combine(_tempRoot, "runtime", contextId);
        var definitionsDirectory = Path.Combine(_tempRoot, "definitions", contextId);
        var instancesDirectory = Path.Combine(_tempRoot, "instances", contextId);
        var logsDirectory = Path.Combine(_tempRoot, "logs", contextId);

        Directory.CreateDirectory(workingDirectory);

        return new ProcessTestContext(
            typeof(Program).Assembly.Location,
            workingDirectory,
            runtimeDirectory,
            definitionsDirectory,
            instancesDirectory,
            logsDirectory,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [RuntimePathOptions.RuntimeDirEnvironmentVariable] = runtimeDirectory,
                [RuntimePathOptions.AppDefinitionsDirEnvironmentVariable] = definitionsDirectory,
                [RuntimePathOptions.AppInstancesDirEnvironmentVariable] = instancesDirectory,
                [RuntimePathOptions.LogDirEnvironmentVariable] = logsDirectory,
                ["DEVHUB_SINGLE_INSTANCE_SLOT_FOR_TESTS"] = contextId
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

    private sealed record ProcessTestContext(
        string HostAssemblyPath,
        string WorkingDirectory,
        string RuntimeDirectory,
        string DefinitionsDirectory,
        string InstancesDirectory,
        string LogsDirectory,
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
