namespace DevHub.Tests;

using System.Text.Json;
using DevHub.Core.Models;
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

        Assert.False(File.Exists(context.RuntimePathOptions.DefinitionsCatalogPath));
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
                    scope = ScopeContract.Global,
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
        Assert.True(File.Exists(context.RuntimePathOptions.DefinitionsCatalogPath));

        var getResponse = await context.Handler.HandleAsync(new JsonRpcRequest
        {
            Id = "get-managed-definition",
            Method = HubRpcMethods.HubAppsGetDefinition,
            Params = JsonSerializer.SerializeToElement(new { appId = "managed.definition.app", scope = ScopeContract.Global })
        }, CancellationToken.None);
        Assert.Null(getResponse.Error);

        var deleteResponse = await context.Handler.HandleAsync(new JsonRpcRequest
        {
            Id = "delete-managed-definition",
            Method = HubRpcMethods.HubAppsDeleteDefinition,
            Params = JsonSerializer.SerializeToElement(new { appId = "managed.definition.app", scope = ScopeContract.Global })
        }, CancellationToken.None);

        Assert.Null(deleteResponse.Error);
        Assert.True(File.Exists(context.RuntimePathOptions.DefinitionsCatalogPath));
        Assert.Empty(DefinitionCatalogTestHelper.ReadDefinitions(context.RuntimePathOptions.DefinitionsCatalogPath));
        Assert.Null(context.DefinitionProvider.GetDefinition("managed.definition.app", ScopeContract.Global));

        var getMissingResponse = await context.Handler.HandleAsync(new JsonRpcRequest
        {
            Id = "get-deleted-definition",
            Method = HubRpcMethods.HubAppsGetDefinition,
            Params = JsonSerializer.SerializeToElement(new { appId = "managed.definition.app", scope = ScopeContract.Global })
        }, CancellationToken.None);
        Assert.NotNull(getMissingResponse.Error);
        Assert.Equal("app_definition_not_found", getMissingResponse.Error!.Message);

        var deliveries = context.EventBus.DrainDeliveries("conn-definition-events", maxCount: 10);
        Assert.Equal(2, deliveries.Count);
        Assert.Equal(HubEventTypes.AppDefinitionUpserted, deliveries[0].Type);
        Assert.Equal(HubEventTypes.AppDefinitionDeleted, deliveries[1].Type);

        var upsertPayload = JsonSerializer.SerializeToElement(deliveries[0].Payload);
        Assert.Equal("managed.definition.app", upsertPayload.GetProperty("appId").GetString());
        Assert.Equal(ScopeContract.Global, upsertPayload.GetProperty("scope").GetString());
        Assert.Equal("managed.definition.app", upsertPayload.GetProperty("definition").GetProperty("appId").GetString());
        Assert.Equal(ScopeContract.Global, upsertPayload.GetProperty("definition").GetProperty("scope").GetString());

        var deletePayload = JsonSerializer.SerializeToElement(deliveries[1].Payload);
        Assert.Equal("managed.definition.app", deletePayload.GetProperty("appId").GetString());
        Assert.Equal(ScopeContract.Global, deletePayload.GetProperty("scope").GetString());
    }

    [Fact]
    public async Task Impl_UpsertDefinition_WhenScopeLiteralGlobal_ShouldRemainDistinctFromGlobalAndEchoCanonicalIdentifiers()
    {
        using var context = CreateContext();
        const string appId = "Sample.App_01";
        const string scope = "global";

        var globalResponse = await context.Handler.HandleAsync(new JsonRpcRequest
        {
            Id = "upsert-global-definition",
            Method = HubRpcMethods.HubAppsUpsertDefinition,
            Params = JsonSerializer.SerializeToElement(new
            {
                definition = new
                {
                    appId,
                    scope = ScopeContract.Global,
                    displayName = "Sample Global Definition"
                }
            })
        }, CancellationToken.None);
        Assert.Null(globalResponse.Error);

        var explicitGlobalResponse = await context.Handler.HandleAsync(new JsonRpcRequest
        {
            Id = "upsert-explicit-global-definition",
            Method = HubRpcMethods.HubAppsUpsertDefinition,
            Params = JsonSerializer.SerializeToElement(new
            {
                definition = new
                {
                    appId,
                    scope,
                    displayName = "Sample Explicit Global Definition"
                }
            })
        }, CancellationToken.None);

        Assert.Null(explicitGlobalResponse.Error);
        var definition = JsonSerializer.SerializeToElement(explicitGlobalResponse.Result).GetProperty("definition");
        Assert.Equal(appId, definition.GetProperty("appId").GetString());
        Assert.Equal(scope, definition.GetProperty("scope").GetString());

        using var catalog = JsonDocument.Parse(File.ReadAllText(context.RuntimePathOptions.DefinitionsCatalogPath));
        var scopes = catalog.RootElement.GetProperty("definitions")
            .EnumerateArray()
            .Select(entry => entry.GetProperty("scope").GetString())
            .ToArray();
        Assert.Equal(new[] { ScopeContract.Global, scope }, scopes);
        Assert.All(catalog.RootElement.GetProperty("definitions").EnumerateArray(), entry => Assert.Equal(appId, entry.GetProperty("appId").GetString()));

        var getResponse = await context.Handler.HandleAsync(new JsonRpcRequest
        {
            Id = "get-explicit-global-definition",
            Method = HubRpcMethods.HubAppsGetDefinition,
            Params = JsonSerializer.SerializeToElement(new { appId, scope })
        }, CancellationToken.None);

        Assert.Null(getResponse.Error);
        var storedDefinition = JsonSerializer.SerializeToElement(getResponse.Result).GetProperty("definition");
        Assert.Equal(appId, storedDefinition.GetProperty("appId").GetString());
        Assert.Equal(scope, storedDefinition.GetProperty("scope").GetString());

        var getGlobalResponse = await context.Handler.HandleAsync(new JsonRpcRequest
        {
            Id = "get-global-definition",
            Method = HubRpcMethods.HubAppsGetDefinition,
            Params = JsonSerializer.SerializeToElement(new { appId, scope = ScopeContract.Global })
        }, CancellationToken.None);

        Assert.Null(getGlobalResponse.Error);
        var storedGlobalDefinition = JsonSerializer.SerializeToElement(getGlobalResponse.Result).GetProperty("definition");
        Assert.Equal(appId, storedGlobalDefinition.GetProperty("appId").GetString());
        Assert.Equal(ScopeContract.Global, storedGlobalDefinition.GetProperty("scope").GetString());
    }

    [Fact]
    public void Impl_DefinitionManager_ObjectOverloads_ShouldOmitNullOptionalFieldsWhenRevalidatingAndPersisting()
    {
        using var context = CreateContext();
        var definition = new DevHub.Core.Models.AppDefinition
        {
            AppId = "managed.nullable.app",
            Scope = ScopeContract.Global,
            DisplayName = "Managed Nullable App"
        };

        var validationResult = context.DefinitionManager.Validate(definition);
        Assert.True(validationResult.Valid);
        Assert.Empty(validationResult.Errors);

        var upserted = context.DefinitionManager.TryUpsert(definition, out var storedDefinition, out var upsertValidationResult);
        Assert.True(upserted);
        Assert.NotNull(storedDefinition);
        Assert.True(upsertValidationResult.Valid);
        Assert.Empty(upsertValidationResult.Errors);

        var path = context.RuntimePathOptions.DefinitionsCatalogPath;
        Assert.True(File.Exists(path));

        using var persisted = JsonDocument.Parse(File.ReadAllText(path));
        var definitionEntry = Assert.Single(persisted.RootElement.GetProperty("definitions").EnumerateArray());
        Assert.Equal("managed.nullable.app", definitionEntry.GetProperty("appId").GetString());
        Assert.Equal(ScopeContract.Global, definitionEntry.GetProperty("scope").GetString());
        Assert.False(definitionEntry.TryGetProperty("description", out _));
        Assert.False(definitionEntry.TryGetProperty("launch", out _));
        Assert.False(definitionEntry.TryGetProperty("capabilities", out _));
    }

    [Fact]
    public async Task Impl_UpsertDefinition_WhenOptionalFieldsExplicitlyNull_ShouldReturnDefinitionInvalidErrors()
    {
        using var context = CreateContext();

        var response = await context.Handler.HandleAsync(new JsonRpcRequest
        {
            Id = "upsert-null-optional-fields",
            Method = HubRpcMethods.HubAppsUpsertDefinition,
            Params = JsonSerializer.SerializeToElement(new
            {
                definition = new
                {
                    appId = "managed.nullable.app",
                    scope = ScopeContract.Global,
                    displayName = "Managed Nullable App",
                    description = (string?)null,
                    launch = (object?)null,
                    capabilities = (object?)null
                }
            })
        }, CancellationToken.None);

        Assert.NotNull(response.Error);
        Assert.Equal(-32602, response.Error!.Code);
        Assert.Equal("invalid_params", response.Error.Message);

        var errorData = JsonSerializer.SerializeToElement(response.Error.Data);
        Assert.Equal("definition_invalid", errorData.GetProperty("reason").GetString());

        var errors = errorData.GetProperty("errors").EnumerateArray().ToList();
        Assert.Contains(errors, issue => issue.GetProperty("path").GetString() == "definition.description");
        Assert.Contains(errors, issue => issue.GetProperty("path").GetString() == "definition.launch");
        Assert.Contains(errors, issue => issue.GetProperty("path").GetString() == "definition.capabilities");
    }

    [Fact]
    public async Task Impl_DeleteDefinition_WhenMissing_ShouldReturnAppDefinitionNotFound()
    {
        using var context = CreateContext();

        var response = await context.Handler.HandleAsync(new JsonRpcRequest
        {
            Id = "delete-missing-definition",
            Method = HubRpcMethods.HubAppsDeleteDefinition,
            Params = JsonSerializer.SerializeToElement(new { appId = "missing.definition.app", scope = ScopeContract.Global })
        }, CancellationToken.None);

        Assert.NotNull(response.Error);
        Assert.Equal(-32014, response.Error!.Code);
        Assert.Equal("app_definition_not_found", response.Error.Message);

        var errorData = JsonSerializer.SerializeToElement(response.Error.Data);
        Assert.Equal("missing.definition.app", errorData.GetProperty("appId").GetString());
        Assert.Equal(ScopeContract.Global, errorData.GetProperty("scope").GetString());
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
        Directory.CreateDirectory(runtimePathOptions.AppsPath);

        var validator = new AppDefinitionValidator();
        var definitionLoader = new DefinitionLoader(runtimePathOptions.DefinitionsCatalogPath, Mock.Of<ILogger<DefinitionLoader>>(), validator);
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
            definitionManager,
            new AppDefinitionsHandler(definitionProvider, definitionManager, Mock.Of<ILogger<AppDefinitionsHandler>>()));
    }

    private sealed record TestContext(
        RuntimePathOptions RuntimePathOptions,
        IDefinitionProvider DefinitionProvider,
        HubEventBus EventBus,
        DefinitionManager DefinitionManager,
        AppDefinitionsHandler Handler) : IDisposable
    {
        public void Dispose()
        {
        }
    }
}
