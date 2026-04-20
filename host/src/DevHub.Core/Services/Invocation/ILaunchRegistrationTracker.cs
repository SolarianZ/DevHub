using DevHub.Core.Models;

namespace DevHub.Core.Services.Invocation;

/// <summary>
/// 启动记录与注册回调绑定跟踪器。
/// </summary>
public interface ILaunchRegistrationTracker
{
    /// <summary>
    /// 校验注册请求是否与待完成启动记录匹配。
    /// </summary>
    /// <param name="launchId">注册请求携带的 launchId。</param>
    /// <param name="appId">注册 appId。</param>
    /// <param name="scope">注册 scope。</param>
    /// <returns>校验结果。</returns>
    LaunchRegistrationValidationResult ValidateRegistration(string? launchId, string appId, string? scope);

    /// <summary>
    /// 记录一次成功注册。
    /// </summary>
    /// <param name="launchId">注册请求携带的 launchId。</param>
    /// <param name="instance">已写入注册表的实例快照。</param>
    void RecordSuccessfulRegistration(string? launchId, AppInstance instance);
}

/// <summary>
/// 启动绑定校验结果。
/// </summary>
public sealed record LaunchRegistrationValidationResult(
    LaunchRegistrationValidationStatus Status,
    object? ErrorData = null);

/// <summary>
/// 启动绑定校验状态。
/// </summary>
public enum LaunchRegistrationValidationStatus
{
    NotTracked,
    Matched,
    Mismatched
}

/// <summary>
/// 空实现。
/// </summary>
public sealed class NullLaunchRegistrationTracker : ILaunchRegistrationTracker
{
    /// <summary>
    /// 单例。
    /// </summary>
    public static NullLaunchRegistrationTracker Instance { get; } = new();

    /// <inheritdoc />
    public LaunchRegistrationValidationResult ValidateRegistration(string? launchId, string appId, string? scope)
    {
        return new LaunchRegistrationValidationResult(LaunchRegistrationValidationStatus.NotTracked);
    }

    /// <inheritdoc />
    public void RecordSuccessfulRegistration(string? launchId, AppInstance instance)
    {
    }
}
