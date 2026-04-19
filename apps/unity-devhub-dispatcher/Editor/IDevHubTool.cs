using Newtonsoft.Json.Linq;

namespace DevHub.Editor
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
}
