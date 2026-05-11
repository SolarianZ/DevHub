using System;

namespace DevHubDispatcher.Editor
{
    internal static class DevHubDispatcherLaunchBinding
    {
        private const string LaunchIdEnvironmentVariableName = "DEVHUB_LAUNCH_ID";

        internal static string ResolveLaunchId()
        {
            return ResolveLaunchId(Environment.GetEnvironmentVariable(LaunchIdEnvironmentVariableName));
        }

        internal static string ResolveLaunchId(string launchId)
        {
            return string.IsNullOrWhiteSpace(launchId) ? null : launchId;
        }
    }
}
