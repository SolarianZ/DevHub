using DevHub.Core.Models;

namespace DevHub.Core.Services;

/// <summary>
/// AppDefinition 读取提供器。
/// </summary>
/// <remarks>
/// 提供显式刷新与快照读取能力，调用方不直接依赖底层加载器，避免重复 I/O 与职责扩散。
/// </remarks>
public interface IDefinitionProvider
{
    /// <summary>
    /// 刷新定义快照。
    /// </summary>
    void Refresh();

    /// <summary>
    /// 获取全部定义快照。
    /// </summary>
    /// <returns>当前定义只读快照。</returns>
    IReadOnlyList<AppDefinition> GetAllDefinitions();

    /// <summary>
    /// 获取指定应用定义。
    /// </summary>
    /// <param name="appId">应用标识。</param>
    /// <returns>匹配的定义，不存在时返回 <c>null</c>。</returns>
    AppDefinition? GetDefinition(string appId);
}
