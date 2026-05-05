namespace DevHub.Host.Tests;

using System.Diagnostics;
using System.Text.Json;
using DevHub.Core.Models;
using DevHub.Core.Models.Rpc;
using DevHub.Core.Services;
using DevHub.Core.Services.Abstractions;
using DevHub.Core.Services.Events;
using DevHub.Core.Services.Invocation;
using DevHub.Core.Services.Rpc.Handlers;
using Microsoft.Extensions.Logging;
using Moq;

/// <summary>
/// Host 使用的共享定义管理与启动 RPC 处理器测试。
/// </summary>
[Trait("Category", "Spec")]
public sealed class AppDefinitionsAndLaunchRpcHandlerTests : IDisposable
{
    private readonly string _tempRoot;

    /// <summary>
    /// 初始化测试上下文。
    /// </summary>
    public AppDefinitionsAndLaunchRpcHandlerTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "DevHubHostAppDefinitionsAndLaunchRpcHandlerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    [Fact]
    [Trait("Category", "Spec")]
    [Trait("SpecRef", "6.3.3")]
    [Trait("SpecRef", "6.3.4")]
    public async Task Spec_6_3_3_And_6_3_4_AppDefinitionsRpcHandler_ShouldListAndGetDefinitions()
    {
        using var context = CreateDefinitionContext();
        WriteDefinition(
            context.RuntimePathOptions.DefinitionsCatalogPath,
            """
            {
              "appId": "adapter.alpha",
              "scope": "",
              "displayName": "Adapter Alpha"
            }
            """);
        WriteDefinition(
            context.RuntimePathOptions.DefinitionsCatalogPath,
            """
            {
              "appId": "adapter.beta",
              "scope": "",
              "displayName": "Adapter Beta"
            }
            """);

        var handler = new AppDefinitionsHandler(context.DefinitionProvider, context.DefinitionManager, Mock.Of<ILogger<AppDefinitionsHandler>>());

        var listResponse = await handler.HandleAsync(
            CreateRequest(HubRpcMethods.HubAppsListDefinitions, "list-definitions", new { scope = (string?)null }),
            CancellationToken.None);
        var listResult = JsonSerializer.SerializeToElement(listResponse.Result);
        var definitions = listResult.GetProperty("definitions").EnumerateArray().ToArray();

        Assert.True(listResult.GetProperty("ok").GetBoolean());
        Assert.Equal(2, definitions.Length);
        Assert.Equal("adapter.alpha", definitions[0].GetProperty("appId").GetString());
        Assert.Equal("adapter.beta", definitions[1].GetProperty("appId").GetString());

        var getResponse = await handler.HandleAsync(
            CreateRequest(HubRpcMethods.HubAppsGetDefinition, "get-definition", new { appId = "adapter.alpha", scope = ScopeContract.Global }),
            CancellationToken.None);

        var getResult = JsonSerializer.SerializeToElement(getResponse.Result);
        Assert.True(getResult.GetProperty("ok").GetBoolean());
        Assert.Equal("adapter.alpha", getResult.GetProperty("definition").GetProperty("appId").GetString());
        Assert.Equal("Adapter Alpha", getResult.GetProperty("definition").GetProperty("displayName").GetString());
    }

    [Fact]
    [Trait("Category", "Spec")]
    [Trait("SpecRef", "6.3.4")]
    public async Task Spec_6_3_4_AppDefinitionsRpcHandler_WhenDefinitionMissing_ShouldReturnAppDefinitionNotFound()
    {
        using var context = CreateDefinitionContext();
        var handler = new AppDefinitionsHandler(context.DefinitionProvider, context.DefinitionManager, Mock.Of<ILogger<AppDefinitionsHandler>>());

        var response = await handler.HandleAsync(
            CreateRequest(HubRpcMethods.HubAppsGetDefinition, "get-missing-definition", new { appId = "missing.definition", scope = ScopeContract.Global }),
            CancellationToken.None);

        AssertError(response, -32014, "app_definition_not_found", "get-missing-definition");
        Assert.Equal("missing.definition", JsonSerializer.SerializeToElement(response.Error!.Data).GetProperty("appId").GetString());
        Assert.Equal(ScopeContract.Global, JsonSerializer.SerializeToElement(response.Error!.Data).GetProperty("scope").GetString());
    }

    [Fact]
    [Trait("Category", "Spec")]
    [Trait("SpecRef", "6.3.5")]
    [Trait("SpecRef", "6.3.6")]
    public async Task Spec_6_3_5_And_6_3_6_AppDefinitionsRpcHandler_ShouldValidateAndRejectInvalidDefinitions()
    {
        using var context = CreateDefinitionContext();
        var handler = new AppDefinitionsHandler(context.DefinitionProvider, context.DefinitionManager, Mock.Of<ILogger<AppDefinitionsHandler>>());

        var invalidDefinition = new
        {
            definition = new
            {
                appId = "Bad App",
                scope = ScopeContract.Global,
                displayName = "Broken Definition"
            }
        };

        var validateResponse = await handler.HandleAsync(
            CreateRequest(HubRpcMethods.HubAppsValidateDefinition, "validate-definition", invalidDefinition),
            CancellationToken.None);
        var validateResult = JsonSerializer.SerializeToElement(validateResponse.Result);

        Assert.True(validateResult.GetProperty("ok").GetBoolean());
        Assert.False(validateResult.GetProperty("valid").GetBoolean());
        Assert.Contains(
            validateResult.GetProperty("errors").EnumerateArray(),
            issue => issue.GetProperty("code").GetString() == "invalid_app_id");

        var upsertResponse = await handler.HandleAsync(
            CreateRequest(HubRpcMethods.HubAppsUpsertDefinition, "upsert-invalid-definition", invalidDefinition),
            CancellationToken.None);

        AssertError(upsertResponse, -32602, "invalid_params", "upsert-invalid-definition");
        var errorData = JsonSerializer.SerializeToElement(upsertResponse.Error!.Data);
        Assert.Equal("definition_invalid", errorData.GetProperty("reason").GetString());
        Assert.Contains(errorData.GetProperty("errors").EnumerateArray(), issue => issue.GetProperty("code").GetString() == "invalid_app_id");
    }

    [Fact]
    [Trait("Category", "Spec")]
    [Trait("SpecRef", "6.3.5")]
    public async Task Spec_6_3_5_AppDefinitionsRpcHandler_WhenDefinitionParamMissing_ShouldReturnInvalidParams()
    {
        using var context = CreateDefinitionContext();
        var handler = new AppDefinitionsHandler(context.DefinitionProvider, context.DefinitionManager, Mock.Of<ILogger<AppDefinitionsHandler>>());

        var response = await handler.HandleAsync(
            CreateRequest(HubRpcMethods.HubAppsValidateDefinition, "validate-missing-definition", new { appId = "bad" }),
            CancellationToken.None);

        AssertError(response, -32602, "invalid_params", "validate-missing-definition");
    }

    [Fact]
    [Trait("Category", "Spec")]
    [Trait("SpecRef", "6.3.6")]
    [Trait("SpecRef", "6.3.7")]
    public async Task Spec_6_3_6_And_6_3_7_AppDefinitionsRpcHandler_ShouldUpsertAndDeleteDefinition()
    {
        var eventPublisher = new Mock<IHubEventPublisher>();
        using var context = CreateDefinitionContext(eventPublisher.Object);
        var handler = new AppDefinitionsHandler(context.DefinitionProvider, context.DefinitionManager, Mock.Of<ILogger<AppDefinitionsHandler>>());

        var upsertResponse = await handler.HandleAsync(
            CreateRequest(
                HubRpcMethods.HubAppsUpsertDefinition,
                "upsert-definition",
                new
                {
                    definition = new
                    {
                        appId = "managed.adapter",
                        scope = ScopeContract.Global,
                        displayName = "Managed Adapter",
                        launch = new
                        {
                            exePath = "dotnet",
                            argsTemplate = "--info"
                        }
                    }
                }),
            CancellationToken.None);

        var upsertResult = JsonSerializer.SerializeToElement(upsertResponse.Result);
        Assert.True(upsertResult.GetProperty("ok").GetBoolean());
        Assert.Equal("managed.adapter", upsertResult.GetProperty("definition").GetProperty("appId").GetString());
        eventPublisher.Verify(
            publisher => publisher.Publish(It.Is<HubEventMessage>(message => message.Type == HubEventTypes.AppDefinitionUpserted)),
            Times.Once);

        var deleteResponse = await handler.HandleAsync(
            CreateRequest(HubRpcMethods.HubAppsDeleteDefinition, "delete-definition", new { appId = "managed.adapter", scope = ScopeContract.Global }),
            CancellationToken.None);

        var deleteResult = JsonSerializer.SerializeToElement(deleteResponse.Result);
        Assert.True(deleteResult.GetProperty("ok").GetBoolean());
        eventPublisher.Verify(
            publisher => publisher.Publish(It.Is<HubEventMessage>(message => message.Type == HubEventTypes.AppDefinitionDeleted)),
            Times.Once);

        var getResponse = await handler.HandleAsync(
            CreateRequest(HubRpcMethods.HubAppsGetDefinition, "get-deleted-definition", new { appId = "managed.adapter", scope = ScopeContract.Global }),
            CancellationToken.None);

        AssertError(getResponse, -32014, "app_definition_not_found", "get-deleted-definition");
    }

    [Fact]
    [Trait("Category", "Spec")]
    [Trait("SpecRef", "6.3.5")]
    [Trait("SpecRef", "6.3.6")]
    [Trait("SpecRef", "6.3.12")]
    public async Task Spec_6_3_5_And_6_3_6_And_6_3_12_AppDefinitionsAndLaunch_WhenLaunchExePathBlank_ShouldStoreDefinitionAndReturnLaunchConfigMissing()
    {
        using var context = CreateDefinitionContext();
        var definitionsHandler = new AppDefinitionsHandler(context.DefinitionProvider, context.DefinitionManager, Mock.Of<ILogger<AppDefinitionsHandler>>());
        var launchHandler = CreateLaunchHandler(context);

        var definitionParams = new
        {
            definition = new
            {
                appId = "blank-launch-config",
                scope = ScopeContract.Global,
                displayName = "Blank Launch Config",
                launch = new
                {
                    exePath = "   ",
                    argsTemplate = "--info"
                }
            }
        };

        var validateResponse = await definitionsHandler.HandleAsync(
            CreateRequest(HubRpcMethods.HubAppsValidateDefinition, "validate-blank-launch", definitionParams),
            CancellationToken.None);
        Assert.Null(validateResponse.Error);
        Assert.True(JsonSerializer.SerializeToElement(validateResponse.Result).GetProperty("valid").GetBoolean());

        var upsertResponse = await definitionsHandler.HandleAsync(
            CreateRequest(HubRpcMethods.HubAppsUpsertDefinition, "upsert-blank-launch", definitionParams),
            CancellationToken.None);
        Assert.Null(upsertResponse.Error);
        var upsertDefinition = JsonSerializer.SerializeToElement(upsertResponse.Result).GetProperty("definition");
        Assert.Equal("   ", upsertDefinition.GetProperty("launch").GetProperty("exePath").GetString());

        var launchResponse = await launchHandler.HandleAsync(
            CreateRequest(HubRpcMethods.HubAppsLaunch, "launch-blank-launch", new { appId = "blank-launch-config", scope = ScopeContract.Global }),
            CancellationToken.None);

        AssertError(launchResponse, -32020, "launch_failed", "launch-blank-launch");
        Assert.Equal("launch_config_missing", JsonSerializer.SerializeToElement(launchResponse.Error!.Data).GetProperty("reason").GetString());
    }

    [Fact]
    [Trait("Category", "Spec")]
    [Trait("SpecRef", "6.3.3")]
    public async Task Spec_6_3_3_AppDefinitionsRpcHandler_ShouldKeepDefinitionsSeparateByScopeAndStableOrder()
    {
        var eventPublisher = new Mock<IHubEventPublisher>();
        using var context = CreateDefinitionContext(eventPublisher.Object);
        var handler = new AppDefinitionsHandler(context.DefinitionProvider, context.DefinitionManager, Mock.Of<ILogger<AppDefinitionsHandler>>());

        var upsertScopedZ = await handler.HandleAsync(
            CreateRequest(
                HubRpcMethods.HubAppsUpsertDefinition,
                "upsert-scoped-z",
                new
                {
                    definition = new
                    {
                        appId = "managed.scoped.adapter",
                        scope = "workspace-Z",
                        displayName = "Managed Scoped Adapter Workspace Z"
                    }
                }),
            CancellationToken.None);
        Assert.True(JsonSerializer.SerializeToElement(upsertScopedZ.Result).GetProperty("ok").GetBoolean());

        var upsertGlobal = await handler.HandleAsync(
            CreateRequest(
                HubRpcMethods.HubAppsUpsertDefinition,
                "upsert-global",
                new
                {
                    definition = new
                    {
                        appId = "managed.scoped.adapter",
                        scope = ScopeContract.Global,
                        displayName = "Managed Scoped Adapter Global"
                    }
                }),
            CancellationToken.None);
        Assert.True(JsonSerializer.SerializeToElement(upsertGlobal.Result).GetProperty("ok").GetBoolean());

        var upsertScopedA = await handler.HandleAsync(
            CreateRequest(
                HubRpcMethods.HubAppsUpsertDefinition,
                "upsert-scoped-a",
                new
                {
                    definition = new
                    {
                        appId = "managed.scoped.adapter",
                        scope = "workspace-A",
                        displayName = "Managed Scoped Adapter Workspace A"
                    }
                }),
            CancellationToken.None);
        Assert.True(JsonSerializer.SerializeToElement(upsertScopedA.Result).GetProperty("ok").GetBoolean());

        var listResponse = await handler.HandleAsync(
            CreateRequest(HubRpcMethods.HubAppsListDefinitions, "list-scoped", new { scope = (string?)null }),
            CancellationToken.None);
        var definitions = JsonSerializer.SerializeToElement(listResponse.Result)
            .GetProperty("definitions")
            .EnumerateArray()
            .Where(definition => definition.GetProperty("appId").GetString() == "managed.scoped.adapter")
            .ToArray();
        Assert.Equal(3, definitions.Length);
        Assert.Equal(ScopeContract.Global, definitions[0].GetProperty("scope").GetString());
        Assert.Equal("workspace-A", definitions[1].GetProperty("scope").GetString());
        Assert.Equal("workspace-Z", definitions[2].GetProperty("scope").GetString());

        var getScoped = await handler.HandleAsync(
            CreateRequest(
                HubRpcMethods.HubAppsGetDefinition,
                "get-scoped",
                new
                {
                    appId = "managed.scoped.adapter",
                    scope = "workspace-A"
                }),
            CancellationToken.None);
        Assert.Equal(
            "Managed Scoped Adapter Workspace A",
            JsonSerializer.SerializeToElement(getScoped.Result).GetProperty("definition").GetProperty("displayName").GetString());

        var deleteGlobal = await handler.HandleAsync(
            CreateRequest(
                HubRpcMethods.HubAppsDeleteDefinition,
                "delete-global",
                new
                {
                    appId = "managed.scoped.adapter",
                    scope = ScopeContract.Global
                }),
            CancellationToken.None);
        Assert.True(JsonSerializer.SerializeToElement(deleteGlobal.Result).GetProperty("ok").GetBoolean());

        var getGlobalMissing = await handler.HandleAsync(
            CreateRequest(
                HubRpcMethods.HubAppsGetDefinition,
                "get-global-missing",
                new
                {
                    appId = "managed.scoped.adapter",
                    scope = ScopeContract.Global
                }),
            CancellationToken.None);
        AssertError(getGlobalMissing, -32014, "app_definition_not_found", "get-global-missing");

        var getScopedAfterDelete = await handler.HandleAsync(
            CreateRequest(
                HubRpcMethods.HubAppsGetDefinition,
                "get-scoped-after-delete",
                new
                {
                    appId = "managed.scoped.adapter",
                    scope = "workspace-A"
                }),
            CancellationToken.None);
        Assert.True(JsonSerializer.SerializeToElement(getScopedAfterDelete.Result).GetProperty("ok").GetBoolean());

        eventPublisher.Verify(
            publisher => publisher.Publish(It.Is<HubEventMessage>(message =>
                message.Type == HubEventTypes.AppDefinitionUpserted
                && JsonSerializer.SerializeToElement(message.Payload).GetProperty("scope").GetString() == ScopeContract.Global)),
            Times.Once);
        eventPublisher.Verify(
            publisher => publisher.Publish(It.Is<HubEventMessage>(message =>
                message.Type == HubEventTypes.AppDefinitionUpserted
                && JsonSerializer.SerializeToElement(message.Payload).GetProperty("scope").GetString() == "workspace-A")),
            Times.Once);
        eventPublisher.Verify(
            publisher => publisher.Publish(It.Is<HubEventMessage>(message =>
                message.Type == HubEventTypes.AppDefinitionUpserted
                && JsonSerializer.SerializeToElement(message.Payload).GetProperty("scope").GetString() == "workspace-Z")),
            Times.Once);
        eventPublisher.Verify(
            publisher => publisher.Publish(It.Is<HubEventMessage>(message =>
                message.Type == HubEventTypes.AppDefinitionDeleted
                && JsonSerializer.SerializeToElement(message.Payload).GetProperty("scope").GetString() == ScopeContract.Global)),
            Times.Once);
    }

    [Fact]
    [Trait("Category", "Spec")]
    [Trait("SpecRef", "6.3.12")]
    public async Task Spec_6_3_12_LaunchRpcHandler_ShouldValidateScopeWaitAndDedupeKey()
    {
        using var context = CreateDefinitionContext();
        var handler = CreateLaunchHandler(context);

        var invalidScope = await handler.HandleAsync(
            CreateRequest(HubRpcMethods.HubAppsLaunch, "launch-invalid-scope", new { appId = "launch.app", scope = 1 }),
            CancellationToken.None);
        AssertError(invalidScope, -32602, "invalid_params", "launch-invalid-scope");
        Assert.Equal("invalid_scope", JsonSerializer.SerializeToElement(invalidScope.Error!.Data).GetProperty("reason").GetString());

        var invalidWait = await handler.HandleAsync(
            CreateRequest(HubRpcMethods.HubAppsLaunch, "launch-invalid-wait", new { appId = "launch.app", scope = ScopeContract.Global, waitForRegisterMs = -1 }),
            CancellationToken.None);
        AssertError(invalidWait, -32602, "invalid_params", "launch-invalid-wait");

        var invalidDedupe = await handler.HandleAsync(
            CreateRequest(HubRpcMethods.HubAppsLaunch, "launch-invalid-dedupe", new { appId = "launch.app", scope = ScopeContract.Global, dedupeKey = 1 }),
            CancellationToken.None);
        AssertError(invalidDedupe, -32602, "invalid_params", "launch-invalid-dedupe");
    }

    [Fact]
    [Trait("Category", "Spec")]
    [Trait("SpecRef", "6.3.12")]
    public async Task Spec_6_3_12_LaunchRpcHandler_ShouldMapLaunchErrorsAndSuccess()
    {
        using var context = CreateDefinitionContext();
        var processLauncher = new Mock<IProcessLauncher>();
        processLauncher
            .Setup(launcher => launcher.Start(It.IsAny<LaunchConfiguration>(), It.IsAny<string?>()))
            .Returns(Process.GetCurrentProcess());

        var missingDefinitionHandler = CreateLaunchHandler(context, processLauncher: processLauncher.Object);
        var missingDefinitionResponse = await missingDefinitionHandler.HandleAsync(
            CreateRequest(HubRpcMethods.HubAppsLaunch, "launch-missing-definition", new { appId = "missing.launch.app", scope = ScopeContract.Global }),
            CancellationToken.None);

        AssertError(missingDefinitionResponse, -32014, "app_definition_not_found", "launch-missing-definition");

        WriteDefinition(
            context.RuntimePathOptions.DefinitionsCatalogPath,
            """
            {
              "appId": "launch.no-config",
              "scope": "",
              "displayName": "Launch Without Config"
            }
            """);

        var missingConfigResponse = await missingDefinitionHandler.HandleAsync(
            CreateRequest(HubRpcMethods.HubAppsLaunch, "launch-missing-config", new { appId = "launch.no-config", scope = ScopeContract.Global }),
            CancellationToken.None);

        AssertError(missingConfigResponse, -32020, "launch_failed", "launch-missing-config");
        Assert.Equal("launch_config_missing", JsonSerializer.SerializeToElement(missingConfigResponse.Error!.Data).GetProperty("reason").GetString());

        WriteDefinition(
            context.RuntimePathOptions.DefinitionsCatalogPath,
            """
            {
              "appId": "launch.success",
              "scope": "",
              "displayName": "Launch Success",
              "launch": {
                "exePath": "dotnet",
                "argsTemplate": "--info"
              }
            }
            """);

        var successResponse = await missingDefinitionHandler.HandleAsync(
            CreateRequest(HubRpcMethods.HubAppsLaunch, "launch-success", new { appId = "launch.success", scope = ScopeContract.Global }),
            CancellationToken.None);

        var successResult = JsonSerializer.SerializeToElement(successResponse.Result);
        Assert.True(successResult.GetProperty("ok").GetBoolean());
        Assert.Equal("started", successResult.GetProperty("status").GetString());
        Assert.Equal(Process.GetCurrentProcess().Id, successResult.GetProperty("pid").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(successResult.GetProperty("launchId").GetString()));
        Assert.Equal("launch.success:global", successResult.GetProperty("dedupeKey").GetString());
    }

    /// <inheritdoc />
    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch
        {
        }
    }

    private DefinitionTestContext CreateDefinitionContext(IHubEventPublisher? eventPublisher = null, IClock? clock = null)
    {
        var runtimePathOptions = RuntimePathOptions.Create(Path.Combine(_tempRoot, Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(runtimePathOptions.AppsPath);

        var validator = new AppDefinitionValidator();
        var definitionLoader = new DefinitionLoader(runtimePathOptions.DefinitionsCatalogPath, Mock.Of<ILogger<DefinitionLoader>>(), validator);
        var definitionProvider = new DefinitionProvider(definitionLoader);
        definitionProvider.Refresh();

        var definitionManager = new DefinitionManager(
            runtimePathOptions,
            definitionProvider,
            validator,
            clock ?? new SystemClock(),
            Mock.Of<ILogger<DefinitionManager>>(),
            eventPublisher);

        return new DefinitionTestContext(runtimePathOptions, definitionProvider, definitionManager);
    }

    private static LaunchHandler CreateLaunchHandler(
        DefinitionTestContext context,
        IClock? clock = null,
        AppRegistry? appRegistry = null,
        IProcessLauncher? processLauncher = null,
        RuntimeTuningOptions? runtimeTuningOptions = null)
    {
        var effectiveClock = clock ?? new SystemClock();
        var effectiveRuntimeTuningOptions = runtimeTuningOptions ?? RuntimeTuningOptions.Default;
        var effectiveRegistry = appRegistry ?? new AppRegistry(effectiveClock, Mock.Of<ILogger<AppRegistry>>(), effectiveRuntimeTuningOptions);
        var runtimeHttpBaseUrlProvider = new Mock<IRuntimeHttpBaseUrlProvider>();
        runtimeHttpBaseUrlProvider.Setup(provider => provider.GetHttpBaseUrl()).Returns("http://127.0.0.1:57231");

        var coordinator = new LaunchCoordinator(
            context.DefinitionProvider,
            effectiveRegistry,
            runtimeHttpBaseUrlProvider.Object,
            processLauncher ?? Mock.Of<IProcessLauncher>(),
            effectiveClock,
            effectiveRuntimeTuningOptions,
            Mock.Of<ILogger<LaunchCoordinator>>());

        return new LaunchHandler(coordinator, Mock.Of<ILogger<LaunchHandler>>());
    }

    private static JsonRpcRequest CreateRequest(string method, object id, object? parameters)
    {
        return new JsonRpcRequest
        {
            Id = id,
            Method = method,
            Params = parameters is null ? null : JsonSerializer.SerializeToElement(parameters)
        };
    }

    private static void WriteDefinition(string catalogPath, string json)
    {
        DefinitionCatalogTestHelper.UpsertDefinition(catalogPath, json);
    }

    private static void AssertError(JsonRpcResponse response, int code, string message, object id)
    {
        Assert.NotNull(response.Error);
        Assert.Equal(code, response.Error!.Code);
        Assert.Equal(message, response.Error.Message);
        Assert.Equal(id, response.Id);
    }

    private sealed record DefinitionTestContext(
        RuntimePathOptions RuntimePathOptions,
        DefinitionProvider DefinitionProvider,
        DefinitionManager DefinitionManager) : IDisposable
    {
        public void Dispose()
        {
        }
    }
}
