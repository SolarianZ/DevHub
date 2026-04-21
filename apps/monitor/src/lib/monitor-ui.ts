import {
  DevHubRpcError,
  DevHubRpcErrorCode,
  type AppDefinition,
  type AppDefinitionIdentity,
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
} from "./models";
export type MonitorWorkspace = "home" | "help" | "settings" | "definition";
export type SidebarWorkspace = Exclude<MonitorWorkspace, "definition">;
export type HomeWorkspaceMode = "discovery" | "status";
export type HostSessionStatus = "idle" | "connecting" | "connected" | "recovering";
export type DefinitionWorkspaceMode = "view" | "create" | "edit";

export interface DefinitionWorkspaceState {
  mode: DefinitionWorkspaceMode;
  title: string;
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

export function areMonitorSettingsEqual(
  left?: MonitorSettings | null,
  right?: MonitorSettings | null,
): boolean {
  return normalizeOptionalInput(left?.dataDirOverride) === normalizeOptionalInput(right?.dataDirOverride)
    && normalizeOptionalInput(left?.hostExecutablePath) === normalizeOptionalInput(right?.hostExecutablePath)
    && (left?.hideHostCommandLineWindow ?? true) === (right?.hideHostCommandLineWindow ?? true);
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
  return [...definitions].sort((left, right) => {
    const appCompare = left.appId.localeCompare(right.appId, "zh-CN");
    if (appCompare !== 0) {
      return appCompare;
    }

    const scopeCompare = formatScope(left.scope).localeCompare(formatScope(right.scope), "zh-CN");
    if (scopeCompare !== 0) {
      return scopeCompare;
    }

    return left.displayName.localeCompare(right.displayName, "zh-CN");
  });
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
    ...current.filter((definition) => !isSameDefinitionIdentity(definition, nextDefinition)),
    nextDefinition,
  ]);
}

export function removeDefinition(
  current: readonly AppDefinition[],
  identity: AppDefinitionIdentity,
): AppDefinition[] {
  return current.filter((definition) => !isSameDefinitionIdentity(definition, identity));
}

export function createDefinitionIdentity(appId: string, scope?: string | null): AppDefinitionIdentity {
  return {
    appId,
    scope: normalizeOptionalInput(scope),
  };
}

export function definitionIdentityKey(identity: Pick<AppDefinitionIdentity, "appId" | "scope">): string {
  return JSON.stringify([identity.appId, normalizeOptionalInput(identity.scope)]);
}

export function isSameDefinitionIdentity(
  left: Pick<AppDefinitionIdentity, "appId" | "scope">,
  right: Pick<AppDefinitionIdentity, "appId" | "scope">,
): boolean {
  return definitionIdentityKey(left) === definitionIdentityKey(right);
}

export function createMissingDefinitionForm(identity: AppDefinitionIdentity): DefinitionFormState {
  const form = createEmptyDefinitionForm();
  form.appId = identity.appId;
  form.scope = identity.scope ?? "";
  form.enableRpc = false;
  return form;
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

export function formatScope(scope?: string | null): string {
  return scope && scope.trim() ? scope : "global";
}

export function formatDefinitionScopeLabel(scope?: string | null): string {
  return `scope：${formatScope(scope)}`;
}

export function formatDefinitionIdentity(identity: Pick<AppDefinitionIdentity, "appId" | "scope">): string {
  return `${identity.appId}（${formatDefinitionScopeLabel(identity.scope)}）`;
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
