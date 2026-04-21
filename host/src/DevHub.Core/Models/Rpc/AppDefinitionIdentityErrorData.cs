using System.Text.Json.Serialization;

namespace DevHub.Core.Models.Rpc;

/// <summary>
/// `appId + scope` 复合身份错误数据。
/// </summary>
public sealed class AppDefinitionIdentityErrorData
{
    /// <summary>
    /// 应用标识。
    /// </summary>
    [JsonPropertyName("appId")]
    public required string AppId { get; init; }

    /// <summary>
    /// Definition 作用域；<see langword="null"/> 表示 Global。
    /// </summary>
    [JsonPropertyName("scope")]
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? Scope { get; init; }
}
