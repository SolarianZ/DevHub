using System.Text.Json;
using System.Text.Json.Serialization;
using DevHub.Sdk.Internal;
using DevHub.Sdk.Models;

namespace DevHub.Sdk.UnitTests.Discovery;

/// <summary>
/// Runtime discovery 白盒测试。
/// </summary>
public sealed class RuntimeDiscoveryTests : IDisposable
{
    private const string ExpectedHubVersion = "test-hub-version";

    private static readonly JsonSerializerOptions HubJsonSerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string _tempRoot;

    public RuntimeDiscoveryTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "DevHubSdkRuntimeDiscoveryTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    [Fact]
    public async Task RuntimeDiscovery_WithValidHubJson_ShouldReadTokenFile()
    {
        var dataDir = CreateDataDirectory();
        var runtimeDir = GetRuntimeDirectory(dataDir);
        var tokenFile = Path.Combine(runtimeDir, "token.txt");
        await File.WriteAllTextAsync(tokenFile, "token-1  \r\n");
        await WriteHubJsonAsync(dataDir, new HubRuntime
        {
            ProtocolVersion = 1,
            HubVersion = ExpectedHubVersion,
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
            DataDir = dataDir
        }, CancellationToken.None);

        Assert.Equal("token-1", connectionInfo.Token);
        Assert.Equal(runtimeDir, connectionInfo.RuntimeDirectory);
        Assert.Equal(ExpectedHubVersion, connectionInfo.Runtime.HubVersion);
        Assert.Equal("http://127.0.0.1:47231", connectionInfo.Runtime.HttpBaseUrl);
        Assert.Equal("ws://127.0.0.1:47231/ws", connectionInfo.Runtime.WsUrl);
        Assert.Equal(tokenFile, connectionInfo.Runtime.TokenFile);
    }

    [Fact]
    public async Task RuntimeDiscovery_WithIpv6LoopbackEndpoints_ShouldAcceptRuntime()
    {
        var dataDir = CreateDataDirectory();
        var runtimeDir = GetRuntimeDirectory(dataDir);
        var tokenFile = Path.Combine(runtimeDir, "token.txt");
        await File.WriteAllTextAsync(tokenFile, "token-ipv6");
        await WriteHubJsonAsync(dataDir, new HubRuntime
        {
            ProtocolVersion = 1,
            HubVersion = ExpectedHubVersion,
            Pid = 12345,
            HttpBaseUrl = "http://[::1]:47231",
            WsUrl = "ws://[::1]:47231/ws",
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
            DataDir = dataDir
        }, CancellationToken.None);

        Assert.Equal("token-ipv6", connectionInfo.Token);
        Assert.Equal("http://[::1]:47231", connectionInfo.Runtime.HttpBaseUrl);
        Assert.Equal("ws://[::1]:47231/ws", connectionInfo.Runtime.WsUrl);
    }

    [Fact]
    public async Task RuntimeDiscovery_WithIpv4LoopbackRangeEndpoints_ShouldAcceptRuntime()
    {
        var dataDir = CreateDataDirectory();
        var runtimeDir = GetRuntimeDirectory(dataDir);
        var tokenFile = Path.Combine(runtimeDir, "token.txt");
        await File.WriteAllTextAsync(tokenFile, "token-ipv4-loopback");
        await WriteHubJsonAsync(dataDir, new HubRuntime
        {
            ProtocolVersion = 1,
            HubVersion = ExpectedHubVersion,
            Pid = 12345,
            HttpBaseUrl = "http://127.0.0.23:47231",
            WsUrl = "ws://127.0.0.23:47231/ws",
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
            DataDir = dataDir
        }, CancellationToken.None);

        Assert.Equal("token-ipv4-loopback", connectionInfo.Token);
        Assert.Equal("http://127.0.0.23:47231", connectionInfo.Runtime.HttpBaseUrl);
        Assert.Equal("ws://127.0.0.23:47231/ws", connectionInfo.Runtime.WsUrl);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("123")]
    public async Task RuntimeDiscovery_WhenHubVersionIsNotString_ShouldThrowInvalidOperationException(string hubVersionLiteral)
    {
        var dataDir = CreateDataDirectory();
        var runtimeDir = GetRuntimeDirectory(dataDir);
        var tokenFile = Path.Combine(runtimeDir, "token.txt");
        await File.WriteAllTextAsync(tokenFile, "token-1");

        await File.WriteAllTextAsync(
            Path.Combine(runtimeDir, "hub.json"),
            $$"""
            {
              "protocolVersion": 1,
              "hubVersion": {{hubVersionLiteral}},
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

        await Assert.ThrowsAsync<InvalidOperationException>(() => RuntimeDiscovery.DiscoverAsync(new DevHubClientOptions
        {
            ClientId = "unit-test-client",
            DataDir = dataDir
        }, CancellationToken.None));
    }

    [Theory]
    [InlineData("httpBaseUrl")]
    [InlineData("wsUrl")]
    [InlineData("tokenFile")]
    [InlineData("startedAtUtc")]
    public async Task RuntimeDiscovery_WhenHubJsonMissingRequiredField_ShouldThrowInvalidOperationException(string missingProperty)
    {
        var dataDir = CreateDataDirectory();
        var runtimeDir = GetRuntimeDirectory(dataDir);
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
            DataDir = dataDir
        }, CancellationToken.None));
    }

    [Theory]
    [InlineData(2, 30)]
    [InlineData(1, 0)]
    public async Task RuntimeDiscovery_WhenProtocolVersionOrRuntimeTuningInvalid_ShouldThrowInvalidOperationException(int protocolVersion, int leaseSeconds)
    {
        var dataDir = CreateDataDirectory();
        var runtimeDir = GetRuntimeDirectory(dataDir);
        var tokenFile = Path.Combine(runtimeDir, "token.txt");
        await File.WriteAllTextAsync(tokenFile, "token-1");
        await WriteHubJsonAsync(dataDir, new HubRuntime
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
            DataDir = dataDir
        }, CancellationToken.None));
    }

    [Theory]
    [InlineData("http://127.0.0.1:47231/", "ws://127.0.0.1:47231/ws", "httpBaseUrl")]
    [InlineData("http://127.0.0.1:47231/base", "ws://127.0.0.1:47231/ws", "httpBaseUrl")]
    [InlineData("http://127.0.0.1:47231?rpc=1", "ws://127.0.0.1:47231/ws", "httpBaseUrl")]
    [InlineData("http://127.0.0.1:47231#rpc", "ws://127.0.0.1:47231/ws", "httpBaseUrl")]
    [InlineData("http://user@127.0.0.1:47231", "ws://127.0.0.1:47231/ws", "httpBaseUrl")]
    [InlineData("http://devhub.example.com:47231", "ws://127.0.0.1:47231/ws", "httpBaseUrl")]
    [InlineData("http://127.0.0.1:47231", "ws://127.0.0.1:47231/ws/", "wsUrl")]
    [InlineData("http://127.0.0.1:47231", "wss://devhub.example.com/ws", "wsUrl")]
    public async Task RuntimeDiscovery_WhenEndpointViolatesSpec_ShouldThrowInvalidOperationException(
        string httpBaseUrl,
        string wsUrl,
        string invalidProperty)
    {
        var dataDir = CreateDataDirectory();
        var runtimeDir = GetRuntimeDirectory(dataDir);
        var tokenFile = Path.Combine(runtimeDir, "token.txt");
        await File.WriteAllTextAsync(tokenFile, "token-1");
        await WriteHubJsonAsync(dataDir, new HubRuntime
        {
            ProtocolVersion = 1,
            Pid = 12345,
            HttpBaseUrl = httpBaseUrl,
            WsUrl = wsUrl,
            TokenFile = tokenFile,
            StartedAtUtc = DateTimeOffset.UtcNow,
            RuntimeTuning = new HubRuntimeTuning
            {
                LeaseSeconds = 30,
                OnlineThresholdSeconds = 30,
                LaunchDedupeWindowSeconds = 30
            }
        });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => RuntimeDiscovery.DiscoverAsync(new DevHubClientOptions
        {
            ClientId = "unit-test-client",
            DataDir = dataDir
        }, CancellationToken.None));

        Assert.Contains($"hub.json.{invalidProperty}", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RuntimeDiscovery_WhenTokenFileIsNotAbsolutePath_ShouldThrowInvalidOperationException()
    {
        var dataDir = CreateDataDirectory();
        await WriteHubJsonAsync(dataDir, new HubRuntime
        {
            ProtocolVersion = 1,
            Pid = 12345,
            HttpBaseUrl = "http://127.0.0.1:47231",
            WsUrl = "ws://127.0.0.1:47231/ws",
            TokenFile = "token.txt",
            StartedAtUtc = DateTimeOffset.UtcNow,
            RuntimeTuning = new HubRuntimeTuning
            {
                LeaseSeconds = 30,
                OnlineThresholdSeconds = 30,
                LaunchDedupeWindowSeconds = 30
            }
        });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => RuntimeDiscovery.DiscoverAsync(new DevHubClientOptions
        {
            ClientId = "unit-test-client",
            DataDir = dataDir
        }, CancellationToken.None));

        Assert.Contains("hub.json.tokenFile", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RuntimeDiscovery_WhenTokenFileIsBlank_ShouldThrowInvalidOperationException()
    {
        var dataDir = CreateDataDirectory();
        var runtimeDir = GetRuntimeDirectory(dataDir);
        var tokenFile = Path.Combine(runtimeDir, "token.txt");
        await File.WriteAllTextAsync(tokenFile, " \r\n\t ");
        await WriteHubJsonAsync(dataDir, new HubRuntime
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

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => RuntimeDiscovery.DiscoverAsync(new DevHubClientOptions
        {
            ClientId = "unit-test-client",
            DataDir = dataDir
        }, CancellationToken.None));

        Assert.Contains("token 文件为空", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RuntimeDiscovery_WhenEnvironmentOverrideProvided_ShouldUseEnvironmentDataDir()
    {
        var dataDir = CreateDataDirectory();
        var runtimeDir = GetRuntimeDirectory(dataDir);
        var tokenFile = Path.Combine(runtimeDir, "token.txt");
        await File.WriteAllTextAsync(tokenFile, "token-env");
        await WriteHubJsonAsync(dataDir, new HubRuntime
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

        using var scope = CreateEnvironmentScope(dataDir);
        var connectionInfo = await RuntimeDiscovery.DiscoverAsync(new DevHubClientOptions
        {
            ClientId = "unit-test-client"
        }, CancellationToken.None);

        Assert.Equal(runtimeDir, connectionInfo.RuntimeDirectory);
        Assert.Equal("token-env", connectionInfo.Token);
    }

    [Fact]
    public async Task RuntimeDiscovery_WhenExplicitDataDirProvided_ShouldOverrideEnvironmentVariable()
    {
        var dataDir = CreateDataDirectory();
        var runtimeDir = GetRuntimeDirectory(dataDir);
        var tokenFile = Path.Combine(runtimeDir, "token.txt");
        await File.WriteAllTextAsync(tokenFile, "token-explicit");
        await WriteHubJsonAsync(dataDir, new HubRuntime
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

        var envDataDir = CreateDataDirectory();
        using var scope = CreateEnvironmentScope(envDataDir);
        var connectionInfo = await RuntimeDiscovery.DiscoverAsync(new DevHubClientOptions
        {
            ClientId = "unit-test-client",
            DataDir = dataDir
        }, CancellationToken.None);

        Assert.Equal(runtimeDir, connectionInfo.RuntimeDirectory);
        Assert.Equal("token-explicit", connectionInfo.Token);
    }

    [Fact]
    public async Task RuntimeDiscovery_WhenRuntimeDirectoryPassedAsDataDir_ShouldThrowMigrationException()
    {
        var dataDir = CreateDataDirectory();
        var runtimeDir = GetRuntimeDirectory(dataDir);
        var tokenFile = Path.Combine(runtimeDir, "token.txt");
        await File.WriteAllTextAsync(tokenFile, "token-1");
        await WriteHubJsonAsync(dataDir, new HubRuntime
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

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => RuntimeDiscovery.DiscoverAsync(new DevHubClientOptions
        {
            ClientId = "unit-test-client",
            DataDir = runtimeDir
        }, CancellationToken.None));

        Assert.Contains("runtime 子目录", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RuntimeDiscovery_WhenNoOverrideProvided_ShouldResolvePlatformDefaultDataDir()
    {
        using var scope = CreateEnvironmentScope();

        var dataDir = RuntimeDiscovery.ResolveDataDirectory(dataDirectoryOverride: null);

        Assert.Equal(GetExpectedDefaultDataDirectory(), dataDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    private string CreateDataDirectory()
    {
        var dataDir = Path.Combine(_tempRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(GetRuntimeDirectory(dataDir));
        return dataDir;
    }

    private static string GetRuntimeDirectory(string dataDir)
    {
        return Path.Combine(dataDir, "runtime");
    }

    private static Task WriteHubJsonAsync(string dataDir, HubRuntime runtime)
    {
        return File.WriteAllTextAsync(Path.Combine(GetRuntimeDirectory(dataDir), "hub.json"), JsonSerializer.Serialize(runtime, HubJsonSerializerOptions));
    }

    private static string GetExpectedDefaultDataDirectory()
    {
        if (OperatingSystem.IsWindows())
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DevHub");
        }

        if (OperatingSystem.IsMacOS())
        {
            return Path.Combine(
                GetUserHomePath(),
                "Library",
                "Application Support",
                "DevHub");
        }

        var xdgDataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        var dataHome = string.IsNullOrWhiteSpace(xdgDataHome)
            ? Path.Combine(GetUserHomePath(), ".local", "share")
            : xdgDataHome;

        return Path.Combine(Path.GetFullPath(dataHome), "DevHub");
    }

    private static string GetUserHomePath()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            return userProfile;
        }

        var personal = Environment.GetFolderPath(Environment.SpecialFolder.Personal);
        if (!string.IsNullOrWhiteSpace(personal))
        {
            return personal;
        }

        var home = Environment.GetEnvironmentVariable("HOME");
        if (!string.IsNullOrWhiteSpace(home))
        {
            return home;
        }

        throw new InvalidOperationException("无法解析当前用户主目录。");
    }

    private static EnvironmentVariableCollectionScope CreateEnvironmentScope(string? dataDir = null, params (string Name, string? Value)[] overrides)
    {
        var values = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["DEVHUB_DATA_DIR"] = dataDir
        };

        foreach (var (name, value) in overrides)
        {
            values[name] = value;
        }

        return new EnvironmentVariableCollectionScope(values);
    }

    private sealed class EnvironmentVariableCollectionScope : IDisposable
    {
        private readonly IReadOnlyDictionary<string, string?> _originalValues;

        public EnvironmentVariableCollectionScope(IReadOnlyDictionary<string, string?> values)
        {
            _originalValues = values.Keys.ToDictionary(
                static name => name,
                static name => Environment.GetEnvironmentVariable(name),
                StringComparer.Ordinal);

            foreach (var (name, value) in values)
            {
                Environment.SetEnvironmentVariable(name, value);
            }
        }

        public void Dispose()
        {
            foreach (var (name, value) in _originalValues)
            {
                Environment.SetEnvironmentVariable(name, value);
            }
        }
    }
}
