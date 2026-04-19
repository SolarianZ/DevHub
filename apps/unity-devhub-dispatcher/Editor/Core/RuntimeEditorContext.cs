using System.Diagnostics;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace DevHubDispatcher.Editor
{
    internal sealed class RuntimeEditorContext
    {
        public static RuntimeEditorContext Capture()
        {
            string projectPath = ResolveProjectPath();
            return new RuntimeEditorContext
            {
                ProjectPath = projectPath,
                ProjectName = new DirectoryInfo(projectPath).Name,
                UnityEditorPath = EditorApplication.applicationPath,
                UnityVersion = Application.unityVersion,
                ProcessId = Process.GetCurrentProcess().Id
            };
        }

        public string ProjectPath { get; private set; }
        public string ProjectName { get; private set; }
        public string UnityEditorPath { get; private set; }
        public string UnityVersion { get; private set; }
        public int ProcessId { get; private set; }

        private static string ResolveProjectPath()
        {
            DirectoryInfo assetsDirectory = new DirectoryInfo(Application.dataPath);
            return assetsDirectory.Parent == null ? assetsDirectory.FullName : assetsDirectory.Parent.FullName;
        }
    }
}