using DevHubDispatcher.Editor;

namespace DevHubDispatcher.Tests;

public sealed class DevHubDispatcherLaunchBindingTests
{
    [Fact]
    public void LaunchBinding_ResolveLaunchId_WhenValueMissing_ShouldReturnNull()
    {
        Assert.Null(DevHubDispatcherLaunchBinding.ResolveLaunchId((string)null));
    }

    [Fact]
    public void LaunchBinding_ResolveLaunchId_WhenValueEmpty_ShouldReturnNull()
    {
        Assert.Null(DevHubDispatcherLaunchBinding.ResolveLaunchId(string.Empty));
    }

    [Fact]
    public void LaunchBinding_ResolveLaunchId_WhenValueWhitespace_ShouldReturnNull()
    {
        Assert.Null(DevHubDispatcherLaunchBinding.ResolveLaunchId("   "));
    }

    [Fact]
    public void LaunchBinding_ResolveLaunchId_WhenValueProvided_ShouldReturnOriginalValue()
    {
        const string launchId = "launch-123";

        Assert.Equal(launchId, DevHubDispatcherLaunchBinding.ResolveLaunchId(launchId));
    }
}
