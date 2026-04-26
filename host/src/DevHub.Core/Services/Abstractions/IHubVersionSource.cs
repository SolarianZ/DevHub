namespace DevHub.Core.Services.Abstractions;

/// <summary>
/// 提供当前 Hub 运行时版本字符串。
/// </summary>
public interface IHubVersionSource
{
    /// <summary>
    /// 获取当前运行中的 Hub 版本。
    /// </summary>
    string CurrentVersion { get; }
}
