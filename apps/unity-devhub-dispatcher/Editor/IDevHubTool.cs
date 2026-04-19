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

    internal class DevHubToolDelegate : IDevHubTool
    {
        public string ToolId { get; }

        private readonly Func<JToken, JToken> _handleDevHubRequest;
        private readonly Action<JToken> _handleDevHubNotify;


        public DevHubToolDelegate(string toolId, Func<JToken, JToken> handleDevHubRequest, Action<JToken> handleDevHubNotify)
        {
            if (string.IsNullOrEmpty(toolId))
                throw new ArgumentNullException(nameof(toolId));

            if (handleDevHubRequest == null && handleDevHubNotify == null)
                throw new ArgumentException($"{nameof(handleDevHubRequest)}和{nameof(handleDevHubNotify)}不能同时为null");

            ToolId = toolId;
            _handleDevHubRequest = handleDevHubRequest;
            _handleDevHubNotify = handleDevHubNotify;
        }

        public DevHubToolDelegate(string toolId, Func<JToken, JToken> handleDevHubRequest)
            : this(toolId, handleDevHubRequest, null)
        {
        }

        public DevHubToolDelegate(string toolId, Action<JToken> devHubRequestHandler)
            : this(toolId, null, devHubRequestHandler)
        {
        }

        /// <inheritdoc />
        public JToken HandleDevHubRequest(string method, JToken payload)
        {
            throw new System.NotImplementedException();
        }

        /// <inheritdoc />
        public void HandleDevHubNotify(string method, JToken payload)
        {
            throw new System.NotImplementedException();
        }
    }
}