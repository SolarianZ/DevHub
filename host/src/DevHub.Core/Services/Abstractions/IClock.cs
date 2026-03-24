namespace DevHub.Core.Services.Abstractions;

/// <summary>
/// 系统时钟抽象。
/// </summary>
public interface IClock
{
    /// <summary>
    /// 获取当前 UTC 时间。
    /// </summary>
    DateTime UtcNow { get; }
}

/// <summary>
/// 默认系统时钟实现。
/// </summary>
public sealed class SystemClock : IClock
{
    /// <inheritdoc />
    public DateTime UtcNow => DateTime.UtcNow;
}
