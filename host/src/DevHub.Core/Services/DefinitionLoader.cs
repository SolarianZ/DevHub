using DevHub.Core.Models;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace DevHub.Core.Services;

/// <summary>
/// 应用程序定义加载器
/// </summary>
public class DefinitionLoader
{
    private readonly string _definitionsPath;
    private readonly ILogger<DefinitionLoader> _logger;
    private readonly AppDefinitionValidator _validator;

    private List<AppDefinition> _definitions = new();
    private Dictionary<AppDefinitionIdentity, AppDefinition> _definitionsByIdentity = new();

    /// <summary>
    /// 初始化应用程序定义加载器。
    /// </summary>
    /// <param name="definitionsPath">应用定义目录路径。</param>
    /// <param name="logger">日志记录器。</param>
    public DefinitionLoader(string definitionsPath, ILogger<DefinitionLoader> logger, AppDefinitionValidator? validator = null)
    {
        _definitionsPath = definitionsPath;
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
            _logger.LogDebug("开始加载应用程序定义，目录: {Path}", _definitionsPath);

            if (!Directory.Exists(_definitionsPath))
            {
                _logger.LogWarning("应用程序定义目录不存在: {Path}", _definitionsPath);
                _definitions = new List<AppDefinition>();
                return;
            }

            var files = Directory.GetFiles(_definitionsPath, "*.json");
            _logger.LogDebug("发现 {Count} 个应用程序定义文件", files.Length);
            var definitions = new List<AppDefinition>();
            var definitionsByIdentity = new Dictionary<AppDefinitionIdentity, AppDefinition>();

            foreach (var file in files)
            {
                try
                {
                    _logger.LogDebug("开始加载应用程序定义文件: {File}", file);
                    var content = File.ReadAllText(file);
                    using var document = JsonDocument.Parse(content);
                    if (!_validator.TryParseAndValidate(
                            document.RootElement,
                            out var definition,
                            out var validationResult,
                            Path.GetFileName(file)))
                    {
                        var invalidReason = validationResult.Errors.Count == 0
                            ? "定义校验失败"
                            : string.Join("; ", validationResult.Errors.Select(issue => $"{issue.Path}: {issue.Message}"));
                        _logger.LogWarning("应用程序定义无效，已忽略: {File}, 原因: {Reason}", file, invalidReason);
                        continue;
                    }

                    var identity = AppDefinitionIdentity.FromDefinition(definition!);
                    definitionsByIdentity[identity] = definition!;
                    definitions.Add(definition!);
                    _logger.LogDebug("成功加载应用程序定义: {AppId} (文件: {File}, 详细信息: {DefinitionDetails})",
                        definition!.AppId, file, JsonSerializer.Serialize(definition));
                }
                catch (JsonException ex)
                {
                    _logger.LogError(ex, "JSON 反序列化失败，应用程序定义文件: {File}", file);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "加载应用程序定义文件失败: {File}", file);
                }
            }

            _definitions = definitions;
            _definitionsByIdentity = definitionsByIdentity;
            _logger.LogInformation("成功加载 {Count} 个应用程序定义", definitions.Count);
        }
        catch (Exception ex)
        {
            _definitions = new List<AppDefinition>();
            _definitionsByIdentity = new Dictionary<AppDefinitionIdentity, AppDefinition>();
            _logger.LogError(ex, "加载应用程序定义失败，目录: {Path}", _definitionsPath);
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
    /// 根据应用程序ID获取定义
    /// </summary>
    public AppDefinition? GetDefinition(string appId)
    {
        return GetDefinition(appId, scope: null);
    }

    /// <summary>
    /// 根据复合键获取定义。
    /// </summary>
    public AppDefinition? GetDefinition(string appId, string? scope)
    {
        var normalizedScope = AppDefinitionIdentity.NormalizeScope(scope);
        _logger.LogDebug("尝试获取应用程序定义，AppId: {AppId}, Scope: {Scope}", appId, normalizedScope);
        var definition = _definitionsByIdentity.GetValueOrDefault(AppDefinitionIdentity.Create(appId, normalizedScope));

        if (definition != null)
        {
            _logger.LogDebug("成功获取应用程序定义: {AppId}, Scope: {Scope}", appId, normalizedScope);
        }
        else
        {
            _logger.LogDebug("未找到应用程序定义: {AppId}, Scope: {Scope}", appId, normalizedScope);
        }

        return definition;
    }

    /// <summary>
    /// 判断指定 appId 是否存在任意 Definition。
    /// </summary>
    public bool HasDefinitions(string appId)
    {
        return _definitions.Any(definition => definition.AppId == appId);
    }
}
