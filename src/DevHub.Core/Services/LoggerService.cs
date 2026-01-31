using Microsoft.Extensions.Logging;
using System;

namespace DevHub.Core.Services;

/// <summary>
/// 日志服务实现类
/// 封装 Microsoft.Extensions.Logging.ILogger 的日志记录逻辑
/// 提供统一的日志访问方式
/// </summary>
public class LoggerService : ILoggerService
{
    private readonly ILogger<LoggerService> _logger;

    /// <summary>
    /// 构造函数
    /// </summary>
    /// <param name="logger">Microsoft.Extensions.Logging.ILogger 实例</param>
    public LoggerService(ILogger<LoggerService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// 检查指定日志级别是否启用
    /// </summary>
    /// <param name="level">日志级别</param>
    /// <returns>true 表示启用，false 表示未启用</returns>
    public bool IsEnabled(LogLevel level)
    {
        return _logger.IsEnabled(level);
    }

    /// <summary>
    /// 记录跟踪信息
    /// 最详细的日志级别，包含系统内部的详细过程信息
    /// </summary>
    /// <param name="message">日志消息</param>
    /// <param name="args">格式化参数</param>
    public void Trace(string message, params object?[] args)
    {
        if (_logger.IsEnabled(LogLevel.Trace))
        {
            _logger.LogTrace(message, args);
        }
    }

    /// <summary>
    /// 记录调试信息
    /// 仅用于开发和调试过程中的详细信息
    /// </summary>
    /// <param name="message">日志消息</param>
    /// <param name="args">格式化参数</param>
    public void Debug(string message, params object?[] args)
    {
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug(message, args);
        }
    }

    /// <summary>
    /// 记录信息级日志
    /// 记录系统正常运行的关键操作
    /// </summary>
    /// <param name="message">日志消息</param>
    /// <param name="args">格式化参数</param>
    public void Information(string message, params object?[] args)
    {
        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation(message, args);
        }
    }

    /// <summary>
    /// 记录警告级日志
    /// 记录可能导致问题的情况，但不影响系统运行
    /// </summary>
    /// <param name="message">日志消息</param>
    /// <param name="args">格式化参数</param>
    public void Warning(string message, params object?[] args)
    {
        if (_logger.IsEnabled(LogLevel.Warning))
        {
            _logger.LogWarning(message, args);
        }
    }

    /// <summary>
    /// 记录警告级日志（带异常）
    /// 记录可能导致问题的情况，但不影响系统运行，并包含异常信息
    /// </summary>
    /// <param name="ex">异常对象</param>
    /// <param name="message">日志消息</param>
    /// <param name="args">格式化参数</param>
    public void Warning(Exception ex, string message, params object?[] args)
    {
        if (_logger.IsEnabled(LogLevel.Warning))
        {
            _logger.LogWarning(ex, message, args);
        }
    }

    /// <summary>
    /// 记录错误级日志
    /// 记录系统错误和异常
    /// </summary>
    /// <param name="ex">异常对象</param>
    /// <param name="message">日志消息</param>
    /// <param name="args">格式化参数</param>
    public void Error(Exception ex, string message, params object?[] args)
    {
        if (_logger.IsEnabled(LogLevel.Error))
        {
            _logger.LogError(ex, message, args);
        }
    }

    /// <summary>
    /// 记录错误级日志（无异常）
    /// 记录系统错误但无异常抛出的情况
    /// </summary>
    /// <param name="message">日志消息</param>
    /// <param name="args">格式化参数</param>
    public void Error(string message, params object?[] args)
    {
        if (_logger.IsEnabled(LogLevel.Error))
        {
            _logger.LogError(message, args);
        }
    }

    /// <summary>
    /// 记录致命级日志
    /// 记录致命错误，可能导致系统崩溃
    /// </summary>
    /// <param name="ex">异常对象</param>
    /// <param name="message">日志消息</param>
    /// <param name="args">格式化参数</param>
    public void Fatal(Exception ex, string message, params object?[] args)
    {
        if (_logger.IsEnabled(LogLevel.Critical))
        {
            _logger.LogCritical(ex, message, args);
        }
    }

    /// <summary>
    /// 记录致命级日志（无异常）
    /// 记录致命错误但无异常抛出的情况
    /// </summary>
    /// <param name="message">日志消息</param>
    /// <param name="args">格式化参数</param>
    public void Fatal(string message, params object?[] args)
    {
        if (_logger.IsEnabled(LogLevel.Critical))
        {
            _logger.LogCritical(message, args);
        }
    }

    /// <summary>
    /// 开始一个日志范围，用于关联相关的日志记录
    /// </summary>
    /// <typeparam name="TState">范围状态类型</typeparam>
    /// <param name="state">范围状态</param>
    /// <returns>范围的 Disposable 对象</returns>
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull
    {
        return _logger.BeginScope(state);
    }
}
