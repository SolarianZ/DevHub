using System;
using UnityEngine;

namespace DevHub.Editor
{
    internal sealed class UnityDevHubDispatcherLogger : IDevHubDispatcherLogger
    {
        public void Log(DevHubDispatcherLogLevel level, string category, string message, Exception exception)
        {
            var formattedMessage = "[DevHub.Dispatcher][" + level + "][" + (category ?? "General") + "] " + (message ?? string.Empty);
            if (exception != null)
            {
                formattedMessage += " Exception: " + exception;
            }

            switch (level)
            {
                case DevHubDispatcherLogLevel.Info:
                    Debug.Log(formattedMessage);
                    break;
                case DevHubDispatcherLogLevel.Warning:
                    Debug.LogWarning(formattedMessage);
                    break;
                default:
                    Debug.LogError(formattedMessage);
                    break;
            }
        }
    }
}
