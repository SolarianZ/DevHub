using System.Text.Json;
using System.Diagnostics;
using DevHub.Sdk.Models;

namespace DevHub.Sdk.IntegrationTests.TestHost;

/// <summary>
/// Host 测试夹具回归测试。
/// </summary>
public sealed class DevHubHostFixtureTests
{
    [Fact]
    public void Impl_ResolveConfiguredHostAssemblyPath_WhenRelativePathProvided_ShouldResolveAgainstRepositoryRoot()
    {
        var repoRoot = CreateFakeRepositoryRoot();
        try
        {
            var hostAssemblyPath = CreateConfiguredHostAssembly(repoRoot, Path.Combine("artifacts", "DevHub.Host.dll"));

            var resolvedPath = DevHubHostFixture.ResolveConfiguredHostAssemblyPath(
                repoRoot,
                Path.Combine("artifacts", "DevHub.Host.dll"));

            Assert.Equal(hostAssemblyPath, resolvedPath);
        }
        finally
        {
            DeleteDirectoryIfExists(repoRoot);
        }
    }

    [Fact]
    public void Impl_ResolveConfiguredHostAssemblyPath_WhenAbsolutePathProvided_ShouldPreserveAbsolutePath()
    {
        var repoRoot = CreateFakeRepositoryRoot();
        try
        {
            var hostAssemblyPath = CreateConfiguredHostAssembly(repoRoot, Path.Combine("absolute", "DevHub.Host.dll"));

            var resolvedPath = DevHubHostFixture.ResolveConfiguredHostAssemblyPath(repoRoot, hostAssemblyPath);

            Assert.Equal(hostAssemblyPath, resolvedPath);
        }
        finally
        {
            DeleteDirectoryIfExists(repoRoot);
        }
    }

    [Fact]
    public void Impl_ResolveConfiguredHostAssemblyPath_WhenConfiguredPathMissing_ShouldThrow()
    {
        var repoRoot = CreateFakeRepositoryRoot();
        try
        {
            var exception = Assert.Throws<InvalidOperationException>(
                () => DevHubHostFixture.ResolveConfiguredHostAssemblyPath(
                    repoRoot,
                    Path.Combine("missing", "DevHub.Host.dll")));
            Assert.Contains("DEVHUB_DOTNET_SDK_HOST_ASSEMBLY", exception.Message);
        }
        finally
        {
            DeleteDirectoryIfExists(repoRoot);
        }
    }

    [Fact]
    public async Task Impl_HostFixture_WhenStarted_ShouldCreateIsolatedArtifactDirectories()
    {
        await using var host = await DevHubHostFixture.StartAsync();

        Assert.True(Directory.Exists(host.DataDirectory));
        Assert.True(Directory.Exists(host.RuntimeDirectory));
        Assert.True(Directory.Exists(host.DefinitionsDirectory));
        Assert.True(Directory.Exists(host.InstancesDirectory));
        Assert.True(Directory.Exists(host.LogsDirectory));

        var hubJsonPath = Path.Combine(host.RuntimeDirectory, "hub.json");
        var tokenFilePath = Path.Combine(host.RuntimeDirectory, "token.txt");

        Assert.True(File.Exists(hubJsonPath));
        Assert.True(File.Exists(tokenFilePath));

        using var hubJson = JsonDocument.Parse(await File.ReadAllTextAsync(hubJsonPath));
        Assert.Equal(tokenFilePath, hubJson.RootElement.GetProperty("tokenFile").GetString());

        var logFiles = await WaitForFilesAsync(host.LogsDirectory, "*.log", TimeSpan.FromSeconds(10));
        Assert.NotEmpty(logFiles);
    }

    [Fact]
    public async Task Impl_HostFixture_WhenDisposed_ShouldCleanupTempRootAndExitHostProcess()
    {
        var host = await DevHubHostFixture.StartAsync();
        var tempRoot = host.TempRoot;
        var hostProcessId = host.HostProcessId;

        await host.DisposeAsync();

        Assert.False(Directory.Exists(tempRoot));
        Assert.False(IsProcessRunning(hostProcessId));
    }

    [Fact]
    public async Task Impl_HostFixture_WhenDisposedAfterLaunch_ShouldCleanupProcessTreeAndTempRoot()
    {
        var host = await DevHubHostFixture.StartAsync();
        var tempRoot = host.TempRoot;
        var hostProcessId = host.HostProcessId;
        int? launchedProcessId = null;

        try
        {
            await host.WriteDefinitionAsync(CreateLongRunningLaunchDefinition("host.cleanup.app"));

            await using var client = await host.CreateClientAsync("host-cleanup-client");
            var launchResult = await client.LaunchAsync(new LaunchRequest
            {
                AppId = "host.cleanup.app",
                WaitForRegisterMs = 0
            });

            Assert.Contains(launchResult.Status, new[] { "started", "starting" });
            Assert.NotNull(launchResult.Pid);
            launchedProcessId = launchResult.Pid!.Value;
            Assert.True(await WaitForProcessStateAsync(launchedProcessId.Value, expectedRunning: true, TimeSpan.FromSeconds(10)));
        }
        finally
        {
            await host.DisposeAsync();
        }

        Assert.False(Directory.Exists(tempRoot));
        Assert.False(IsProcessRunning(hostProcessId));
        Assert.NotNull(launchedProcessId);
        Assert.True(await WaitForProcessStateAsync(launchedProcessId.Value, expectedRunning: false, TimeSpan.FromSeconds(10)));
    }

    private static async Task<IReadOnlyList<string>> WaitForFilesAsync(string directory, string searchPattern, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow.Add(timeout);

        while (DateTime.UtcNow < deadline)
        {
            if (Directory.Exists(directory))
            {
                var files = Directory.GetFiles(directory, searchPattern);
                if (files.Length > 0)
                {
                    return files;
                }
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250));
        }

        return Array.Empty<string>();
    }

    private static async Task<bool> WaitForProcessStateAsync(int processId, bool expectedRunning, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow.Add(timeout);
        while (DateTime.UtcNow < deadline)
        {
            if (IsProcessRunning(processId) == expectedRunning)
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250));
        }

        return IsProcessRunning(processId) == expectedRunning;
    }

    private static AppDefinition CreateLongRunningLaunchDefinition(string appId)
    {
        if (OperatingSystem.IsWindows())
        {
            return new AppDefinition
            {
                AppId = appId,
                DisplayName = appId,
                Launch = new LaunchConfiguration
                {
                    ExePath = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
                    ArgsTemplate = "-NoProfile -Command Start-Sleep -Seconds 30"
                }
            };
        }

        return new AppDefinition
        {
            AppId = appId,
            DisplayName = appId,
            Launch = new LaunchConfiguration
            {
                ExePath = "/bin/sh",
                ArgsTemplate = "-c \"sleep 30\""
            }
        };
    }

    private static bool IsProcessRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }

    private static string CreateFakeRepositoryRoot()
    {
        var repoRoot = Path.Combine(Path.GetTempPath(), "DevHubHostFixtureTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repoRoot);
        return repoRoot;
    }

    private static string CreateConfiguredHostAssembly(string repoRoot, string relativePath)
    {
        var hostAssemblyPath = Path.Combine(repoRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(hostAssemblyPath)!);
        File.WriteAllText(hostAssemblyPath, string.Empty);
        return hostAssemblyPath;
    }

    private static void DeleteDirectoryIfExists(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }
}
