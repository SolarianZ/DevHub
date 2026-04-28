using DevHubDispatcher.Editor;

namespace DevHubDispatcher.Tests;

public sealed class DevHubDispatcherIdentityLogicTests
{
    [Fact]
    public void IdentityLogic_ResolveAppId_WhenOverrideUsesCanonicalMixedCaseAndUnderscore_ShouldPreferOverride()
    {
        var resolution = DevHubDispatcherIdentityLogic.ResolveAppId(
            new[] { "-projectPath", "/tmp/project", "-devhubAppId", "Sample_App.v2" },
            "stored.app",
            () => "generated.app");

        Assert.Equal("Sample_App.v2", resolution.AppId);
        Assert.Null(resolution.WarningMessage);
    }

    [Fact]
    public void IdentityLogic_ResolveAppId_WhenOverrideValueMissing_ShouldFallBackToStoredIdentity()
    {
        var generatorCalled = false;

        var resolution = DevHubDispatcherIdentityLogic.ResolveAppId(
            new[] { "-devhubAppId" },
            "stored.app",
            () =>
            {
                generatorCalled = true;
                return "generated.app";
            });

        Assert.Equal("stored.app", resolution.AppId);
        Assert.False(generatorCalled);
        Assert.Equal("忽略缺少值的 -devhubAppId 参数。", resolution.WarningMessage);
    }

    [Fact]
    public void IdentityLogic_ResolveAppId_WhenOverrideValueInvalid_ShouldRejectAndUseGeneratedIdentity()
    {
        var generatorCalled = false;

        var resolution = DevHubDispatcherIdentityLogic.ResolveAppId(
            new[] { "-devhubAppId", ".sample" },
            null,
            () =>
            {
                generatorCalled = true;
                return "Generated_App.v3";
            });

        Assert.Equal("Generated_App.v3", resolution.AppId);
        Assert.True(generatorCalled);
        Assert.Equal("忽略非法 -devhubAppId 参数: .sample", resolution.WarningMessage);
    }
}
