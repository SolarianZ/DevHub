namespace DevHub.Tests;

using System.Text.Json;
using DevHub.Core.Models;
using DevHub.Core.Models.Rpc;
using DevHub.Core.Services;
using DevHub.Core.Services.Rpc;
using DevHub.Core.Services.Rpc.Handlers;
using Microsoft.Extensions.Logging;
using Moq;

/// <summary>
/// RPC 日志脱敏测试。
/// </summary>
[Trait("Category", "Impl")]
public sealed class RpcLogJsonSerializerTests
{
    [Fact]
    public void Impl_Serialize_WhenSensitiveFieldsNested_ShouldRedactRecursively()
    {
        var payload = new
        {
            password = "top-secret",
            items = new object[]
            {
                new
                {
                    instanceSessionToken = "token-secret",
                    nested = new
                    {
                        password = "nested-secret"
                    }
                }
            },
            instance = new
            {
                instanceId = "inst-log-redaction",
                appId = "app.log.redaction"
            }
        };

        var json = RpcLogJsonSerializer.Serialize(payload);

        Assert.DoesNotContain("top-secret", json, StringComparison.Ordinal);
        Assert.DoesNotContain("token-secret", json, StringComparison.Ordinal);
        Assert.DoesNotContain("nested-secret", json, StringComparison.Ordinal);
        Assert.Contains("\"password\":\"<redacted>\"", json, StringComparison.Ordinal);
        Assert.Contains("\"instanceSessionToken\":\"<redacted>\"", json, StringComparison.Ordinal);
        Assert.Contains("\"instanceId\":\"inst-log-redaction\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Impl_AppInstancesHandler_WhenLoggingSensitivePayloads_ShouldRedactSecrets()
    {
        var logger = new CapturingLogger<AppInstancesHandler>();
        using var appRegistry = new AppRegistry(new SystemClock(), Mock.Of<ILogger<AppRegistry>>());
        var handler = new AppInstancesHandler(appRegistry, new SystemClock(), logger);
        const string password = "handler-log-password";

        var register = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "handler-log-register",
            Method = HubRpcMethods.HubAppsRegisterInstance,
            Params = JsonSerializer.SerializeToElement(new
            {
                password,
                instance = new
                {
                    instanceId = "inst-handler-log",
                    appId = "app.handler.log",
                    scope = ScopeContract.Global,
                    pid = 4201,
                    invoke = new { poll = true, respond = true }
                }
            })
        }, CancellationToken.None);

        Assert.Null(register.Error);
        var instanceSessionToken = JsonSerializer.SerializeToElement(register.Result).GetProperty("instanceSessionToken").GetString();
        Assert.False(string.IsNullOrWhiteSpace(instanceSessionToken));

        var heartbeat = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "handler-log-heartbeat",
            Method = HubRpcMethods.HubAppsHeartbeat,
            Params = JsonSerializer.SerializeToElement(new
            {
                instanceId = "inst-handler-log",
                instanceSessionToken
            })
        }, CancellationToken.None);

        Assert.Null(heartbeat.Error);

        var unregister = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "handler-log-unregister",
            Method = HubRpcMethods.HubAppsUnregisterInstance,
            Params = JsonSerializer.SerializeToElement(new
            {
                instanceId = "inst-handler-log",
                instanceSessionToken
            })
        }, CancellationToken.None);

        Assert.Null(unregister.Error);

        var logText = string.Join(Environment.NewLine, logger.Entries.Select(entry => entry.Message));
        Assert.DoesNotContain(password, logText, StringComparison.Ordinal);
        Assert.DoesNotContain(instanceSessionToken, logText, StringComparison.Ordinal);
        Assert.Contains("\"password\":\"<redacted>\"", logText, StringComparison.Ordinal);
        Assert.Contains("\"instanceSessionToken\":\"<redacted>\"", logText, StringComparison.Ordinal);
        Assert.Contains("\"instanceId\":\"inst-handler-log\"", logText, StringComparison.Ordinal);
        Assert.Contains("\"appId\":\"app.handler.log\"", logText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Impl_RpcRouter_WhenLoggingSensitivePayloads_ShouldRedactSecrets()
    {
        var logger = new CapturingLogger<RpcRouter>();
        const string password = "router-log-password";
        const string instanceSessionToken = "router-log-session-token";
        var handler = new Mock<IRpcHandler>();
        handler.SetupGet(static candidate => candidate.Method).Returns("hub.apps.registerInstance");
        handler
            .Setup(static candidate => candidate.HandleAsync(It.IsAny<JsonRpcRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new JsonRpcResponse
            {
                Id = "router-log-register",
                Result = new
                {
                    ok = true,
                    instance = new
                    {
                        instanceId = "inst-router-log",
                        appId = "app.router.log"
                    },
                    instanceSessionToken
                }
            });

        var router = new RpcRouter([handler.Object], logger);

        var response = await router.RouteAsync(new JsonRpcRequest
        {
            Id = "router-log-register",
            Method = "hub.apps.registerInstance",
            Params = JsonSerializer.SerializeToElement(new
            {
                password,
                instance = new
                {
                    instanceId = "inst-router-log",
                    appId = "app.router.log"
                }
            })
        }, CancellationToken.None);

        Assert.Null(response.Error);

        var logText = string.Join(Environment.NewLine, logger.Entries.Select(entry => entry.Message));
        Assert.DoesNotContain(password, logText, StringComparison.Ordinal);
        Assert.DoesNotContain(instanceSessionToken, logText, StringComparison.Ordinal);
        Assert.Contains("\"password\":\"<redacted>\"", logText, StringComparison.Ordinal);
        Assert.Contains("\"instanceSessionToken\":\"<redacted>\"", logText, StringComparison.Ordinal);
        Assert.Contains("\"instanceId\":\"inst-router-log\"", logText, StringComparison.Ordinal);
        Assert.Contains("\"appId\":\"app.router.log\"", logText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Impl_RpcRouter_WhenHandlerThrows_ShouldRedactSensitiveParamsInErrorLog()
    {
        var logger = new CapturingLogger<RpcRouter>();
        const string password = "router-log-error-password";
        var handler = new Mock<IRpcHandler>();
        handler.SetupGet(static candidate => candidate.Method).Returns("hub.apps.registerInstance");
        handler
            .Setup(static candidate => candidate.HandleAsync(It.IsAny<JsonRpcRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));

        var router = new RpcRouter([handler.Object], logger);

        var response = await router.RouteAsync(new JsonRpcRequest
        {
            Id = "router-log-error",
            Method = "hub.apps.registerInstance",
            Params = JsonSerializer.SerializeToElement(new
            {
                password,
                instance = new
                {
                    instanceId = "inst-router-log-error",
                    appId = "app.router.log.error"
                }
            })
        }, CancellationToken.None);

        Assert.NotNull(response.Error);
        Assert.Equal("internal_error", response.Error!.Message);

        var logText = string.Join(Environment.NewLine, logger.Entries.Select(entry => entry.Message));
        Assert.DoesNotContain(password, logText, StringComparison.Ordinal);
        Assert.Contains("\"password\":\"<redacted>\"", logText, StringComparison.Ordinal);
        Assert.Contains("\"instanceId\":\"inst-router-log-error\"", logText, StringComparison.Ordinal);
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
