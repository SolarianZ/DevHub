using System.Reflection;
using System.Reflection.Emit;

namespace DevHub.Host.Tests;

[Trait("Category", "Impl")]
public sealed class HostVersionProviderTests
{
    [Fact]
    public void Impl_HostVersionProvider_ResolveHubVersion_WithoutAssembly_ShouldUseHostAssembly()
    {
        Assert.Equal(
            HostVersionProvider.ResolveHubVersion(typeof(Program).Assembly),
            HostVersionProvider.ResolveHubVersion());
    }

    [Fact]
    public void Impl_HostVersionProvider_ResolveHubVersion_WithInformationalVersion_ShouldReturnTrimmedValue()
    {
        var assembly = CreateDynamicAssembly(" 1.2.3-preview ", new Version(9, 8, 7, 6));

        Assert.Equal("1.2.3-preview", HostVersionProvider.ResolveHubVersion(assembly));
    }

    [Fact]
    public void Impl_HostVersionProvider_ResolveHubVersion_WithoutInformationalVersion_ShouldFallbackToAssemblyVersion()
    {
        var assembly = CreateDynamicAssembly(null, new Version(2, 3, 4, 5));

        Assert.Equal("2.3.4.5", HostVersionProvider.ResolveHubVersion(assembly));
    }

    [Fact]
    public void Impl_HostVersionProvider_ResolveRuntimeVersion_WithPreferredSemVer_ShouldReturnTrimmedValue()
    {
        Assert.Equal("1.2.3-preview+build.5", HostVersionProvider.ResolveRuntimeVersion(" 1.2.3-preview+build.5 "));
    }

    [Fact]
    public void Impl_HostVersionProvider_ResolveRuntimeVersion_WithInvalidInformationalVersion_ShouldFallbackToThreePartAssemblyVersion()
    {
        var assembly = CreateDynamicAssembly("v1.2.3", new Version(2, 3, 4, 5));

        Assert.Equal("2.3.4", HostVersionProvider.ResolveRuntimeVersion(assembly: assembly));
    }

    private static Assembly CreateDynamicAssembly(string? informationalVersion, Version assemblyVersion)
    {
        var assemblyName = new AssemblyName($"DevHub.Host.Tests.Dynamic.{Guid.NewGuid():N}")
        {
            Version = assemblyVersion
        };
        var assemblyBuilder = AssemblyBuilder.DefineDynamicAssembly(assemblyName, AssemblyBuilderAccess.Run);

        if (informationalVersion is not null)
        {
            var attributeConstructor = typeof(AssemblyInformationalVersionAttribute).GetConstructor([typeof(string)])
                ?? throw new InvalidOperationException("无法解析 AssemblyInformationalVersionAttribute 构造函数。");
            assemblyBuilder.SetCustomAttribute(new CustomAttributeBuilder(attributeConstructor, [informationalVersion]));
        }

        return assemblyBuilder;
    }
}
