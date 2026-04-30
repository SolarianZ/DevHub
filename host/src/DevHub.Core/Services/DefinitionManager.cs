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
    private readonly string _appsPath;
    private readonly string _definitionsCatalogPath;
    private readonly IDefinitionProvider _definitionProvider;
    private readonly AppDefinitionValidator _validator;
    private readonly IClock _clock;
    private readonly ILogger<DefinitionManager> _logger;
    private readonly IHubEventPublisher? _eventPublisher;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly object _syncRoot = new();

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

        _appsPath = runtimePathOptions.AppsPath;
        _definitionsCatalogPath = runtimePathOptions.DefinitionsCatalogPath;
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
    public bool Delete(string appId, string scope)
    {
        ProtocolIdentifier.EnsureAppId(appId, nameof(appId));
        ScopeContract.EnsureScopedString(scope, nameof(scope));

        var identity = AppDefinitionIdentity.Create(appId, scope);
        lock (_syncRoot)
        {
            var definitionsByIdentity = _definitionProvider
                .GetAllDefinitions()
                .ToDictionary(AppDefinitionIdentity.FromDefinition);
            if (!definitionsByIdentity.Remove(identity))
            {
                return false;
            }

            PersistCatalog(definitionsByIdentity.Values);
            _definitionProvider.Refresh();
        }

        PublishDefinitionDeleted(identity);
        _logger.LogInformation("已删除应用定义: {AppId}, Scope: {Scope}", appId, identity.Scope);
        return true;
    }

    private AppDefinition UpsertCore(AppDefinition definition)
    {
        var identity = AppDefinitionIdentity.FromDefinition(definition);
        lock (_syncRoot)
        {
            var definitionsByIdentity = _definitionProvider
                .GetAllDefinitions()
                .ToDictionary(AppDefinitionIdentity.FromDefinition);
            definitionsByIdentity[identity] = definition;
            PersistCatalog(definitionsByIdentity.Values);
            _definitionProvider.Refresh();
        }

        var storedDefinition = _definitionProvider.GetDefinition(definition.AppId, definition.Scope) ?? definition;
        PublishDefinitionUpserted(storedDefinition);
        _logger.LogInformation("已写入应用定义: {AppId}, Scope: {Scope}", definition.AppId, identity.Scope);
        return storedDefinition;
    }

    private void PersistCatalog(IEnumerable<AppDefinition> definitions)
    {
        Directory.CreateDirectory(_appsPath);

        var tempPath = Path.Combine(_appsPath, $".definitions.{Guid.NewGuid():N}.tmp");
        try
        {
            var catalog = AppDefinitionsCatalogMapper.BuildCatalog(definitions);
            var content = JsonSerializer.Serialize(catalog, _jsonOptions);
            File.WriteAllText(tempPath, content);
            File.Move(tempPath, _definitionsCatalogPath, overwrite: true);
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
                _logger.LogDebug(ex, "清理定义目录索引临时文件失败: {TempPath}", tempPath);
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
