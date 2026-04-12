using UnityEditor;
using UnityEngine;

namespace DevHub.Editor
{
    /// <summary>
    /// Dispatcher 最小运行态窗口。
    /// </summary>
    public sealed class DevHubDispatcherStatusWindow : EditorWindow
    {
        /// <summary>
        /// 打开 dispatcher 状态窗口。
        /// </summary>
        [MenuItem("Window/DevHub/Dispatcher Status")]
        public static void Open()
        {
            GetWindow<DevHubDispatcherStatusWindow>("DevHub Dispatcher");
        }

        private void OnGUI()
        {
            var status = DevHubDispatcher.GetStatus();

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("DevHub Dispatcher", EditorStyles.boldLabel);
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Host 连接", status.HostConnected ? "已连接" : "未连接");
            EditorGUILayout.LabelField("已注册 Tool 数量", status.RegisteredToolCount.ToString());
            EditorGUILayout.LabelField("AppId", string.IsNullOrEmpty(status.AppId) ? "-" : status.AppId);
            EditorGUILayout.LabelField("InstanceId", string.IsNullOrEmpty(status.InstanceId) ? "-" : status.InstanceId);

            var lastConnected = status.LastConnectedAtUtc.HasValue
                ? status.LastConnectedAtUtc.Value.ToString("u")
                : "-";
            EditorGUILayout.LabelField("最近连接时间", lastConnected);

            if (!string.IsNullOrEmpty(status.LastError))
            {
                EditorGUILayout.Space();
                EditorGUILayout.HelpBox(status.LastError, MessageType.Warning);
            }

            EditorGUILayout.Space();
            if (GUILayout.Button("刷新"))
            {
                Repaint();
            }
        }

        private void OnInspectorUpdate()
        {
            Repaint();
        }
    }
}
