using DevHub.Sdk.IntegrationTests.TestHost;
using DevHub.Sdk.Models;

namespace DevHub.Sdk.IntegrationTests.Http;

/// <summary>
/// Launch 黑盒测试。
/// </summary>
public sealed class LaunchFlowTests
{
    [Fact]
    public async Task M5_E2E_002_Launch_ShouldCoverStartedStartingAndAlreadyRunning()
    {
        await using var host = await DevHubHostFixture.StartAsync();
        await host.WriteDefinitionAsync(CreateLaunchDefinition("launch.started.app"));
        await host.WriteDefinitionAsync(CreateLaunchDefinition("launch.starting.app"));
        await host.WriteDefinitionAsync(CreateLaunchDefinition("launch.running.app"));
        await host.WriteDefinitionAsync(CreateLaunchDefinition("launch.dedupe.app"));

        await using var client = await host.CreateClientAsync("launch-client");

        var started = await client.LaunchAsync(new LaunchRequest
        {
            AppId = "launch.started.app",
            WaitForRegisterMs = 0
        });
        Assert.Equal("started", started.Status);
        Assert.True(started.Pid > 0);

        var starting = await client.LaunchAsync(new LaunchRequest
        {
            AppId = "launch.starting.app",
            WaitForRegisterMs = 200
        });
        Assert.Equal("starting", starting.Status);
        Assert.True(starting.Pid > 0);

        var registered = await client.RegisterInstanceAsync(new AppInstanceRegistration
        {
            InstanceId = "launch-running-inst-1",
            AppId = "launch.running.app",
            Pid = Environment.ProcessId,
            Invoke = new InvokeCapability
            {
                Poll = true,
                Respond = true
            }
        });

        var alreadyRunning = await client.LaunchAsync(new LaunchRequest
        {
            AppId = "launch.running.app"
        });
        Assert.Equal("already_running", alreadyRunning.Status);
        Assert.Equal(registered.Pid, alreadyRunning.Pid);

        var firstDedupeLaunch = await client.LaunchAsync(new LaunchRequest
        {
            AppId = "launch.dedupe.app",
            DedupeKey = "launch-dedupe-key",
            WaitForRegisterMs = 0
        });
        var secondDedupeLaunch = await client.LaunchAsync(new LaunchRequest
        {
            AppId = "launch.dedupe.app",
            DedupeKey = "launch-dedupe-key",
            WaitForRegisterMs = 0
        });

        Assert.Equal("started", firstDedupeLaunch.Status);
        Assert.True(firstDedupeLaunch.Pid > 0);
        Assert.Equal("already_running", secondDedupeLaunch.Status);
        Assert.Equal(firstDedupeLaunch.LaunchId, secondDedupeLaunch.LaunchId);
        Assert.Equal(firstDedupeLaunch.Pid, secondDedupeLaunch.Pid);
    }

    private static AppDefinition CreateLaunchDefinition(string appId)
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
                    ArgsTemplate = "-NoProfile -Command Start-Sleep -Seconds 5"
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
                ArgsTemplate = "-c \"sleep 5\""
            }
        };
    }
}
