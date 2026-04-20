using System.Text.Json;
using DevHub.Sdk.Internal;
using DevHub.Sdk.Models;

namespace DevHub.Sdk.UnitTests.Rpc;

/// <summary>
/// 调用请求构造白盒测试。
/// </summary>
public sealed class InvocationRequestBuilderTests
{
    [Fact]
    public void NotifyBuilder_ShouldApplyDefaultOptions()
    {
        var payload = RequestPayloadFactory.BuildNotifyParams(new InvokeRequest
        {
            AppId = "test.app",
            Method = "test.notify"
        });

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(payload, DevHubJson.SerializerOptions));
        var options = document.RootElement.GetProperty("options");
        Assert.Equal(60000, options.GetProperty("ttlMs").GetInt32());
        Assert.True(options.GetProperty("queueIfOffline").GetBoolean());
        Assert.True(options.GetProperty("autoLaunch").GetBoolean());
        Assert.False(document.RootElement.TryGetProperty("target", out _));
    }

    [Fact]
    public void RequestBuilder_ShouldApplyDefaultOptions()
    {
        var payload = RequestPayloadFactory.BuildRequestParams(new InvokeRequest
        {
            AppId = "test.app",
            Method = "test.request"
        });

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(payload, DevHubJson.SerializerOptions));
        var options = document.RootElement.GetProperty("options");
        Assert.Equal(300000, options.GetProperty("ttlMs").GetInt32());
        Assert.Equal(120000, options.GetProperty("waitTimeoutMs").GetInt32());
        Assert.True(options.GetProperty("queueIfOffline").GetBoolean());
        Assert.True(options.GetProperty("autoLaunch").GetBoolean());
    }

    [Fact]
    public void NotifyBuilder_WhenWaitTimeoutSpecified_ShouldThrowArgumentException()
    {
        var exception = Assert.Throws<ArgumentException>(() => RequestPayloadFactory.BuildNotifyParams(new InvokeRequest
        {
            AppId = "test.app",
            Method = "test.notify",
            Options = new InvocationOptions
            {
                WaitTimeoutMs = 1000
            }
        }));

        Assert.Contains("waitTimeoutMs", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RequestBuilder_ShouldPreserveExplicitEmptyScope()
    {
        var payload = RequestPayloadFactory.BuildNotifyParams(new InvokeRequest
        {
            AppId = "test.app",
            Method = "test.notify",
            Target = new InvocationTarget
            {
                Scope = string.Empty,
                InstanceId = null
            }
        });

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(payload, DevHubJson.SerializerOptions));
        Assert.Equal(string.Empty, document.RootElement.GetProperty("target").GetProperty("scope").GetString());
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

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(payload, DevHubJson.SerializerOptions));
        Assert.Equal(10, document.RootElement.GetProperty("maxCount").GetInt32());
        Assert.Equal(25000, document.RootElement.GetProperty("waitMs").GetInt32());
    }

    [Fact]
    public void LaunchBuilder_ShouldOmitOptionalFieldsByDefault()
    {
        var payload = RequestPayloadFactory.BuildLaunchParams(new LaunchRequest
        {
            AppId = "test.app"
        });

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(payload, DevHubJson.SerializerOptions));
        Assert.Equal("test.app", document.RootElement.GetProperty("appId").GetString());
        Assert.False(document.RootElement.TryGetProperty("scope", out _));
        Assert.False(document.RootElement.TryGetProperty("dedupeKey", out _));
        Assert.False(document.RootElement.TryGetProperty("waitForRegisterMs", out _));
    }

    [Fact]
    public void LaunchBuilder_ShouldPreserveOptionalFields()
    {
        var payload = RequestPayloadFactory.BuildLaunchParams(new LaunchRequest
        {
            AppId = "test.app",
            Scope = string.Empty,
            DedupeKey = "launch-key",
            WaitForRegisterMs = 0
        });

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(payload, DevHubJson.SerializerOptions));
        Assert.Equal("test.app", document.RootElement.GetProperty("appId").GetString());
        Assert.Equal(string.Empty, document.RootElement.GetProperty("scope").GetString());
        Assert.Equal("launch-key", document.RootElement.GetProperty("dedupeKey").GetString());
        Assert.Equal(0, document.RootElement.GetProperty("waitForRegisterMs").GetInt32());
    }

    [Fact]
    public void LaunchBuilder_WhenWaitForRegisterNegative_ShouldThrowArgumentOutOfRangeException()
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => RequestPayloadFactory.BuildLaunchParams(new LaunchRequest
        {
            AppId = "test.app",
            WaitForRegisterMs = -1
        }));

        Assert.Equal("WaitForRegisterMs", exception.ParamName);
    }

    [Fact]
    public void ListInstancesBuilder_WhenNoFilterSpecified_ShouldReturnNull()
    {
        var payload = RequestPayloadFactory.BuildListInstancesParams(new ListInstancesRequest());

        Assert.Null(payload);
    }

    [Fact]
    public void ListInstancesBuilder_ShouldOnlyIncludeExplicitFilters()
    {
        var payload = RequestPayloadFactory.BuildListInstancesParams(new ListInstancesRequest
        {
            AppId = "test.app",
            Scope = string.Empty,
            IncludeAllScopes = true,
            IncludeOffline = true
        });

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(payload));
        Assert.Equal("test.app", document.RootElement.GetProperty("appId").GetString());
        Assert.Equal(string.Empty, document.RootElement.GetProperty("scope").GetString());
        Assert.True(document.RootElement.GetProperty("includeAllScopes").GetBoolean());
        Assert.True(document.RootElement.GetProperty("includeOffline").GetBoolean());
    }

    [Fact]
    public void Impl_DefinitionBuilders_ShouldIncludeScopeInDefinitionAndIdentityPayloads()
    {
        var definition = new AppDefinition
        {
            AppId = string.Empty,
            Scope = null,
            DisplayName = string.Empty
        };

        var validatePayload = RequestPayloadFactory.BuildValidateDefinitionParams(definition);
        var upsertPayload = RequestPayloadFactory.BuildUpsertDefinitionParams(definition);
        var getPayload = RequestPayloadFactory.BuildGetDefinitionParams("test.app", string.Empty);
        var deletePayload = RequestPayloadFactory.BuildDeleteDefinitionParams("test.app", "scope-a");

        using var validateDocument = JsonDocument.Parse(JsonSerializer.Serialize(validatePayload, DevHubJson.SerializerOptions));
        using var upsertDocument = JsonDocument.Parse(JsonSerializer.Serialize(upsertPayload, DevHubJson.SerializerOptions));
        using var getDocument = JsonDocument.Parse(JsonSerializer.Serialize(getPayload, DevHubJson.SerializerOptions));
        using var deleteDocument = JsonDocument.Parse(JsonSerializer.Serialize(deletePayload, DevHubJson.SerializerOptions));

        Assert.True(validateDocument.RootElement.TryGetProperty("definition", out var validateDefinition));
        Assert.Equal(JsonValueKind.Object, validateDefinition.ValueKind);
        Assert.Equal(JsonValueKind.Null, validateDefinition.GetProperty("scope").ValueKind);
        Assert.False(validateDefinition.TryGetProperty("description", out _));
        Assert.False(validateDefinition.TryGetProperty("capabilities", out _));
        Assert.False(validateDefinition.TryGetProperty("launch", out _));
        Assert.True(upsertDocument.RootElement.TryGetProperty("definition", out var upsertDefinition));
        Assert.Equal(JsonValueKind.Object, upsertDefinition.ValueKind);
        Assert.Equal(JsonValueKind.Null, upsertDefinition.GetProperty("scope").ValueKind);
        Assert.False(upsertDefinition.TryGetProperty("description", out _));
        Assert.False(upsertDefinition.TryGetProperty("capabilities", out _));
        Assert.False(upsertDefinition.TryGetProperty("launch", out _));
        Assert.Equal("test.app", getDocument.RootElement.GetProperty("appId").GetString());
        Assert.Equal(JsonValueKind.Null, getDocument.RootElement.GetProperty("scope").ValueKind);
        Assert.Equal("test.app", deleteDocument.RootElement.GetProperty("appId").GetString());
        Assert.Equal("scope-a", deleteDocument.RootElement.GetProperty("scope").GetString());
    }

    [Fact]
    public void Impl_DefinitionIdentityBuilders_WhenScopeIsWhitespace_ShouldThrowArgumentException()
    {
        Assert.Throws<ArgumentException>(() => RequestPayloadFactory.BuildGetDefinitionParams("test.app", " "));
        Assert.Throws<ArgumentException>(() => RequestPayloadFactory.BuildDeleteDefinitionParams("test.app", "\t"));
    }

    [Fact]
    public void Impl_InstanceBuilders_ShouldPlacePasswordAtTopLevel()
    {
        var registerPayload = RequestPayloadFactory.BuildRegisterInstanceParams(
            new AppInstanceRegistration
            {
                InstanceId = "inst-1",
                AppId = "test.app",
                Pid = Environment.ProcessId,
                Invoke = new InvokeCapability
                {
                    Poll = true,
                    Respond = true
                }
            },
            "secret-1");
        var unregisterPayload = RequestPayloadFactory.BuildUnregisterParams("inst-1", "secret-1");

        using var registerDocument = JsonDocument.Parse(JsonSerializer.Serialize(registerPayload, DevHubJson.SerializerOptions));
        using var unregisterDocument = JsonDocument.Parse(JsonSerializer.Serialize(unregisterPayload, DevHubJson.SerializerOptions));

        Assert.Equal("secret-1", registerDocument.RootElement.GetProperty("password").GetString());
        Assert.False(registerDocument.RootElement.GetProperty("instance").TryGetProperty("password", out _));
        Assert.Equal("secret-1", unregisterDocument.RootElement.GetProperty("password").GetString());
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

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(payload, DevHubJson.SerializerOptions));
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("value").ValueKind);
        Assert.False(document.RootElement.TryGetProperty("error", out _));
    }

    [Fact]
    public void RegisterInstanceBuilder_WhenMetaIsNotObject_ShouldThrowArgumentException()
    {
        Assert.Throws<ArgumentException>(() => RequestPayloadFactory.BuildRegisterInstanceParams(
            new AppInstanceRegistration
            {
                InstanceId = "inst-1",
                AppId = "test.app",
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
}
