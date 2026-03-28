namespace DevHub.Tests;

using System.Text.Json;
using DevHub.Core.Models;
using DevHub.Core.Models.Rpc;
using DevHub.Core.Services;
using DevHub.Core.Services.Rpc.Handlers;
using Microsoft.Extensions.Logging;
using Moq;

/// <summary>
/// AppInstancesHandler 参数校验测试。
/// </summary>
[Trait("Category", "Impl")]
public sealed class AppInstancesHandlerValidationTests
{
    private readonly Mock<ILogger<AppInstancesHandler>> _handlerLogger = new();

    [Fact]
    public async Task Impl_RegisterInstance_WhenParamsNotObject_ShouldReturnInvalidParams()
    {
        var handler = CreateHandler();

        var response = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "register-invalid-root",
            Method = HubRpcMethods.HubAppsRegisterInstance,
            Params = JsonSerializer.SerializeToElement("bad")
        }, CancellationToken.None);

        AssertError(response, -32602, "invalid_params");
    }

    [Fact]
    public async Task Impl_RegisterInstance_WhenInstanceMissing_ShouldReturnInvalidParams()
    {
        var handler = CreateHandler();

        var response = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "register-missing-instance",
            Method = HubRpcMethods.HubAppsRegisterInstance,
            Params = JsonSerializer.SerializeToElement(new { })
        }, CancellationToken.None);

        AssertError(response, -32602, "invalid_params");
    }

    [Fact]
    public async Task Impl_RegisterInstance_WhenInstanceIdInvalid_ShouldReturnInvalidParams()
    {
        var handler = CreateHandler();

        var response = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "register-invalid-instance-id",
            Method = HubRpcMethods.HubAppsRegisterInstance,
            Params = JsonSerializer.SerializeToElement(new
            {
                instance = new
                {
                    instanceId = "invalid instance id",
                    appId = "app.validation",
                    pid = 100,
                    invoke = new { poll = true, respond = true }
                }
            })
        }, CancellationToken.None);

        AssertError(response, -32602, "invalid_params");
    }

    [Fact]
    public async Task Impl_RegisterInstance_WhenAppIdInvalidOrInvokeInvalid_ShouldReturnInvalidParams()
    {
        var handler = CreateHandler();

        var missingAppId = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "register-missing-app-id",
            Method = HubRpcMethods.HubAppsRegisterInstance,
            Params = JsonSerializer.SerializeToElement(new
            {
                instance = new
                {
                    instanceId = "inst-missing-app-id",
                    pid = 100,
                    invoke = new { poll = true, respond = true }
                }
            })
        }, CancellationToken.None);
        AssertError(missingAppId, -32602, "invalid_params");

        var emptyAppId = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "register-empty-app-id",
            Method = HubRpcMethods.HubAppsRegisterInstance,
            Params = JsonSerializer.SerializeToElement(new
            {
                instance = new
                {
                    instanceId = "inst-empty-app-id",
                    appId = "",
                    pid = 100,
                    invoke = new { poll = true, respond = true }
                }
            })
        }, CancellationToken.None);
        AssertError(emptyAppId, -32602, "invalid_params");

        var invalidInvoke = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "register-invalid-invoke",
            Method = HubRpcMethods.HubAppsRegisterInstance,
            Params = JsonSerializer.SerializeToElement(new
            {
                instance = new
                {
                    instanceId = "inst-invalid-invoke",
                    appId = "app.validation",
                    pid = 100,
                    invoke = new { poll = 1, respond = true }
                }
            })
        }, CancellationToken.None);
        AssertError(invalidInvoke, -32602, "invalid_params");
    }

    [Fact]
    public async Task Impl_RegisterInstance_WhenScopeInvalid_ShouldReturnInvalidParamsWithReason()
    {
        var handler = CreateHandler();

        var response = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "register-invalid-scope",
            Method = HubRpcMethods.HubAppsRegisterInstance,
            Params = JsonSerializer.SerializeToElement(new
            {
                instance = new
                {
                    instanceId = "inst-scope-invalid",
                    appId = "app.validation",
                    pid = 101,
                    scope = new { bad = true },
                    invoke = new { poll = true, respond = true }
                }
            })
        }, CancellationToken.None);

        AssertError(response, -32602, "invalid_params");
        var errorData = JsonSerializer.SerializeToElement(response.Error!.Data);
        Assert.Equal("invalid_scope", errorData.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Impl_RegisterInstance_WhenMetaProvided_ShouldPersistMeta()
    {
        var handler = CreateHandler();

        var response = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "register-with-meta",
            Method = HubRpcMethods.HubAppsRegisterInstance,
            Params = JsonSerializer.SerializeToElement(new
            {
                instance = new
                {
                    instanceId = "inst-with-meta",
                    appId = "app.validation",
                    pid = 102,
                    invoke = new { poll = true, respond = true },
                    meta = new
                    {
                        env = "test",
                        priority = 7,
                        enabled = true
                    }
                }
            })
        }, CancellationToken.None);

        Assert.Null(response.Error);

        var result = JsonSerializer.SerializeToElement(response.Result);
        var meta = result.GetProperty("instance").GetProperty("meta");
        Assert.Equal("test", meta.GetProperty("env").GetString());
        Assert.Equal(7, meta.GetProperty("priority").GetInt32());
        Assert.True(meta.GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public async Task Impl_RegisterInstance_WhenMetaIsNotObject_ShouldReturnInvalidParams()
    {
        var handler = CreateHandler();

        var response = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "register-invalid-meta",
            Method = HubRpcMethods.HubAppsRegisterInstance,
            Params = JsonSerializer.SerializeToElement(new
            {
                instance = new
                {
                    instanceId = "inst-invalid-meta",
                    appId = "app.validation",
                    pid = 102,
                    invoke = new { poll = true, respond = true },
                    meta = "bad"
                }
            })
        }, CancellationToken.None);

        AssertError(response, -32602, "invalid_params");
    }

    [Fact]
    public async Task Impl_Heartbeat_WhenUnknownInstance_ShouldReturnInstanceNotFound()
    {
        var handler = CreateHandler();

        var response = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "heartbeat-unknown",
            Method = HubRpcMethods.HubAppsHeartbeat,
            Params = JsonSerializer.SerializeToElement(new { instanceId = "missing-instance" })
        }, CancellationToken.None);

        AssertError(response, -32010, "instance_not_found");
    }

    [Fact]
    public async Task Impl_Heartbeat_WhenInstanceIdInvalid_ShouldReturnInvalidParams()
    {
        var handler = CreateHandler();

        var missingInstanceId = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "heartbeat-missing-instance-id",
            Method = HubRpcMethods.HubAppsHeartbeat,
            Params = JsonSerializer.SerializeToElement(new { })
        }, CancellationToken.None);
        AssertError(missingInstanceId, -32602, "invalid_params");

        var emptyInstanceId = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "heartbeat-empty-instance-id",
            Method = HubRpcMethods.HubAppsHeartbeat,
            Params = JsonSerializer.SerializeToElement(new { instanceId = "" })
        }, CancellationToken.None);
        AssertError(emptyInstanceId, -32602, "invalid_params");
    }

    [Fact]
    public async Task Impl_Unregister_WhenParamsInvalid_ShouldReturnInvalidParams()
    {
        var handler = CreateHandler();

        var response = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "unregister-invalid",
            Method = HubRpcMethods.HubAppsUnregisterInstance,
            Params = JsonSerializer.SerializeToElement(new { instanceId = 123 })
        }, CancellationToken.None);

        AssertError(response, -32602, "invalid_params");
    }

    [Fact]
    public async Task Impl_Unregister_WhenParamsNotObject_ShouldReturnInvalidParams()
    {
        var handler = CreateHandler();

        var response = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "unregister-invalid-root",
            Method = HubRpcMethods.HubAppsUnregisterInstance,
            Params = JsonSerializer.SerializeToElement("bad")
        }, CancellationToken.None);

        AssertError(response, -32602, "invalid_params");
    }

    [Fact]
    public async Task Impl_ListInstances_WhenScopeAndFlagsInvalid_ShouldReturnInvalidParams()
    {
        var handler = CreateHandler();

        var invalidScope = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "list-invalid-scope",
            Method = HubRpcMethods.HubAppsListInstances,
            Params = JsonSerializer.SerializeToElement(new { scope = new { invalid = true } })
        }, CancellationToken.None);
        AssertError(invalidScope, -32602, "invalid_params");

        var invalidIncludeAllScopes = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "list-invalid-include-all-scopes",
            Method = HubRpcMethods.HubAppsListInstances,
            Params = JsonSerializer.SerializeToElement(new { includeAllScopes = "yes" })
        }, CancellationToken.None);
        AssertError(invalidIncludeAllScopes, -32602, "invalid_params");

        var invalidIncludeOffline = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "list-invalid-include-offline",
            Method = HubRpcMethods.HubAppsListInstances,
            Params = JsonSerializer.SerializeToElement(new { includeOffline = "yes" })
        }, CancellationToken.None);
        AssertError(invalidIncludeOffline, -32602, "invalid_params");

        var invalidAppId = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "list-invalid-appid",
            Method = HubRpcMethods.HubAppsListInstances,
            Params = JsonSerializer.SerializeToElement(new { appId = 1 })
        }, CancellationToken.None);
        AssertError(invalidAppId, -32602, "invalid_params");
    }

    [Fact]
    public async Task Impl_ListInstances_WhenParamsNotObject_ShouldReturnInvalidParams()
    {
        var handler = CreateHandler();

        var response = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "list-invalid-root",
            Method = HubRpcMethods.HubAppsListInstances,
            Params = JsonSerializer.SerializeToElement("bad")
        }, CancellationToken.None);

        AssertError(response, -32602, "invalid_params");
    }

    [Fact]
    public async Task Impl_Methods_WhenLoggerThrowsInTry_ShouldReturnInternalError()
    {
        var appRegistry = new AppRegistry(new SystemClock(), Mock.Of<ILogger<AppRegistry>>());
        var handler = new AppInstancesHandler(appRegistry, new SystemClock(), new ThrowOnDebugLogger<AppInstancesHandler>());

        var register = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "register-logger-throw",
            Method = HubRpcMethods.HubAppsRegisterInstance,
            Params = JsonSerializer.SerializeToElement(new
            {
                instance = new
                {
                    instanceId = "inst-logger-throw",
                    appId = "app.validation",
                    pid = 103,
                    invoke = new { poll = true, respond = true }
                }
            })
        }, CancellationToken.None);
        AssertError(register, -32603, "internal_error");

        var heartbeat = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "heartbeat-logger-throw",
            Method = HubRpcMethods.HubAppsHeartbeat,
            Params = JsonSerializer.SerializeToElement(new { instanceId = "inst-logger-throw" })
        }, CancellationToken.None);
        AssertError(heartbeat, -32603, "internal_error");

        var unregister = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "unregister-logger-throw",
            Method = HubRpcMethods.HubAppsUnregisterInstance,
            Params = JsonSerializer.SerializeToElement(new { instanceId = "inst-logger-throw" })
        }, CancellationToken.None);
        AssertError(unregister, -32603, "internal_error");

        var list = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "list-logger-throw",
            Method = HubRpcMethods.HubAppsListInstances,
            Params = JsonSerializer.SerializeToElement(new { includeOffline = true })
        }, CancellationToken.None);
        AssertError(list, -32603, "internal_error");
    }

    [Fact]
    public async Task Impl_RegisterAndUnregisterWithoutEventBus_ShouldReturnOk()
    {
        var appRegistry = new AppRegistry(new SystemClock(), Mock.Of<ILogger<AppRegistry>>());
        var handler = new AppInstancesHandler(appRegistry, new SystemClock(), _handlerLogger.Object, eventBus: null);

        var register = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "register-no-event-bus",
            Method = HubRpcMethods.HubAppsRegisterInstance,
            Params = JsonSerializer.SerializeToElement(new
            {
                instance = new
                {
                    instanceId = "inst-no-event-bus",
                    appId = "app.validation",
                    pid = 103,
                    invoke = new { poll = true, respond = true }
                }
            })
        }, CancellationToken.None);

        Assert.Null(register.Error);

        var unregister = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "unregister-no-event-bus",
            Method = HubRpcMethods.HubAppsUnregisterInstance,
            Params = JsonSerializer.SerializeToElement(new
            {
                instanceId = "inst-no-event-bus"
            })
        }, CancellationToken.None);

        Assert.Null(unregister.Error);
        Assert.True(JsonSerializer.SerializeToElement(unregister.Result).GetProperty("ok").GetBoolean());
    }

    private AppInstancesHandler CreateHandler()
    {
        var appRegistry = new AppRegistry(new SystemClock(), Mock.Of<ILogger<AppRegistry>>());
        return new AppInstancesHandler(appRegistry, new SystemClock(), _handlerLogger.Object);
    }

    private static void AssertError(JsonRpcResponse response, int code, string message)
    {
        Assert.NotNull(response.Error);
        Assert.Equal(code, response.Error!.Code);
        Assert.Equal(message, response.Error.Message);
    }

    private sealed class ThrowOnDebugLogger<T> : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            return null;
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
            if (logLevel == LogLevel.Debug && formatter(state, exception).Contains("处理hub.apps", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("mock logger failure");
            }
        }
    }
}


