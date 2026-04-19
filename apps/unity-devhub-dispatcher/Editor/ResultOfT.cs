namespace DevHub.Editor
{
    /// <summary>
    /// 带返回值的 Dispatcher 操作结果。
    /// </summary>
    /// <typeparam name="T">返回值类型。</typeparam>
    public struct Result<T>
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
        /// 操作返回值。
        /// </summary>
        public T Value { get; private set; }

        /// <summary>
        /// 创建成功结果。
        /// </summary>
        /// <param name="value">返回值。</param>
        /// <param name="message">诊断消息。</param>
        /// <returns>成功结果。</returns>
        public static Result<T> Ok(T value, string message)
        {
            return new Result<T>
            {
                Success = true,
                Message = message ?? string.Empty,
                Value = value
            };
        }

        /// <summary>
        /// 创建失败结果。
        /// </summary>
        /// <param name="message">诊断消息。</param>
        /// <returns>失败结果。</returns>
        public static Result<T> Fail(string message)
        {
            return new Result<T>
            {
                Success = false,
                Message = message ?? string.Empty,
                Value = default(T)
            };
        }
    }
}
