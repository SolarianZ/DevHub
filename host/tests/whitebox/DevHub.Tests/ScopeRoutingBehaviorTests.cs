namespace DevHub.Tests;

using System.Text.Json;
using DevHub.Core.Models;
using DevHub.Core.Models.Rpc;
using DevHub.Core.Services;
using DevHub.Core.Services.Abstractions;
using DevHub.Core.Services.Invocation;
using DevHub.Core.Services.Rpc.Handlers;
using Microsoft.Extensions.Logging;
using Moq;

/// <summary>
/// scope 与路由矩阵实现行为白盒测试。
/// </summary>
[Trait("Category", "Impl")]
public class ScopeRoutingBehaviorTests : IDisposable
{
    private const string InstancePassword = "scope-routing-password";
    private readonly string _tempDirectory;

    public ScopeRoutingBehaviorTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "DevHubScopeRoutingBehaviorTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
    }

    [Fact]
    public async Task Impl_5_5_Register_WhenScopeOmittedOrNull_ShouldBeRejected_And_EmptyString_ShouldBeGlobal()
    {
        const string appId = "spec-5.5-register-global";

        using var appRegistry = new AppRegistry(new SystemClock(), Mock.Of<ILogger<AppRegistry>>());
        var handler = CreateAppInstancesHandler(appRegistry);

        var omitted = await RegisterInstanceAsync(handler, "spec-5.5-omitted", appId, scopeValue: null, includeScopeProperty: false, pid: 8101);
        var withNull = await RegisterInstanceAsync(handler, "spec-5.5-null", appId, scopeValue: null, includeScopeProperty: true, pid: 8102);
        var withEmpty = await RegisterInstanceAsync(handler, "spec-5.5-empty", appId, scopeValue: string.Empty, includeScopeProperty: true, pid: 8103);

        AssertError(omitted, -32602, "invalid_params");
        Assert.Equal("invalid_scope", JsonSerializer.SerializeToElement(omitted.Error!.Data).GetProperty("reason").GetString());

        AssertError(withNull, -32602, "invalid_params");
        Assert.Equal("invalid_scope", JsonSerializer.SerializeToElement(withNull.Error!.Data).GetProperty("reason").GetString());

        AssertSuccess(withEmpty);

        var listAllScopes = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "spec-5.5-list-all-scopes",
            Method = "hub.apps.listInstances",
            Params = JsonSerializer.SerializeToElement(new { appId, scope = (string?)null })
        }, CancellationToken.None);

        var listEmpty = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "spec-5.5-list-empty",
            Method = "hub.apps.listInstances",
            Params = JsonSerializer.SerializeToElement(new { appId, scope = string.Empty })
        }, CancellationToken.None);

        AssertSuccess(listAllScopes);
        AssertSuccess(listEmpty);

        var allScopeIds = ExtractInstanceIds(listAllScopes);
        var emptyScopeIds = ExtractInstanceIds(listEmpty);

        Assert.DoesNotContain("spec-5.5-omitted", allScopeIds);
        Assert.DoesNotContain("spec-5.5-null", allScopeIds);
        Assert.Contains("spec-5.5-empty", allScopeIds);

        Assert.Contains("spec-5.5-empty", emptyScopeIds);
    }

    [Fact]
    public async Task Impl_5_5_Register_WhenScopeIsGlobalLiteral_ShouldBeExplicitScope()
    {
        const string appId = "spec-5.5-global-literal";

        using var appRegistry = new AppRegistry(new SystemClock(), Mock.Of<ILogger<AppRegistry>>());
        var handler = CreateAppInstancesHandler(appRegistry);

        var explicitGlobal = await RegisterInstanceAsync(handler, "spec-5.5-explicit-global", appId, scopeValue: "global", includeScopeProperty: true, pid: 8111);
        var defaultGlobal = await RegisterInstanceAsync(handler, "spec-5.5-default-global", appId, scopeValue: string.Empty, includeScopeProperty: true, pid: 8112);

        AssertSuccess(explicitGlobal);
        AssertSuccess(defaultGlobal);

        var listGlobal = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "spec-5.5-global-list-global",
            Method = "hub.apps.listInstances",
            Params = JsonSerializer.SerializeToElement(new { appId, scope = string.Empty })
        }, CancellationToken.None);
        var listExplicit = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "spec-5.5-global-list-explicit",
            Method = "hub.apps.listInstances",
            Params = JsonSerializer.SerializeToElement(new { appId, scope = "global" })
        }, CancellationToken.None);

        AssertSuccess(listGlobal);
        AssertSuccess(listExplicit);

        var globalIds = ExtractInstanceIds(listGlobal);
        var explicitIds = ExtractInstanceIds(listExplicit);

        Assert.Contains("spec-5.5-default-global", globalIds);
        Assert.DoesNotContain("spec-5.5-explicit-global", globalIds);

        Assert.Contains("spec-5.5-explicit-global", explicitIds);
        Assert.DoesNotContain("spec-5.5-default-global", explicitIds);
    }

    [Fact]
    public async Task Impl_5_5_Launch_WhenScopeOmittedOrNull_ShouldReturnInvalidParams()
    {
        const string appId = "spec-5.5-launch-scope";
        WriteDefinition(appId, includeLaunch: true, dedupeKeyTemplate: "{appId}:{scopeOrGlobal}");

        var processLauncher = new Mock<IProcessLauncher>();
        processLauncher
            .Setup(launcher => launcher.Start(It.IsAny<LaunchConfiguration>(), It.IsAny<string?>()))
            .Returns(System.Diagnostics.Process.GetCurrentProcess());

        var handler = CreateLaunchHandler(processLauncher: processLauncher.Object);

        var first = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "spec-5.5-launch-omitted",
            Method = "hub.apps.launch",
            Params = JsonSerializer.SerializeToElement(new
            {
                appId,
                waitForRegisterMs = 0
            })
        }, CancellationToken.None);

        var second = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "spec-5.5-launch-null",
            Method = "hub.apps.launch",
            Params = JsonSerializer.SerializeToElement(new
            {
                appId,
                scope = (string?)null,
                waitForRegisterMs = 0
            })
        }, CancellationToken.None);

        AssertError(first, -32602, "invalid_params");
        Assert.Equal("invalid_scope", JsonSerializer.SerializeToElement(first.Error!.Data).GetProperty("reason").GetString());

        AssertError(second, -32602, "invalid_params");
        Assert.Equal("invalid_scope", JsonSerializer.SerializeToElement(second.Error!.Data).GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Impl_5_5_Launch_WhenScopeEmpty_ShouldLaunchGlobalDefinition()
    {
        const string appId = "spec-5.5-launch-empty";
        WriteDefinition(appId, includeLaunch: true);

        var response = await CreateLaunchHandler().HandleAsync(new JsonRpcRequest
        {
            Id = "spec-5.5-launch-empty-invalid",
            Method = "hub.apps.launch",
            Params = JsonSerializer.SerializeToElement(new
            {
                appId,
                scope = string.Empty,
                waitForRegisterMs = 0
            })
        }, CancellationToken.None);

        AssertSuccess(response);
        var result = JsonSerializer.SerializeToElement(response.Result);
        Assert.Equal("started", result.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Impl_5_5_Invoke_WhenTargetScopeOmittedOrNull_ShouldBeRejected_And_EmptyString_ShouldRouteOnlyToGlobal()
    {
        const string appId = "spec-5.5-target-default";
        WriteDefinition(appId, rpcEnabled: true);

        using var appRegistry = new AppRegistry(new SystemClock(), Mock.Of<ILogger<AppRegistry>>());
        RegisterInstance(appRegistry, "spec-5.5-target-global", appId, scope: null, poll: true, respond: true, pid: 8121);
        RegisterInstance(appRegistry, "spec-5.5-target-scoped", appId, scope: "workspace-A", poll: true, respond: true, pid: 8122);

        var handler = CreateInvocationHandler(appRegistry);

        var omitted = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "spec-5.5-target-default-omitted",
            Method = "hub.invoke.notify",
            Params = JsonSerializer.SerializeToElement(new
            {
                appId,
                target = new { },
                method = "asset.rebuild",
                args = new { caseName = "omitted" },
                options = new
                {
                    ttlMs = 60000,
                    queueIfOffline = false,
                    autoLaunch = false
                }
            })
        }, CancellationToken.None);
        AssertError(omitted, -32602, "invalid_params");
        Assert.Equal("invalid_target_scope", JsonSerializer.SerializeToElement(omitted.Error!.Data).GetProperty("reason").GetString());

        var withNull = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "spec-5.5-target-default-null",
            Method = "hub.invoke.notify",
            Params = JsonSerializer.SerializeToElement(new
            {
                appId,
                target = new { scope = (string?)null, instanceId = (string?)null },
                method = "asset.rebuild",
                args = new { caseName = "null" },
                options = new
                {
                    ttlMs = 60000,
                    queueIfOffline = false,
                    autoLaunch = false
                }
            })
        }, CancellationToken.None);
        AssertError(withNull, -32602, "invalid_params");
        Assert.Equal("invalid_target_scope", JsonSerializer.SerializeToElement(withNull.Error!.Data).GetProperty("reason").GetString());

        var withEmpty = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "spec-5.5-target-default-empty",
            Method = "hub.invoke.notify",
            Params = JsonSerializer.SerializeToElement(new
            {
                appId,
                target = new { scope = string.Empty, instanceId = (string?)null },
                method = "asset.rebuild",
                args = new { caseName = "empty" },
                options = new
                {
                    ttlMs = 60000,
                    queueIfOffline = false,
                    autoLaunch = false
                }
            })
        }, CancellationToken.None);

        AssertSuccess(withEmpty);
        var invocationId = JsonSerializer.SerializeToElement(withEmpty.Result).GetProperty("invocationId").GetString();

        var globalPoll = await PollAsync(handler, appRegistry, "spec-5.5-target-global", maxCount: 1, waitMs: 120);
        var scopedPoll = await PollAsync(handler, appRegistry, "spec-5.5-target-scoped", maxCount: 1, waitMs: 0);

        AssertSuccess(globalPoll);
        AssertSuccess(scopedPoll);

        var globalItems = JsonSerializer.SerializeToElement(globalPoll.Result).GetProperty("items").EnumerateArray().ToList();
        var scopedItems = JsonSerializer.SerializeToElement(scopedPoll.Result).GetProperty("items").EnumerateArray().ToList();

        Assert.Single(globalItems);
        Assert.Equal(invocationId, globalItems[0].GetProperty("invocationId").GetString());
        Assert.Empty(scopedItems);
    }

    [Fact]
    public async Task Impl_5_5_Register_WhenScopeTypeInvalid_ShouldReturnInvalidParams()
    {
        using var appRegistry = new AppRegistry(new SystemClock(), Mock.Of<ILogger<AppRegistry>>());
        var handler = CreateAppInstancesHandler(appRegistry);

        var response = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "spec-5.5-register-invalid-scope-type",
            Method = "hub.apps.registerInstance",
            Params = JsonSerializer.SerializeToElement(new
            {
                password = InstancePassword,
                instance = new
                {
                    instanceId = "spec-5.5-register-invalid",
                    appId = "spec-5.5-register-invalid-app",
                    scope = new { bad = true },
                    pid = 8131,
                    invoke = new { poll = true, respond = true }
                }
            })
        }, CancellationToken.None);

        AssertError(response, -32602, "invalid_params");
        var data = JsonSerializer.SerializeToElement(response.Error!.Data);
        Assert.Equal("invalid_scope", data.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Impl_5_5_Launch_WhenScopeTypeInvalid_ShouldReturnInvalidParams()
    {
        const string appId = "spec-5.5-launch-invalid";
        WriteDefinition(appId, includeLaunch: true);

        var response = await CreateLaunchHandler().HandleAsync(new JsonRpcRequest
        {
            Id = "spec-5.5-launch-invalid-scope",
            Method = "hub.apps.launch",
            Params = JsonSerializer.SerializeToElement(new
            {
                appId,
                scope = 123,
                waitForRegisterMs = 0
            })
        }, CancellationToken.None);

        AssertError(response, -32602, "invalid_params");
        var data = JsonSerializer.SerializeToElement(response.Error!.Data);
        Assert.Equal("invalid_scope", data.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Impl_5_5_Invoke_WhenTargetScopeTypeInvalid_ShouldReturnInvalidParams()
    {
        const string appId = "spec-5.5-target-invalid";
        WriteDefinition(appId, rpcEnabled: true);

        using var appRegistry = new AppRegistry(new SystemClock(), Mock.Of<ILogger<AppRegistry>>());
        var handler = CreateInvocationHandler(appRegistry);

        var response = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "spec-5.5-target-invalid-scope",
            Method = "hub.invoke.notify",
            Params = JsonSerializer.SerializeToElement(new
            {
                appId,
                target = new
                {
                    scope = 123,
                    instanceId = (string?)null
                },
                method = "asset.rebuild",
                options = new
                {
                    ttlMs = 60000,
                    queueIfOffline = false,
                    autoLaunch = false
                }
            })
        }, CancellationToken.None);

        AssertError(response, -32602, "invalid_params");
        var data = JsonSerializer.SerializeToElement(response.Error!.Data);
        Assert.Equal("invalid_target_scope", data.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Impl_5_5_Invoke_WhenTargetScopeIsExplicit_ShouldUseCaseSensitiveExactMatch()
    {
        const string appId = "spec-5.5-case-sensitive";
        WriteDefinition(appId, rpcEnabled: true);

        using var appRegistry = new AppRegistry(new SystemClock(), Mock.Of<ILogger<AppRegistry>>());
        RegisterInstance(appRegistry, "spec-5.5-upper", appId, scope: "workspace-A", poll: true, respond: true, pid: 8141);
        RegisterInstance(appRegistry, "spec-5.5-lower", appId, scope: "workspace-a", poll: true, respond: true, pid: 8142);

        var handler = CreateInvocationHandler(appRegistry);

        foreach (var testCase in new (string Scope, string ExpectedInstance, string UnexpectedInstance)[]
                 {
                     ("workspace-A", "spec-5.5-upper", "spec-5.5-lower"),
                     ("workspace-a", "spec-5.5-lower", "spec-5.5-upper")
                 })
        {
            var notify = await handler.HandleAsync(new JsonRpcRequest
            {
                Id = $"spec-5.5-case-{testCase.Scope}",
                Method = "hub.invoke.notify",
                Params = JsonSerializer.SerializeToElement(new
                {
                    appId,
                    target = new { scope = testCase.Scope, instanceId = (string?)null },
                    method = "asset.rebuild",
                    options = new
                    {
                        ttlMs = 60000,
                        queueIfOffline = false,
                        autoLaunch = false
                    }
                })
            }, CancellationToken.None);

            AssertSuccess(notify);
            var invocationId = JsonSerializer.SerializeToElement(notify.Result).GetProperty("invocationId").GetString();

            var expectedPoll = await PollAsync(handler, appRegistry, testCase.ExpectedInstance, maxCount: 1, waitMs: 120);
            var unexpectedPoll = await PollAsync(handler, appRegistry, testCase.UnexpectedInstance, maxCount: 1, waitMs: 0);

            AssertSuccess(expectedPoll);
            AssertSuccess(unexpectedPoll);

            var expectedItems = JsonSerializer.SerializeToElement(expectedPoll.Result).GetProperty("items").EnumerateArray().ToList();
            var unexpectedItems = JsonSerializer.SerializeToElement(unexpectedPoll.Result).GetProperty("items").EnumerateArray().ToList();

            Assert.Single(expectedItems);
            Assert.Equal(invocationId, expectedItems[0].GetProperty("invocationId").GetString());
            Assert.Empty(unexpectedItems);
        }
    }

    [Fact]
    public async Task Impl_5_5_Invoke_WhenTargetScopeHasNoMatch_ShouldNotFallbackToGlobal()
    {
        const string appId = "spec-5.5-no-fallback-global";
        WriteDefinition(appId, rpcEnabled: true);

        using var appRegistry = new AppRegistry(new SystemClock(), Mock.Of<ILogger<AppRegistry>>());
        RegisterInstance(appRegistry, "spec-5.5-no-fallback-global-inst", appId, scope: null, poll: true, respond: true, pid: 8149);

        var handler = CreateInvocationHandler(appRegistry);

        var notify = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "spec-5.5-no-fallback-global",
            Method = "hub.invoke.notify",
            Params = JsonSerializer.SerializeToElement(new
            {
                appId,
                target = new { scope = "workspace-A", instanceId = (string?)null },
                method = "asset.rebuild",
                options = new
                {
                    ttlMs = 60000,
                    queueIfOffline = false,
                    autoLaunch = false
                }
            })
        }, CancellationToken.None);

        AssertError(notify, -32010, "instance_not_found");

        var globalPoll = await PollAsync(handler, appRegistry, "spec-5.5-no-fallback-global-inst", maxCount: 1, waitMs: 0);
        AssertSuccess(globalPoll);
        var globalItems = JsonSerializer.SerializeToElement(globalPoll.Result).GetProperty("items").EnumerateArray().ToList();
        Assert.Empty(globalItems);
    }

    [Fact]
    public async Task Impl_7_1_Invoke_WhenTargetInstanceIdProvided_ShouldRouteOnlyToThatInstance()
    {
        const string appId = "spec-7.1-instance-id-priority";
        WriteDefinition(appId, rpcEnabled: true);

        using var appRegistry = new AppRegistry(new SystemClock(), Mock.Of<ILogger<AppRegistry>>());
        RegisterInstance(appRegistry, "spec-7.1-target-instance", appId, scope: "workspace-A", poll: true, respond: true, pid: 8151);
        RegisterInstance(appRegistry, "spec-7.1-other-instance", appId, scope: "workspace-B", poll: true, respond: true, pid: 8152);

        var handler = CreateInvocationHandler(appRegistry);

        var notify = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "spec-7.1-instance-id-priority",
            Method = "hub.invoke.notify",
            Params = JsonSerializer.SerializeToElement(new
            {
                appId,
                target = new
                {
                    scope = "workspace-A",
                    instanceId = "spec-7.1-target-instance"
                },
                method = "asset.rebuild",
                options = new
                {
                    ttlMs = 60000,
                    queueIfOffline = false,
                    autoLaunch = false
                }
            })
        }, CancellationToken.None);

        AssertSuccess(notify);
        var invocationId = JsonSerializer.SerializeToElement(notify.Result).GetProperty("invocationId").GetString();

        var targetPoll = await PollAsync(handler, appRegistry, "spec-7.1-target-instance", maxCount: 1, waitMs: 120);
        var otherPoll = await PollAsync(handler, appRegistry, "spec-7.1-other-instance", maxCount: 1, waitMs: 0);

        AssertSuccess(targetPoll);
        AssertSuccess(otherPoll);

        var targetItems = JsonSerializer.SerializeToElement(targetPoll.Result).GetProperty("items").EnumerateArray().ToList();
        var otherItems = JsonSerializer.SerializeToElement(otherPoll.Result).GetProperty("items").EnumerateArray().ToList();

        Assert.Single(targetItems);
        Assert.Equal(invocationId, targetItems[0].GetProperty("invocationId").GetString());
        Assert.Empty(otherItems);
    }

    [Fact]
    public async Task Impl_7_1_Invoke_WhenQueueIfOfflineTrueAndDefinitionMissing_ShouldReturnInstanceNotFound()
    {
        using var appRegistry = new AppRegistry(new SystemClock(), Mock.Of<ILogger<AppRegistry>>());
        var handler = CreateInvocationHandler(appRegistry);

        var notify = await handler.HandleAsync(new JsonRpcRequest
        {
            Id = "spec-7.1-no-definition-no-pending",
            Method = "hub.invoke.notify",
            Params = JsonSerializer.SerializeToElement(new
            {
                appId = "spec-7.1-no-definition",
                target = new { scope = "workspace-A", instanceId = (string?)null },
                method = "asset.rebuild",
                options = new
                {
                    ttlMs = 60000,
                    queueIfOffline = true,
                    autoLaunch = false
                }
            })
        }, CancellationToken.None);

        AssertError(notify, -32010, "instance_not_found");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }

    private AppInstancesHandler CreateAppInstancesHandler(AppRegistry appRegistry, IClock? clock = null)
    {
        return new AppInstancesHandler(
            appRegistry,
            clock ?? new SystemClock(),
            Mock.Of<ILogger<AppInstancesHandler>>());
    }

    private InvocationHandler CreateInvocationHandler(AppRegistry appRegistry, IClock? clock = null, IProcessLauncher? processLauncher = null)
    {
        var effectiveClock = clock ?? new SystemClock();
        var definitionLoader = new DefinitionLoader(DefinitionCatalogTestHelper.GetCatalogPath(_tempDirectory), Mock.Of<ILogger<DefinitionLoader>>());
        var definitionProvider = new DefinitionProvider(definitionLoader);
        definitionProvider.Refresh();

        var routingService = new InvocationRoutingService(appRegistry, Mock.Of<ILogger<InvocationRoutingService>>());
        var store = new InvocationStore(Mock.Of<ILogger<InvocationStore>>(), routingService, effectiveClock);
        var waiter = new InvocationRequestWaiter(Mock.Of<ILogger<InvocationRequestWaiter>>());

        var runtimeProvider = new Mock<IRuntimeHttpBaseUrlProvider>();
        runtimeProvider.Setup(provider => provider.GetHttpBaseUrl()).Returns("http://127.0.0.1:7451");

        var coordinator = new LaunchCoordinator(
            definitionProvider,
            appRegistry,
            runtimeProvider.Object,
            processLauncher ?? new ProcessLauncher(),
            effectiveClock,
            Mock.Of<ILogger<LaunchCoordinator>>());

        return new InvocationHandler(
            appRegistry,
            definitionProvider,
            routingService,
            store,
            waiter,
            coordinator,
            effectiveClock,
            Mock.Of<ILogger<InvocationHandler>>());
    }

    private LaunchHandler CreateLaunchHandler(IClock? clock = null, IProcessLauncher? processLauncher = null, AppRegistry? appRegistry = null)
    {
        var effectiveClock = clock ?? new SystemClock();
        var definitionLoader = new DefinitionLoader(DefinitionCatalogTestHelper.GetCatalogPath(_tempDirectory), Mock.Of<ILogger<DefinitionLoader>>());
        var definitionProvider = new DefinitionProvider(definitionLoader);
        definitionProvider.Refresh();
        var effectiveRegistry = appRegistry ?? new AppRegistry(effectiveClock, Mock.Of<ILogger<AppRegistry>>());

        var runtimeProvider = new Mock<IRuntimeHttpBaseUrlProvider>();
        runtimeProvider.Setup(provider => provider.GetHttpBaseUrl()).Returns("http://127.0.0.1:7452");

        var coordinator = new LaunchCoordinator(
            definitionProvider,
            effectiveRegistry,
            runtimeProvider.Object,
            processLauncher ?? new ProcessLauncher(),
            effectiveClock,
            Mock.Of<ILogger<LaunchCoordinator>>());

        return new LaunchHandler(coordinator, Mock.Of<ILogger<LaunchHandler>>());
    }

    private void WriteDefinition(string appId, bool rpcEnabled = true, bool includeLaunch = false, string? dedupeKeyTemplate = null)
    {
        const string definitionScope = ScopeContract.Global;
        var payload = new Dictionary<string, object?>
        {
            ["appId"] = appId,
            ["scope"] = definitionScope,
            ["displayName"] = appId,
            ["capabilities"] = new Dictionary<string, object?>
            {
                ["rpc"] = rpcEnabled,
                ["events"] = false
            }
        };

        if (includeLaunch)
        {
            var launch = new Dictionary<string, object?>
            {
                ["exePath"] = "dotnet",
                ["argsTemplate"] = "--version"
            };

            if (dedupeKeyTemplate is not null)
            {
                launch["dedupeKeyTemplate"] = dedupeKeyTemplate;
            }

            payload["launch"] = launch;
        }

        DefinitionCatalogTestHelper.UpsertDefinition(
            DefinitionCatalogTestHelper.GetCatalogPath(_tempDirectory),
            JsonSerializer.Serialize(payload));
    }

    private static async Task<JsonRpcResponse> RegisterInstanceAsync(
        AppInstancesHandler handler,
        string instanceId,
        string appId,
        string? scopeValue,
        bool includeScopeProperty,
        int pid)
    {
        var instance = new Dictionary<string, object?>
        {
            ["instanceId"] = instanceId,
            ["appId"] = appId,
            ["pid"] = pid,
            ["invoke"] = new Dictionary<string, object?>
            {
                ["poll"] = true,
                ["respond"] = true
            }
        };

        if (includeScopeProperty)
        {
            instance["scope"] = scopeValue;
        }

        return await handler.HandleAsync(new JsonRpcRequest
        {
            Id = $"register-{instanceId}",
            Method = "hub.apps.registerInstance",
            Params = JsonSerializer.SerializeToElement(new
            {
                password = InstancePassword,
                instance
            })
        }, CancellationToken.None);
    }

    private static AppInstance RegisterInstance(
        AppRegistry appRegistry,
        string instanceId,
        string appId,
        string? scope,
        bool poll,
        bool respond,
        int pid)
    {
        var normalizedScope = scope ?? ScopeContract.Global;
        return appRegistry.RegisterInstance(new AppInstance
        {
            InstanceId = instanceId,
            AppId = appId,
            Scope = normalizedScope,
            Pid = pid,
            Invoke = new InvokeCapability
            {
                Poll = poll,
                Respond = respond
            }
        });
    }

    private static async Task<JsonRpcResponse> PollAsync(InvocationHandler handler, AppRegistry appRegistry, string instanceId, int maxCount, int waitMs)
    {
        return await handler.HandleAsync(new JsonRpcRequest
        {
            Id = $"poll-{instanceId}-{Guid.NewGuid():N}",
            Method = "hub.invoke.poll",
            Params = JsonSerializer.SerializeToElement(new
            {
                instanceId,
                instanceSessionToken = GetInstanceSessionToken(appRegistry, instanceId),
                maxCount,
                waitMs
            })
        }, CancellationToken.None);
    }

    private static string GetInstanceSessionToken(AppRegistry appRegistry, string instanceId)
    {
        return appRegistry.GetCurrentInstanceSessionToken(instanceId)
               ?? throw new InvalidOperationException($"Instance '{instanceId}' session token was not registered.");
    }

    private static HashSet<string?> ExtractInstanceIds(JsonRpcResponse response)
    {
        var result = JsonSerializer.SerializeToElement(response.Result);
        return result
            .GetProperty("instances")
            .EnumerateArray()
            .Select(item => item.GetProperty("instanceId").GetString())
            .ToHashSet(StringComparer.Ordinal);
    }

    private static void AssertSuccess(JsonRpcResponse response)
    {
        Assert.Null(response.Error);
        Assert.NotNull(response.Result);
        var result = JsonSerializer.SerializeToElement(response.Result);
        Assert.True(result.GetProperty("ok").GetBoolean());
    }

    private static void AssertError(JsonRpcResponse response, int expectedCode, string expectedMessage)
    {
        Assert.NotNull(response.Error);
        Assert.Equal(expectedCode, response.Error!.Code);
        Assert.Equal(expectedMessage, response.Error.Message);
    }
}
