using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace DevHub.Core.Services.Invocation;

/// <summary>
/// request 调用等待器，负责挂起与完成结果传递。
/// </summary>
public class InvocationRequestWaiter
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<InvocationRequestCompletion>> _waiters = new();
    private readonly ILogger<InvocationRequestWaiter> _logger;

    /// <summary>
    /// 初始化 request 等待器。
    /// </summary>
    /// <param name="logger">日志记录器。</param>
    public InvocationRequestWaiter(ILogger<InvocationRequestWaiter> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// 注册等待句柄。
    /// </summary>
    /// <param name="invocationId">调用 ID。</param>
    public Task<InvocationRequestCompletion> Register(string invocationId)
    {
        var tcs = new TaskCompletionSource<InvocationRequestCompletion>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_waiters.TryAdd(invocationId, tcs))
        {
            throw new InvalidOperationException($"Invocation waiter 已存在: {invocationId}");
        }

        return tcs.Task;
    }

    /// <summary>
    /// 完成成功结果。
    /// </summary>
    public bool CompleteSuccess(string invocationId, object? value)
    {
        return Complete(invocationId, new InvocationRequestCompletion
        {
            Kind = InvocationRequestCompletionKind.Success,
            Value = value
        });
    }

    /// <summary>
    /// 完成失败结果（callee error）。
    /// </summary>
    public bool CompleteFailure(string invocationId, object? calleeError)
    {
        return Complete(invocationId, new InvocationRequestCompletion
        {
            Kind = InvocationRequestCompletionKind.Failed,
            CalleeError = calleeError
        });
    }

    /// <summary>
    /// 完成超时结果。
    /// </summary>
    public bool CompleteTimeout(string invocationId, int elapsedMs)
    {
        return Complete(invocationId, new InvocationRequestCompletion
        {
            Kind = InvocationRequestCompletionKind.Timeout,
            ElapsedMs = elapsedMs
        });
    }

    /// <summary>
    /// 完成过期结果。
    /// </summary>
    public bool CompleteExpired(string invocationId, int elapsedMs)
    {
        return Complete(invocationId, new InvocationRequestCompletion
        {
            Kind = InvocationRequestCompletionKind.Expired,
            ElapsedMs = elapsedMs
        });
    }

    /// <summary>
    /// 清理等待句柄。
    /// </summary>
    public bool Cleanup(string invocationId)
    {
        return _waiters.TryRemove(invocationId, out _);
    }

    private bool Complete(string invocationId, InvocationRequestCompletion completion)
    {
        if (!_waiters.TryRemove(invocationId, out var tcs))
        {
            return false;
        }

        var completed = tcs.TrySetResult(completion);
        if (!completed)
        {
            _logger.LogWarning("Invocation waiter 完成失败，InvocationId: {InvocationId}, CompletionKind: {CompletionKind}", invocationId, completion.Kind);
        }

        return completed;
    }
}

/// <summary>
/// request 完成结果。
/// </summary>
public class InvocationRequestCompletion
{
    /// <summary>
    /// 完成类型。
    /// </summary>
    public InvocationRequestCompletionKind Kind { get; set; }

    /// <summary>
    /// 成功值。
    /// </summary>
    public object? Value { get; set; }

    /// <summary>
    /// 被调用方错误。
    /// </summary>
    public object? CalleeError { get; set; }

    /// <summary>
    /// 已耗时毫秒。
    /// </summary>
    public int? ElapsedMs { get; set; }
}

/// <summary>
/// request 完成类型。
/// </summary>
public enum InvocationRequestCompletionKind
{
    /// <summary>
    /// 成功。
    /// </summary>
    Success,

    /// <summary>
    /// 被调用方错误。
    /// </summary>
    Failed,

    /// <summary>
    /// 等待超时。
    /// </summary>
    Timeout,

    /// <summary>
    /// TTL 过期。
    /// </summary>
    Expired
}
