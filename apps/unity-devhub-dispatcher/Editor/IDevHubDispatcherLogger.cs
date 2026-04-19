using System;

namespace DevHub.Editor
{
    internal interface IDevHubDispatcherLogger
    {
        void Log(DevHubDispatcherLogLevel level, string category, string message, Exception exception);
    }
}
