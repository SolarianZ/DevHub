using System;
using System.Text.RegularExpressions;

namespace DevHubDispatcher.Editor
{
    internal static class DevHubDispatcherIdentityLogic
    {
        private static readonly Regex CanonicalAppIdPattern = new Regex(
            "^[A-Za-z0-9_](?:[A-Za-z0-9_.-]*[A-Za-z0-9_])?$",
            RegexOptions.Compiled);

        internal static DevHubDispatcherAppIdResolution ResolveAppId(string[] commandLineArgs, string storedAppId, Func<string> generateAppId)
        {
            if (generateAppId == null)
            {
                throw new ArgumentNullException(nameof(generateAppId));
            }

            string warningMessage;
            string appIdOverride = TryGetCommandLineAppIdOverride(commandLineArgs, out warningMessage);
            string appId = !string.IsNullOrEmpty(appIdOverride)
                ? appIdOverride
                : IsValidAppId(storedAppId) ? storedAppId : generateAppId();

            return new DevHubDispatcherAppIdResolution(appId, warningMessage);
        }

        internal static string TryGetCommandLineAppIdOverride(string[] commandLineArgs, out string warningMessage)
        {
            const string appIdSwitch = "-devhubAppId";

            warningMessage = null;

            string[] args = commandLineArgs ?? Array.Empty<string>();
            for (int index = 0; index < args.Length; index++)
            {
                if (!string.Equals(args[index], appIdSwitch, StringComparison.Ordinal))
                {
                    continue;
                }

                if (index + 1 >= args.Length)
                {
                    warningMessage = "忽略缺少值的 -devhubAppId 参数。";
                    return null;
                }

                string candidate = args[index + 1];
                if (IsValidAppId(candidate))
                {
                    return candidate;
                }

                warningMessage = "忽略非法 -devhubAppId 参数: " + candidate;
                return null;
            }

            return null;
        }

        internal static bool IsValidAppId(string value)
        {
            return !string.IsNullOrEmpty(value) && CanonicalAppIdPattern.IsMatch(value);
        }
    }

    internal sealed class DevHubDispatcherAppIdResolution
    {
        internal DevHubDispatcherAppIdResolution(string appId, string warningMessage)
        {
            if (string.IsNullOrEmpty(appId))
            {
                throw new ArgumentException("appId 不能为空。", nameof(appId));
            }

            AppId = appId;
            WarningMessage = warningMessage;
        }

        public string AppId { get; private set; }

        public string WarningMessage { get; private set; }
    }
}
