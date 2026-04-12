using System;
using Newtonsoft.Json.Linq;

namespace DevHub.Editor
{
    /// <summary>
    /// Dispatcher 操作结果。
    /// </summary>
    public struct Result
    {
        /// <summary>
        /// 操作是否成功。
        /// </summary>
        public bool Success { get; private set; }

        /// <summary>
        /// 操作诊断消息。
        /// </summary>
        public string Message { get; private set; }

        /// <summary>
        /// 创建成功结果。
        /// </summary>
        /// <param name="message">诊断消息。</param>
        /// <returns>成功结果。</returns>
        public static Result Ok(string message)
        {
            return new Result
            {
                Success = true,
                Message = message ?? string.Empty
            };
        }

        /// <summary>
        /// 创建失败结果。
        /// </summary>
        /// <param name="message">诊断消息。</param>
        /// <returns>失败结果。</returns>
        public static Result Fail(string message)
        {
            return new Result
            {
                Success = false,
                Message = message ?? string.Empty
            };
        }
    }

    /// <summary>
    /// Unity 内部 Tool 接入 dispatcher 的最小契约。
    /// </summary>
    public interface IDevHubTool
    {
        /// <summary>
        /// Unity 内部 Tool 的稳定标识；只用于 dispatcher 本地路由，不会成为 Host appId。
        /// </summary>
        string ToolId { get; }

        /// <summary>
        /// 处理 Host 发往当前 Tool 的 request。
        /// </summary>
        /// <param name="method">Host invocation 的方法名。</param>
        /// <param name="payload">信封中的业务载荷。</param>
        /// <returns>返回给 Host 的业务结果；返回 <see langword="null"/> 表示 JSON null。</returns>
        JToken HandleDevHubRequest(string method, JToken payload);

        /// <summary>
        /// 处理 Host 发往当前 Tool 的 notify。
        /// </summary>
        /// <param name="method">Host invocation 的方法名。</param>
        /// <param name="payload">信封中的业务载荷。</param>
        void HandleDevHubNotify(string method, JToken payload);
    }

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
