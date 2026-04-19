using DevHub.Sdk;
using DevHub.Sdk.Models;

namespace DevHub.Editor
{
    internal sealed class RuntimeConnection
    {
        public RuntimeConnection(DevHubClient client, AppInstance instance, HubRuntimeTuning runtimeTuning)
        {
            Client = client;
            Instance = instance;
            RuntimeTuning = runtimeTuning;
        }

        public DevHubClient Client { get; private set; }

        public AppInstance Instance { get; private set; }

        public HubRuntimeTuning RuntimeTuning { get; private set; }
    }
}
