using Microsoft.Extensions.Logging;
using System;

namespace DevHub.Core.Services;

/// <summary>
/// 统一日志处理接口
/// 封装日志记录逻辑，提供统一的日志访问方式
/// </summary>
public interface ILoggerService
{
    /// <summary>
    /// 检查指定日志级别是否启用
    /// </summary>
    /// <param name="level">日志级别</param>
    /// <returns>true 表示启用，false 表示未启用</returns>
    bool IsEnabled(LogLevel level);

    /// <summary>
    /// 记录跟踪信息
    /// 最详细的日志级别，包含系统内部的详细过程信息
    /// </summary>
    /// <param name="message">日志消息</param>
    /// <param name="args">格式化参数</param>
    void Trace(string message, params object[] args);

    /// <summary>
    /// 记录调试信息
    /// 仅用于开发和调试过程中的详细信息
    /// </summary>
    /// <param name="message">日志消息</param>
    /// <param name="args">格式化参数</param>
    void Debug(string message, params object[] args);

    /// <summary>
    /// 记录信息级日志
    /// 记录系统正常运行的关键操作
    /// </summary>
    /// <param name="message">日志消息</param>
    /// <param name="args">格式化参数</param>
    void Information(string message, params object[] args);

    /// <summary>
    /// 记录警告级日志
    /// 记录可能导致问题的情况，但不影响系统运行
    /// </summary>
    /// <param name="message">日志消息</param>
    /// <param name="args">格式化参数</param>
    void Warning(string message, params object[] args);

    /// <summary>
    /// 记录警告级日志（带异常）
    /// 记录可能导致问题的情况，但不影响系统运行，并包含异常信息
    /// </summary>
    /// <param name="ex">异常对象</param>
    /// <param name="message">日志消息</param>
    /// <param name="args">格式化参数</param>
    void Warning(Exception ex, string message, params object[] args);

    /// <summary>
    /// 记录错误级日志
    /// 记录系统错误和异常
    /// </summary>
    /// <param name="ex">异常对象</param>
    /// <param name="message">日志消息</param>
    /// <param name="args">格式化参数</param>
    void Error(Exception ex, string message, params object[] args);

    /// <summary>
    /// 记录错误级日志（无异常）
    /// 记录系统错误但无异常抛出的情况
    /// </summary>
    /// <param name="message">日志消息</param>
    /// <param name="args">格式化参数</param>
    void Error(string message, params object[] args);

    /// <summary>
    /// 记录致命级日志
    /// 记录致命错误，可能导致系统崩溃
    /// </summary>
    /// <param name="ex">异常对象</param>
    /// <param name="message">日志消息</param>
    /// <param name="args">格式化参数</param>
    void Fatal(Exception ex, string message, params object[] args);

    /// <summary>
    /// 记录致命级日志（无异常）
    /// 记录致命错误但无异常抛出的情况
    /// </summary>
    /// <param name="message">日志消息</param>
    /// <param name="args">格式化参数</param>
    void Fatal(string message, params object[] args);

    /// <summary>
    /// 开始一个日志范围，用于关联相关的日志记录
    /// </summary>
    /// <typeparam name="TState">范围状态类型</typeparam>
    /// <param name="state">范围状态</param>
    /// <returns>范围的 Disposable 对象</returns>
    IDisposable BeginScope<TState>(TState state);
}
