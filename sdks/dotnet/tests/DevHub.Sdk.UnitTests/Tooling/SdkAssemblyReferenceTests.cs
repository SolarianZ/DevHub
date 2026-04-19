using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace DevHub.Sdk.UnitTests.Tooling;

/// <summary>
/// SDK 程序集引用元数据测试。
/// </summary>
public sealed class SdkAssemblyReferenceTests
{
    [Fact]
    public void Impl_SdkAssembly_ShouldReferenceUnsignedNewtonsoftAssembly()
    {
        var sdkAssemblyPath = typeof(DevHubClient).Assembly.Location;

        var references = ReadAssemblyReferences(sdkAssemblyPath);
        var jsonReference = references.Single(reference => reference.Name == "Newtonsoft.Json");

        Assert.Equal(new Version(9, 0, 0, 0), jsonReference.Version);
        Assert.Equal(string.Empty, jsonReference.PublicKeyOrToken);
        Assert.False(jsonReference.HasPublicKey);
    }

    private static IReadOnlyList<AssemblyReferenceSnapshot> ReadAssemblyReferences(string assemblyPath)
    {
        using var stream = File.OpenRead(assemblyPath);
        using var peReader = new PEReader(stream);
        var metadataReader = peReader.GetMetadataReader();

        return metadataReader.AssemblyReferences
            .Select(handle =>
            {
                var reference = metadataReader.GetAssemblyReference(handle);
                return new AssemblyReferenceSnapshot(
                    metadataReader.GetString(reference.Name),
                    reference.Version,
                    Convert.ToHexString(metadataReader.GetBlobBytes(reference.PublicKeyOrToken)).ToLowerInvariant(),
                    reference.Flags.HasFlag(AssemblyFlags.PublicKey));
            })
            .OrderBy(reference => reference.Name, StringComparer.Ordinal)
            .ToArray();
    }

    private sealed record AssemblyReferenceSnapshot(string Name, Version Version, string PublicKeyOrToken, bool HasPublicKey);
}
