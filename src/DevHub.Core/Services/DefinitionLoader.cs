using DevHub.Core.Models;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DevHub.Core.Services;

/// <summary>
/// 应用程序定义加载器
/// </summary>
public class DefinitionLoader
{
    private static readonly Regex AppIdPattern = new("^[a-z0-9][a-z0-9.-]*$", RegexOptions.Compiled);

    private readonly string _definitionsPath;
    private readonly ILogger<DefinitionLoader> _logger;
    private readonly JsonSerializerOptions _jsonOptions;

    private List<AppDefinition> _definitions = new();

    /// <summary>
    /// 初始化应用程序定义加载器。
    /// </summary>
    /// <param name="definitionsPath">应用定义目录路径。</param>
    /// <param name="logger">日志记录器。</param>
    public DefinitionLoader(string definitionsPath, ILogger<DefinitionLoader> logger)
    {
        _definitionsPath = definitionsPath;
        _logger = logger;
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = true
        };
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

            foreach (var file in files)
            {
                try
                {
                    _logger.LogDebug("开始加载应用程序定义文件: {File}", file);
                    var content = File.ReadAllText(file);
                    var definition = JsonSerializer.Deserialize<AppDefinition>(content, _jsonOptions);

                    if (definition == null)
                    {
                        _logger.LogWarning("应用程序定义文件反序列化为空: {File}", file);
                        continue;
                    }

                    if (!IsValidDefinition(definition, file, out var invalidReason))
                    {
                        _logger.LogWarning("应用程序定义无效，已忽略: {File}, 原因: {Reason}", file, invalidReason);
                        continue;
                    }

                    definitions.Add(definition);
                    _logger.LogDebug("成功加载应用程序定义: {AppId} (文件: {File}, 详细信息: {DefinitionDetails})",
                        definition.AppId, file, JsonSerializer.Serialize(definition));
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
            _logger.LogInformation("成功加载 {Count} 个应用程序定义", definitions.Count);
        }
        catch (Exception ex)
        {
            _definitions = new List<AppDefinition>();
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
        _logger.LogDebug("尝试获取应用程序定义，AppId: {AppId}", appId);
        var definition = _definitions.FirstOrDefault(d => d.AppId == appId);

        if (definition != null)
        {
            _logger.LogDebug("成功获取应用程序定义: {AppId}", appId);
        }
        else
        {
            _logger.LogDebug("未找到应用程序定义: {AppId}", appId);
        }

        return definition;
    }

    /// <summary>
    /// 校验 AppDefinition 是否符合 Spec 约束
    /// </summary>
    private static bool IsValidDefinition(AppDefinition definition, string filePath, out string reason)
    {
        if (string.IsNullOrWhiteSpace(definition.AppId))
        {
            reason = "缺少 appId";
            return false;
        }

        if (!AppIdPattern.IsMatch(definition.AppId))
        {
            reason = "appId 不符合格式要求";
            return false;
        }

        var expectedFileName = $"{definition.AppId}.json";
        var actualFileName = Path.GetFileName(filePath);
        if (!string.Equals(actualFileName, expectedFileName, StringComparison.Ordinal))
        {
            reason = "文件名与 appId 不匹配";
            return false;
        }

        if (string.IsNullOrWhiteSpace(definition.DisplayName))
        {
            reason = "缺少 displayName";
            return false;
        }

        if (definition.Launch is not null && string.IsNullOrWhiteSpace(definition.Launch.ExePath))
        {
            reason = "launch.exePath 缺失或为空";
            return false;
        }

        reason = string.Empty;
        return true;
    }
}
