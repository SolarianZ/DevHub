using System.Buffers;
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
    /// 按 appId 分组的定义条目。
    /// </summary>
    [JsonPropertyName("definitions")]
    public List<AppDefinitionsCatalogAppEntry> Definitions { get; set; } = [];
}

/// <summary>
/// 目录索引中的单个 app 分组。
/// </summary>
internal sealed class AppDefinitionsCatalogAppEntry
{
    /// <summary>
    /// 应用标识。
    /// </summary>
    [JsonPropertyName("appId")]
    public string AppId { get; set; } = string.Empty;

    /// <summary>
    /// 按 scope 分组的定义条目。
    /// </summary>
    [JsonPropertyName("scopes")]
    public List<AppDefinitionsCatalogScopeEntry> Scopes { get; set; } = [];
}

/// <summary>
/// 目录索引中的单个 scope 定义条目。
/// </summary>
internal sealed class AppDefinitionsCatalogScopeEntry
{
    /// <summary>
    /// Definition 作用域。
    /// </summary>
    [JsonPropertyName("scope")]
    public string Scope { get; set; } = string.Empty;

    /// <summary>
    /// 显示名称。
    /// </summary>
    [JsonPropertyName("displayName")]
    public string? DisplayName { get; set; }

    /// <summary>
    /// 描述。
    /// </summary>
    [JsonPropertyName("description")]
    public string? Description { get; set; }

    /// <summary>
    /// 启动配置。
    /// </summary>
    [JsonPropertyName("launch")]
    public LaunchConfiguration? Launch { get; set; }

    /// <summary>
    /// 能力配置。
    /// </summary>
    [JsonPropertyName("capabilities")]
    public AppCapabilities? Capabilities { get; set; }
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

        var orderedDefinitions = OrderDefinitions(definitions);
        var catalog = new AppDefinitionsCatalog();

        foreach (var group in orderedDefinitions.GroupBy(static definition => definition.AppId, StringComparer.Ordinal))
        {
            catalog.Definitions.Add(new AppDefinitionsCatalogAppEntry
            {
                AppId = group.Key,
                Scopes = group
                    .Select(static definition => new AppDefinitionsCatalogScopeEntry
                    {
                        Scope = definition.Scope,
                        DisplayName = definition.DisplayName,
                        Description = definition.Description,
                        Launch = definition.Launch,
                        Capabilities = definition.Capabilities
                    })
                    .ToList()
            });
        }

        return catalog;
    }

    /// <summary>
    /// 校验并解析目录索引中的单个 scope 条目。
    /// </summary>
    public static bool TryParseDefinition(
        string appId,
        JsonElement scopeElement,
        AppDefinitionValidator validator,
        out AppDefinition? definition,
        out AppDefinitionValidationResult validationResult)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        ArgumentNullException.ThrowIfNull(validator);

        using var definitionDocument = CreateDefinitionDocument(appId, scopeElement);
        return validator.TryParseAndValidate(definitionDocument.RootElement, out definition, out validationResult);
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

    private static JsonDocument CreateDefinitionDocument(string appId, JsonElement scopeElement)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("appId", appId);

            foreach (var property in scopeElement.EnumerateObject())
            {
                if (property.NameEquals("appId"))
                {
                    continue;
                }

                property.WriteTo(writer);
            }

            writer.WriteEndObject();
        }

        return JsonDocument.Parse(buffer.WrittenMemory);
    }
}
