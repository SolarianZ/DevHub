using System.Text.Json.Serialization;

namespace DevHub.Core.Models;

/// <summary>
/// 定义校验问题。
/// </summary>
public sealed class ValidationIssue
{
    /// <summary>
    /// 出错字段路径。
    /// </summary>
    [JsonPropertyName("path")]
    public required string Path { get; init; }

    /// <summary>
    /// 稳定的机器可读错误码。
    /// </summary>
    [JsonPropertyName("code")]
    public required string Code { get; init; }

    /// <summary>
    /// 面向调用方的诊断消息。
    /// </summary>
    [JsonPropertyName("message")]
    public required string Message { get; init; }
}

/// <summary>
/// AppDefinition 结构化校验结果。
/// </summary>
public sealed class AppDefinitionValidationResult
{
    /// <summary>
    /// 是否通过校验。
    /// </summary>
    [JsonPropertyName("valid")]
    public bool Valid { get; init; }

    /// <summary>
    /// 校验问题列表。
    /// </summary>
    [JsonPropertyName("errors")]
    public required IReadOnlyList<ValidationIssue> Errors { get; init; }
}
