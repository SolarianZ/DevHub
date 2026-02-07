using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using DevHub.Core.Services;
using DevHub.Core.Services.Invocation;
using DevHub.Core.Services.Rpc;
using DevHub.Core.Services.Rpc.Handlers;

namespace DevHub.Core.Extensions;

/// <summary>
/// 服务集合扩展方法
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// 添加DevHub核心服务
    /// </summary>
    public static IServiceCollection AddDevHubCore(this IServiceCollection services, string definitionsPath)
    {
        // 注册核心服务
        services.AddSingleton<FileSystemManager>(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<FileSystemManager>>();
            return new FileSystemManager(logger, definitionsPath);
        });
        services.AddSingleton<AppRegistry>();
        services.AddSingleton<DefinitionLoader>(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<DefinitionLoader>>();
            return new DefinitionLoader(definitionsPath, logger);
        });
        services.AddSingleton<InvocationRoutingService>();
        services.AddSingleton<InvocationStore>();
        services.AddSingleton<InvocationRequestWaiter>();

        // 注册RPC处理器
        services.AddSingleton<IRpcHandler, HubPingHandler>();
        services.AddSingleton<IRpcHandler, AppDefinitionsHandler>();
        services.AddSingleton<IRpcHandler, AppInstancesHandler>();
        services.AddSingleton<IRpcHandler, InvocationHandler>();
        services.AddSingleton<IRpcHandler, LaunchHandler>();

        // 注册RPC路由器
        services.AddSingleton<RpcRouter>();

        return services;
    }
}
