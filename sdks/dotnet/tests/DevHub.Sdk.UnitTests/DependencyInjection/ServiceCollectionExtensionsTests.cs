using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DevHub.Sdk.UnitTests.DependencyInjection;

/// <summary>
/// 依赖注入注册测试。
/// </summary>
public sealed class ServiceCollectionExtensionsTests
{
    [Fact]
    public async Task M5_DN_UT_007_AddDevHubSdk_WithConfigureDelegate_ShouldCreateHttpAndEventsClients()
    {
        var dataDir = await CreateDataDirectoryAsync();
        try
        {
            var services = new ServiceCollection();
            services.AddDevHubSdk(options =>
            {
                options.ClientId = "di-client";
                options.DataDir = dataDir;
                options.RequestTimeout = TimeSpan.FromSeconds(5);
            });

            using var provider = services.BuildServiceProvider();
            var clientFactory = provider.GetRequiredService<IDevHubClientFactory>();
            var eventsClientFactory = provider.GetRequiredService<IDevHubEventsClientFactory>();

            await using var client = await clientFactory.CreateAsync();
            await using var eventsClient = await eventsClientFactory.CreateAsync();

            Assert.Equal("di-client", client.Options.ClientId);
            Assert.Equal(dataDir, client.Options.DataDir);
            Assert.Equal("http://127.0.0.1:47231", client.Runtime.HttpBaseUrl);
            Assert.Equal("di-client", eventsClient.Options.ClientId);
            Assert.Equal(dataDir, eventsClient.Options.DataDir);
            Assert.Equal("ws://127.0.0.1:47231/ws", eventsClient.Runtime.WsUrl);
        }
        finally
        {
            DeleteDataDirectory(dataDir);
        }
    }

    [Fact]
    public async Task M5_DN_UT_007_AddDevHubSdk_WithoutDelegate_ShouldHonorExternalOptionsConfiguration()
    {
        var dataDir = await CreateDataDirectoryAsync();
        try
        {
            var services = new ServiceCollection();
            services.AddDevHubSdk();
            services.Configure<DevHubClientOptions>(options =>
            {
                options.ClientId = "configured-client";
                options.DataDir = dataDir;
            });

            using var provider = services.BuildServiceProvider();
            var clientFactory = provider.GetRequiredService<IDevHubClientFactory>();
            var configuredOptions = provider.GetRequiredService<IOptions<DevHubClientOptions>>().Value;

            await using var client = await clientFactory.CreateAsync();

            Assert.Equal("configured-client", configuredOptions.ClientId);
            Assert.Equal(dataDir, client.Options.DataDir);
            Assert.Equal("configured-client", client.Options.ClientId);
        }
        finally
        {
            DeleteDataDirectory(dataDir);
        }
    }

    private static async Task<string> CreateDataDirectoryAsync()
    {
        var dataDir = Path.Combine(Path.GetTempPath(), "DevHub.Sdk.UnitTests", Guid.NewGuid().ToString("N"));
        var runtimeDir = Path.Combine(dataDir, "runtime");
        Directory.CreateDirectory(runtimeDir);

        var tokenFile = Path.Combine(runtimeDir, "token.txt");
        await File.WriteAllTextAsync(tokenFile, "sdk-di-token\n");

        var hubJson = JsonSerializer.Serialize(new
        {
            protocolVersion = 1,
            hubVersion = "1.0.0",
            pid = 12345,
            httpBaseUrl = "http://127.0.0.1:47231",
            wsUrl = "ws://127.0.0.1:47231/ws",
            tokenFile,
            startedAtUtc = "2026-03-09T00:00:00Z",
            runtimeTuning = new
            {
                leaseSeconds = 30,
                onlineThresholdSeconds = 30,
                launchDedupeWindowSeconds = 30
            }
        });

        await File.WriteAllTextAsync(Path.Combine(runtimeDir, "hub.json"), hubJson);
        return dataDir;
    }

    private static void DeleteDataDirectory(string dataDir)
    {
        if (Directory.Exists(dataDir))
        {
            Directory.Delete(dataDir, recursive: true);
        }
    }
}
