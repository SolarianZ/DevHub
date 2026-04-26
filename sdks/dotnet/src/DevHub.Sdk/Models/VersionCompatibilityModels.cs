namespace DevHub.Sdk.Models;

/// <summary>
/// SDK 与 Host 的版本兼容状态。
/// </summary>
public enum VersionCompatibilityStatus
{
    /// <summary>
    /// 无法判断兼容性。
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// 版本兼容。
    /// </summary>
    Compatible = 1,

    /// <summary>
    /// 建议更新以保持主次版本一致。
    /// </summary>
    UpdateRecommended = 2,

    /// <summary>
    /// 版本不兼容。
    /// </summary>
    Incompatible = 3
}

/// <summary>
/// SDK 与 Host 的版本兼容检查结果。
/// </summary>
public sealed class VersionCompatibilityResult
{
    /// <summary>
    /// 当前 SDK 的版本字符串。
    /// </summary>
    public string? SdkVersion { get; set; }

    /// <summary>
    /// Host 的版本字符串。
    /// 当 Host 不支持 <c>hub.getVersion</c> 且发现文件缺少可用回退值时，可能为 <see langword="null"/>。
    /// </summary>
    public string? HostVersion { get; set; }

    /// <summary>
    /// 兼容状态。
    /// </summary>
    public VersionCompatibilityStatus Status { get; set; }
}
