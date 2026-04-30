namespace DevHub.Host.Tests;

using System.Text.Json;
using System.Text.Json.Serialization;
using DevHub.Core.Models;
using DevHub.Core.Services;

/// <summary>
/// Definition catalog 测试辅助方法。
/// </summary>
internal static class DefinitionCatalogTestHelper
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// 获取目录索引文件路径。
    /// </summary>
    public static string GetCatalogPath(string directoryPath)
    {
        return Path.Combine(directoryPath, "definitions.json");
    }

    /// <summary>
    /// 将单个 Definition upsert 到目录索引。
    /// </summary>
    public static void UpsertDefinition(string catalogPath, AppDefinition definition)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogPath);
        ArgumentNullException.ThrowIfNull(definition);

        var definitionsByIdentity = ReadDefinitions(catalogPath).ToDictionary(AppDefinitionIdentity.FromDefinition);
        definitionsByIdentity[AppDefinitionIdentity.FromDefinition(definition)] = definition;
        WriteDefinitions(catalogPath, definitionsByIdentity.Values);
    }

    /// <summary>
    /// 将 JSON Definition upsert 到目录索引。
    /// </summary>
    public static void UpsertDefinition(string catalogPath, string definitionJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(definitionJson);

        using var document = JsonDocument.Parse(definitionJson);
        var validator = new AppDefinitionValidator();
        if (!validator.TryParseAndValidate(document.RootElement, out var definition, out var validationResult))
        {
            var reason = validationResult.Errors.Count == 0
                ? "definition invalid"
                : string.Join("; ", validationResult.Errors.Select(issue => $"{issue.Path}: {issue.Message}"));
            throw new InvalidOperationException(reason);
        }

        UpsertDefinition(catalogPath, definition!);
    }

    /// <summary>
    /// 直接写入目录索引原始内容。
    /// </summary>
    public static void WriteCatalogText(string catalogPath, string content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogPath);
        ArgumentNullException.ThrowIfNull(content);

        var directory = Path.GetDirectoryName(catalogPath) ?? throw new InvalidOperationException("catalogPath 缺少父目录。");
        Directory.CreateDirectory(directory);
        File.WriteAllText(catalogPath, content);
    }

    /// <summary>
    /// 读取目录索引中的 Definition 集合。
    /// </summary>
    public static IReadOnlyList<AppDefinition> ReadDefinitions(string catalogPath)
    {
        if (!File.Exists(catalogPath))
        {
            return [];
        }

        var content = File.ReadAllText(catalogPath);
        var catalog = JsonSerializer.Deserialize<AppDefinitionsCatalog>(content, JsonOptions) ?? new AppDefinitionsCatalog();
        return catalog.Definitions
            .SelectMany(static appEntry => appEntry.Scopes.Select(scopeEntry => new AppDefinition
            {
                AppId = appEntry.AppId,
                Scope = scopeEntry.Scope,
                DisplayName = scopeEntry.DisplayName,
                Description = scopeEntry.Description,
                Launch = scopeEntry.Launch,
                Capabilities = scopeEntry.Capabilities
            }))
            .ToArray();
    }

    /// <summary>
    /// 将 Definition 集合写入目录索引。
    /// </summary>
    public static void WriteDefinitions(string catalogPath, IEnumerable<AppDefinition> definitions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogPath);
        ArgumentNullException.ThrowIfNull(definitions);

        var directory = Path.GetDirectoryName(catalogPath) ?? throw new InvalidOperationException("catalogPath 缺少父目录。");
        Directory.CreateDirectory(directory);
        var catalog = AppDefinitionsCatalogMapper.BuildCatalog(definitions);
        File.WriteAllText(catalogPath, JsonSerializer.Serialize(catalog, JsonOptions));
    }
}
