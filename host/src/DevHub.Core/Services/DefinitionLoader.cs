using DevHub.Core.Models;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace DevHub.Core.Services;

/// <summary>
/// 应用程序定义加载器
/// </summary>
public class DefinitionLoader
{
    private readonly string _definitionsCatalogPath;
    private readonly ILogger<DefinitionLoader> _logger;
    private readonly AppDefinitionValidator _validator;

    private List<AppDefinition> _definitions = new();
    private Dictionary<AppDefinitionIdentity, AppDefinition> _definitionsByIdentity = new();

    /// <summary>
    /// 初始化应用程序定义加载器。
    /// </summary>
    /// <param name="definitionsCatalogPath">应用定义目录索引文件路径。</param>
    /// <param name="logger">日志记录器。</param>
    public DefinitionLoader(string definitionsCatalogPath, ILogger<DefinitionLoader> logger, AppDefinitionValidator? validator = null)
    {
        _definitionsCatalogPath = definitionsCatalogPath;
        _logger = logger;
        _validator = validator ?? new AppDefinitionValidator();
    }

    /// <summary>
    /// 加载应用程序定义
    /// </summary>
    public void Load()
    {
        try
        {
            _logger.LogDebug("开始加载应用程序定义目录索引，路径: {Path}", _definitionsCatalogPath);

            if (!File.Exists(_definitionsCatalogPath))
            {
                _logger.LogDebug("应用程序定义目录索引不存在，按空集合处理: {Path}", _definitionsCatalogPath);
                _definitions = new List<AppDefinition>();
                _definitionsByIdentity = new Dictionary<AppDefinitionIdentity, AppDefinition>();
                return;
            }

            var definitionsByIdentity = new Dictionary<AppDefinitionIdentity, AppDefinition>();
            var content = File.ReadAllText(_definitionsCatalogPath);
            using var document = JsonDocument.Parse(content);
            if (!AppDefinitionsCatalogMapper.TryGetDefinitionsArray(document.RootElement, out var appEntries, out var errorMessage))
            {
                _definitions = new List<AppDefinition>();
                _definitionsByIdentity = new Dictionary<AppDefinitionIdentity, AppDefinition>();
                _logger.LogError("应用程序定义目录索引结构无效，已按空集合处理: {Path}, 原因: {Reason}", _definitionsCatalogPath, errorMessage);
                return;
            }

            foreach (var appEntry in appEntries.EnumerateArray())
            {
                if (appEntry.ValueKind != JsonValueKind.Object)
                {
                    _logger.LogWarning("应用程序定义分组无效，已忽略: {Path}, 原因: app entry must be an object", _definitionsCatalogPath);
                    continue;
                }

                if (!appEntry.TryGetProperty("appId", out var appIdElement) || appIdElement.ValueKind != JsonValueKind.String)
                {
                    _logger.LogWarning("应用程序定义分组无效，已忽略: {Path}, 原因: appId is required", _definitionsCatalogPath);
                    continue;
                }

                var appId = appIdElement.GetString();
                if (!ProtocolIdentifier.IsValidAppId(appId))
                {
                    _logger.LogWarning("应用程序定义分组无效，已忽略: {Path}, AppId: {AppId}, 原因: appId must match {Pattern}", _definitionsCatalogPath, appId, ProtocolIdentifier.CanonicalPattern);
                    continue;
                }

                if (!appEntry.TryGetProperty("scopes", out var scopesElement) || scopesElement.ValueKind != JsonValueKind.Array)
                {
                    _logger.LogWarning("应用程序定义分组无效，已忽略: {Path}, AppId: {AppId}, 原因: scopes must be an array", _definitionsCatalogPath, appId);
                    continue;
                }

                foreach (var scopeEntry in scopesElement.EnumerateArray())
                {
                    if (scopeEntry.ValueKind != JsonValueKind.Object)
                    {
                        _logger.LogWarning("应用程序定义条目无效，已忽略: {Path}, AppId: {AppId}, 原因: scope entry must be an object", _definitionsCatalogPath, appId);
                        continue;
                    }

                    if (!AppDefinitionsCatalogMapper.TryParseDefinition(appId!, scopeEntry, _validator, out var definition, out var validationResult))
                    {
                        var invalidReason = validationResult.Errors.Count == 0
                            ? "定义校验失败"
                            : string.Join("; ", validationResult.Errors.Select(issue => $"{issue.Path}: {issue.Message}"));
                        _logger.LogWarning("应用程序定义条目无效，已忽略: {Path}, AppId: {AppId}, 原因: {Reason}", _definitionsCatalogPath, appId, invalidReason);
                        continue;
                    }

                    var identity = AppDefinitionIdentity.FromDefinition(definition!);
                    if (definitionsByIdentity.ContainsKey(identity))
                    {
                        _logger.LogWarning("应用程序定义条目重复，已忽略后续记录: {Path}, AppId: {AppId}, Scope: {Scope}", _definitionsCatalogPath, identity.AppId, identity.Scope);
                        continue;
                    }

                    definitionsByIdentity.Add(identity, definition!);
                    _logger.LogDebug("成功加载应用程序定义: {AppId}, Scope: {Scope}, 路径: {Path}", definition!.AppId, definition.Scope, _definitionsCatalogPath);
                }
            }

            _definitions = AppDefinitionsCatalogMapper.OrderDefinitions(definitionsByIdentity.Values).ToList();
            _definitionsByIdentity = definitionsByIdentity;
            _logger.LogInformation("成功加载 {Count} 个应用程序定义", _definitions.Count);
        }
        catch (JsonException ex)
        {
            _definitions = new List<AppDefinition>();
            _definitionsByIdentity = new Dictionary<AppDefinitionIdentity, AppDefinition>();
            _logger.LogError(ex, "应用程序定义目录索引 JSON 无效，已按空集合处理: {Path}", _definitionsCatalogPath);
        }
        catch (Exception ex)
        {
            _definitions = new List<AppDefinition>();
            _definitionsByIdentity = new Dictionary<AppDefinitionIdentity, AppDefinition>();
            _logger.LogError(ex, "加载应用程序定义失败，路径: {Path}", _definitionsCatalogPath);
        }
    }

    /// <summary>
    /// 获取所有应用程序定义
    /// </summary>
    public IReadOnlyList<AppDefinition> GetAllDefinitions()
    {
        _logger.LogDebug("获取所有应用程序定义，数量: {Count}", _definitions.Count);
        return _definitions.AsReadOnly();
    }

    /// <summary>
    /// 根据复合键获取定义。
    /// </summary>
    public AppDefinition? GetDefinition(string appId, string scope)
    {
        ProtocolIdentifier.EnsureAppId(appId, nameof(appId));
        ScopeContract.EnsureScopedString(scope, nameof(scope));

        _logger.LogDebug("尝试获取应用程序定义，AppId: {AppId}, Scope: {Scope}", appId, scope);
        var definition = _definitionsByIdentity.GetValueOrDefault(AppDefinitionIdentity.Create(appId, scope));

        if (definition != null)
        {
            _logger.LogDebug("成功获取应用程序定义: {AppId}, Scope: {Scope}", appId, scope);
        }
        else
        {
            _logger.LogDebug("未找到应用程序定义: {AppId}, Scope: {Scope}", appId, scope);
        }

        return definition;
    }

    /// <summary>
    /// 判断指定 appId 是否存在任意 Definition。
    /// </summary>
    public bool HasDefinitions(string appId)
    {
        ProtocolIdentifier.EnsureAppId(appId, nameof(appId));
        return _definitions.Any(definition => definition.AppId == appId);
    }
}
