using System;

namespace DevHub.Editor
{
    /// <summary>
    /// Dispatcher 当前运行态快照。
    /// </summary>
    public struct DevHubDispatcherStatus
    {
        internal DevHubDispatcherStatus(
            bool hostConnected,
            string appId,
            string instanceId,
            int registeredToolCount,
            string lastError,
            DateTime? lastConnectedAtUtc)
        {
            HostConnected = hostConnected;
            AppId = appId ?? string.Empty;
            InstanceId = instanceId ?? string.Empty;
            RegisteredToolCount = registeredToolCount;
            LastError = lastError ?? string.Empty;
            LastConnectedAtUtc = lastConnectedAtUtc;
        }

        /// <summary>
        /// Dispatcher 是否已经建立可用 Host 连接。
        /// </summary>
        public bool HostConnected { get; private set; }

        /// <summary>
        /// 当前 Unity Editor 作为 Host app 的 appId。
        /// </summary>
        public string AppId { get; private set; }

        /// <summary>
        /// 当前 Unity Editor 注册到 Host 的 instanceId。
        /// </summary>
        public string InstanceId { get; private set; }

        /// <summary>
        /// 当前已注册到 dispatcher 的 Unity 内部 Tool 数量。
        /// </summary>
        public int RegisteredToolCount { get; private set; }

        /// <summary>
        /// 最近一次 dispatcher 运行错误。
        /// </summary>
        public string LastError { get; private set; }

        /// <summary>
        /// 最近一次确认 Host 连接可用的 UTC 时间。
        /// </summary>
        public DateTime? LastConnectedAtUtc { get; private set; }
    }
}
