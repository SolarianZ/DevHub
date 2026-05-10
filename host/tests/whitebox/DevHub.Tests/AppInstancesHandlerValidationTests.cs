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
    private const string InstancePassword = "validation-tests-password";
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
    public async Task Impl_RegisterInstance_WhenPasswordMissing_ShouldReturnInvalidParams()
    {
        var handler = CreateHandler();

        var response = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "register-missing-password",
            Method = HubRpcMethods.HubAppsRegisterInstance,
            Params = JsonSerializer.SerializeToElement(new
            {
                instance = new
                {
                    instanceId = "inst-missing-password",
                    appId = "app.validation",
                    pid = 100,
                    invoke = new { poll = true, respond = true }
                }
            })
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
            Params = JsonSerializer.SerializeToElement(CreateRegisterParams(new
            {
                instanceId = "invalid instance id",
                appId = "app.validation",
                pid = 100,
                invoke = new { poll = true, respond = true }
            }))
        }, CancellationToken.None);

        AssertError(response, -32602, "invalid_params");

        var overlongResponse = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "register-overlong-instance-id",
            Method = HubRpcMethods.HubAppsRegisterInstance,
            Params = JsonSerializer.SerializeToElement(CreateRegisterParams(new
            {
                instanceId = new string('a', ProtocolIdentifier.MaxInstanceIdLength + 1),
                appId = "app.validation",
                pid = 100,
                invoke = new { poll = true, respond = true }
            }))
        }, CancellationToken.None);

        AssertError(overlongResponse, -32602, "invalid_params");
    }

    [Fact]
    public async Task Impl_RegisterInstance_WhenCanonicalIdentifiersContainInternalDots_ShouldEchoVerbatim()
    {
        var handler = CreateHandler();

        var registerResponse = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "register-canonical-identifiers",
            Method = HubRpcMethods.HubAppsRegisterInstance,
            Params = JsonSerializer.SerializeToElement(CreateRegisterParams(new
            {
                instanceId = "NODE_01.alpha",
                appId = "Sample.App_01",
                scope = "Workspace-A.v2",
                pid = 100,
                invoke = new { poll = true, respond = true }
            }))
        }, CancellationToken.None);

        Assert.Null(registerResponse.Error);
        var instance = JsonSerializer.SerializeToElement(registerResponse.Result).GetProperty("instance");
        Assert.Equal("NODE_01.alpha", instance.GetProperty("instanceId").GetString());
        Assert.Equal("Sample.App_01", instance.GetProperty("appId").GetString());
        Assert.Equal("Workspace-A.v2", instance.GetProperty("scope").GetString());
    }

    [Fact]
    public async Task Impl_RegisterInstance_WhenAppIdInvalidOrInvokeInvalid_ShouldReturnInvalidParams()
    {
        var handler = CreateHandler();

        var missingAppId = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "register-missing-app-id",
            Method = HubRpcMethods.HubAppsRegisterInstance,
            Params = JsonSerializer.SerializeToElement(CreateRegisterParams(new
            {
                instanceId = "inst-missing-app-id",
                pid = 100,
                invoke = new { poll = true, respond = true }
            }))
        }, CancellationToken.None);
        AssertError(missingAppId, -32602, "invalid_params");

        var emptyAppId = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "register-empty-app-id",
            Method = HubRpcMethods.HubAppsRegisterInstance,
            Params = JsonSerializer.SerializeToElement(CreateRegisterParams(new
            {
                instanceId = "inst-empty-app-id",
                appId = "",
                pid = 100,
                invoke = new { poll = true, respond = true }
            }))
        }, CancellationToken.None);
        AssertError(emptyAppId, -32602, "invalid_params");

        var invalidInvoke = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "register-invalid-invoke",
            Method = HubRpcMethods.HubAppsRegisterInstance,
            Params = JsonSerializer.SerializeToElement(CreateRegisterParams(new
            {
                instanceId = "inst-invalid-invoke",
                appId = "app.validation",
                pid = 100,
                invoke = new { poll = 1, respond = true }
            }))
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
            Params = JsonSerializer.SerializeToElement(CreateRegisterParams(new
            {
                instanceId = "inst-scope-invalid",
                appId = "app.validation",
                pid = 101,
                scope = new { bad = true },
                invoke = new { poll = true, respond = true }
            }))
        }, CancellationToken.None);

        AssertError(response, -32602, "invalid_params");
        var errorData = JsonSerializer.SerializeToElement(response.Error!.Data);
        Assert.Equal("invalid_scope", errorData.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Impl_RegisterInstance_WhenIdentifiersUseLeadingOrTrailingDotOrHyphen_ShouldReturnInvalidParams()
    {
        var handler = CreateHandler();

        var invalidInstanceId = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "register-leading-dot-instance-id",
            Method = HubRpcMethods.HubAppsRegisterInstance,
            Params = JsonSerializer.SerializeToElement(CreateRegisterParams(new
            {
                instanceId = ".node-01",
                appId = "app.validation",
                pid = 101,
                invoke = new { poll = true, respond = true }
            }))
        }, CancellationToken.None);
        AssertError(invalidInstanceId, -32602, "invalid_params");

        var invalidAppId = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "register-trailing-dot-app-id",
            Method = HubRpcMethods.HubAppsRegisterInstance,
            Params = JsonSerializer.SerializeToElement(CreateRegisterParams(new
            {
                instanceId = "inst-valid-id",
                appId = "app.validation.",
                pid = 101,
                invoke = new { poll = true, respond = true }
            }))
        }, CancellationToken.None);
        AssertError(invalidAppId, -32602, "invalid_params");

        var invalidScope = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "register-trailing-hyphen-scope",
            Method = HubRpcMethods.HubAppsRegisterInstance,
            Params = JsonSerializer.SerializeToElement(CreateRegisterParams(new
            {
                instanceId = "inst-valid-scope",
                appId = "app.validation",
                pid = 101,
                scope = "workspace-",
                invoke = new { poll = true, respond = true }
            }))
        }, CancellationToken.None);
        AssertError(invalidScope, -32602, "invalid_params");
        var errorData = JsonSerializer.SerializeToElement(invalidScope.Error!.Data);
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
            Params = JsonSerializer.SerializeToElement(CreateRegisterParams(new
            {
                instanceId = "inst-with-meta",
                appId = "app.validation",
                scope = ScopeContract.Global,
                pid = 102,
                invoke = new { poll = true, respond = true },
                meta = new
                {
                    env = "test",
                    priority = 7,
                    enabled = true
                }
            }))
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
            Params = JsonSerializer.SerializeToElement(CreateRegisterParams(new
            {
                instanceId = "inst-invalid-meta",
                appId = "app.validation",
                pid = 102,
                invoke = new { poll = true, respond = true },
                meta = "bad"
            }))
        }, CancellationToken.None);

        AssertError(response, -32602, "invalid_params");
    }

    [Fact]
    public async Task Impl_RegisterInstance_WhenInstancePayloadContainsPassword_ShouldReturnInvalidParamsAndNotCreateState()
    {
        var appRegistry = new AppRegistry(new SystemClock(), Mock.Of<ILogger<AppRegistry>>());
        var handler = new AppInstancesHandler(appRegistry, new SystemClock(), _handlerLogger.Object);

        var response = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "register-nested-password",
            Method = HubRpcMethods.HubAppsRegisterInstance,
            Params = JsonSerializer.SerializeToElement(CreateRegisterParams(new
            {
                instanceId = "inst-nested-password",
                appId = "app.validation",
                scope = ScopeContract.Global,
                pid = 104,
                invoke = new { poll = true, respond = true },
                password = "nested-password"
            }))
        }, CancellationToken.None);

        AssertError(response, -32602, "invalid_params");
        Assert.Null(appRegistry.GetInstance("inst-nested-password"));
    }

    [Fact]
    public async Task Impl_RegisterInstance_WhenInstancePayloadContainsInstanceSessionToken_ShouldReturnInvalidParamsAndKeepStoredState()
    {
        var appRegistry = new AppRegistry(new SystemClock(), Mock.Of<ILogger<AppRegistry>>());
        var handler = new AppInstancesHandler(appRegistry, new SystemClock(), _handlerLogger.Object);

        var firstRegister = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "register-before-nested-token",
            Method = HubRpcMethods.HubAppsRegisterInstance,
            Params = JsonSerializer.SerializeToElement(CreateRegisterParams(new
            {
                instanceId = "inst-nested-token",
                appId = "app.original",
                scope = ScopeContract.Global,
                pid = 105,
                invoke = new { poll = true, respond = true }
            }, password: "correct-password"))
        }, CancellationToken.None);
        Assert.Null(firstRegister.Error);

        var secondRegister = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "register-nested-token",
            Method = HubRpcMethods.HubAppsRegisterInstance,
            Params = JsonSerializer.SerializeToElement(CreateRegisterParams(new
            {
                instanceId = "inst-nested-token",
                appId = "app.updated",
                scope = "workspace-updated",
                pid = 106,
                invoke = new { poll = true, respond = true },
                instanceSessionToken = "nested-token"
            }, password: "correct-password"))
        }, CancellationToken.None);

        AssertError(secondRegister, -32602, "invalid_params");
        var stored = appRegistry.GetInstance("inst-nested-token");
        Assert.NotNull(stored);
        Assert.Equal("app.original", stored!.AppId);
        Assert.Equal(ScopeContract.Global, stored.Scope);
        Assert.Equal(105, stored.Pid);
    }

    [Fact]
    public async Task Impl_Heartbeat_WhenUnknownInstance_ShouldReturnInstanceNotFound()
    {
        var handler = CreateHandler();

        var response = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "heartbeat-unknown",
            Method = HubRpcMethods.HubAppsHeartbeat,
            Params = JsonSerializer.SerializeToElement(new
            {
                instanceId = "missing-instance",
                instanceSessionToken = "missing-instance-token"
            })
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

        var leadingDotInstanceId = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "heartbeat-leading-dot-instance-id",
            Method = HubRpcMethods.HubAppsHeartbeat,
            Params = JsonSerializer.SerializeToElement(new { instanceId = ".invalid" })
        }, CancellationToken.None);
        AssertError(leadingDotInstanceId, -32602, "invalid_params");

        var overlongInstanceId = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "heartbeat-overlong-instance-id",
            Method = HubRpcMethods.HubAppsHeartbeat,
            Params = JsonSerializer.SerializeToElement(new { instanceId = new string('a', ProtocolIdentifier.MaxInstanceIdLength + 1) })
        }, CancellationToken.None);
        AssertError(overlongInstanceId, -32602, "invalid_params");
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

        var overlongResponse = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "unregister-overlong-instance-id",
            Method = HubRpcMethods.HubAppsUnregisterInstance,
            Params = JsonSerializer.SerializeToElement(new { instanceId = new string('a', ProtocolIdentifier.MaxInstanceIdLength + 1) })
        }, CancellationToken.None);

        AssertError(overlongResponse, -32602, "invalid_params");
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
    public async Task Impl_Unregister_WhenPasswordMissing_ShouldReturnInvalidParams()
    {
        var handler = CreateHandler();

        var response = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "unregister-missing-password",
            Method = HubRpcMethods.HubAppsUnregisterInstance,
            Params = JsonSerializer.SerializeToElement(new
            {
                instanceId = "inst-missing-password"
            })
        }, CancellationToken.None);

        AssertError(response, -32602, "invalid_params");
    }

    [Fact]
    public async Task Impl_GetInstance_WhenParamsInvalid_ShouldReturnInvalidParams()
    {
        var handler = CreateHandler();

        var invalidRoot = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "get-invalid-root",
            Method = HubRpcMethods.HubAppsGetInstance,
            Params = JsonSerializer.SerializeToElement("bad")
        }, CancellationToken.None);
        AssertError(invalidRoot, -32602, "invalid_params");

        var missingInstanceId = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "get-missing-instance-id",
            Method = HubRpcMethods.HubAppsGetInstance,
            Params = JsonSerializer.SerializeToElement(new { })
        }, CancellationToken.None);
        AssertError(missingInstanceId, -32602, "invalid_params");

        var emptyInstanceId = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "get-empty-instance-id",
            Method = HubRpcMethods.HubAppsGetInstance,
            Params = JsonSerializer.SerializeToElement(new { instanceId = "" })
        }, CancellationToken.None);
        AssertError(emptyInstanceId, -32602, "invalid_params");

        var malformedInstanceId = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "get-malformed-instance-id",
            Method = HubRpcMethods.HubAppsGetInstance,
            Params = JsonSerializer.SerializeToElement(new { instanceId = "invalid instance id" })
        }, CancellationToken.None);
        AssertError(malformedInstanceId, -32602, "invalid_params");

        var trailingHyphenInstanceId = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "get-trailing-hyphen-instance-id",
            Method = HubRpcMethods.HubAppsGetInstance,
            Params = JsonSerializer.SerializeToElement(new { instanceId = "invalid-instance-" })
        }, CancellationToken.None);
        AssertError(trailingHyphenInstanceId, -32602, "invalid_params");

        var overlongInstanceId = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "get-overlong-instance-id",
            Method = HubRpcMethods.HubAppsGetInstance,
            Params = JsonSerializer.SerializeToElement(new { instanceId = new string('a', ProtocolIdentifier.MaxInstanceIdLength + 1) })
        }, CancellationToken.None);
        AssertError(overlongInstanceId, -32602, "invalid_params");
    }

    [Fact]
    public async Task Impl_GetInstance_WhenUnknown_ShouldReturnInstanceNotFoundWithInstanceId()
    {
        var handler = CreateHandler();

        var response = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "get-unknown-instance",
            Method = HubRpcMethods.HubAppsGetInstance,
            Params = JsonSerializer.SerializeToElement(new { instanceId = "missing-instance" })
        }, CancellationToken.None);

        AssertError(response, -32010, "instance_not_found");
        var errorData = JsonSerializer.SerializeToElement(response.Error!.Data);
        Assert.Equal("unknown_instance", errorData.GetProperty("reason").GetString());
        Assert.Equal("missing-instance", errorData.GetProperty("instanceId").GetString());
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

        var missingScope = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "list-missing-scope",
            Method = HubRpcMethods.HubAppsListInstances,
            Params = JsonSerializer.SerializeToElement(new { includeOffline = true })
        }, CancellationToken.None);
        AssertError(missingScope, -32602, "invalid_params");

        var invalidIncludeOffline = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "list-invalid-include-offline",
            Method = HubRpcMethods.HubAppsListInstances,
            Params = JsonSerializer.SerializeToElement(new { scope = (string?)null, includeOffline = "yes" })
        }, CancellationToken.None);
        AssertError(invalidIncludeOffline, -32602, "invalid_params");

        var invalidAppId = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "list-invalid-appid",
            Method = HubRpcMethods.HubAppsListInstances,
            Params = JsonSerializer.SerializeToElement(new { appId = 1, scope = (string?)null })
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
    public async Task Impl_ListInstances_WhenScopeNull_ShouldReturnAllScopes()
    {
        var appRegistry = new AppRegistry(new SystemClock(), Mock.Of<ILogger<AppRegistry>>());
        appRegistry.RegisterInstance(new AppInstance
        {
            InstanceId = "inst-global",
            AppId = "app.validation.scope",
            Scope = ScopeContract.Global,
            Pid = 2001,
            Invoke = new InvokeCapability
            {
                Poll = true,
                Respond = true
            }
        });
        appRegistry.RegisterInstance(new AppInstance
        {
            InstanceId = "inst-scoped",
            AppId = "app.validation.scope",
            Scope = "workspace-a",
            Pid = 2002,
            Invoke = new InvokeCapability
            {
                Poll = true,
                Respond = true
            }
        });

        var handler = new AppInstancesHandler(appRegistry, new SystemClock(), _handlerLogger.Object);

        var response = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "list-all-scopes",
            Method = HubRpcMethods.HubAppsListInstances,
            Params = JsonSerializer.SerializeToElement(new
            {
                scope = (string?)null,
                includeOffline = true
            })
        }, CancellationToken.None);

        Assert.Null(response.Error);
        var instances = JsonSerializer.SerializeToElement(response.Result).GetProperty("instances").EnumerateArray().ToList();
        Assert.Contains(instances, instance => instance.GetProperty("instanceId").GetString() == "inst-global");
        Assert.Contains(instances, instance => instance.GetProperty("instanceId").GetString() == "inst-scoped");
    }

    [Fact]
    public async Task Impl_RegisterAndUnregisterWithoutEventBus_ShouldReturnOk()
    {
        var appRegistry = new AppRegistry(new SystemClock(), Mock.Of<ILogger<AppRegistry>>());
        var handler = new AppInstancesHandler(appRegistry, new SystemClock(), _handlerLogger.Object, eventPublisher: null);

        var register = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "register-no-event-bus",
            Method = HubRpcMethods.HubAppsRegisterInstance,
            Params = JsonSerializer.SerializeToElement(CreateRegisterParams(new
            {
                instanceId = "inst-no-event-bus",
                appId = "app.validation",
                scope = ScopeContract.Global,
                pid = 103,
                invoke = new { poll = true, respond = true }
            }))
        }, CancellationToken.None);

        Assert.Null(register.Error);
        var instanceSessionToken = ExtractInstanceSessionToken(register);

        var unregister = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "unregister-no-event-bus",
            Method = HubRpcMethods.HubAppsUnregisterInstance,
            Params = JsonSerializer.SerializeToElement(new
            {
                instanceId = "inst-no-event-bus",
                instanceSessionToken
            })
        }, CancellationToken.None);

        Assert.Null(unregister.Error);
        Assert.True(JsonSerializer.SerializeToElement(unregister.Result).GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task Impl_RegisterInstance_WhenPasswordMismatch_ShouldReturnForbiddenAndKeepStoredInstance()
    {
        var appRegistry = new AppRegistry(new SystemClock(), Mock.Of<ILogger<AppRegistry>>());
        var handler = new AppInstancesHandler(appRegistry, new SystemClock(), _handlerLogger.Object);

        var firstRegister = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "register-password-initial",
            Method = HubRpcMethods.HubAppsRegisterInstance,
            Params = JsonSerializer.SerializeToElement(CreateRegisterParams(new
            {
                instanceId = "inst-password-guard",
                appId = "app.original",
                scope = ScopeContract.Global,
                pid = 201,
                invoke = new { poll = true, respond = true }
            }, password: "correct-password"))
        }, CancellationToken.None);
        Assert.Null(firstRegister.Error);

        var secondRegister = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "register-password-mismatch",
            Method = HubRpcMethods.HubAppsRegisterInstance,
            Params = JsonSerializer.SerializeToElement(CreateRegisterParams(new
            {
                instanceId = "inst-password-guard",
                appId = "app.updated",
                scope = ScopeContract.Global,
                pid = 202,
                invoke = new { poll = true, respond = true }
            }, password: "wrong-password"))
        }, CancellationToken.None);

        AssertError(secondRegister, -32002, "forbidden");
        var errorData = JsonSerializer.SerializeToElement(secondRegister.Error!.Data);
        Assert.Equal("instance_password_mismatch", errorData.GetProperty("reason").GetString());

        var stored = appRegistry.GetInstance("inst-password-guard");
        Assert.NotNull(stored);
        Assert.Equal("app.original", stored!.AppId);
        Assert.Equal(201, stored.Pid);
    }

    [Fact]
    public async Task Impl_RegisterInstance_WhenIdentityMismatchWithMatchingPassword_ShouldReturnForbiddenAndKeepStoredInstance()
    {
        var appRegistry = new AppRegistry(new SystemClock(), Mock.Of<ILogger<AppRegistry>>());
        var handler = new AppInstancesHandler(appRegistry, new SystemClock(), _handlerLogger.Object);

        var firstRegister = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "register-identity-initial",
            Method = HubRpcMethods.HubAppsRegisterInstance,
            Params = JsonSerializer.SerializeToElement(CreateRegisterParams(new
            {
                instanceId = "inst-identity-guard",
                appId = "app.original",
                scope = ScopeContract.Global,
                pid = 211,
                invoke = new { poll = true, respond = true }
            }, password: "correct-password"))
        }, CancellationToken.None);
        Assert.Null(firstRegister.Error);

        var secondRegister = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "register-identity-mismatch",
            Method = HubRpcMethods.HubAppsRegisterInstance,
            Params = JsonSerializer.SerializeToElement(CreateRegisterParams(new
            {
                instanceId = "inst-identity-guard",
                appId = "app.updated",
                scope = "workspace-updated",
                pid = 212,
                invoke = new { poll = true, respond = true }
            }, password: "correct-password"))
        }, CancellationToken.None);

        AssertError(secondRegister, -32002, "forbidden");
        var errorData = JsonSerializer.SerializeToElement(secondRegister.Error!.Data);
        Assert.Equal("instance_identity_mismatch", errorData.GetProperty("reason").GetString());

        var stored = appRegistry.GetInstance("inst-identity-guard");
        Assert.NotNull(stored);
        Assert.Equal("app.original", stored!.AppId);
        Assert.Equal(ScopeContract.Global, stored.Scope);
        Assert.Equal(211, stored.Pid);
    }

    [Fact]
    public async Task Impl_Unregister_WhenPasswordMismatch_ShouldReturnForbiddenAndKeepStoredInstance()
    {
        var appRegistry = new AppRegistry(new SystemClock(), Mock.Of<ILogger<AppRegistry>>());
        var handler = new AppInstancesHandler(appRegistry, new SystemClock(), _handlerLogger.Object);

        var register = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "register-before-unregister-mismatch",
            Method = HubRpcMethods.HubAppsRegisterInstance,
            Params = JsonSerializer.SerializeToElement(CreateRegisterParams(new
            {
                instanceId = "inst-unregister-guard",
                appId = "app.validation",
                scope = ScopeContract.Global,
                pid = 301,
                invoke = new { poll = true, respond = true }
            }, password: "correct-password"))
        }, CancellationToken.None);
        Assert.Null(register.Error);

        var currentToken = ExtractInstanceSessionToken(register);
        var unregister = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "unregister-password-mismatch",
            Method = HubRpcMethods.HubAppsUnregisterInstance,
            Params = JsonSerializer.SerializeToElement(CreateUnregisterParams("inst-unregister-guard", instanceSessionToken: $"wrong-{currentToken}"))
        }, CancellationToken.None);

        AssertError(unregister, -32002, "forbidden");
        var errorData = JsonSerializer.SerializeToElement(unregister.Error!.Data);
        Assert.Equal("instance_session_token_mismatch", errorData.GetProperty("reason").GetString());
        Assert.NotNull(appRegistry.GetInstance("inst-unregister-guard"));
    }

    [Fact]
    public async Task Impl_Unregister_WhenUnknownInstanceAndTokenPresent_ShouldReturnOk()
    {
        var handler = CreateHandler();

        var response = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "unregister-unknown-with-password",
            Method = HubRpcMethods.HubAppsUnregisterInstance,
            Params = JsonSerializer.SerializeToElement(CreateUnregisterParams("missing-instance", instanceSessionToken: "missing-instance-token"))
        }, CancellationToken.None);

        Assert.Null(response.Error);
        Assert.True(JsonSerializer.SerializeToElement(response.Result).GetProperty("ok").GetBoolean());
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

    private static object CreateRegisterParams(object instance, string? password = null)
    {
        return new
        {
            password = password ?? InstancePassword,
            instance
        };
    }

    private static string ExtractInstanceSessionToken(JsonRpcResponse response)
    {
        return JsonSerializer.SerializeToElement(response.Result).GetProperty("instanceSessionToken").GetString()!;
    }

    private static object CreateUnregisterParams(string instanceId, string? instanceSessionToken = null)
    {
        return new
        {
            instanceId,
            instanceSessionToken = instanceSessionToken ?? "validation-instance-token"
        };
    }

}
