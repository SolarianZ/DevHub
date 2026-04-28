using System;
using UnityEditor;

namespace DevHubDispatcher.Editor
{
    internal sealed class DevHubDispatcherIdentity
    {
        private const string AppIdKey = "DevHub.Dispatcher.AppId";
        private const string InstanceIdKey = "DevHub.Dispatcher.InstanceId";
        private const string InstancePasswordKey = "DevHub.Dispatcher.InstancePassword";
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
            string storedAppId = EditorUserSettings.GetConfigValue(AppIdKey);
            string storedInstanceId = EditorUserSettings.GetConfigValue(InstanceIdKey);
            string storedInstancePassword = EditorUserSettings.GetConfigValue(InstancePasswordKey);
            DevHubDispatcherAppIdResolution appIdResolution = DevHubDispatcherIdentityLogic.ResolveAppId(
                Environment.GetCommandLineArgs(),
                storedAppId,
                GenerateAppId);

            if (!string.IsNullOrEmpty(appIdResolution.WarningMessage))
            {
                DevHubDispatcherLogger.Warning("Identity", appIdResolution.WarningMessage);
            }

            string appId = appIdResolution.AppId;
            string instanceId = IsValidInstanceId(storedInstanceId) ? storedInstanceId : GenerateInstanceId();
            string instancePassword = string.IsNullOrWhiteSpace(storedInstancePassword) ? GenerateInstancePassword() : storedInstancePassword;

            DevHubDispatcherIdentity identity = new DevHubDispatcherIdentity(appId, instanceId, instancePassword);
            identity.Save();
            return identity;
        }

        public void Save()
        {
            EditorUserSettings.SetConfigValue(AppIdKey, AppId);
            EditorUserSettings.SetConfigValue(InstanceIdKey, InstanceId);
            EditorUserSettings.SetConfigValue(InstancePasswordKey, InstancePassword);
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

        private static bool IsValidInstanceId(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > 256)
            {
                return false;
            }

            for (int index = 0; index < value.Length; index++)
            {
                char ch = value[index];
                if (!IsAsciiAlphaNumeric(ch) && ch != '.' && ch != '_' && ch != ':' && ch != '-')
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsAsciiAlphaNumeric(char ch)
        {
            return ch >= 'a' && ch <= 'z' ||
                   ch >= 'A' && ch <= 'Z' ||
                   ch >= '0' && ch <= '9';
        }
    }
}
