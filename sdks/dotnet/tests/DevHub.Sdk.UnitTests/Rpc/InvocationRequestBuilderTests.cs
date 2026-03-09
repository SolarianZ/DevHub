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
    public void M5_DN_UT_006_NotifyBuilder_ShouldApplyDefaultOptions()
    {
        var payload = RequestPayloadFactory.BuildNotifyParams(new InvokeRequest
        {
            AppId = "test.app",
            Method = "test.notify"
        });

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(payload));
        var options = document.RootElement.GetProperty("options");
        Assert.Equal(60000, options.GetProperty("ttlMs").GetInt32());
        Assert.True(options.GetProperty("queueIfOffline").GetBoolean());
        Assert.True(options.GetProperty("autoLaunch").GetBoolean());
        Assert.False(document.RootElement.TryGetProperty("target", out _));
    }

    [Fact]
    public void M5_DN_UT_006_RequestBuilder_ShouldApplyDefaultOptions()
    {
        var payload = RequestPayloadFactory.BuildRequestParams(new InvokeRequest
        {
            AppId = "test.app",
            Method = "test.request"
        });

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(payload));
        var options = document.RootElement.GetProperty("options");
        Assert.Equal(300000, options.GetProperty("ttlMs").GetInt32());
        Assert.Equal(120000, options.GetProperty("waitTimeoutMs").GetInt32());
        Assert.True(options.GetProperty("queueIfOffline").GetBoolean());
        Assert.True(options.GetProperty("autoLaunch").GetBoolean());
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

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(payload));
        Assert.Equal(string.Empty, document.RootElement.GetProperty("target").GetProperty("scope").GetString());
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
            InstanceId = "inst-1"
        });

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(payload));
        Assert.Equal(10, document.RootElement.GetProperty("maxCount").GetInt32());
        Assert.Equal(25000, document.RootElement.GetProperty("waitMs").GetInt32());
    }
}
