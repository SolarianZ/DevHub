using System;
using UnityEditor;

namespace DevHub.Editor
{
    // 仅作为示例，根据实际需求调整
    public struct Result
    {
        public bool Success;
        public string Message;
    }

    // 唯一对外接口
    [InitializeOnLoad]
    public static class DevHubDispatcher
    {
        static DevHubDispatcher()
        {
            AssemblyReloadEvents.beforeAssemblyReload -= OnBeforeAssemblyReload;
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
            AssemblyReloadEvents.afterAssemblyReload -= OnAfterAssemblyReload;
            AssemblyReloadEvents.afterAssemblyReload += OnAfterAssemblyReload;
            
            // 需要向host注册app示例（Unity自身）
        }

        // Unity工程会频繁触发DomainReload，要保证appId等关键数据不会因此改变，可以用 EditorUserSettings 存储和恢复

        private static void OnBeforeAssemblyReload()
        {
            // 可以做一些数据存储
        }

        private static void OnAfterAssemblyReload()
        {
            // 可以做一些数据恢复
        }


        #region 消息

        // 需要提供一个接口，直接向host发送消息
        // 如果需要收到host回信，必须注册工具

        #endregion


        #region Tools

        // Tool注册信息不用在Reload时保存，工具会自行重新注册

        // 仅作为示例，根据实际需求调整
        public static Result RegisterTool(object tool) => throw new NotImplementedException();
        public static Result UnregisterTool(object tool) => throw new NotImplementedException();

        // 收到Host传来的消息时，找到对应的Tool，将消息原样转发给该Tool，如果找不到，打印错误日志
        // 需要规定一个字段，标识要用的Tool（注意不是host规范中的appId，因为注册到host的app始终是Unity本身，这里的Tool是Unity内部的Tool，对host不可见）

        #endregion
    }
}