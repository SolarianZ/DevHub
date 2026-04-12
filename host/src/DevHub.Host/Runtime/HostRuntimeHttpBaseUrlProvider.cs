using DevHub.Core.Services.Invocation;

namespace DevHub.Host.Runtime;

/// <summary>
/// 基于 Host 内存态运行时上下文提供当前 HTTP 基础地址。
/// </summary>
public sealed class HostRuntimeHttpBaseUrlProvider : IRuntimeHttpBaseUrlProvider
{
    private readonly HostRuntimeContext _runtimeContext;

    /// <summary>
    /// 初始化提供器。
    /// </summary>
    /// <param name="runtimeContext">Host 运行时上下文。</param>
    public HostRuntimeHttpBaseUrlProvider(HostRuntimeContext runtimeContext)
    {
        _runtimeContext = runtimeContext;
    }

    /// <inheritdoc />
    public string GetHttpBaseUrl()
    {
        return _runtimeContext.GetHttpBaseUrl();
    }
}
