using Microsoft.Extensions.Logging;

namespace DevHub.Core.Services.Rpc;

/// <summary>
/// 仅供测试使用的 RPC 故障注入策略。
/// </summary>
public sealed class RpcTestFaultInjectionPolicy
{
    /// <summary>
    /// 通过方法名强制返回 <c>internal_error</c> 的环境变量。
    /// </summary>
    public const string ForceInternalErrorMethodsEnvironmentVariable = "DEVHUB_TEST_RPC_FORCE_INTERNAL_ERROR_METHODS";

    private readonly HashSet<string> _forceInternalErrorMethods;

    internal RpcTestFaultInjectionPolicy(IEnumerable<string> forceInternalErrorMethods, ILogger<RpcTestFaultInjectionPolicy>? logger = null)
    {
        _forceInternalErrorMethods = forceInternalErrorMethods
            .Where(method => !string.IsNullOrWhiteSpace(method))
            .Select(method => method.Trim())
            .ToHashSet(StringComparer.Ordinal);

        if (_forceInternalErrorMethods.Count > 0)
        {
            logger?.LogWarning(
                "已启用测试故障注入，将对以下 RPC 方法强制返回 internal_error：{Methods}",
                string.Join(", ", _forceInternalErrorMethods.OrderBy(static method => method, StringComparer.Ordinal)));
        }
    }

    /// <summary>
    /// 从环境变量解析测试故障注入配置。
    /// </summary>
    public static RpcTestFaultInjectionPolicy Resolve(ILogger<RpcTestFaultInjectionPolicy>? logger = null)
    {
        return new RpcTestFaultInjectionPolicy(
            ParseConfiguredMethods(Environment.GetEnvironmentVariable(ForceInternalErrorMethodsEnvironmentVariable)),
            logger);
    }

    /// <summary>
    /// 判断指定方法是否需要强制返回 <c>internal_error</c>。
    /// </summary>
    public bool ShouldForceInternalError(string? method)
    {
        return !string.IsNullOrWhiteSpace(method) && _forceInternalErrorMethods.Contains(method);
    }

    internal static IReadOnlyList<string> ParseConfiguredMethods(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        return raw
            .Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(method => !string.IsNullOrWhiteSpace(method))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }
}
