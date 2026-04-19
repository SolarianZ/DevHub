namespace DevHub.Editor
{
    /// <summary>
    /// Tool 通过 dispatcher 主动发送消息时使用的可选目标与调用选项。
    /// </summary>
    public sealed class DevHubDispatcherSendOptions
    {
        /// <summary>
        /// 目标作用域。
        /// </summary>
        public string Scope { get; set; }

        /// <summary>
        /// 目标实例标识。
        /// </summary>
        public string InstanceId { get; set; }

        /// <summary>
        /// 调用生存时间，单位毫秒。
        /// </summary>
        public int? TtlMs { get; set; }

        /// <summary>
        /// request 等待超时，单位毫秒。
        /// </summary>
        public int? WaitTimeoutMs { get; set; }

        /// <summary>
        /// 目标离线时是否允许入队。
        /// </summary>
        public bool? QueueIfOffline { get; set; }

        /// <summary>
        /// 目标离线时是否允许 Host 自动拉起。
        /// </summary>
        public bool? AutoLaunch { get; set; }
    }
}
