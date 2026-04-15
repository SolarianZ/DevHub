import {
  DevHubRpcError,
  DevHubRpcErrorCode,
  type AppDefinition,
  type AppInstance,
  type DevHubClient,
  type DevHubEventsClient,
} from "@devhub/sdk";
import {
  createEmptyDefinitionForm,
  type DefinitionFormState,
  type DefinitionIssueMap,
} from "./definition-form";
import type {
  BootstrapSnapshot,
  MonitorRuntimeConnectionInfo,
  MonitorSettings,
  SettingsSnapshot,
} from "./models";

export const ROUTE_SEQUENCE = ["bootstrap", "status", "settings", "logs"] as const;

export type RoutePage = (typeof ROUTE_SEQUENCE)[number];
export type HostSessionStatus = "idle" | "connecting" | "connected" | "recovering";
export type DefinitionDialogMode = "view" | "create" | "edit";

export interface DefinitionDialogState {
  mode: DefinitionDialogMode;
  title: string;
  subtitle: string;
  form: DefinitionFormState;
  fieldErrors: DefinitionIssueMap;
  loading: boolean;
  saving: boolean;
  readOnly: boolean;
  missing: boolean;
  emptyStateMessage: string | null;
  submitError: string | null;
}

export interface SettingsFieldErrors {
  dataDirOverride?: string | null;
  hostExecutablePath?: string | null;
}

export function readHashRoute(): RoutePage {
  const raw = window.location.hash.replace(/^#\/?/, "").trim();
  return ROUTE_SEQUENCE.includes(raw as RoutePage) ? (raw as RoutePage) : "bootstrap";
}

export function writeHashRoute(route: RoutePage): void {
  const nextHash = route === "bootstrap" ? "" : `#/${route}`;
  if (window.location.hash !== nextHash) {
    window.location.hash = nextHash;
  }
}

export function normalizeOptionalInput(value?: string | null): string | null {
  const trimmed = value?.trim() ?? "";
  return trimmed ? trimmed : null;
}

export function validateSettingsDraft(settings: MonitorSettings): SettingsFieldErrors {
  const errors: SettingsFieldErrors = {};

  if (settings.dataDirOverride && !isAbsolutePath(settings.dataDirOverride)) {
    errors.dataDirOverride = "数据目录必须填写绝对路径。";
  }

  if (settings.hostExecutablePath && !isAbsolutePath(settings.hostExecutablePath)) {
    errors.hostExecutablePath = "Host 可执行文件路径必须填写绝对路径。";
  }

  return errors;
}

export function hasSettingsFieldErrors(errors: SettingsFieldErrors): boolean {
  return Object.values(errors).some((value) => Boolean(value));
}

export function shouldRecoverHostSession(error: unknown): boolean {
  if (!(error instanceof DevHubRpcError)) {
    return false;
  }

  return (
    error.is(DevHubRpcErrorCode.Unauthorized)
    || error.is(DevHubRpcErrorCode.Forbidden)
  );
}

export async function disposeSessionResources(
  hostClient: DevHubClient | null,
  eventsClient: DevHubEventsClient | null,
  subscriptionId: string | null,
): Promise<void> {
  if (eventsClient && subscriptionId) {
    try {
      await eventsClient.unsubscribe(subscriptionId);
    } catch {
      // 连接已关闭时取消订阅可能失败，这里按幂等清理处理。
    }
  }

  const cleanupTasks: Promise<void>[] = [];
  if (eventsClient) {
    cleanupTasks.push(eventsClient.dispose());
  }
  if (hostClient) {
    cleanupTasks.push(hostClient.dispose());
  }

  if (cleanupTasks.length > 0) {
    await Promise.allSettled(cleanupTasks);
  }
}

export function sortDefinitions(definitions: readonly AppDefinition[]): AppDefinition[] {
  return [...definitions].sort((left, right) =>
    left.appId.localeCompare(right.appId, "zh-CN"),
  );
}

export function sortInstances(instances: readonly AppInstance[]): AppInstance[] {
  return [...instances].sort((left, right) => {
    const appCompare = left.appId.localeCompare(right.appId, "zh-CN");
    if (appCompare !== 0) {
      return appCompare;
    }

    const scopeCompare = formatScope(left.scope).localeCompare(formatScope(right.scope), "zh-CN");
    if (scopeCompare !== 0) {
      return scopeCompare;
    }

    return left.instanceId.localeCompare(right.instanceId, "zh-CN");
  });
}

export function upsertDefinition(
  current: readonly AppDefinition[],
  nextDefinition: AppDefinition,
): AppDefinition[] {
  return sortDefinitions([
    ...current.filter((definition) => definition.appId !== nextDefinition.appId),
    nextDefinition,
  ]);
}

export function createMissingDefinitionForm(appId: string): DefinitionFormState {
  const form = createEmptyDefinitionForm();
  form.appId = appId;
  form.enableRpc = false;
  return form;
}

export function formatPhaseLabel(phase?: BootstrapSnapshot["phase"]): string {
  switch (phase) {
    case "scanning":
      return "正在扫描";
    case "launch_available":
      return "可启动 Host";
    case "settings_required":
      return "需要设置";
    case "host_available":
      return "Host 可用";
    default:
      return "初始化中";
  }
}

export function formatSessionLabel(status: HostSessionStatus): string {
  switch (status) {
    case "connecting":
      return "连接中";
    case "connected":
      return "已连接";
    case "recovering":
      return "恢复中";
    default:
      return "未连接";
  }
}

export function formatDataDirSource(source?: SettingsSnapshot["dataDirSource"]): string {
  switch (source) {
    case "settings_override":
      return "设置覆盖";
    case "environment":
      return "环境变量";
    case "platform_default":
      return "平台默认";
    default:
      return "加载中";
  }
}

export function formatBootstrapHeadline(phase?: BootstrapSnapshot["phase"]): string {
  switch (phase) {
    case "launch_available":
      return "3 秒内未发现可用 Host，已经开放启动入口。";
    case "settings_required":
      return "缺少 Host 可执行文件路径，需要先完成设置。";
    case "host_available":
      return "已验证到可用 Host，前端会自动切入状态页。";
    case "scanning":
      return "正在持续扫描当前有效数据目录。";
    default:
      return "正在初始化 Monitor。";
  }
}

export function formatBootstrapDescription(snapshot: BootstrapSnapshot | null): string {
  if (!snapshot) {
    return "正在读取 Monitor 原生后端的初始化状态。";
  }

  if (snapshot.phase === "host_available" && snapshot.connection) {
    return `已通过真实连通性校验确认 ${snapshot.connection.rpcEndpoint} 可用，接下来由前端接管 Host RPC 与事件连接。`;
  }

  if (snapshot.phase === "settings_required") {
    return "用户发起启动请求，但当前尚未配置 DevHub Host 可执行文件路径。";
  }

  if (snapshot.phase === "launch_available") {
    return "虽然初始化页显示了启动按钮，但后台扫描不会停止，一旦发现可用 Host 会立即切到状态页。";
  }

  return "原生后端会持续读取 hub.json 与 token，并通过真实 hub.ping 校验过滤掉过期运行时文件。";
}

export function getRuntimePort(connection?: MonitorRuntimeConnectionInfo | null): string {
  if (!connection) {
    return "未连接";
  }

  try {
    return String(new URL(connection.rpcEndpoint).port || new URL(connection.runtime.httpBaseUrl).port);
  } catch {
    return connection.runtime.httpBaseUrl;
  }
}

export function isInstanceOffline(
  instance: AppInstance,
  connection: MonitorRuntimeConnectionInfo | undefined | null,
  nowTick: number,
): boolean {
  const thresholdSeconds = connection?.runtime.runtimeTuning.onlineThresholdSeconds ?? 30;
  return nowTick - instance.lastSeenUtc.getTime() > thresholdSeconds * 1_000;
}

export function formatScope(scope?: string | null): string {
  return scope && scope.trim() ? scope : "global";
}

export function formatRelativeTime(value: Date, nowTick: number): string {
  const deltaMs = Math.max(0, nowTick - value.getTime());
  const deltaSeconds = Math.floor(deltaMs / 1_000);

  if (deltaSeconds < 60) {
    return `${deltaSeconds}s 前`;
  }

  const minutes = Math.floor(deltaSeconds / 60);
  if (minutes < 60) {
    return `${minutes}m 前`;
  }

  const hours = Math.floor(minutes / 60);
  return `${hours}h 前`;
}

export function formatDefinitionCapabilities(definition: AppDefinition): string[] {
  const capabilities: string[] = [];

  if (definition.capabilities?.rpc ?? true) {
    capabilities.push("RPC");
  }
  if (definition.capabilities?.events) {
    capabilities.push("Events");
  }
  if (definition.launch?.exePath) {
    capabilities.push("Launch");
  }

  return capabilities.length > 0 ? capabilities : ["基础定义"];
}

export function formatBytes(value: number): string {
  if (value < 1_024) {
    return `${value} B`;
  }

  if (value < 1_024 * 1_024) {
    return `${(value / 1_024).toFixed(1)} KB`;
  }

  return `${(value / (1_024 * 1_024)).toFixed(1)} MB`;
}

export function toErrorMessage(error: unknown): string {
  if (typeof error === "object" && error && "message" in error && typeof error.message === "string") {
    return error.message;
  }

  return "发生未知错误。";
}

function isAbsolutePath(value: string): boolean {
  const trimmed = value.trim();
  return (
    trimmed.startsWith("/")
    || /^[A-Za-z]:[\\/]/.test(trimmed)
    || /^\\\\/.test(trimmed)
  );
}
