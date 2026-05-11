using DevHub.Sdk.Models;
using Newtonsoft.Json.Linq;

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
        Assert.NotNull(error.Data);
        Assert.Equal("boom", (string?)error.Data!["errorCode"]!);
        Assert.Equal(2, (int)error.Data!["retryCount"]!);
        Assert.Null(error.Data!["ErrorCode"]);
        Assert.Null(error.Data!["RetryCount"]);
    }

    [Fact]
    public void M5_DN_UT_008_CalleeErrorCreate_WhenDataIsDictionary_ShouldPreserveOriginalKeys()
    {
        var error = DevHubCalleeError.Create(1001, "app_error", new Dictionary<string, object?>
        {
            ["FooBar"] = "boom",
            ["Nested"] = new Dictionary<string, object?>
            {
                ["InnerKey"] = 2
            }
        });

        Assert.NotNull(error.Data);
        Assert.Equal("boom", (string?)error.Data!["FooBar"]!);
        Assert.Null(error.Data!["fooBar"]);
        Assert.Equal(2, (int)error.Data!["Nested"]!["InnerKey"]!);
        Assert.Null(error.Data!["Nested"]!["innerKey"]);
    }

    [Fact]
    public void CalleeErrorCreate_WhenDataIsScalar_ShouldPreserveJsonValue()
    {
        var exception = Assert.Throws<ArgumentException>(() => DevHubCalleeError.Create(1001, "app_error", "boom"));
        Assert.Contains("JSON 对象", exception.Message, StringComparison.Ordinal);
    }

    private sealed class SampleErrorData
    {
        public string ErrorCode { get; init; } = string.Empty;

        public int RetryCount { get; init; }
    }
}
