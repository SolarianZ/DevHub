using DevHub.Sdk.Models;

namespace DevHub.Sdk.Internal;

internal static class VersionCompatibilityEvaluator
{
    internal static async Task<VersionCompatibilityResult> CheckAsync(
        ReadOnlyRpcExecutor.SendAsyncDelegate sendAsync,
        string? fallbackHostVersion,
        CancellationToken cancellationToken)
    {
        string? hostVersion;
        try
        {
            hostVersion = await ReadOnlyRpcExecutor.GetHostVersionAsync(sendAsync, cancellationToken);
        }
        catch (DevHubRpcException exception) when (exception.Is(DevHubRpcErrorCode.MethodNotFound))
        {
            hostVersion = fallbackHostVersion;
        }

        return Evaluate(SdkVersionSource.CurrentVersion, hostVersion);
    }

    internal static VersionCompatibilityResult Evaluate(string? sdkVersion, string? hostVersion)
    {
        return new VersionCompatibilityResult
        {
            SdkVersion = sdkVersion,
            HostVersion = hostVersion,
            Status = GetStatus(sdkVersion, hostVersion)
        };
    }

    private static VersionCompatibilityStatus GetStatus(string? sdkVersion, string? hostVersion)
    {
        if (!SemanticVersionParser.TryParse(sdkVersion, out var sdk) ||
            !SemanticVersionParser.TryParse(hostVersion, out var host))
        {
            return VersionCompatibilityStatus.Unknown;
        }

        if (sdk.Major != host.Major)
        {
            return VersionCompatibilityStatus.Incompatible;
        }

        if (sdk.Minor != host.Minor)
        {
            return VersionCompatibilityStatus.UpdateRecommended;
        }

        return VersionCompatibilityStatus.Compatible;
    }
}
