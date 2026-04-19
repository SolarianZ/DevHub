using System;

namespace DevHub.Editor
{
    internal static class DevHubDispatcherLog
    {
        private static readonly IDevHubDispatcherLogger Logger = new UnityDevHubDispatcherLogger();

        public static void Info(string category, string message)
        {
            Logger.Log(DevHubDispatcherLogLevel.Info, category, message, null);
        }

        public static void Warning(string category, string message, Exception exception = null)
        {
            Logger.Log(DevHubDispatcherLogLevel.Warning, category, message, exception);
        }

        public static void Error(string category, string message, Exception exception = null)
        {
            Logger.Log(DevHubDispatcherLogLevel.Error, category, message, exception);
        }
    }
}
