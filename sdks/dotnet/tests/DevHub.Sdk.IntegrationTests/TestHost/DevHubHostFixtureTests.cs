using System.Text.Json;
using System.Diagnostics;

namespace DevHub.Sdk.IntegrationTests.TestHost;

/// <summary>
/// Host 测试夹具回归测试。
/// </summary>
public sealed class DevHubHostFixtureTests
{
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
}
