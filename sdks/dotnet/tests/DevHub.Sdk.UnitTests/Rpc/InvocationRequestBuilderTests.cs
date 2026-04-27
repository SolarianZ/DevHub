using System.Text.Json;
using DevHub.Sdk.Internal;
using DevHub.Sdk.Models;
using Newtonsoft.Json.Linq;

namespace DevHub.Sdk.UnitTests.Rpc;

/// <summary>
/// 调用请求构造白盒测试。
/// </summary>
public sealed class InvocationRequestBuilderTests
{
    [Fact]
    public void M5_DN_UT_006_NotifyBuilder_ShouldApplyDefaultOptions()
    {
        var payload = RequestPayloadFactory.BuildNotifyParams(new InvokeRequest
        {
            AppId = "test.app",
            Method = "test.notify"
        });

        var document = ToJObject(payload);
        var options = (JObject)document["options"]!;
        Assert.Equal(60000, (int)options["ttlMs"]!);
        Assert.True((bool)options["queueIfOffline"]!);
        Assert.True((bool)options["autoLaunch"]!);
        Assert.Equal(string.Empty, (string?)document["target"]!["scope"]!);
        Assert.Equal(JTokenType.Null, document["target"]!["instanceId"]!.Type);
    }

    [Fact]
    public void M5_DN_UT_006_RequestBuilder_ShouldApplyDefaultOptions()
    {
        var payload = RequestPayloadFactory.BuildRequestParams(new InvokeRequest
        {
            AppId = "test.app",
            Method = "test.request"
        });

        var document = ToJObject(payload);
        var options = (JObject)document["options"]!;
        Assert.Equal(300000, (int)options["ttlMs"]!);
        Assert.Equal(120000, (int)options["waitTimeoutMs"]!);
        Assert.True((bool)options["queueIfOffline"]!);
        Assert.True((bool)options["autoLaunch"]!);
    }

    [Fact]
    public void M5_DN_UT_006_RequestBuilder_ShouldPreserveDictionaryArgKeys()
    {
        var payload = RequestPayloadFactory.BuildRequestParams(new InvokeRequest
        {
            AppId = "test.app",
            Method = "test.request",
            Args = new Dictionary<string, object?>
            {
                ["FooBar"] = 1,
                ["Nested"] = new Dictionary<string, object?>
                {
                    ["InnerKey"] = "value"
                }
            }
        });

        var document = ToJObject(payload);
        Assert.Equal(1, (int)document["args"]!["FooBar"]!);
        Assert.Null(document["args"]!["fooBar"]);
        Assert.Equal("value", (string?)document["args"]!["Nested"]!["InnerKey"]!);
        Assert.Null(document["args"]!["Nested"]!["innerKey"]);
    }

    [Fact]
    public void M5_DN_UT_006_RequestBuilder_ShouldSerializeTopLevelJsonElementArgs()
    {
        using var argsDocument = JsonDocument.Parse("""{"message":"hello-default-global","nested":{"count":2}}""");
        var payload = RequestPayloadFactory.BuildRequestParams(new InvokeRequest
        {
            AppId = "test.app",
            Method = "test.request",
            Args = argsDocument.RootElement.Clone()
        });

        var document = ToJObject(payload);
        Assert.Equal("hello-default-global", (string?)document["args"]!["message"]!);
        Assert.Equal(2, (int)document["args"]!["nested"]!["count"]!);
        Assert.Null(document["args"]!["valueKind"]);
    }

    [Fact]
    public void M5_DN_UT_006_NotifyBuilder_WhenWaitTimeoutSpecified_ShouldThrowArgumentException()
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
    public void M5_DN_UT_006_RequestBuilder_ShouldPreserveExplicitEmptyScope()
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

        var document = ToJObject(payload);
        Assert.Equal(string.Empty, (string?)document["target"]!["scope"]!);
    }

    [Fact]
    public void M5_DN_UT_006_RequestBuilder_WhenAutoLaunchEnabledWithInstanceId_ShouldThrowArgumentException()
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
    public void M5_DN_UT_006_RequestBuilder_WhenAutoLaunchRequiresQueueIfOfflineTrue_ShouldThrowArgumentException()
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
    public void M5_DN_UT_006_PollBuilder_ShouldApplyDefaults()
    {
        var payload = RequestPayloadFactory.BuildPollParams(new PollRequest
        {
            InstanceId = "inst-1",
            InstanceSessionToken = "session-1"
        });

        var document = ToJObject(payload);
        Assert.Equal("session-1", (string?)document["instanceSessionToken"]!);
        Assert.Equal(10, (int)document["maxCount"]!);
        Assert.Equal(25000, (int)document["waitMs"]!);
    }

    [Fact]
    public void M5_DN_UT_006_LaunchBuilder_ShouldOmitOptionalFieldsByDefault()
    {
        var payload = RequestPayloadFactory.BuildLaunchParams(new LaunchRequest
        {
            AppId = "test.app"
        });

        var document = ToJObject(payload);
        Assert.Equal("test.app", (string?)document["appId"]!);
        Assert.Equal(string.Empty, (string?)document["scope"]!);
        Assert.Null(document["dedupeKey"]);
        Assert.Null(document["waitForRegisterMs"]);
    }

    [Fact]
    public void M5_DN_UT_006_LaunchBuilder_ShouldPreserveOptionalFields()
    {
        var payload = RequestPayloadFactory.BuildLaunchParams(new LaunchRequest
        {
            AppId = "test.app",
            Scope = string.Empty,
            DedupeKey = "launch-key",
            WaitForRegisterMs = 0
        });

        var document = ToJObject(payload);
        Assert.Equal("test.app", (string?)document["appId"]!);
        Assert.Equal(string.Empty, (string?)document["scope"]!);
        Assert.Equal("launch-key", (string?)document["dedupeKey"]!);
        Assert.Equal(0, (int)document["waitForRegisterMs"]!);
    }

    [Fact]
    public void M5_DN_UT_006_LaunchBuilder_WhenWaitForRegisterNegative_ShouldThrowArgumentOutOfRangeException()
    {
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => RequestPayloadFactory.BuildLaunchParams(new LaunchRequest
        {
            AppId = "test.app",
            WaitForRegisterMs = -1
        }));

        Assert.Equal("WaitForRegisterMs", exception.ParamName);
    }

    [Fact]
    public void M5_DN_UT_006_ListInstancesBuilder_WhenNoFilterSpecified_ShouldEmitExplicitNullScope()
    {
        var payload = RequestPayloadFactory.BuildListInstancesParams(new ListInstancesRequest());

        var document = ToJObject(payload);
        Assert.NotNull(document.Property("scope"));
        Assert.Equal(JTokenType.Null, document["scope"]!.Type);
    }

    [Fact]
    public void M5_DN_UT_006_ListInstancesBuilder_ShouldOnlyIncludeExplicitFilters()
    {
        var payload = RequestPayloadFactory.BuildListInstancesParams(new ListInstancesRequest
        {
            AppId = "test.app",
            Scope = string.Empty,
            IncludeOffline = true
        });

        var document = ToJObject(payload!);
        Assert.Equal("test.app", (string?)document["appId"]!);
        Assert.Equal(string.Empty, (string?)document["scope"]!);
        Assert.True((bool)document["includeOffline"]!);
    }

    [Fact]
    public void M5_DN_UT_006_RespondBuilder_WhenValueExplicitlyNull_ShouldWriteJsonNull()
    {
        var payload = RequestPayloadFactory.BuildRespondParams(new RespondRequest
        {
            InstanceId = "inst-1",
            InstanceSessionToken = "session-1",
            InvocationId = "invk-1",
            Value = null
        });

        var document = ToJObject(payload);
        Assert.Equal(JTokenType.Null, document["value"]!.Type);
        Assert.Null(document["error"]);
    }

    [Fact]
    public void M5_DN_UT_006_RespondBuilder_ShouldPreserveDictionaryValueKeys()
    {
        var payload = RequestPayloadFactory.BuildRespondParams(new RespondRequest
        {
            InstanceId = "inst-1",
            InstanceSessionToken = "session-1",
            InvocationId = "invk-1",
            Value = new Dictionary<string, object?>
            {
                ["FooBar"] = true,
                ["Nested"] = new Dictionary<string, object?>
                {
                    ["InnerKey"] = "value"
                }
            }
        });

        var document = ToJObject(payload);
        Assert.True((bool)document["value"]!["FooBar"]!);
        Assert.Null(document["value"]!["fooBar"]);
        Assert.Equal("value", (string?)document["value"]!["Nested"]!["InnerKey"]!);
        Assert.Null(document["value"]!["Nested"]!["innerKey"]);
    }

    [Fact]
    public void M5_DN_UT_006_RespondBuilder_ShouldSerializeNestedJsonElementValue()
    {
        using var valueDocument = JsonDocument.Parse("""{"message":"hello","count":2}""");
        var payload = RequestPayloadFactory.BuildRespondParams(new RespondRequest
        {
            InstanceId = "inst-1",
            InstanceSessionToken = "session-1",
            InvocationId = "invk-1",
            Value = new Dictionary<string, object?>
            {
                ["Payload"] = valueDocument.RootElement.Clone()
            }
        });

        var document = ToJObject(payload);
        Assert.Equal("hello", (string?)document["value"]!["Payload"]!["message"]!);
        Assert.Equal(2, (int)document["value"]!["Payload"]!["count"]!);
        Assert.Null(document["value"]!["Payload"]!["valueKind"]);
    }

    [Fact]
    public void M5_DN_UT_006_RegisterInstanceBuilder_WhenMetaIsNotObject_ShouldThrowArgumentException()
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
    public void M5_DN_UT_006_RegisterInstanceBuilder_ShouldSerializeJsonElementMetaAsObject()
    {
        using var metaDocument = JsonDocument.Parse("""{"FooBar":true,"Nested":{"InnerKey":"value"}}""");
        var payload = RequestPayloadFactory.BuildRegisterInstanceParams(
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
                Meta = metaDocument.RootElement.Clone()
            },
            "secret-1");

        var document = ToJObject(payload);
        Assert.Equal("secret-1", (string?)document["password"]!);
        Assert.Null(document["instance"]!["password"]);
        Assert.True((bool)document["instance"]!["meta"]!["FooBar"]!);
        Assert.Equal("value", (string?)document["instance"]!["meta"]!["Nested"]!["InnerKey"]!);
        Assert.Null(document["instance"]!["meta"]!["valueKind"]);
    }

    [Fact]
    public void Impl_UnregisterBuilder_ShouldPlacePasswordAtTopLevel()
    {
        var payload = RequestPayloadFactory.BuildUnregisterParams("inst-1", "secret-1");

        var document = ToJObject(payload);
        Assert.Equal("inst-1", (string?)document["instanceId"]!);
        Assert.Equal("secret-1", (string?)document["instanceSessionToken"]!);
    }

    [Fact]
    public void M5_DN_UT_006_RespondBuilder_WhenErrorMessageMissing_ShouldThrowArgumentException()
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
    public void M5_DN_UT_006_RespondBuilder_WhenErrorDataIsNotObject_ShouldThrowArgumentException()
    {
        var exception = Assert.Throws<ArgumentException>(() => RequestPayloadFactory.BuildRespondParams(new RespondRequest
        {
            InstanceId = "inst-1",
            InstanceSessionToken = "session-1",
            InvocationId = "invk-1",
            Error = new DevHubCalleeError
            {
                Code = 1001,
                Message = "app_error",
                Data = JValue.CreateString("boom")
            }
        }));

        Assert.Contains("Error.Data", exception.Message, StringComparison.Ordinal);
    }

    private static JObject ToJObject(object payload)
    {
        return JObject.Parse(DevHubJson.Serialize(payload));
    }
}
