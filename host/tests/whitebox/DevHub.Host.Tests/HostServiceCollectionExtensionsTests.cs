namespace DevHub.Host.Tests;

using DevHub.Core.Services;
using DevHub.Core.Services.Abstractions;
using DevHub.Core.Services.Events;
using DevHub.Core.Services.Invocation;
using DevHub.Core.Services.Rpc;
using DevHub.Core.Services.Rpc.Handlers;
using DevHub.Host.BackgroundServices;
using DevHub.Host.Extensions;
using DevHub.Host.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

/// <summary>
/// Host ServiceCollectionExtensions 注册行为测试。
/// </summary>
[Trait("Category", "Impl")]
public sealed class HostServiceCollectionExtensionsTests : IDisposable
{
    private readonly string _tempDirectory;

    /// <summary>
    /// 初始化测试上下文。
    /// </summary>
    public HostServiceCollectionExtensionsTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "DevHubHostServiceCollectionTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
    }

    [Fact]
    public void Impl_AddDevHubHost_ShouldRegisterAndResolveHostAndCoreServices()
    {
        var runtimePathOptions = RuntimePathOptions.Create(Path.Combine(_tempDirectory, "data"));
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDevHubHost(runtimePathOptions, "test-host-version");

        using var provider = services.BuildServiceProvider();

        var resolvedRuntimePathOptions = provider.GetRequiredService<RuntimePathOptions>();
        Assert.Equal(runtimePathOptions.RootPath, resolvedRuntimePathOptions.RootPath);

        Assert.NotNull(provider.GetRequiredService<RuntimeTuningOptions>());
        Assert.IsType<SystemClock>(provider.GetRequiredService<IClock>());
        Assert.False(string.IsNullOrWhiteSpace(provider.GetRequiredService<IHubVersionSource>().CurrentVersion));
        Assert.IsType<ProcessLauncher>(provider.GetRequiredService<IProcessLauncher>());
        Assert.NotNull(provider.GetRequiredService<AppDefinitionValidator>());
        Assert.NotNull(provider.GetRequiredService<AppRegistry>());
        Assert.NotNull(provider.GetRequiredService<DefinitionLoader>());
        Assert.NotNull(provider.GetRequiredService<IDefinitionProvider>());
        Assert.NotNull(provider.GetRequiredService<IDefinitionManager>());
        Assert.NotNull(provider.GetRequiredService<HostRuntimeContext>());
        Assert.IsType<HostRuntimeHttpBaseUrlProvider>(provider.GetRequiredService<IRuntimeHttpBaseUrlProvider>());
        Assert.NotNull(provider.GetRequiredService<HubEventBus>());
        Assert.Same(
            provider.GetRequiredService<HubEventBus>(),
            provider.GetRequiredService<IHubEventPublisher>());
        Assert.NotNull(provider.GetRequiredService<InvocationRoutingService>());
        Assert.NotNull(provider.GetRequiredService<InvocationStore>());
        Assert.NotNull(provider.GetRequiredService<InvocationRequestWaiter>());
        Assert.NotNull(provider.GetRequiredService<InvocationTimeoutWorker>());
        Assert.NotNull(provider.GetRequiredService<LaunchCoordinator>());
        Assert.NotNull(provider.GetRequiredService<RpcRouter>());
        Assert.NotNull(provider.GetRequiredService<HostDataDirectoryInitializer>());
        Assert.NotNull(provider.GetRequiredService<HostRuntimeArtifactManager>());
        Assert.NotNull(provider.GetRequiredService<HostBootstrapper>());
        Assert.NotNull(provider.GetRequiredService<AppRegistryCleanupBackgroundService>());
        Assert.NotNull(provider.GetRequiredService<InvocationTimeoutBackgroundService>());
        Assert.NotNull(provider.GetRequiredService<RpcHttpEndpointHandler>());
        Assert.NotNull(provider.GetRequiredService<WebSocketSessionHandler>());

        var handlers = provider.GetServices<IRpcHandler>().ToList();
        Assert.Contains(handlers, handler => handler is HubPingHandler);
        Assert.Contains(handlers, handler => handler is HubGetVersionHandler);
        Assert.Contains(handlers, handler => handler is AppDefinitionsHandler);
        Assert.Contains(handlers, handler => handler is AppInstancesHandler);
        Assert.Contains(handlers, handler => handler is InvocationHandler);
        Assert.Contains(handlers, handler => handler is LaunchHandler);

        var hostedServices = provider.GetServices<IHostedService>().ToList();
        Assert.Contains(hostedServices, service => service is AppRegistryCleanupBackgroundService);
        Assert.Contains(hostedServices, service => service is InvocationTimeoutBackgroundService);
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
}
