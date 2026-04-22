using System.Text.Json;
using DevHub.Sdk.Internal;
using DevHub.Sdk.Models;

namespace DevHub.Sdk.UnitTests.Rpc;

/// <summary>
/// 显式作用域契约下的请求构造白盒测试。
/// </summary>
public sealed class InvocationRequestBuilderTests
{
    [Fact]
    public void NotifyBuilder_ShouldRequireExplicitTargetScopeAndApplyDefaultOptions()
    {
        var payload = RequestPayloadFactory.BuildNotifyParams(new InvokeRequest
        {
            AppId = "test.app",
            Method = "test.notify",
            Target = new InvocationTarget
            {
                Scope = string.Empty
            }
        });

        using var document = Serialize(payload);
        var options = document.RootElement.GetProperty("options");
        var target = document.RootElement.GetProperty("target");

        Assert.Equal(60000, options.GetProperty("ttlMs").GetInt32());
        Assert.True(options.GetProperty("queueIfOffline").GetBoolean());
        Assert.True(options.GetProperty("autoLaunch").GetBoolean());
        Assert.Equal(string.Empty, target.GetProperty("scope").GetString());
        Assert.Equal(JsonValueKind.Null, target.GetProperty("instanceId").ValueKind);
    }

    [Fact]
    public void RequestBuilder_ShouldPreserveExplicitScopeAndDefaults()
    {
        var payload = RequestPayloadFactory.BuildRequestParams(new InvokeRequest
        {
            AppId = "test.app",
            Method = "test.request",
            Target = new InvocationTarget
            {
                Scope = "workspace-a"
            }
        });

        using var document = Serialize(payload);
        var options = document.RootElement.GetProperty("options");
        var target = document.RootElement.GetProperty("target");

        Assert.Equal(300000, options.GetProperty("ttlMs").GetInt32());
        Assert.Equal(120000, options.GetProperty("waitTimeoutMs").GetInt32());
        Assert.True(options.GetProperty("queueIfOffline").GetBoolean());
        Assert.True(options.GetProperty("autoLaunch").GetBoolean());
        Assert.Equal("workspace-a", target.GetProperty("scope").GetString());
    }

    [Fact]
    public void NotifyBuilder_WhenTargetMissing_ShouldThrowArgumentException()
    {
        var exception = Assert.Throws<ArgumentException>(() => RequestPayloadFactory.BuildNotifyParams(new InvokeRequest
        {
            AppId = "test.app",
            Method = "test.notify"
        }));

        Assert.Contains("Target", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NotifyBuilder_WhenWaitTimeoutSpecified_ShouldThrowArgumentException()
    {
        var exception = Assert.Throws<ArgumentException>(() => RequestPayloadFactory.BuildNotifyParams(new InvokeRequest
        {
            AppId = "test.app",
            Method = "test.notify",
            Target = new InvocationTarget
            {
                Scope = string.Empty
            },
            Options = new InvocationOptions
            {
                WaitTimeoutMs = 1000
            }
        }));

        Assert.Contains("waitTimeoutMs", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RequestBuilder_WhenAutoLaunchEnabledWithInstanceId_ShouldThrowArgumentException()
    {
        Assert.Throws<ArgumentException>(() => RequestPayloadFactory.BuildNotifyParams(new InvokeRequest
        {
            AppId = "test.app",
            Method = "test.notify",
            Target = new InvocationTarget
            {
                Scope = string.Empty,
                InstanceId = "inst-1"
            },
            Options = new InvocationOptions
            {
                AutoLaunch = true
            }
        }));
    }

    [Fact]
    public void RequestBuilder_WhenAutoLaunchRequiresQueueIfOfflineTrue_ShouldThrowArgumentException()
    {
        Assert.Throws<ArgumentException>(() => RequestPayloadFactory.BuildNotifyParams(new InvokeRequest
        {
            AppId = "test.app",
            Method = "test.notify",
            Target = new InvocationTarget
            {
                Scope = string.Empty
            },
            Options = new InvocationOptions
            {
                AutoLaunch = true,
                QueueIfOffline = false
            }
        }));
    }

    [Fact]
    public void PollBuilder_ShouldApplyDefaults()
    {
        var payload = RequestPayloadFactory.BuildPollParams(new PollRequest
        {
            InstanceId = "inst-1"
        });

        using var document = Serialize(payload);
        Assert.Equal(10, document.RootElement.GetProperty("maxCount").GetInt32());
        Assert.Equal(25000, document.RootElement.GetProperty("waitMs").GetInt32());
    }

    [Fact]
    public void LaunchBuilder_ShouldSerializeExplicitGlobalScopeAndOptionalFields()
    {
        var payload = RequestPayloadFactory.BuildLaunchParams(new LaunchRequest
        {
            AppId = "test.app",
            Scope = string.Empty,
            DedupeKey = "launch-key",
            WaitForRegisterMs = 0
        });

        using var document = Serialize(payload);
        Assert.Equal("test.app", document.RootElement.GetProperty("appId").GetString());
        Assert.Equal(string.Empty, document.RootElement.GetProperty("scope").GetString());
        Assert.Equal("launch-key", document.RootElement.GetProperty("dedupeKey").GetString());
        Assert.Equal(0, document.RootElement.GetProperty("waitForRegisterMs").GetInt32());
    }

    [Fact]
    public void LaunchBuilder_WhenScopeIsWhitespace_ShouldThrowArgumentException()
    {
        Assert.Throws<ArgumentException>(() => new LaunchRequest { Scope = " " });
    }

    [Fact]
    public void LaunchBuilder_WhenWaitForRegisterNegative_ShouldThrowArgumentOutOfRangeException()
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => RequestPayloadFactory.BuildLaunchParams(new LaunchRequest
        {
            AppId = "test.app",
            Scope = string.Empty,
            WaitForRegisterMs = -1
        }));

        Assert.Equal("WaitForRegisterMs", exception.ParamName);
    }

    [Fact]
    public void ListDefinitionsBuilder_ShouldSerializeExplicitNullScopeFilter()
    {
        var payload = RequestPayloadFactory.BuildListDefinitionsParams(new ListDefinitionsRequest());

        using var document = Serialize(payload);
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("scope").ValueKind);
        Assert.False(document.RootElement.TryGetProperty("includeAllScopes", out _));
    }

    [Fact]
    public void ListInstancesBuilder_ShouldOnlyIncludeExplicitFilters()
    {
        var payload = RequestPayloadFactory.BuildListInstancesParams(new ListInstancesRequest
        {
            AppId = "test.app",
            Scope = string.Empty,
            IncludeOffline = true
        });

        using var document = Serialize(payload);
        Assert.Equal("test.app", document.RootElement.GetProperty("appId").GetString());
        Assert.Equal(string.Empty, document.RootElement.GetProperty("scope").GetString());
        Assert.True(document.RootElement.GetProperty("includeOffline").GetBoolean());
        Assert.False(document.RootElement.TryGetProperty("includeAllScopes", out _));
    }

    [Fact]
    public void ListScopeFilters_WhenScopeIsWhitespace_ShouldThrowArgumentException()
    {
        Assert.Throws<ArgumentException>(() => new ListDefinitionsRequest
        {
            Scope = " "
        });

        Assert.Throws<ArgumentException>(() => new ListInstancesRequest
        {
            Scope = "\t"
        });
    }

    [Fact]
    public void DefinitionBuilders_ShouldSerializeExplicitStringScope()
    {
        var definition = new AppDefinition
        {
            AppId = "test.app",
            Scope = string.Empty,
            DisplayName = "Test App"
        };

        var validatePayload = RequestPayloadFactory.BuildValidateDefinitionParams(definition);
        var upsertPayload = RequestPayloadFactory.BuildUpsertDefinitionParams(definition);
        var getPayload = RequestPayloadFactory.BuildGetDefinitionParams("test.app", string.Empty);
        var deletePayload = RequestPayloadFactory.BuildDeleteDefinitionParams("test.app", "scope-a");

        using var validateDocument = Serialize(validatePayload);
        using var upsertDocument = Serialize(upsertPayload);
        using var getDocument = Serialize(getPayload);
        using var deleteDocument = Serialize(deletePayload);

        var validateDefinition = validateDocument.RootElement.GetProperty("definition");
        var upsertDefinition = upsertDocument.RootElement.GetProperty("definition");

        Assert.Equal(string.Empty, validateDefinition.GetProperty("scope").GetString());
        Assert.Equal(string.Empty, upsertDefinition.GetProperty("scope").GetString());
        Assert.Equal(string.Empty, getDocument.RootElement.GetProperty("scope").GetString());
        Assert.Equal("scope-a", deleteDocument.RootElement.GetProperty("scope").GetString());
    }

    [Fact]
    public void DefinitionIdentityBuilders_WhenScopeIsWhitespace_ShouldThrowArgumentException()
    {
        Assert.Throws<ArgumentException>(() => RequestPayloadFactory.BuildGetDefinitionParams("test.app", " "));
        Assert.Throws<ArgumentException>(() => RequestPayloadFactory.BuildDeleteDefinitionParams("test.app", "\t"));
    }

    [Fact]
    public void ScopeBearingModels_ShouldEnforceUpdatedContract()
    {
        Assert.Throws<ArgumentException>(() => new AppDefinition { Scope = null! });
        Assert.Throws<ArgumentException>(() => new AppDefinition { Scope = " " });

        Assert.Throws<ArgumentException>(() => new AppInstance { Scope = null! });
        Assert.Throws<ArgumentException>(() => new AppInstanceRegistration { Scope = null! });
        Assert.Throws<ArgumentException>(() => new LaunchRequest { Scope = null! });
        Assert.Throws<ArgumentException>(() => new InvocationTarget { Scope = null! });

        var globalInstance = new AppInstanceRegistration
        {
            Scope = string.Empty
        };

        Assert.Equal(string.Empty, globalInstance.Scope);
    }

    [Fact]
    public void RegisterInstanceBuilder_ShouldPlacePasswordAtTopLevelAndPreserveGlobalScope()
    {
        var registerPayload = RequestPayloadFactory.BuildRegisterInstanceParams(
            new AppInstanceRegistration
            {
                InstanceId = "inst-1",
                AppId = "test.app",
                Scope = string.Empty,
                Pid = Environment.ProcessId,
                Invoke = new InvokeCapability
                {
                    Poll = true,
                    Respond = true
                }
            },
            "secret-1");
        var unregisterPayload = RequestPayloadFactory.BuildUnregisterParams("inst-1", "secret-1");

        using var registerDocument = Serialize(registerPayload);
        using var unregisterDocument = Serialize(unregisterPayload);

        Assert.Equal("secret-1", registerDocument.RootElement.GetProperty("password").GetString());
        Assert.Equal(string.Empty, registerDocument.RootElement.GetProperty("instance").GetProperty("scope").GetString());
        Assert.False(registerDocument.RootElement.GetProperty("instance").TryGetProperty("password", out _));
        Assert.Equal("secret-1", unregisterDocument.RootElement.GetProperty("password").GetString());
    }

    [Fact]
    public void RegisterInstanceBuilder_WhenMetaIsNotObject_ShouldThrowArgumentException()
    {
        Assert.Throws<ArgumentException>(() => RequestPayloadFactory.BuildRegisterInstanceParams(
            new AppInstanceRegistration
            {
                InstanceId = "inst-1",
                AppId = "test.app",
                Scope = string.Empty,
                Pid = Environment.ProcessId,
                Invoke = new InvokeCapability
                {
                    Poll = true,
                    Respond = true
                },
                Meta = new[] { 1, 2, 3 }
            },
            "secret-1"));
    }

    [Fact]
    public void RespondBuilder_WhenValueExplicitlyNull_ShouldWriteJsonNull()
    {
        var payload = RequestPayloadFactory.BuildRespondParams(new RespondRequest
        {
            InstanceId = "inst-1",
            InvocationId = "invk-1",
            Value = null
        });

        using var document = Serialize(payload);
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("value").ValueKind);
        Assert.False(document.RootElement.TryGetProperty("error", out _));
    }

    [Fact]
    public void RespondBuilder_WhenErrorMessageMissing_ShouldThrowArgumentException()
    {
        Assert.Throws<ArgumentException>(() => RequestPayloadFactory.BuildRespondParams(new RespondRequest
        {
            InstanceId = "inst-1",
            InvocationId = "invk-1",
            Error = new DevHubCalleeError
            {
                Code = 1001,
                Message = string.Empty
            }
        }));
    }

    [Fact]
    public void RespondBuilder_WhenErrorDataIsNotObject_ShouldThrowArgumentException()
    {
        var exception = Assert.Throws<ArgumentException>(() => RequestPayloadFactory.BuildRespondParams(new RespondRequest
        {
            InstanceId = "inst-1",
            InvocationId = "invk-1",
            Error = new DevHubCalleeError
            {
                Code = 1001,
                Message = "app_error",
                Data = JsonSerializer.SerializeToElement("boom")
            }
        }));

        Assert.Contains("Error.Data", exception.Message, StringComparison.Ordinal);
    }

    private static JsonDocument Serialize(object payload)
    {
        return JsonDocument.Parse(JsonSerializer.Serialize(payload, DevHubJson.SerializerOptions));
    }
}
