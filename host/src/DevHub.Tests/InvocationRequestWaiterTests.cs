namespace DevHub.Tests;

using DevHub.Core.Services.Invocation;
using Microsoft.Extensions.Logging;
using Moq;

/// <summary>
/// InvocationRequestWaiter 生命周期测试。
/// </summary>
[Trait("Category", "Impl")]
public class InvocationRequestWaiterTests
{
    private readonly Mock<ILogger<InvocationRequestWaiter>> _waiterLogger = new();

    [Fact]
    public async Task Impl_Register_ThenCompleteSuccess_ShouldReturnSuccessAndCleanup()
    {
        var waiter = new InvocationRequestWaiter(_waiterLogger.Object);

        var task = waiter.Register("invk-success");
        var completed = waiter.CompleteSuccess("invk-success", new { ok = true });

        Assert.True(completed);

        var result = await task;
        Assert.Equal(InvocationRequestCompletionKind.Success, result.Kind);
        Assert.NotNull(result.Value);

        var secondComplete = waiter.CompleteSuccess("invk-success", new { ok = true });
        Assert.False(secondComplete);
    }

    [Fact]
    public async Task Impl_Register_ThenCompleteTimeout_ShouldReturnTimeoutAndCleanup()
    {
        var waiter = new InvocationRequestWaiter(_waiterLogger.Object);

        var task = waiter.Register("invk-timeout");
        var completed = waiter.CompleteTimeout("invk-timeout", 1234);

        Assert.True(completed);

        var result = await task;
        Assert.Equal(InvocationRequestCompletionKind.Timeout, result.Kind);
        Assert.Equal(1234, result.ElapsedMs);

        var secondComplete = waiter.CompleteTimeout("invk-timeout", 1);
        Assert.False(secondComplete);
    }

    [Fact]
    public async Task Impl_Register_ThenCompleteFailure_ShouldReturnFailedAndCleanup()
    {
        var waiter = new InvocationRequestWaiter(_waiterLogger.Object);

        var task = waiter.Register("invk-failed");
        var completed = waiter.CompleteFailure("invk-failed", new
        {
            code = 1001,
            message = "app_error",
            data = new { reason = "bad_input" }
        });

        Assert.True(completed);

        var result = await task;
        Assert.Equal(InvocationRequestCompletionKind.Failed, result.Kind);
        Assert.NotNull(result.CalleeError);

        var secondComplete = waiter.CompleteFailure("invk-failed", new { code = 1, message = "x" });
        Assert.False(secondComplete);
    }

    [Fact]
    public async Task Impl_Cleanup_ShouldRemoveWaiterAndAllowReRegister()
    {
        var waiter = new InvocationRequestWaiter(_waiterLogger.Object);

        var pendingTask = waiter.Register("invk-cleanup");
        var cleanupResult = waiter.Cleanup("invk-cleanup");

        Assert.True(cleanupResult);
        Assert.False(waiter.CompleteSuccess("invk-cleanup", new { ok = true }));

        var secondTask = waiter.Register("invk-cleanup");
        Assert.True(waiter.CompleteExpired("invk-cleanup", 2000));

        var secondResult = await secondTask;
        Assert.Equal(InvocationRequestCompletionKind.Expired, secondResult.Kind);
        Assert.Equal(2000, secondResult.ElapsedMs);

        _ = pendingTask;
    }
}



