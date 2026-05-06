using System.Text.Json;
using System.Text.Json.Serialization;
using DevHub.Core.Models;

namespace DevHub.Core.Services;

/// <summary>
/// AppDefinition 目录索引模型。
/// </summary>
internal sealed class AppDefinitionsCatalog
{
    /// <summary>
    /// 目录索引版本。
    /// </summary>
    [JsonPropertyName("version")]
    public int Version { get; set; } = AppDefinitionsCatalogMapper.CurrentVersion;

    /// <summary>
    /// 完整 AppDefinition 记录集合。
    /// </summary>
    [JsonPropertyName("definitions")]
    public List<AppDefinition> Definitions { get; set; } = [];
}

/// <summary>
/// AppDefinition 与目录索引之间的映射辅助。
/// </summary>
internal static class AppDefinitionsCatalogMapper
{
    /// <summary>
    /// 当前目录索引版本。
    /// </summary>
    public const int CurrentVersion = 1;

    /// <summary>
    /// 对 Definition 集合执行稳定排序。
    /// </summary>
    public static IReadOnlyList<AppDefinition> OrderDefinitions(IEnumerable<AppDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);

        return definitions
            .OrderBy(static definition => definition.AppId, StringComparer.Ordinal)
            .ThenBy(static definition => ScopeContract.IsGlobal(definition.Scope) ? 0 : 1)
            .ThenBy(static definition => definition.Scope, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    /// 将 Definition 集合映射为目录索引模型。
    /// </summary>
    public static AppDefinitionsCatalog BuildCatalog(IEnumerable<AppDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);

        var catalog = new AppDefinitionsCatalog();
        catalog.Definitions.AddRange(OrderDefinitions(definitions));
        return catalog;
    }

    /// <summary>
    /// 验证并解析目录索引中的单条 Definition 记录。
    /// </summary>
    public static bool TryParseDefinition(
        JsonElement definitionElement,
        AppDefinitionValidator validator,
        out AppDefinition? definition,
        out AppDefinitionValidationResult validationResult)
    {
        ArgumentNullException.ThrowIfNull(validator);

        return validator.TryParseAndValidate(definitionElement, out definition, out validationResult);
    }

    /// <summary>
    /// 校验目录索引根对象结构。
    /// </summary>
    public static bool TryGetDefinitionsArray(JsonElement rootElement, out JsonElement definitionsArray, out string errorMessage)
    {
        definitionsArray = default;
        errorMessage = string.Empty;

        if (rootElement.ValueKind != JsonValueKind.Object)
        {
            errorMessage = "catalog root must be an object";
            return false;
        }

        if (!rootElement.TryGetProperty("version", out var versionElement)
            || versionElement.ValueKind != JsonValueKind.Number
            || !versionElement.TryGetInt32(out var version)
            || version != CurrentVersion)
        {
            errorMessage = $"catalog version must be {CurrentVersion}";
            return false;
        }

        if (!rootElement.TryGetProperty("definitions", out definitionsArray) || definitionsArray.ValueKind != JsonValueKind.Array)
        {
            errorMessage = "catalog definitions must be an array";
            return false;
        }

        return true;
    }
}
