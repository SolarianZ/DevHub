using System.Text.Json;
using DevHub.Core.Models;

namespace DevHub.Core.Services;

/// <summary>
/// AppDefinition 管理服务。
/// </summary>
public interface IDefinitionManager
{
    /// <summary>
    /// 校验候选定义，但不修改持久化状态或内存快照。
    /// </summary>
    /// <param name="definitionElement">候选定义 JSON 对象。</param>
    /// <returns>结构化校验结果。</returns>
    AppDefinitionValidationResult Validate(JsonElement definitionElement);

    /// <summary>
    /// 校验候选定义，但不修改持久化状态或内存快照。
    /// </summary>
    /// <param name="definition">候选定义模型。</param>
    /// <returns>结构化校验结果。</returns>
    AppDefinitionValidationResult Validate(AppDefinition definition);

    /// <summary>
    /// 校验并原子创建或更新定义。
    /// </summary>
    /// <param name="definitionElement">候选定义 JSON 对象。</param>
    /// <param name="definition">写入成功时返回生效定义。</param>
    /// <param name="validationResult">失败时返回校验结果。</param>
    /// <returns>写入成功返回 <c>true</c>。</returns>
    bool TryUpsert(
        JsonElement definitionElement,
        out AppDefinition? definition,
        out AppDefinitionValidationResult validationResult);

    /// <summary>
    /// 校验并原子创建或更新定义。
    /// </summary>
    /// <param name="definition">候选定义模型。</param>
    /// <param name="storedDefinition">写入成功时返回生效定义。</param>
    /// <param name="validationResult">失败时返回校验结果。</param>
    /// <returns>写入成功返回 <c>true</c>。</returns>
    bool TryUpsert(
        AppDefinition definition,
        out AppDefinition? storedDefinition,
        out AppDefinitionValidationResult validationResult);

    /// <summary>
    /// 删除指定定义。
    /// </summary>
    /// <param name="appId">应用标识。</param>
    /// <param name="scope">Definition 作用域；空字符串表示 Global。</param>
    /// <returns>删除成功返回 <c>true</c>；目标不存在返回 <c>false</c>。</returns>
    bool Delete(string appId, string scope);
}
