using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Newtonsoft.Json.Linq;
using UDebug = UnityEngine.Debug;

namespace DevHubDispatcher.Editor
{
    internal sealed class FocusUnityEditorTool : DevHubBuiltInToolBase
    {
        public const string FixedToolId = "focus-unity-editor";

        /// <inheritdoc />
        public FocusUnityEditorTool() : base(FixedToolId) { }

        /// <inheritdoc />
        protected override object Execute(JToken payload)
        {
#if UNITY_EDITOR_WIN
            return FocusUnityEditorExecutorWIN.Execute();
#elif UNITY_EDITOR_OSX
            return FocusUnityEditorExecutorOSX.Execute();
#else
            UDebug.LogError($"{FixedToolId} is not supported on this platform.");
            return false;
#endif
        }
    }

#if UNITY_EDITOR_WIN
    static class FocusUnityEditorExecutorWIN
    {
        public const string UnityEditorWindowTitlePattern = @" - Unity \d{4}\.\d\.\d{2}(?!\d)";


        #region user32.dll

        private const int SW_RESTORE = 9;

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int maxCount);

        [DllImport("user32.dll")]
        private static extern int GetWindowTextLength(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool BringWindowToTop(IntPtr hWnd);
        // private static extern bool BringToFront(IntPtr hWnd);


        public static IntPtr GetUnityEditorHWnd()
        {
            int currentProcessId = Process.GetCurrentProcess().Id;
            IntPtr targetHWnd = IntPtr.Zero;
            EnumWindows((hWnd, _) =>
            {
                GetWindowThreadProcessId(hWnd, out uint windowProcessId);
                if (windowProcessId != currentProcessId)
                    return true;
                string windowTitle = GetWindowTitle(hWnd);
                if (!Regex.IsMatch(windowTitle, UnityEditorWindowTitlePattern))
                    return true;

                targetHWnd = hWnd;
                return false; // 找到目标窗口，停止枚举
            }, IntPtr.Zero);

            return targetHWnd;
        }

        private static string GetWindowTitle(IntPtr hWnd)
        {
            int length = GetWindowTextLength(hWnd);
            if (length <= 0)
                return string.Empty;

            StringBuilder builder = new StringBuilder(length + 1);
            GetWindowText(hWnd, builder, builder.Capacity);
            return builder.ToString();
        }

        #endregion


        public static bool Execute()
        {
            // IntPtr hWnd = Process.GetCurrentProcess().MainWindowHandle; // 总是返回0，不可用
            IntPtr hWnd = GetUnityEditorHWnd();
            if (hWnd == IntPtr.Zero)
            {
                UDebug.LogError("Failed to get the main window handle of the Unity Editor process.");
                return false;
            }

            if (IsIconic(hWnd)) // 恢复最小化的窗口
                ShowWindow(hWnd, SW_RESTORE);

            BringWindowToTop(hWnd);
            Thread.Sleep(5); // 如果Unity Editor没最小化，不sleep的话就得调用2次此工具才能让窗口获得焦点
            return SetForegroundWindow(hWnd);
        }
    }
#endif
#if UNITY_EDITOR_OSX
    internal sealed class FocusUnityEditorExecutorOSX
    {
        // TODO
        public static bool Execute()
        {
            throw new System.NotImplementedException();
        }
    }
#endif
}