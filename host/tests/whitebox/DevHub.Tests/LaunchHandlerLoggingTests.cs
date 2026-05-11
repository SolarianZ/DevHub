namespace DevHub.Tests;

using System.Text.Json;
using DevHub.Core.Models;
using DevHub.Core.Models.Rpc;
using DevHub.Core.Services;
using DevHub.Core.Services.Invocation;
using DevHub.Core.Services.Rpc.Handlers;
using Microsoft.Extensions.Logging;
using Moq;

/// <summary>
/// LaunchHandler 日志实现回归测试。
/// </summary>
[Trait("Category", "Impl")]
public class LaunchHandlerLoggingTests : IDisposable
{
    private readonly string _tempDirectory;

    public LaunchHandlerLoggingTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "DevHubLaunchHandlerLoggingTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
    }

    [Fact]
    public async Task Impl_LaunchHandler_ShouldLogStructuredFieldsForAcceptedAndSuccessPathsWithoutSecrets()
    {
        const string appId = "impl-launch-log-success";
        const string dedupeKey = "secret-dedupe-key-value";
        WriteDefinition(appId, includeLaunch: true);

        var logger = new CapturingLogger<LaunchHandler>();
        var processLauncher = new Mock<IProcessLauncher>();
        processLauncher
            .Setup(launcher => launcher.Start(It.IsAny<LaunchConfiguration>(), It.IsAny<string?>()))
            .Returns(System.Diagnostics.Process.GetCurrentProcess());

        var handler = CreateLaunchHandler(processLauncher: processLauncher.Object, logger: logger);

        var response = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "launch-log-success",
            Method = "hub.apps.launch",
            Params = JsonSerializer.SerializeToElement(new
            {
                appId,
                scope = ScopeContract.Global,
                dedupeKey,
                waitForRegisterMs = 0,
                password = "secret-password-value",
                instanceSessionToken = "secret-instance-token-value"
            })
        }, CancellationToken.None);

        AssertSuccess(response);

        var accepted = Assert.Single(logger.Entries, entry => entry.Message.Contains("接受 launch 请求", StringComparison.Ordinal));
        Assert.Equal("launch-log-success", accepted.GetValue<object?>("RequestId"));
        Assert.Equal(appId, accepted.GetValue<string>("AppId"));
        Assert.Equal(ScopeContract.Global, accepted.GetValue<string>("Scope"));
        Assert.True(accepted.GetValue<bool>("DedupeKeyPresent"));
        Assert.Equal(0, accepted.GetValue<int>("WaitForRegisterMs"));

        var success = Assert.Single(logger.Entries, entry => entry.Message.Contains("映射 launch 成功", StringComparison.Ordinal));
        Assert.Equal("started", success.GetValue<string>("Status"));
        Assert.False(success.HasValue("LaunchId"));
        Assert.Equal(System.Diagnostics.Process.GetCurrentProcess().Id, success.GetValue<int>("Pid"));

        var result = JsonSerializer.SerializeToElement(response.Result);
        Assert.False(string.IsNullOrWhiteSpace(result.GetProperty("launchId").GetString()));
        Assert.Equal(dedupeKey, result.GetProperty("dedupeKey").GetString());

        var logText = string.Join(Environment.NewLine, logger.Entries.Select(entry => entry.Message));
        Assert.DoesNotContain(dedupeKey, logText, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-password-value", logText, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-instance-token-value", logText, StringComparison.Ordinal);
        Assert.DoesNotContain("\"params\"", logText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Impl_LaunchHandler_ShouldLogStructuredFieldsForParameterRejectionAndFailureMapping()
    {
        const string appId = "impl-launch-log-failure";
        WriteDefinition(appId, includeLaunch: false);

        var logger = new CapturingLogger<LaunchHandler>();
        var handler = CreateLaunchHandler(logger: logger);

        var invalidResponse = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "launch-log-invalid",
            Method = "hub.apps.launch",
            Params = JsonSerializer.SerializeToElement(new
            {
                appId,
                scope = ScopeContract.Global,
                waitForRegisterMs = -1
            })
        }, CancellationToken.None);

        AssertError(invalidResponse, -32602, "invalid_params");

        var failureResponse = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "launch-log-failure",
            Method = "hub.apps.launch",
            Params = JsonSerializer.SerializeToElement(new
            {
                appId,
                scope = ScopeContract.Global,
                waitForRegisterMs = 0
            })
        }, CancellationToken.None);

        AssertError(failureResponse, -32020, "launch_failed");

        var rejected = Assert.Single(logger.Entries, entry => entry.Message.Contains("拒绝 launch 参数", StringComparison.Ordinal));
        Assert.Equal("launch-log-invalid", rejected.GetValue<object?>("RequestId"));
        Assert.Equal(appId, rejected.GetValue<string>("AppId"));
        Assert.Equal(ScopeContract.Global, rejected.GetValue<string>("Scope"));
        Assert.False(rejected.GetValue<bool>("DedupeKeyPresent"));
        Assert.Equal(-32602, rejected.GetValue<int>("ErrorCode"));
        Assert.Equal("invalid_wait_for_register_ms", rejected.GetValue<string>("ErrorReason"));

        var failure = Assert.Single(logger.Entries, entry => entry.Message.Contains("映射 launch 失败", StringComparison.Ordinal));
        Assert.Equal("launch-log-failure", failure.GetValue<object?>("RequestId"));
        Assert.Equal(appId, failure.GetValue<string>("AppId"));
        Assert.Equal(ScopeContract.Global, failure.GetValue<string>("Scope"));
        Assert.Equal(0, failure.GetValue<int>("WaitForRegisterMs"));
        Assert.Equal(-32020, failure.GetValue<int>("ErrorCode"));
        Assert.Equal("launch_config_missing", failure.GetValue<string>("ErrorReason"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }

    private LaunchHandler CreateLaunchHandler(
        IProcessLauncher? processLauncher = null,
        CapturingLogger<LaunchHandler>? logger = null,
        IClock? clock = null,
        AppRegistry? appRegistry = null,
        string httpBaseUrl = "http://127.0.0.1:7301")
    {
        var effectiveClock = clock ?? new SystemClock();
        var definitionLoader = new DefinitionLoader(DefinitionCatalogTestHelper.GetCatalogPath(_tempDirectory), Mock.Of<ILogger<DefinitionLoader>>());
        var definitionProvider = new DefinitionProvider(definitionLoader);
        definitionProvider.Refresh();
        var effectiveAppRegistry = appRegistry ?? new AppRegistry(effectiveClock, Mock.Of<ILogger<AppRegistry>>());

        var runtimeProvider = new Mock<IRuntimeHttpBaseUrlProvider>();
        runtimeProvider
            .Setup(provider => provider.GetHttpBaseUrl())
            .Returns(httpBaseUrl);

        var coordinator = new LaunchCoordinator(
            definitionProvider,
            effectiveAppRegistry,
            runtimeProvider.Object,
            processLauncher ?? new ProcessLauncher(),
            effectiveClock,
            Mock.Of<ILogger<LaunchCoordinator>>());

        return new LaunchHandler(coordinator, logger ?? new CapturingLogger<LaunchHandler>());
    }

    private void WriteDefinition(
        string appId,
        bool includeLaunch,
        string? argsTemplate = "--version")
    {
        var payload = new Dictionary<string, object?>
        {
            ["appId"] = appId,
            ["scope"] = ScopeContract.Global,
            ["displayName"] = appId,
            ["capabilities"] = new Dictionary<string, object?>
            {
                ["rpc"] = true,
                ["events"] = false
            }
        };

        if (includeLaunch)
        {
            payload["launch"] = new Dictionary<string, object?>
            {
                ["exePath"] = "dotnet",
                ["argsTemplate"] = argsTemplate
            };
        }

        DefinitionCatalogTestHelper.UpsertDefinition(
            DefinitionCatalogTestHelper.GetCatalogPath(_tempDirectory),
            JsonSerializer.Serialize(payload));
    }

    private static void AssertSuccess(JsonRpcResponse response)
    {
        Assert.Null(response.Error);
        Assert.NotNull(response.Result);
        var result = JsonSerializer.SerializeToElement(response.Result);
        Assert.True(result.GetProperty("ok").GetBoolean());
    }

    private static void AssertError(JsonRpcResponse response, int expectedCode, string expectedMessage)
    {
        Assert.NotNull(response.Error);
        Assert.Equal(expectedCode, response.Error!.Code);
        Assert.Equal(expectedMessage, response.Error.Message);
    }

    private sealed class CapturingLogger<T> : ILogger<T>
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
            var values = state as IReadOnlyList<KeyValuePair<string, object?>>;
            Entries.Add(new LogEntry(logLevel, formatter(state, exception), values?.ToArray() ?? [], exception));
        }
    }

    private sealed record LogEntry(
        LogLevel Level,
        string Message,
        IReadOnlyList<KeyValuePair<string, object?>> Values,
        Exception? Exception)
    {
        public TValue? GetValue<TValue>(string key)
        {
            var match = Values.FirstOrDefault(value => string.Equals(value.Key, key, StringComparison.Ordinal));
            Assert.NotNull(match.Key);
            return match.Value is null ? default : (TValue)match.Value;
        }

        public bool HasValue(string key)
        {
            return Values.Any(value => string.Equals(value.Key, key, StringComparison.Ordinal));
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static NullScope Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
