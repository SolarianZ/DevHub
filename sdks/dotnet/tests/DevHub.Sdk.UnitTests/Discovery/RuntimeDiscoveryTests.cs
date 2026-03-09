using System.Text.Json;
using DevHub.Sdk.Internal;
using DevHub.Sdk.Models;

namespace DevHub.Sdk.UnitTests.Discovery;

/// <summary>
/// Runtime discovery 白盒测试。
/// </summary>
public sealed class RuntimeDiscoveryTests : IDisposable
{
    private readonly string _tempRoot;

    public RuntimeDiscoveryTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "DevHubSdkRuntimeDiscoveryTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    [Fact]
    public async Task M5_DN_UT_001_RuntimeDiscovery_WithValidHubJson_ShouldReadTokenFile()
    {
        var runtimeDir = CreateRuntimeDirectory();
        var tokenFile = Path.Combine(runtimeDir, "token.txt");
        await File.WriteAllTextAsync(tokenFile, "token-1  \r\n");
        await WriteHubJsonAsync(runtimeDir, new HubRuntime
        {
            ProtocolVersion = 1,
            Pid = 12345,
            HttpBaseUrl = "http://127.0.0.1:47231",
            WsUrl = "ws://127.0.0.1:47231/ws",
            TokenFile = tokenFile,
            StartedAtUtc = DateTimeOffset.UtcNow,
            RuntimeTuning = new HubRuntimeTuning
            {
                LeaseSeconds = 30,
                OnlineThresholdSeconds = 30,
                LaunchDedupeWindowSeconds = 30
            }
        });

        var connectionInfo = await RuntimeDiscovery.DiscoverAsync(new DevHubClientOptions
        {
            ClientId = "unit-test-client",
            RuntimeDir = runtimeDir
        }, CancellationToken.None);

        Assert.Equal("token-1", connectionInfo.Token);
        Assert.Equal("http://127.0.0.1:47231", connectionInfo.Runtime.HttpBaseUrl);
        Assert.Equal("ws://127.0.0.1:47231/ws", connectionInfo.Runtime.WsUrl);
        Assert.Equal(tokenFile, connectionInfo.Runtime.TokenFile);
    }

    [Theory]
    [InlineData("httpBaseUrl")]
    [InlineData("wsUrl")]
    [InlineData("tokenFile")]
    public async Task M5_DN_UT_002_RuntimeDiscovery_WhenHubJsonMissingRequiredField_ShouldThrowInvalidOperationException(string missingProperty)
    {
        var runtimeDir = CreateRuntimeDirectory();
        var tokenFile = Path.Combine(runtimeDir, "token.txt");
        await File.WriteAllTextAsync(tokenFile, "token-1");

        using var document = JsonDocument.Parse($$"""
        {
          "protocolVersion": 1,
          "pid": 12345,
          "httpBaseUrl": "http://127.0.0.1:47231",
          "wsUrl": "ws://127.0.0.1:47231/ws",
          "tokenFile": "{{tokenFile.Replace("\\", "\\\\")}}",
          "startedAtUtc": "2026-03-09T00:00:00Z",
          "runtimeTuning": {
            "leaseSeconds": 30,
            "onlineThresholdSeconds": 30,
            "launchDedupeWindowSeconds": 30
          }
        }
        """);

        var payload = document.RootElement.EnumerateObject()
            .Where(property => !string.Equals(property.Name, missingProperty, StringComparison.Ordinal))
            .ToDictionary(static property => property.Name, static property => property.Value.Clone());
        await File.WriteAllTextAsync(Path.Combine(runtimeDir, "hub.json"), JsonSerializer.Serialize(payload));

        await Assert.ThrowsAsync<InvalidOperationException>(() => RuntimeDiscovery.DiscoverAsync(new DevHubClientOptions
        {
            ClientId = "unit-test-client",
            RuntimeDir = runtimeDir
        }, CancellationToken.None));
    }

    [Theory]
    [InlineData(2, 30)]
    [InlineData(1, 0)]
    public async Task M5_DN_UT_002_RuntimeDiscovery_WhenProtocolVersionOrRuntimeTuningInvalid_ShouldThrowInvalidOperationException(int protocolVersion, int leaseSeconds)
    {
        var runtimeDir = CreateRuntimeDirectory();
        var tokenFile = Path.Combine(runtimeDir, "token.txt");
        await File.WriteAllTextAsync(tokenFile, "token-1");
        await WriteHubJsonAsync(runtimeDir, new HubRuntime
        {
            ProtocolVersion = protocolVersion,
            Pid = 12345,
            HttpBaseUrl = "http://127.0.0.1:47231",
            WsUrl = "ws://127.0.0.1:47231/ws",
            TokenFile = tokenFile,
            StartedAtUtc = DateTimeOffset.UtcNow,
            RuntimeTuning = new HubRuntimeTuning
            {
                LeaseSeconds = leaseSeconds,
                OnlineThresholdSeconds = 30,
                LaunchDedupeWindowSeconds = 30
            }
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() => RuntimeDiscovery.DiscoverAsync(new DevHubClientOptions
        {
            ClientId = "unit-test-client",
            RuntimeDir = runtimeDir
        }, CancellationToken.None));
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    private string CreateRuntimeDirectory()
    {
        var runtimeDir = Path.Combine(_tempRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(runtimeDir);
        return runtimeDir;
    }

    private static Task WriteHubJsonAsync(string runtimeDir, HubRuntime runtime)
    {
        return File.WriteAllTextAsync(Path.Combine(runtimeDir, "hub.json"), JsonSerializer.Serialize(runtime));
    }
}
