namespace DevHub.Tests;

using System.Text.Json;
using DevHub.Core.Models.Rpc;
using DevHub.Core.Services;
using DevHub.Core.Services.Abstractions;
using DevHub.Core.Services.Events;
using DevHub.Core.Services.Rpc.Handlers;
using Microsoft.Extensions.Logging;
using Moq;

/// <summary>
/// AppDefinition 管理能力测试。
/// </summary>
[Trait("Category", "Impl")]
public sealed class AppDefinitionManagementTests : IDisposable
{
    private readonly string _tempDirectory;

    /// <summary>
    /// 初始化测试上下文。
    /// </summary>
    public AppDefinitionManagementTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "DevHubAppDefinitionManagementTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
    }

    [Fact]
    public async Task Impl_ValidateDefinition_WhenDefinitionInvalid_ShouldReturnStructuredIssuesWithoutPersisting()
    {
        using var context = CreateContext();

        var response = await context.Handler.HandleAsync(new JsonRpcRequest
        {
            Id = "validate-invalid-definition",
            Method = HubRpcMethods.HubAppsValidateDefinition,
            Params = JsonSerializer.SerializeToElement(new
            {
                definition = new
                {
                    appId = "Invalid App"
                }
            })
        }, CancellationToken.None);

        Assert.Null(response.Error);
        var result = JsonSerializer.SerializeToElement(response.Result);
        Assert.True(result.GetProperty("ok").GetBoolean());
        Assert.False(result.GetProperty("valid").GetBoolean());

        var errors = result.GetProperty("errors").EnumerateArray().ToList();
        Assert.Contains(errors, issue => issue.GetProperty("path").GetString() == "definition.appId"
            && issue.GetProperty("code").GetString() == "invalid_app_id");
        Assert.Contains(errors, issue => issue.GetProperty("path").GetString() == "definition.displayName");

        Assert.Empty(Directory.GetFiles(context.RuntimePathOptions.DefinitionsPath, "*.json", SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public async Task Impl_UpsertDefinition_WhenDefinitionInvalid_ShouldReturnDefinitionInvalidErrors()
    {
        using var context = CreateContext();

        var response = await context.Handler.HandleAsync(new JsonRpcRequest
        {
            Id = "upsert-invalid-definition",
            Method = HubRpcMethods.HubAppsUpsertDefinition,
            Params = JsonSerializer.SerializeToElement(new
            {
                definition = new
                {
                    appId = "Invalid App",
                    displayName = ""
                }
            })
        }, CancellationToken.None);

        Assert.NotNull(response.Error);
        Assert.Equal(-32602, response.Error!.Code);
        Assert.Equal("invalid_params", response.Error.Message);

        var errorData = JsonSerializer.SerializeToElement(response.Error.Data);
        Assert.Equal("definition_invalid", errorData.GetProperty("reason").GetString());
        var errors = errorData.GetProperty("errors").EnumerateArray().ToList();
        Assert.NotEmpty(errors);
        Assert.Contains(errors, issue => issue.GetProperty("path").GetString() == "definition.appId");
    }

    [Fact]
    public async Task Impl_UpsertAndDeleteDefinition_ShouldPersistRefreshAndPublishLifecycleEvents()
    {
        using var context = CreateContext();
        context.EventBus.RegisterConnection("conn-definition-events");
        Assert.True(context.EventBus.TryMarkAuthenticated("conn-definition-events", "definition-test-client", Guid.NewGuid().ToString("D")));
        Assert.True(context.EventBus.TrySubscribe("conn-definition-events", [HubEventTypes.AppDefinitionUpserted, HubEventTypes.AppDefinitionDeleted], out _));

        var upsertResponse = await context.Handler.HandleAsync(new JsonRpcRequest
        {
            Id = "upsert-valid-definition",
            Method = HubRpcMethods.HubAppsUpsertDefinition,
            Params = JsonSerializer.SerializeToElement(new
            {
                definition = new
                {
                    appId = "managed.definition.app",
                    displayName = "Managed Definition App",
                    launch = new
                    {
                        exePath = "echo",
                        argsTemplate = "hello"
                    }
                }
            })
        }, CancellationToken.None);

        Assert.Null(upsertResponse.Error);
        var upsertResult = JsonSerializer.SerializeToElement(upsertResponse.Result);
        Assert.True(upsertResult.GetProperty("ok").GetBoolean());
        Assert.Equal("managed.definition.app", upsertResult.GetProperty("definition").GetProperty("appId").GetString());
        Assert.True(File.Exists(Path.Combine(context.RuntimePathOptions.DefinitionsPath, "managed.definition.app.json")));

        var getResponse = await context.Handler.HandleAsync(new JsonRpcRequest
        {
            Id = "get-managed-definition",
            Method = HubRpcMethods.HubAppsGetDefinition,
            Params = JsonSerializer.SerializeToElement(new { appId = "managed.definition.app" })
        }, CancellationToken.None);
        Assert.Null(getResponse.Error);

        var deleteResponse = await context.Handler.HandleAsync(new JsonRpcRequest
        {
            Id = "delete-managed-definition",
            Method = HubRpcMethods.HubAppsDeleteDefinition,
            Params = JsonSerializer.SerializeToElement(new { appId = "managed.definition.app" })
        }, CancellationToken.None);

        Assert.Null(deleteResponse.Error);
        Assert.False(File.Exists(Path.Combine(context.RuntimePathOptions.DefinitionsPath, "managed.definition.app.json")));
        Assert.Null(context.DefinitionProvider.GetDefinition("managed.definition.app"));

        var getMissingResponse = await context.Handler.HandleAsync(new JsonRpcRequest
        {
            Id = "get-deleted-definition",
            Method = HubRpcMethods.HubAppsGetDefinition,
            Params = JsonSerializer.SerializeToElement(new { appId = "managed.definition.app" })
        }, CancellationToken.None);
        Assert.NotNull(getMissingResponse.Error);
        Assert.Equal("app_definition_not_found", getMissingResponse.Error!.Message);

        var deliveries = context.EventBus.DrainDeliveries("conn-definition-events", maxCount: 10);
        Assert.Equal(2, deliveries.Count);
        Assert.Equal(HubEventTypes.AppDefinitionUpserted, deliveries[0].Type);
        Assert.Equal(HubEventTypes.AppDefinitionDeleted, deliveries[1].Type);

        var upsertPayload = JsonSerializer.SerializeToElement(deliveries[0].Payload);
        Assert.Equal("managed.definition.app", upsertPayload.GetProperty("appId").GetString());
        Assert.Equal("managed.definition.app", upsertPayload.GetProperty("definition").GetProperty("appId").GetString());

        var deletePayload = JsonSerializer.SerializeToElement(deliveries[1].Payload);
        Assert.Equal("managed.definition.app", deletePayload.GetProperty("appId").GetString());
    }

    [Fact]
    public async Task Impl_DeleteDefinition_WhenMissing_ShouldReturnAppDefinitionNotFound()
    {
        using var context = CreateContext();

        var response = await context.Handler.HandleAsync(new JsonRpcRequest
        {
            Id = "delete-missing-definition",
            Method = HubRpcMethods.HubAppsDeleteDefinition,
            Params = JsonSerializer.SerializeToElement(new { appId = "missing.definition.app" })
        }, CancellationToken.None);

        Assert.NotNull(response.Error);
        Assert.Equal(-32014, response.Error!.Code);
        Assert.Equal("app_definition_not_found", response.Error.Message);

        var errorData = JsonSerializer.SerializeToElement(response.Error.Data);
        Assert.Equal("missing.definition.app", errorData.GetProperty("appId").GetString());
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

    private TestContext CreateContext()
    {
        var runtimePathOptions = RuntimePathOptions.Create(Path.Combine(_tempDirectory, Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(runtimePathOptions.DefinitionsPath);

        var validator = new AppDefinitionValidator();
        var definitionLoader = new DefinitionLoader(runtimePathOptions.DefinitionsPath, Mock.Of<ILogger<DefinitionLoader>>(), validator);
        var definitionProvider = new DefinitionProvider(definitionLoader);
        var eventBus = new HubEventBus(Mock.Of<ILogger<HubEventBus>>());
        var clock = new SystemClock();
        var definitionManager = new DefinitionManager(
            runtimePathOptions,
            definitionProvider,
            validator,
            clock,
            Mock.Of<ILogger<DefinitionManager>>(),
            eventBus);

        return new TestContext(
            runtimePathOptions,
            definitionProvider,
            eventBus,
            new AppDefinitionsHandler(definitionProvider, definitionManager, Mock.Of<ILogger<AppDefinitionsHandler>>()));
    }

    private sealed record TestContext(
        RuntimePathOptions RuntimePathOptions,
        IDefinitionProvider DefinitionProvider,
        HubEventBus EventBus,
        AppDefinitionsHandler Handler) : IDisposable
    {
        public void Dispose()
        {
        }
    }
}
