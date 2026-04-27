using System.Reflection;
using DevHub.Sdk.Internal;
using DevHub.Sdk.Models;

namespace DevHub.Sdk.UnitTests.Versioning;

/// <summary>
/// 版本兼容逻辑白盒测试。
/// </summary>
public sealed class VersionCompatibilityEvaluatorTests
{
    [Fact]
    public void SdkVersionSource_CurrentVersion_ShouldMatchAssemblyInformationalVersion()
    {
        var assemblyVersion = typeof(DevHubClient).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        Assert.Equal(assemblyVersion, SdkVersionSource.CurrentVersion);
        Assert.True(SemanticVersionParser.TryParse(SdkVersionSource.CurrentVersion, out _));
    }

    [Theory]
    [InlineData("1.2.3", 1, 2, 3)]
    [InlineData("1.2.3-beta.1", 1, 2, 3)]
    [InlineData("1.2.3+build.4", 1, 2, 3)]
    [InlineData("1.2.3-beta.1+build.4", 1, 2, 3)]
    public void SemanticVersionParser_TryParse_WhenSemVerValid_ShouldExtractNumericParts(
        string value,
        int expectedMajor,
        int expectedMinor,
        int expectedPatch)
    {
        var parsed = SemanticVersionParser.TryParse(value, out var version);

        Assert.True(parsed);
        Assert.Equal(expectedMajor, version.Major);
        Assert.Equal(expectedMinor, version.Minor);
        Assert.Equal(expectedPatch, version.Patch);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1.2")]
    [InlineData("v1.2.3")]
    [InlineData("1.2.x")]
    [InlineData("1.2.3-")]
    public void SemanticVersionParser_TryParse_WhenSemVerInvalid_ShouldReturnFalse(string? value)
    {
        Assert.False(SemanticVersionParser.TryParse(value, out _));
    }

    [Theory]
    [InlineData("1.4.2", "2.4.0", VersionCompatibilityStatus.Incompatible)]
    [InlineData("1.4.2", "1.5.0", VersionCompatibilityStatus.UpdateRecommended)]
    [InlineData("1.4.2", "1.4.99", VersionCompatibilityStatus.Compatible)]
    [InlineData("1.4.2-beta.1+build.7", "1.4.0+host.3", VersionCompatibilityStatus.Compatible)]
    [InlineData("1.4.2", "not-semver", VersionCompatibilityStatus.Unknown)]
    [InlineData("not-semver", "1.4.2", VersionCompatibilityStatus.Unknown)]
    public void VersionCompatibilityEvaluator_Evaluate_ShouldApplyDocumentedRules(
        string sdkVersion,
        string hostVersion,
        VersionCompatibilityStatus expectedStatus)
    {
        var result = VersionCompatibilityEvaluator.Evaluate(sdkVersion, hostVersion);

        Assert.Equal(sdkVersion, result.SdkVersion);
        Assert.Equal(hostVersion, result.HostVersion);
        Assert.Equal(expectedStatus, result.Status);
    }
}
