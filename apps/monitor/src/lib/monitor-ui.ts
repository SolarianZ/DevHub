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

interface SemanticVersion {
  major: number;
  minor: number;
  patch: number;
  prerelease: readonly (number | string)[];
}

const GLOBAL_SCOPE_LABEL = "Global";
const MINIMUM_SUPPORTED_HUB_VERSION: SemanticVersion = {
  major: 0,
  minor: 7,
  patch: 0,
  prerelease: [],
};

export function getHomeWorkspaceMode(
  snapshot: BootstrapSnapshot | null,
): HomeWorkspaceMode {
  return snapshot?.phase === "host_available"
      && snapshot.connection
      && !getUnsupportedRuntimeMessage(snapshot.connection)
    ? "status"
    : "discovery";
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

export function getUnsupportedRuntimeMessage(
  connection?: MonitorRuntimeConnectionInfo | null,
): string | null {
  if (!connection) {
    return null;
  }

  const { protocolVersion, hubVersion } = connection.runtime;
  if (protocolVersion !== 1) {
    return `当前 Monitor 仅支持 protocolVersion=1 且 hubVersion >= 0.7.0 的 DevHub Host。检测到 protocolVersion=${protocolVersion}。`;
  }

  if (typeof hubVersion !== "string" || hubVersion.trim() === "") {
    return "当前 Monitor 仅支持 protocolVersion=1 且 hubVersion >= 0.7.0 的 DevHub Host。当前 Host 缺少可解析的 hubVersion。";
  }

  const parsedVersion = parseSemanticVersion(hubVersion);
  if (!parsedVersion) {
    return `当前 Monitor 仅支持 protocolVersion=1 且 hubVersion >= 0.7.0 的 DevHub Host。检测到不可解析的 hubVersion=${hubVersion}。`;
  }

  if (compareSemanticVersions(parsedVersion, MINIMUM_SUPPORTED_HUB_VERSION) < 0) {
    return `当前 Monitor 仅支持 protocolVersion=1 且 hubVersion >= 0.7.0 的 DevHub Host。检测到 hubVersion=${hubVersion}。`;
  }

  return null;
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

    const scopeCompare = compareDefinitionScopes(left.scope, right.scope);
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

    const scopeCompare = compareDefinitionScopes(left.scope, right.scope);
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
    scope: scope ?? "",
  };
}

export function definitionIdentityKey(identity: Pick<AppDefinitionIdentity, "appId" | "scope">): string {
  return JSON.stringify([identity.appId, identity.scope ?? ""]);
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
  return scope && scope.length > 0 ? scope : GLOBAL_SCOPE_LABEL;
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

function compareDefinitionScopes(left?: string | null, right?: string | null): number {
  const leftScope = left ?? "";
  const rightScope = right ?? "";
  if (leftScope === rightScope) {
    return 0;
  }

  if (leftScope === "") {
    return -1;
  }

  if (rightScope === "") {
    return 1;
  }

  return leftScope.localeCompare(rightScope, "zh-CN");
}

function parseSemanticVersion(value: string): SemanticVersion | null {
  const match = value.trim().match(
    /^(?<major>0|[1-9]\d*)\.(?<minor>0|[1-9]\d*)\.(?<patch>0|[1-9]\d*)(?:-(?<prerelease>[0-9A-Za-z.-]+))?(?:\+[0-9A-Za-z.-]+)?$/,
  );
  if (!match?.groups) {
    return null;
  }

  return {
    major: Number(match.groups.major),
    minor: Number(match.groups.minor),
    patch: Number(match.groups.patch),
    prerelease: parsePrereleaseIdentifiers(match.groups.prerelease),
  };
}

function parsePrereleaseIdentifiers(value?: string): readonly (number | string)[] {
  if (!value) {
    return [];
  }

  return value.split(".").map((identifier) => {
    if (/^(0|[1-9]\d*)$/.test(identifier)) {
      return Number(identifier);
    }

    return identifier;
  });
}

function compareSemanticVersions(left: SemanticVersion, right: SemanticVersion): number {
  const majorCompare = compareNumber(left.major, right.major);
  if (majorCompare !== 0) {
    return majorCompare;
  }

  const minorCompare = compareNumber(left.minor, right.minor);
  if (minorCompare !== 0) {
    return minorCompare;
  }

  const patchCompare = compareNumber(left.patch, right.patch);
  if (patchCompare !== 0) {
    return patchCompare;
  }

  if (left.prerelease.length === 0 && right.prerelease.length === 0) {
    return 0;
  }

  if (left.prerelease.length === 0) {
    return 1;
  }

  if (right.prerelease.length === 0) {
    return -1;
  }

  const maxLength = Math.max(left.prerelease.length, right.prerelease.length);
  for (let index = 0; index < maxLength; index += 1) {
    const leftIdentifier = left.prerelease[index];
    const rightIdentifier = right.prerelease[index];

    if (leftIdentifier === undefined) {
      return -1;
    }

    if (rightIdentifier === undefined) {
      return 1;
    }

    if (leftIdentifier === rightIdentifier) {
      continue;
    }

    const leftIsNumber = typeof leftIdentifier === "number";
    const rightIsNumber = typeof rightIdentifier === "number";
    if (leftIsNumber && rightIsNumber) {
      return compareNumber(leftIdentifier, rightIdentifier);
    }

    if (leftIsNumber) {
      return -1;
    }

    if (rightIsNumber) {
      return 1;
    }

    return leftIdentifier.localeCompare(rightIdentifier);
  }

  return 0;
}

function compareNumber(left: number, right: number): number {
  if (left === right) {
    return 0;
  }

  return left < right ? -1 : 1;
}
