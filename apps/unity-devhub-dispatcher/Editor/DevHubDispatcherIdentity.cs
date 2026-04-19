using System;
using UnityEditor;

namespace DevHub.Editor
{
    internal sealed class DevHubDispatcherIdentity
    {
        private const string AppIdKey = "DevHub.Dispatcher.AppId";
        private const string InstanceIdKey = "DevHub.Dispatcher.InstanceId";
        private const string InstancePasswordKey = "DevHub.Dispatcher.InstancePassword";
        private const string AppIdSwitch = "-devhubAppId";

        private DevHubDispatcherIdentity(string appId, string instanceId, string instancePassword)
        {
            AppId = appId;
            InstanceId = instanceId;
            InstancePassword = instancePassword;
        }

        public string AppId { get; private set; }

        public string InstanceId { get; private set; }

        public string InstancePassword { get; private set; }

        public static DevHubDispatcherIdentity LoadOrCreate()
        {
            var appIdOverride = TryGetCommandLineAppId();
            var storedAppId = EditorUserSettings.GetConfigValue(AppIdKey);
            var storedInstanceId = EditorUserSettings.GetConfigValue(InstanceIdKey);
            var storedInstancePassword = EditorUserSettings.GetConfigValue(InstancePasswordKey);

            var appId = !string.IsNullOrEmpty(appIdOverride)
                ? appIdOverride
                : IsValidAppId(storedAppId) ? storedAppId : GenerateAppId();

            var instanceId = IsValidInstanceId(storedInstanceId) ? storedInstanceId : GenerateInstanceId();
            var instancePassword = string.IsNullOrWhiteSpace(storedInstancePassword) ? GenerateInstancePassword() : storedInstancePassword;

            var identity = new DevHubDispatcherIdentity(appId, instanceId, instancePassword);
            identity.Save();
            return identity;
        }

        public void Save()
        {
            EditorUserSettings.SetConfigValue(AppIdKey, AppId);
            EditorUserSettings.SetConfigValue(InstanceIdKey, InstanceId);
            EditorUserSettings.SetConfigValue(InstancePasswordKey, InstancePassword);
        }

        private static string TryGetCommandLineAppId()
        {
            var args = Environment.GetCommandLineArgs();
            for (var index = 0; index < args.Length; index++)
            {
                if (!string.Equals(args[index], AppIdSwitch, StringComparison.Ordinal))
                {
                    continue;
                }

                if (index + 1 >= args.Length)
                {
                    DevHubDispatcherLog.Warning("Identity", "忽略缺少值的 -devhubAppId 参数。");
                    return null;
                }

                var candidate = args[index + 1];
                if (IsValidAppId(candidate))
                {
                    return candidate;
                }

                DevHubDispatcherLog.Warning("Identity", "忽略非法 -devhubAppId 参数: " + candidate);
                return null;
            }

            return null;
        }

        private static string GenerateAppId()
        {
            return "unity.editor." + Guid.NewGuid().ToString("N");
        }

        private static string GenerateInstanceId()
        {
            return "unity-editor-" + Guid.NewGuid().ToString("N");
        }

        private static string GenerateInstancePassword()
        {
            return Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        }

        private static bool IsValidAppId(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            if (!IsLowerAlphaNumeric(value[0]))
            {
                return false;
            }

            for (var index = 1; index < value.Length; index++)
            {
                var ch = value[index];
                if (!IsLowerAlphaNumeric(ch) && ch != '.' && ch != '-')
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsValidInstanceId(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > 256)
            {
                return false;
            }

            for (var index = 0; index < value.Length; index++)
            {
                var ch = value[index];
                if (!IsAsciiAlphaNumeric(ch) && ch != '.' && ch != '_' && ch != ':' && ch != '-')
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsLowerAlphaNumeric(char ch)
        {
            return ch >= 'a' && ch <= 'z' || ch >= '0' && ch <= '9';
        }

        private static bool IsAsciiAlphaNumeric(char ch)
        {
            return ch >= 'a' && ch <= 'z' ||
                   ch >= 'A' && ch <= 'Z' ||
                   ch >= '0' && ch <= '9';
        }
    }
}
