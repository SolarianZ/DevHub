using System;
using UnityEngine;

namespace DevHubDispatcher.Editor
{
    public interface IDevHubDispatcherLogger
    {
        void Log(LogType level, string category, string message, Exception exception);
    }

    public static class DevHubDispatcherLogger
    {
        private static IDevHubDispatcherLogger _logger;
        public static IDevHubDispatcherLogger Logger
        {
            get
            {
                _logger = _logger ?? new UnityLogger();
                return _logger;
            }
            set => _logger = value;
        }


        internal static void Info(string category, string message)
        {
            Logger.Log(LogType.Log, category, message, null);
        }

        internal static void Warning(string category, string message, Exception exception = null)
        {
            Logger.Log(LogType.Warning, category, message, exception);
        }

        internal static void Error(string category, string message, Exception exception = null)
        {
            Logger.Log(LogType.Error, category, message, exception);
        }
    }

    internal sealed class UnityLogger : IDevHubDispatcherLogger
    {
        public void Log(LogType level, string category, string message, Exception exception)
        {
            string formattedMessage = "[DevHubDispatcher][" + level + "][" + (category ?? "General") + "] " + (message ?? string.Empty);
            if (exception != null)
            {
                formattedMessage += " Exception: " + exception;
            }

            switch (level)
            {
                case LogType.Log:
                    Debug.Log(formattedMessage);
                    break;
                case LogType.Warning:
                    Debug.LogWarning(formattedMessage);
                    break;
                case LogType.Assert:
                case LogType.Error:
                case LogType.Exception:
                    Debug.LogError(formattedMessage);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(level), level, null);
            }
        }
    }
}