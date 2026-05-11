using System;
using System.Runtime.InteropServices;
using System.Threading;
using Newtonsoft.Json.Linq;
using UDebug = UnityEngine.Debug;
#if UNITY_EDITOR_WIN
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
#endif

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
        // 仅判断一次 active 状态不足以覆盖 Dock 最小化窗口的恢复过程，这里留出短暂轮询窗口。
        private const int ActivationWaitRetryCount = 20;
        private const int ActivationWaitDelayMs = 20;
        private const ulong NSApplicationActivateIgnoringOtherApps = 1;

        [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_getClass")]
        private static extern IntPtr objc_getClass(string className);

        [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "sel_registerName")]
        private static extern IntPtr sel_registerName(string selectorName);

        [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
        private static extern IntPtr IntPtr_objc_msgSend(IntPtr receiver, IntPtr selector);

        [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool bool_objc_msgSend(IntPtr receiver, IntPtr selector);

        [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
        private static extern void void_objc_msgSend_bool(
            IntPtr receiver,
            IntPtr selector,
            [MarshalAs(UnmanagedType.I1)] bool arg1);

        [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool bool_objc_msgSend_UInt64(IntPtr receiver, IntPtr selector, ulong arg1);

        [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
        private static extern IntPtr IntPtr_objc_msgSend_UInt64(IntPtr receiver, IntPtr selector, ulong arg1);

        [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
        private static extern ulong UInt64_objc_msgSend(IntPtr receiver, IntPtr selector);

        [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
        private static extern void void_objc_msgSend_IntPtr(IntPtr receiver, IntPtr selector, IntPtr arg1);


        public static bool Execute()
        {
            try
            {
                var nsApplicationClass = objc_getClass("NSApplication");
                if (nsApplicationClass == IntPtr.Zero)
                {
                    UDebug.LogError("Failed to resolve NSApplication class.");
                    return false;
                }

                var sharedApplicationSelector = sel_registerName("sharedApplication");
                var sharedApplication = IntPtr_objc_msgSend(nsApplicationClass, sharedApplicationSelector);
                if (sharedApplication == IntPtr.Zero)
                {
                    UDebug.LogError("Failed to resolve the shared NSApplication instance.");
                    return false;
                }

                var currentApplication = GetCurrentApplication();
                PrepareApplicationForForeground(sharedApplication, currentApplication);

                // 仅激活应用不足以保证 Dock 最小化窗口立刻恢复到可交互状态，因此激活后仍需等待窗口真正回到前台。
                var activateIgnoringOtherAppsSelector = sel_registerName("activateIgnoringOtherApps:");
                void_objc_msgSend_bool(sharedApplication, activateIgnoringOtherAppsSelector, true);
                if (WaitUntilForegroundReady(sharedApplication))
                    return true;

                var activated = currentApplication != IntPtr.Zero && ActivateRunningApplication(currentApplication);
                if (activated || WaitUntilForegroundReady(sharedApplication))
                    return true;

                UDebug.LogError(
                    "Failed to activate the current Unity Editor application on macOS after restore and foreground activation attempts.");
                return false;
            }
            catch (Exception ex)
            {
                UDebug.LogError($"Failed to focus the Unity Editor application on macOS. {ex}");
                return false;
            }
        }

        private static IntPtr GetCurrentApplication()
        {
            var nsRunningApplicationClass = objc_getClass("NSRunningApplication");
            if (nsRunningApplicationClass == IntPtr.Zero)
            {
                UDebug.LogError("Failed to resolve NSRunningApplication class.");
                return IntPtr.Zero;
            }

            var currentApplicationSelector = sel_registerName("currentApplication");
            var currentApplication = IntPtr_objc_msgSend(nsRunningApplicationClass, currentApplicationSelector);
            if (currentApplication == IntPtr.Zero)
            {
                UDebug.LogError("Failed to resolve the current NSRunningApplication instance.");
                return IntPtr.Zero;
            }

            return currentApplication;
        }

        private static void PrepareApplicationForForeground(IntPtr sharedApplication, IntPtr currentApplication)
        {
            if (currentApplication != IntPtr.Zero)
                bool_objc_msgSend(currentApplication, sel_registerName("unhide"));

            RestoreMiniaturizedWindows(sharedApplication);
        }

        private static void RestoreMiniaturizedWindows(IntPtr sharedApplication)
        {
            var windowsSelector = sel_registerName("windows");
            var windows = IntPtr_objc_msgSend(sharedApplication, windowsSelector);
            if (windows == IntPtr.Zero)
                return;

            var countSelector = sel_registerName("count");
            var windowCount = UInt64_objc_msgSend(windows, countSelector);
            if (windowCount == 0)
                return;

            var objectAtIndexSelector = sel_registerName("objectAtIndex:");
            var isMiniaturizedSelector = sel_registerName("isMiniaturized");
            var deminiaturizeSelector = sel_registerName("deminiaturize:");
            var arrangeInFrontSelector = sel_registerName("arrangeInFront:");
            var makeKeyAndOrderFrontSelector = sel_registerName("makeKeyAndOrderFront:");
            var frontWindow = IntPtr.Zero;

            for (ulong index = 0; index < windowCount; index++)
            {
                var window = IntPtr_objc_msgSend_UInt64(windows, objectAtIndexSelector, index);
                if (window == IntPtr.Zero)
                    continue;

                if (frontWindow == IntPtr.Zero)
                    frontWindow = window;

                if (bool_objc_msgSend(window, isMiniaturizedSelector))
                    // 最小化到 Dock 的窗口仅激活应用不会自动恢复，必须逐个窗口执行 deminiaturize。
                    void_objc_msgSend_IntPtr(window, deminiaturizeSelector, IntPtr.Zero);
            }

            void_objc_msgSend_IntPtr(sharedApplication, arrangeInFrontSelector, IntPtr.Zero);
            if (frontWindow != IntPtr.Zero)
                void_objc_msgSend_IntPtr(frontWindow, makeKeyAndOrderFrontSelector, IntPtr.Zero);
        }

        private static bool ActivateRunningApplication(IntPtr currentApplication)
        {
            var activateWithOptionsSelector = sel_registerName("activateWithOptions:");
            return bool_objc_msgSend_UInt64(
                currentApplication,
                activateWithOptionsSelector,
                NSApplicationActivateIgnoringOtherApps);
        }

        private static bool WaitUntilForegroundReady(IntPtr sharedApplication)
        {
            var isActiveSelector = sel_registerName("isActive");
            var keyWindowSelector = sel_registerName("keyWindow");
            for (var attempt = 0; attempt < ActivationWaitRetryCount; attempt++)
            {
                // 仅 isActive 可能发生在窗口还没从 Dock 最小化状态恢复之前，因此需要同时确认 keyWindow/可见窗口。
                if (bool_objc_msgSend(sharedApplication, isActiveSelector))
                {
                    var keyWindow = IntPtr_objc_msgSend(sharedApplication, keyWindowSelector);
                    if (keyWindow != IntPtr.Zero)
                        return true;
                }

                if (HasVisibleWindow(sharedApplication))
                    return true;

                Thread.Sleep(ActivationWaitDelayMs);
            }

            return false;
        }

        private static bool HasVisibleWindow(IntPtr sharedApplication)
        {
            var windowsSelector = sel_registerName("windows");
            var windows = IntPtr_objc_msgSend(sharedApplication, windowsSelector);
            if (windows == IntPtr.Zero)
                return false;

            var countSelector = sel_registerName("count");
            var windowCount = UInt64_objc_msgSend(windows, countSelector);
            if (windowCount == 0)
                return false;

            var objectAtIndexSelector = sel_registerName("objectAtIndex:");
            var isMiniaturizedSelector = sel_registerName("isMiniaturized");
            var isVisibleSelector = sel_registerName("isVisible");

            for (ulong index = 0; index < windowCount; index++)
            {
                var window = IntPtr_objc_msgSend_UInt64(windows, objectAtIndexSelector, index);
                if (window == IntPtr.Zero)
                    continue;

                if (!bool_objc_msgSend(window, isMiniaturizedSelector) && bool_objc_msgSend(window, isVisibleSelector))
                    return true;
            }

            return false;
        }
    }
#endif
}