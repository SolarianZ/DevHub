namespace DevHub.Tests;

using DevHub.Core.Models;
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
        var loader = new DefinitionLoader(DefinitionCatalogTestHelper.GetCatalogPath(_tempDirectory), Mock.Of<ILogger<DefinitionLoader>>());
        var provider = new DefinitionProvider(loader);

        provider.Refresh();
        Assert.Empty(provider.GetAllDefinitions());

        WriteDefinition("provider.app");

        provider.Refresh();
        var definition = provider.GetDefinition("provider.app", ScopeContract.Global);
        Assert.NotNull(definition);
        Assert.Equal("provider.app", definition!.AppId);
        Assert.Single(provider.GetAllDefinitions());
    }

    [Fact]
    public void Impl_GetDefinition_WhenMissing_ShouldReturnNull()
    {
        var loader = new DefinitionLoader(DefinitionCatalogTestHelper.GetCatalogPath(_tempDirectory), Mock.Of<ILogger<DefinitionLoader>>());
        var provider = new DefinitionProvider(loader);
        provider.Refresh();

        var missing = provider.GetDefinition("missing.app", ScopeContract.Global);
        Assert.Null(missing);
    }

    [Fact]
    public void Impl_Refresh_WhenLaunchExePathMissing_ShouldIgnoreInvalidDefinition()
    {
        var loader = new DefinitionLoader(DefinitionCatalogTestHelper.GetCatalogPath(_tempDirectory), Mock.Of<ILogger<DefinitionLoader>>());
        var provider = new DefinitionProvider(loader);

        var payload = """
        {
          "appId": "broken.launch.app",
          "scope": "",
          "displayName": "broken.launch.app",
          "launch": {
            "argsTemplate": "--serve"
          }
        }
        """;

        DefinitionCatalogTestHelper.WriteCatalogText(
            DefinitionCatalogTestHelper.GetCatalogPath(_tempDirectory),
            $$"""
            {
              "version": 1,
              "definitions": [
                {
                  "appId": "broken.launch.app",
                  "scopes": [
                    {{payload}}
                  ]
                }
              ]
            }
            """);

        provider.Refresh();

        Assert.Null(provider.GetDefinition("broken.launch.app", ScopeContract.Global));
        Assert.Empty(provider.GetAllDefinitions());
    }

    [Fact]
    public void Impl_Refresh_WhenDefinitionDirectoryMissing_ShouldClearStaleSnapshot()
    {
        var loader = new DefinitionLoader(DefinitionCatalogTestHelper.GetCatalogPath(_tempDirectory), Mock.Of<ILogger<DefinitionLoader>>());
        var provider = new DefinitionProvider(loader);

        WriteDefinition("provider.app");
        provider.Refresh();
        Assert.Single(provider.GetAllDefinitions());

        Directory.Delete(_tempDirectory, recursive: true);

        provider.Refresh();

        Assert.Empty(provider.GetAllDefinitions());
        Assert.Null(provider.GetDefinition("provider.app", ScopeContract.Global));
    }

    [Fact]
    public void Impl_Refresh_WithMixedAppIdsAndScopes_ShouldExposeStableOrderedSnapshot()
    {
        var loader = new DefinitionLoader(DefinitionCatalogTestHelper.GetCatalogPath(_tempDirectory), Mock.Of<ILogger<DefinitionLoader>>());
        var provider = new DefinitionProvider(loader);

        WriteDefinition("provider.zeta", "workspace-z");
        WriteDefinition("provider.alpha", "workspace-z");
        WriteDefinition("provider.zeta");
        WriteDefinition("provider.zeta", "workspace-a");
        WriteDefinition("provider.alpha");

        provider.Refresh();

        var orderedDefinitions = provider.GetAllDefinitions()
            .Select(definition => $"{definition.AppId}|{definition.Scope}")
            .ToArray();

        Assert.Equal(
            new[]
            {
                "provider.alpha|",
                "provider.alpha|workspace-z",
                "provider.zeta|",
                "provider.zeta|workspace-a",
                "provider.zeta|workspace-z"
            },
            orderedDefinitions);
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

    private void WriteDefinition(string appId, string scope = ScopeContract.Global)
    {
        var payload = $$"""
        {
          "appId": "{{appId}}",
          "scope": "{{scope}}",
          "displayName": "{{appId}} {{(scope.Length == 0 ? "global" : scope)}}",
          "entry": {
            "type": "stdio"
          }
        }
        """;

        DefinitionCatalogTestHelper.UpsertDefinition(
            DefinitionCatalogTestHelper.GetCatalogPath(_tempDirectory),
            payload);
    }
}
