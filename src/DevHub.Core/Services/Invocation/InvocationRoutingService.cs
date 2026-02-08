using DevHub.Core.Models;
using Microsoft.Extensions.Logging;
using InvocationModel = DevHub.Core.Models.Invocation;

namespace DevHub.Core.Services.Invocation;

/// <summary>
/// Invocation 路由服务。
/// </summary>
public class InvocationRoutingService
{
    private readonly AppRegistry _appRegistry;
    private readonly ILogger<InvocationRoutingService> _logger;

    /// <summary>
    /// 初始化路由服务。
    /// </summary>
    public InvocationRoutingService(AppRegistry appRegistry, ILogger<InvocationRoutingService> logger)
    {
        _appRegistry = appRegistry;
        _logger = logger;
    }

    /// <summary>
    /// 获取当前在线候选实例。
    /// </summary>
    /// <param name="appId">应用 ID。</param>
    /// <param name="target">目标约束。</param>
    public List<AppInstance> GetOnlineCandidates(string appId, InvocationTarget target)
    {
        var allOnline = _appRegistry.ListInstances(appId, includeAllScopes: true, includeOffline: false).ToList();

        if (target.InstanceId is not null)
        {
            var match = allOnline.FirstOrDefault(i => i.InstanceId == target.InstanceId);
            return match is null ? new List<AppInstance>() : [match];
        }

        if (target.Scope is not null)
        {
            return allOnline.Where(i => i.Scope == target.Scope).ToList();
        }

        return allOnline.Where(i => i.Scope is null).ToList();
    }

    /// <summary>
    /// 判断指定调用是否允许路由到实例。
    /// </summary>
    public bool IsEligibleForInstance(InvocationModel invocation, AppInstance instance)
    {
        if (!string.Equals(invocation.AppId, instance.AppId, StringComparison.Ordinal))
        {
            return false;
        }

        var target = invocation.Target;

        if (target.InstanceId is not null)
        {
            return string.Equals(target.InstanceId, instance.InstanceId, StringComparison.Ordinal);
        }

        if (target.Scope is not null)
        {
            return string.Equals(target.Scope, instance.Scope, StringComparison.Ordinal);
        }

        return instance.Scope is null;
    }
}
