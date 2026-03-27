using System.Globalization;
using Microsoft.Extensions.Logging;

namespace DevHub.Core.Services.Rpc;

/// <summary>
/// 仅供测试使用的 RPC 故障注入策略。
/// </summary>
public sealed class RpcTestFaultInjectionPolicy
{
    /// <summary>
    /// 通过请求 ID 强制返回 <c>internal_error</c> 的环境变量。
    /// </summary>
    public const string ForceInternalErrorRequestIdsEnvironmentVariable = "DEVHUB_TEST_RPC_FORCE_INTERNAL_ERROR_REQUEST_IDS";

    private readonly HashSet<string> _forceInternalErrorRequestIds;

    internal RpcTestFaultInjectionPolicy(IEnumerable<string> forceInternalErrorRequestIds, ILogger<RpcTestFaultInjectionPolicy>? logger = null)
    {
        _forceInternalErrorRequestIds = forceInternalErrorRequestIds
            .Where(requestId => !string.IsNullOrWhiteSpace(requestId))
            .Select(requestId => requestId.Trim())
            .ToHashSet(StringComparer.Ordinal);

        if (_forceInternalErrorRequestIds.Count > 0)
        {
            logger?.LogWarning(
                "已启用测试故障注入，将对以下 RequestId 强制返回 internal_error：{RequestIds}",
                string.Join(", ", _forceInternalErrorRequestIds.OrderBy(static requestId => requestId, StringComparer.Ordinal)));
        }
    }

    /// <summary>
    /// 从环境变量解析测试故障注入配置。
    /// </summary>
    public static RpcTestFaultInjectionPolicy Resolve(ILogger<RpcTestFaultInjectionPolicy>? logger = null)
    {
        return new RpcTestFaultInjectionPolicy(
            ParseConfiguredRequestIds(Environment.GetEnvironmentVariable(ForceInternalErrorRequestIdsEnvironmentVariable)),
            logger);
    }

    /// <summary>
    /// 判断指定请求 ID 是否需要强制返回 <c>internal_error</c>。
    /// </summary>
    public bool ShouldForceInternalError(object? requestId)
    {
        return TryNormalizeRequestId(requestId, out var normalizedRequestId)
            && _forceInternalErrorRequestIds.Contains(normalizedRequestId);
    }

    internal static IReadOnlyList<string> ParseConfiguredRequestIds(string? raw)
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

    internal static bool TryNormalizeRequestId(object? requestId, out string normalizedRequestId)
    {
        switch (requestId)
        {
            case null:
                normalizedRequestId = string.Empty;
                return false;
            case string text:
                normalizedRequestId = text.Trim();
                return normalizedRequestId.Length > 0;
            case bool:
                normalizedRequestId = string.Empty;
                return false;
            default:
                var converted = Convert.ToString(requestId, CultureInfo.InvariantCulture);
                if (string.IsNullOrWhiteSpace(converted))
                {
                    normalizedRequestId = string.Empty;
                    return false;
                }

                normalizedRequestId = converted.Trim();
                return true;
        }
    }
}
