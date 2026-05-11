using System;
using UnityEditor;

namespace DevHubDispatcher.Editor
{
    [InitializeOnLoad]
    internal static class DevHubBuiltInToolBootstrap
    {
        private static readonly IDevHubTool[] BuiltInTools =
        {
            new ExecuteMenuItemTool(),
            new ExecuteMethodTool(),
            new GetDataPathTool(),
            new FocusUnityEditorTool()
        };

        static DevHubBuiltInToolBootstrap()
        {
            RegisterBuiltInTools();
        }

        static void RegisterBuiltInTools()
        {
            for (int index = 0; index < BuiltInTools.Length; index++)
            {
                Result result = DevHubDispatcher.RegisterTool(BuiltInTools[index]);
                if (!result.Success && !string.Equals(result.Message, "该 Tool 实例已经注册。", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("内置 Tool 注册失败: " + BuiltInTools[index].ToolId + "。原因: " + result.Message);
                }
            }
        }
    }
}