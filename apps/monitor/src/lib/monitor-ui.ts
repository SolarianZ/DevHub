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
export type MonitorWorkspace = "home" | "help" | "settings" | "definition";
export type SidebarWorkspace = Exclude<MonitorWorkspace, "definition">;
export type HomeWorkspaceMode = "discovery" | "status";
export type HostSessionStatus = "idle" | "connecting" | "connected" | "recovering";
export type DefinitionWorkspaceMode = "view" | "create" | "edit";

export interface DefinitionWorkspaceState {
  mode: DefinitionWorkspaceMode;
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

export function getHomeWorkspaceMode(
  snapshot: BootstrapSnapshot | null,
): HomeWorkspaceMode {
  return snapshot?.phase === "host_available" && snapshot.connection ? "status" : "discovery";
}

export function getSidebarWorkspace(workspace: MonitorWorkspace): SidebarWorkspace {
  return workspace === "definition" ? "home" : workspace;
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
      return "尚未连接到 DevHub Host，可以继续扫描或立即启动。";
    case "settings_required":
      return "启动 Host 前需要先补充本机设置。";
    case "host_available":
      return "已连接到 DevHub Host，可以开始查看定义与实例。";
    case "scanning":
      return "正在查找当前数据目录中的可用 Host。";
    default:
      return "正在准备 Monitor。";
  }
}

export function formatBootstrapDescription(snapshot: BootstrapSnapshot | null): string {
  if (!snapshot) {
    return "Monitor 正在读取本机设置并检查当前运行环境。";
  }

  if (snapshot.phase === "host_available" && snapshot.connection) {
    return `已确认 ${snapshot.connection.rpcEndpoint} 可用，主页会持续展示连接状态、应用定义和实例清单。`;
  }

  if (snapshot.phase === "settings_required") {
    return "当前还没有可用于启动 Host 的可执行文件路径，请前往“设置”补全后再试。";
  }

  if (snapshot.phase === "launch_available") {
    return "Monitor 会继续扫描当前数据目录；如果 Host 尚未运行，你也可以直接从这里启动。";
  }

  return "Monitor 会持续验证当前数据目录中的运行时信息，并在发现可用 Host 后自动进入管理视图。";
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

export function formatHostLogDirectory(effectiveDataDir?: string | null): string {
  const normalized = effectiveDataDir?.trim();
  if (!normalized) {
    return "加载中";
  }

  const trimmed = normalized.replace(/[\\/]+$/, "");
  const separator = trimmed.includes("\\") && !trimmed.includes("/") ? "\\" : "/";
  return `${trimmed}${separator}logs`;
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
