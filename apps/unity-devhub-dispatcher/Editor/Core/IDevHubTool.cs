using System;
using Newtonsoft.Json.Linq;

namespace DevHubDispatcher.Editor
{
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
    /// 处理 Host 发往当前 Tool 的 request。
    /// </summary>
    /// <param name="method">Host invocation 的方法名。</param>
    /// <param name="payload">信封中的业务载荷。</param>
    /// <returns>返回给 Host 的业务结果；返回 <see langword="null"/> 表示 JSON null。</returns>
    public delegate JToken DevHubRequestHandler(string method, JToken payload);

    /// <summary>
    /// 处理 Host 发往当前 Tool 的 notify。
    /// </summary>
    /// <param name="method">Host invocation 的方法名。</param>
    /// <param name="payload">信封中的业务载荷。</param>
    /// <remarks>最终契约使用 <see langword="void"/> 语义；dispatcher 不为早期未实现的占位签名保留兼容层。</remarks>
    public delegate void DevHubNotifyHandler(string method, JToken payload);


    internal sealed class DevHubToolDelegate : IDevHubTool
    {
        public string ToolId { get; }

        private readonly DevHubRequestHandler _requestHandler;
        private readonly DevHubNotifyHandler _notifyHandler;


        public DevHubToolDelegate(string toolId, DevHubRequestHandler requestHandler, DevHubNotifyHandler notifyHandler)
        {
            if (string.IsNullOrWhiteSpace(toolId))
                throw new ArgumentException("toolId 不能为空。", nameof(toolId));

            if (requestHandler == null && notifyHandler == null)
                throw new ArgumentException($"{nameof(requestHandler)} 和 {nameof(notifyHandler)} 不能同时为 null。");

            ToolId = toolId;
            _requestHandler = requestHandler;
            _notifyHandler = notifyHandler;
        }

        public DevHubToolDelegate(string toolId, DevHubRequestHandler requestHandler) : this(toolId, requestHandler, null) { }
        public DevHubToolDelegate(string toolId, DevHubNotifyHandler notifyHandler) : this(toolId, null, notifyHandler) { }

        /// <inheritdoc />
        public JToken HandleDevHubRequest(string method, JToken payload)
        {
            if (string.IsNullOrWhiteSpace(method))
                throw new ArgumentException("method 不能为空。", nameof(method));

            if (_requestHandler == null)
                throw new InvalidOperationException($"Tool {ToolId} 不支持处理 request。");

            return _requestHandler(method, payload);
        }

        /// <inheritdoc />
        public void HandleDevHubNotify(string method, JToken payload)
        {
            if (string.IsNullOrWhiteSpace(method))
                throw new ArgumentException("method 不能为空。", nameof(method));

            if (_notifyHandler == null)
                throw new InvalidOperationException($"Tool {ToolId} 不支持处理 notify。");

            _notifyHandler(method, payload);
        }
    }
}
