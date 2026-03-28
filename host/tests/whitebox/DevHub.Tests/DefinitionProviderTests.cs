namespace DevHub.Tests;

using DevHub.Core.Services;
using Microsoft.Extensions.Logging;
using Moq;

/// <summary>
/// <see cref="DefinitionProvider"/> 测试。
/// </summary>
[Trait("Category", "Impl")]
public class DefinitionProviderTests : IDisposable
{
    private readonly string _tempDirectory;

    /// <summary>
    /// 初始化测试上下文。
    /// </summary>
    public DefinitionProviderTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "DevHubDefinitionProviderTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
    }

    [Fact]
    public void Impl_Refresh_AfterFileAdded_ShouldExposeUpdatedSnapshot()
    {
        var loader = new DefinitionLoader(_tempDirectory, Mock.Of<ILogger<DefinitionLoader>>());
        var provider = new DefinitionProvider(loader);

        provider.Refresh();
        Assert.Empty(provider.GetAllDefinitions());

        WriteDefinition("provider.app");

        provider.Refresh();
        var definition = provider.GetDefinition("provider.app");
        Assert.NotNull(definition);
        Assert.Equal("provider.app", definition!.AppId);
        Assert.Single(provider.GetAllDefinitions());
    }

    [Fact]
    public void Impl_GetDefinition_WhenMissing_ShouldReturnNull()
    {
        var loader = new DefinitionLoader(_tempDirectory, Mock.Of<ILogger<DefinitionLoader>>());
        var provider = new DefinitionProvider(loader);
        provider.Refresh();

        var missing = provider.GetDefinition("missing.app");
        Assert.Null(missing);
    }

    [Fact]
    public void Impl_Refresh_WhenLaunchExePathMissing_ShouldIgnoreInvalidDefinition()
    {
        var loader = new DefinitionLoader(_tempDirectory, Mock.Of<ILogger<DefinitionLoader>>());
        var provider = new DefinitionProvider(loader);

        var payload = """
        {
          "appId": "broken.launch.app",
          "displayName": "broken.launch.app",
          "launch": {
            "argsTemplate": "--serve"
          }
        }
        """;

        File.WriteAllText(Path.Combine(_tempDirectory, "broken.launch.app.json"), payload);

        provider.Refresh();

        Assert.Null(provider.GetDefinition("broken.launch.app"));
        Assert.Empty(provider.GetAllDefinitions());
    }

    [Fact]
    public void Impl_Refresh_WhenDefinitionDirectoryMissing_ShouldClearStaleSnapshot()
    {
        var loader = new DefinitionLoader(_tempDirectory, Mock.Of<ILogger<DefinitionLoader>>());
        var provider = new DefinitionProvider(loader);

        WriteDefinition("provider.app");
        provider.Refresh();
        Assert.Single(provider.GetAllDefinitions());

        Directory.Delete(_tempDirectory, recursive: true);

        provider.Refresh();

        Assert.Empty(provider.GetAllDefinitions());
        Assert.Null(provider.GetDefinition("provider.app"));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDirectory))
            {
                Directory.Delete(_tempDirectory, recursive: true);
            }
        }
        catch
        {
        }
    }

    private void WriteDefinition(string appId)
    {
        var payload = $$"""
        {
          "appId": "{{appId}}",
          "displayName": "{{appId}}",
          "entry": {
            "type": "stdio"
          }
        }
        """;

        File.WriteAllText(Path.Combine(_tempDirectory, $"{appId}.json"), payload);
    }
}
