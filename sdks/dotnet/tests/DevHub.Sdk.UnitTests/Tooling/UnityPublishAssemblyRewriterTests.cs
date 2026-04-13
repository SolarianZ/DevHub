using DevHub.Sdk.UnityPublish;
using Mono.Cecil;

namespace DevHub.Sdk.UnitTests.Tooling;

/// <summary>
/// Unity 本地发布后处理工具测试。
/// </summary>
public sealed class UnityPublishAssemblyRewriterTests : IDisposable
{
    private readonly string _tempRoot;

    public UnityPublishAssemblyRewriterTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "DevHubSdkUnityPublishTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    [Fact]
    public void Impl_RewriteNewtonsoftReference_ShouldClearPublicKeyToken()
    {
        var assemblyPath = CopySdkAssemblyToTemp();
        var beforeReference = ReadAssemblyReferences(assemblyPath).Single(reference => reference.Name == "Newtonsoft.Json");

        var result = UnityPublishAssemblyRewriter.RewriteNewtonsoftReference(assemblyPath);

        var afterReference = ReadAssemblyReferences(assemblyPath).Single(reference => reference.Name == "Newtonsoft.Json");
        Assert.True(result.WasModified);
        Assert.Equal("30ad4fe6b2a6aeed", beforeReference.PublicKeyToken);
        Assert.Equal(string.Empty, afterReference.PublicKeyToken);
        Assert.False(afterReference.HasPublicKey);
        Assert.Equal(beforeReference.Version, afterReference.Version);
    }

    [Fact]
    public void Impl_RewriteNewtonsoftReference_ShouldKeepOtherAssemblyReferencesUnchanged()
    {
        var assemblyPath = CopySdkAssemblyToTemp();
        var beforeReferences = ReadAssemblyReferences(assemblyPath)
            .Where(reference => reference.Name != "Newtonsoft.Json")
            .ToDictionary(reference => reference.Name, reference => reference);

        UnityPublishAssemblyRewriter.RewriteNewtonsoftReference(assemblyPath);

        var afterReferences = ReadAssemblyReferences(assemblyPath)
            .Where(reference => reference.Name != "Newtonsoft.Json")
            .ToDictionary(reference => reference.Name, reference => reference);
        Assert.Equal(beforeReferences, afterReferences);
    }

    public void Dispose()
    {
        Directory.Delete(_tempRoot, recursive: true);
    }

    private string CopySdkAssemblyToTemp()
    {
        var sourceAssemblyPath = typeof(DevHubClient).Assembly.Location;
        var destinationAssemblyPath = Path.Combine(_tempRoot, "DevHub.Sdk.dll");
        File.Copy(sourceAssemblyPath, destinationAssemblyPath, overwrite: true);

        var sourcePdbPath = Path.ChangeExtension(sourceAssemblyPath, ".pdb");
        var destinationPdbPath = Path.ChangeExtension(destinationAssemblyPath, ".pdb");
        if (File.Exists(sourcePdbPath))
        {
            File.Copy(sourcePdbPath, destinationPdbPath, overwrite: true);
        }

        return destinationAssemblyPath;
    }

    private static IReadOnlyList<AssemblyReferenceSnapshot> ReadAssemblyReferences(string assemblyPath)
    {
        using var assembly = AssemblyDefinition.ReadAssembly(assemblyPath);
        return assembly.MainModule.AssemblyReferences
            .Select(reference => new AssemblyReferenceSnapshot(
                reference.Name,
                reference.Version.ToString(),
                ToHexString(reference.PublicKeyToken),
                reference.HasPublicKey))
            .OrderBy(reference => reference.Name, StringComparer.Ordinal)
            .ToArray();
    }

    private static string ToHexString(byte[] bytes)
    {
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private sealed record AssemblyReferenceSnapshot(string Name, string Version, string PublicKeyToken, bool HasPublicKey);
}
