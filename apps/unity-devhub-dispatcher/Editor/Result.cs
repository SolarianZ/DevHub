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
}
