using DevHub.Sdk.Models;
using Newtonsoft.Json.Linq;

namespace DevHubDispatcher.Editor
{
    internal static class DevHubDispatcherAppContractFactory
    {
        internal static AppDefinition BuildAppDefinition(string appId, string projectName, string projectPath, string unityEditorPath)
        {
            return new AppDefinition
            {
                AppId = appId,
                Scope = string.Empty,
                DisplayName = "Unity Editor - " + projectName,
                Description = "DevHub dispatcher for Unity project " + projectName,
                Capabilities = new AppCapabilities
                {
                    Rpc = true
                },
                Launch = new LaunchConfiguration
                {
                    ExePath = unityEditorPath,
                    WorkingDirectory = projectPath,
                    ArgsTemplate = "-projectPath " + QuoteArgument(projectPath) + " -devhubAppId {appId}",
                    DedupeKeyTemplate = "{appId}:{scopeOrGlobal}"
                }
            };
        }

        internal static AppInstanceRegistration BuildAppInstanceRegistration(
            string instanceId,
            string appId,
            string projectPath,
            string unityVersion,
            string unityEditorPath,
            int processId)
        {
            return new AppInstanceRegistration
            {
                InstanceId = instanceId,
                AppId = appId,
                Scope = string.Empty,
                Pid = processId,
                Invoke = new InvokeCapability
                {
                    Poll = true,
                    Respond = true
                },
                Meta = new JObject
                {
                    ["projectPath"] = projectPath,
                    ["unityVersion"] = unityVersion,
                    ["unityEditorPath"] = unityEditorPath,
                    ["dispatcherPackage"] = "devhub.dispatcher",
                    ["dispatcherRole"] = "unity-editor"
                }
            };
        }

        private static string QuoteArgument(string value)
        {
            return "\"" + (value ?? string.Empty).Replace("\"", "\\\"") + "\"";
        }
    }
}
