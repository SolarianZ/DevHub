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
            _snapshot = _definitionLoader.GetAllDefinitions().ToArray();
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<AppDefinition> GetAllDefinitions()
    {
        lock (_syncRoot)
        {
            return _snapshot;
        }
    }

    /// <inheritdoc />
    public AppDefinition? GetDefinition(string appId)
    {
        lock (_syncRoot)
        {
            return _snapshot.FirstOrDefault(definition => definition.AppId == appId);
        }
    }
}
