namespace DevHub.Tests;

using DevHub.Core.Extensions;
using DevHub.Core.Services;
using DevHub.Core.Services.Abstractions;
using DevHub.Core.Services.Events;
using DevHub.Core.Services.Invocation;
using DevHub.Core.Services.Rpc;
using DevHub.Core.Services.Rpc.Handlers;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// ServiceCollectionExtensions 注册行为测试。
/// </summary>
[Trait("Category", "Impl")]
public sealed class ServiceCollectionExtensionsTests : IDisposable
{
    private readonly string _tempDirectory;

    /// <summary>
    /// 初始化测试上下文。
    /// </summary>
    public ServiceCollectionExtensionsTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "DevHubServiceCollectionTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
    }

    [Fact]
    public void Impl_AddDevHubCore_ShouldRegisterAndResolveCoreServices()
    {
        var runtimePathOptions = RuntimePathOptions.Create(Path.Combine(_tempDirectory, "data"));

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDevHubCore(runtimePathOptions);

        using var provider = services.BuildServiceProvider();

        var resolvedRuntimePathOptions = provider.GetRequiredService<RuntimePathOptions>();
        Assert.Equal(runtimePathOptions.RootPath, resolvedRuntimePathOptions.RootPath);
        Assert.Equal(runtimePathOptions.RuntimePath, resolvedRuntimePathOptions.RuntimePath);
        Assert.Equal(runtimePathOptions.DefinitionsPath, resolvedRuntimePathOptions.DefinitionsPath);
        Assert.Equal(runtimePathOptions.InstancesPath, resolvedRuntimePathOptions.InstancesPath);
        Assert.Equal(runtimePathOptions.LogsPath, resolvedRuntimePathOptions.LogsPath);

        Assert.NotNull(provider.GetRequiredService<RuntimeTuningOptions>());
        Assert.IsType<SystemClock>(provider.GetRequiredService<IClock>());
        Assert.IsType<ProcessLauncher>(provider.GetRequiredService<IProcessLauncher>());
        Assert.NotNull(provider.GetRequiredService<AppDefinitionValidator>());
        Assert.NotNull(provider.GetRequiredService<AppRegistry>());
        Assert.NotNull(provider.GetRequiredService<DefinitionLoader>());
        Assert.NotNull(provider.GetRequiredService<IDefinitionProvider>());
        Assert.NotNull(provider.GetRequiredService<IDefinitionManager>());
        Assert.NotNull(provider.GetRequiredService<HubEventBus>());
        Assert.NotNull(provider.GetRequiredService<InvocationRoutingService>());
        Assert.NotNull(provider.GetRequiredService<InvocationStore>());
        Assert.NotNull(provider.GetRequiredService<InvocationRequestWaiter>());
        Assert.NotNull(provider.GetRequiredService<InvocationTimeoutWorker>());
        Assert.NotNull(provider.GetRequiredService<IRuntimeHttpBaseUrlProvider>());
        Assert.NotNull(provider.GetRequiredService<LaunchCoordinator>());
        Assert.NotNull(provider.GetRequiredService<RpcRouter>());

        var handlers = provider.GetServices<IRpcHandler>().ToList();
        Assert.Contains(handlers, handler => handler is HubPingHandler);
        Assert.Contains(handlers, handler => handler is AppDefinitionsHandler);
        Assert.Contains(handlers, handler => handler is AppInstancesHandler);
        Assert.Contains(handlers, handler => handler is InvocationHandler);
        Assert.Contains(handlers, handler => handler is LaunchHandler);
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
