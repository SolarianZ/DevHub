/**
 * 已放弃请求的本地维护过滤条件。
 */
export interface AbandonedRequestFilter {
  /**
   * 仅匹配已放弃时长大于等于该阈值的记录，单位为毫秒。
   */
  olderThanMs?: number;

  /**
   * 仅匹配发送请求时可识别出指定 `appId` 的记录。
   * `appId` 提取属于尽力而为；缺少可识别 `appId` 的记录不会命中该条件。
   */
  appId?: string;

  /**
   * 仅匹配指定 JSON-RPC 方法名的记录。
   */
  method?: string;
}
