using DevHub.Core.Models;

namespace DevHub.Core.Services;

/// <summary>
/// 基于 <see cref="DefinitionLoader"/> 的定义提供器。
/// </summary>
/// <remarks>
/// 采用显式刷新策略：调用 <see cref="Refresh"/> 时更新内部快照，读取方法仅访问快照。
/// </remarks>
public sealed class DefinitionProvider : IDefinitionProvider
{
    private readonly DefinitionLoader _definitionLoader;
    private readonly object _syncRoot = new();
    private IReadOnlyList<AppDefinition> _snapshot = Array.Empty<AppDefinition>();
    private Dictionary<AppDefinitionIdentity, AppDefinition> _snapshotByIdentity = new();

    /// <summary>
    /// 初始化定义提供器。
    /// </summary>
    /// <param name="definitionLoader">底层定义加载器。</param>
    public DefinitionProvider(DefinitionLoader definitionLoader)
    {
        _definitionLoader = definitionLoader;
    }

    /// <inheritdoc />
    public void Refresh()
    {
        lock (_syncRoot)
        {
            _definitionLoader.Load();
            _snapshot = _definitionLoader
                .GetAllDefinitions()
                .Select(CloneDefinition)
                .ToArray();
            _snapshotByIdentity = _snapshot.ToDictionary(AppDefinitionIdentity.FromDefinition);
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<AppDefinition> GetAllDefinitions()
    {
        lock (_syncRoot)
        {
            return _snapshot.Select(CloneDefinition).ToArray();
        }
    }

    /// <inheritdoc />
    public AppDefinition? GetDefinition(string appId, string scope)
    {
        ProtocolIdentifier.EnsureAppId(appId, nameof(appId));
        ScopeContract.EnsureScopedString(scope, nameof(scope));

        lock (_syncRoot)
        {
            return _snapshotByIdentity.TryGetValue(AppDefinitionIdentity.Create(appId, scope), out var definition)
                ? CloneDefinition(definition)
                : null;
        }
    }

    /// <inheritdoc />
    public bool HasDefinitions(string appId)
    {
        ProtocolIdentifier.EnsureAppId(appId, nameof(appId));

        lock (_syncRoot)
        {
            return _snapshot.Any(definition => definition.AppId == appId);
        }
    }

    private static AppDefinition CloneDefinition(AppDefinition definition)
    {
        return new AppDefinition
        {
            AppId = definition.AppId,
            Scope = definition.Scope,
            DisplayName = definition.DisplayName,
            Description = definition.Description,
            Launch = CloneLaunch(definition.Launch),
            Capabilities = CloneCapabilities(definition.Capabilities)
        };
    }

    private static LaunchConfiguration? CloneLaunch(LaunchConfiguration? launch)
    {
        if (launch is null)
        {
            return null;
        }

        return new LaunchConfiguration
        {
            ExePath = launch.ExePath,
            Args = launch.Args is null ? null : [.. launch.Args],
            ArgsTemplate = launch.ArgsTemplate,
            WorkingDirectory = launch.WorkingDirectory,
            DedupeKeyTemplate = launch.DedupeKeyTemplate,
            EnvironmentVariables = launch.EnvironmentVariables is null
                ? null
                : new Dictionary<string, string?>(launch.EnvironmentVariables, StringComparer.Ordinal)
        };
    }

    private static AppCapabilities? CloneCapabilities(AppCapabilities? capabilities)
    {
        if (capabilities is null)
        {
            return null;
        }

        return new AppCapabilities
        {
            Rpc = capabilities.Rpc,
            Events = capabilities.Events
        };
    }
}
