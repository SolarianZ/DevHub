using Microsoft.Extensions.Logging;
using DevHub.Core.Models;
using System.Text.Json;

namespace DevHub.Core.Services;

/// <summary>
/// 应用程序定义加载器
/// </summary>
public class DefinitionLoader
{
    private readonly string _definitionsPath;
    private readonly ILogger<DefinitionLoader> _logger;
    private readonly JsonSerializerOptions _jsonOptions;

    private List<AppDefinition> _definitions = new();

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
            if (!Directory.Exists(_definitionsPath))
            {
                _logger.LogWarning("应用程序定义目录不存在: {Path}", _definitionsPath);
                return;
            }

            var files = Directory.GetFiles(_definitionsPath, "*.json");
            var definitions = new List<AppDefinition>();

            foreach (var file in files)
            {
                try
                {
                    var content = File.ReadAllText(file);
                    var definition = JsonSerializer.Deserialize<AppDefinition>(content, _jsonOptions);

                    if (definition != null && !string.IsNullOrEmpty(definition.AppId))
                    {
                        definitions.Add(definition);
                    }
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
            _logger.LogError(ex, "加载应用程序定义失败");
        }
    }

    /// <summary>
    /// 获取所有应用程序定义
    /// </summary>
    public IReadOnlyList<AppDefinition> GetAllDefinitions()
    {
        return _definitions.AsReadOnly();
    }

    /// <summary>
    /// 根据应用程序ID获取定义
    /// </summary>
    public AppDefinition? GetDefinition(string appId)
    {
        return _definitions.FirstOrDefault(d => d.AppId == appId);
    }
}
