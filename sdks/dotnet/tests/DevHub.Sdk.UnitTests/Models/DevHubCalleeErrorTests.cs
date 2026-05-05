using System.Text.Json;
using DevHub.Sdk.Models;

namespace DevHub.Sdk.UnitTests.Models;

/// <summary>
/// 被调用方错误对象测试。
/// </summary>
public sealed class DevHubCalleeErrorTests
{
    [Fact]
    public void CalleeErrorCreate_ShouldUseSdkJsonNamingPolicy()
    {
        var error = DevHubCalleeError.Create(1001, "app_error", new SampleErrorData
        {
            ErrorCode = "boom",
            RetryCount = 2
        });

        Assert.Equal(1001, error.Code);
        Assert.Equal("app_error", error.Message);
        Assert.True(error.Data.HasValue);
        Assert.Equal("boom", error.Data!.Value.GetProperty("errorCode").GetString());
        Assert.Equal(2, error.Data!.Value.GetProperty("retryCount").GetInt32());
        Assert.False(error.Data!.Value.TryGetProperty("ErrorCode", out _));
        Assert.False(error.Data!.Value.TryGetProperty("RetryCount", out _));
    }

    [Fact]
    public void CalleeErrorCreate_WhenDataIsScalar_ShouldPreserveJsonValue()
    {
        var error = DevHubCalleeError.Create(1001, "app_error", "boom");

        Assert.True(error.Data.HasValue);
        Assert.Equal(JsonValueKind.String, error.Data!.Value.ValueKind);
        Assert.Equal("boom", error.Data!.Value.GetString());
    }

    private sealed class SampleErrorData
    {
        public string ErrorCode { get; init; } = string.Empty;

        public int RetryCount { get; init; }
    }
}
