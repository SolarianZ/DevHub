using System.Text.Json;
using System.Text.Json.Serialization;
using DevHub.Core.Models;
using DevHub.Core.Services.Abstractions;
using DevHub.Core.Services.Events;
using Microsoft.Extensions.Logging;

namespace DevHub.Core.Services;

/// <summary>
/// 默认的 AppDefinition 管理服务实现。
/// </summary>
public sealed class DefinitionManager : IDefinitionManager
{
    private readonly string _definitionsPath;
    private readonly IDefinitionProvider _definitionProvider;
    private readonly AppDefinitionValidator _validator;
    private readonly IClock _clock;
    private readonly ILogger<DefinitionManager> _logger;
    private readonly IHubEventPublisher? _eventPublisher;
    private readonly JsonSerializerOptions _jsonOptions;

    /// <summary>
    /// 初始化定义管理服务。
    /// </summary>
    /// <param name="runtimePathOptions">运行时路径选项。</param>
    /// <param name="definitionProvider">定义快照提供器。</param>
    /// <param name="validator">定义校验器。</param>
    /// <param name="logger">日志记录器。</param>
    /// <param name="eventBus">事件总线。</param>
    public DefinitionManager(
        RuntimePathOptions runtimePathOptions,
        IDefinitionProvider definitionProvider,
        AppDefinitionValidator validator,
        IClock clock,
        ILogger<DefinitionManager> logger,
        IHubEventPublisher? eventPublisher = null)
    {
        ArgumentNullException.ThrowIfNull(runtimePathOptions);
        ArgumentNullException.ThrowIfNull(definitionProvider);
        ArgumentNullException.ThrowIfNull(validator);
        ArgumentNullException.ThrowIfNull(logger);

        _definitionsPath = runtimePathOptions.DefinitionsPath;
        _definitionProvider = definitionProvider;
        _validator = validator;
        _clock = clock;
        _logger = logger;
        _eventPublisher = eventPublisher;
        _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
    }

    /// <inheritdoc />
    public AppDefinitionValidationResult Validate(JsonElement definitionElement)
    {
        _validator.TryParseAndValidate(definitionElement, out _, out var validationResult);
        return validationResult;
    }

    /// <inheritdoc />
    public AppDefinitionValidationResult Validate(AppDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        return Validate(SerializeDefinitionToElement(definition));
    }

    /// <inheritdoc />
    public bool TryUpsert(
        JsonElement definitionElement,
        out AppDefinition? definition,
        out AppDefinitionValidationResult validationResult)
    {
        definition = null;
        if (!_validator.TryParseAndValidate(definitionElement, out var parsedDefinition, out validationResult))
        {
            return false;
        }

        definition = UpsertCore(parsedDefinition!);
        validationResult = new AppDefinitionValidationResult
        {
            Valid = true,
            Errors = Array.Empty<ValidationIssue>()
        };
        return true;
    }

    /// <inheritdoc />
    public bool TryUpsert(
        AppDefinition definition,
        out AppDefinition? storedDefinition,
        out AppDefinitionValidationResult validationResult)
    {
        ArgumentNullException.ThrowIfNull(definition);

        return TryUpsert(SerializeDefinitionToElement(definition), out storedDefinition, out validationResult);
    }

    /// <inheritdoc />
    public bool Delete(string appId)
    {
        return Delete(appId, scope: null);
    }

    /// <inheritdoc />
    public bool Delete(string appId, string? scope)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        if (!AppDefinitionValidator.IsValidAppId(appId))
        {
            throw new ArgumentException("appId format is invalid.", nameof(appId));
        }

        if (scope is not null && string.IsNullOrWhiteSpace(scope))
        {
            throw new ArgumentException("scope must be null or a non-empty string.", nameof(scope));
        }

        var identity = AppDefinitionIdentity.Create(appId, scope);
        var path = Path.Combine(_definitionsPath, identity.GetFileName());
        if (!File.Exists(path))
        {
            return false;
        }

        File.Delete(path);
        _definitionProvider.Refresh();
        PublishDefinitionDeleted(identity);
        _logger.LogInformation("已删除应用定义: {AppId}, Scope: {Scope}", appId, identity.Scope);
        return true;
    }

    private AppDefinition UpsertCore(AppDefinition definition)
    {
        Directory.CreateDirectory(_definitionsPath);

        var identity = AppDefinitionIdentity.FromDefinition(definition);
        var targetPath = Path.Combine(_definitionsPath, identity.GetFileName());
        var tempPath = Path.Combine(_definitionsPath, $".{definition.AppId}.{Guid.NewGuid():N}.tmp");
        try
        {
            var content = JsonSerializer.Serialize(definition, _jsonOptions);
            File.WriteAllText(tempPath, content);
            File.Move(tempPath, targetPath, overwrite: true);

            _definitionProvider.Refresh();
            var storedDefinition = _definitionProvider.GetDefinition(definition.AppId, definition.Scope) ?? definition;
            PublishDefinitionUpserted(storedDefinition);
            _logger.LogInformation("已写入应用定义: {AppId}, Scope: {Scope}", definition.AppId, identity.Scope);
            return storedDefinition;
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "清理定义临时文件失败: {TempPath}", tempPath);
            }
        }
    }

    private JsonElement SerializeDefinitionToElement(AppDefinition definition)
    {
        return JsonSerializer.SerializeToElement(definition, _jsonOptions);
    }

    private void PublishDefinitionUpserted(AppDefinition definition)
    {
        _eventPublisher?.Publish(new HubEventMessage
        {
            Type = HubEventTypes.AppDefinitionUpserted,
            TimeUtc = _clock.UtcNow,
            Payload = new
            {
                appId = definition.AppId,
                scope = definition.Scope,
                definition
            }
        });
    }

    private void PublishDefinitionDeleted(AppDefinitionIdentity identity)
    {
        _eventPublisher?.Publish(new HubEventMessage
        {
            Type = HubEventTypes.AppDefinitionDeleted,
            TimeUtc = _clock.UtcNow,
            Payload = new
            {
                appId = identity.AppId,
                scope = identity.Scope
            }
        });
    }
}
