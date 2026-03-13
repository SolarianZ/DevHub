import { promises as fs } from "node:fs";
import os from "node:os";
import path from "node:path";

export const RUNTIME_DIR_ENV = "DEVHUB_RUNTIME_DIR";

export interface HubRuntimeTuning {
  leaseSeconds: number;
  onlineThresholdSeconds: number;
  launchDedupeWindowSeconds: number;
}

export interface HubRuntime {
  protocolVersion: number;
  pid: number;
  httpBaseUrl: string;
  wsUrl: string;
  tokenFile: string;
  startedAtUtc: Date;
  runtimeTuning: HubRuntimeTuning;
  hubVersion?: string;
}

export interface RuntimeConnectionInfo {
  runtimeDirectory: string;
  token: string;
  runtime: HubRuntime;
  rpcEndpoint: string;
  websocketEndpoint: string;
}

export interface RuntimeResolver {
  resolve(runtimeDirOverride?: string): Promise<RuntimeConnectionInfo>;
}

export class FileSystemRuntimeResolver implements RuntimeResolver {
  async resolve(runtimeDirOverride?: string): Promise<RuntimeConnectionInfo> {
    return discoverRuntime(runtimeDirOverride);
  }
}

export function resolveRuntimeDirectory(runtimeDirOverride?: string): string {
  if (runtimeDirOverride && runtimeDirOverride.trim()) {
    return path.resolve(runtimeDirOverride);
  }

  const envRuntimeDir = process.env[RUNTIME_DIR_ENV];
  if (envRuntimeDir && envRuntimeDir.trim()) {
    return path.resolve(envRuntimeDir);
  }

  const platform = process.platform;
  const home = os.homedir();

  if (platform === "win32") {
    const base = process.env.LOCALAPPDATA ?? path.join(home, "AppData", "Local");
    return path.resolve(base, "DevHub", "runtime");
  }

  if (platform === "darwin") {
    return path.resolve(home, "Library", "Application Support", "DevHub", "runtime");
  }

  const xdgDataHome = process.env.XDG_DATA_HOME;
  const base = xdgDataHome && xdgDataHome.trim() ? xdgDataHome : path.join(home, ".local", "share");
  return path.resolve(base, "DevHub", "runtime");
}

export async function discoverRuntime(runtimeDirOverride?: string): Promise<RuntimeConnectionInfo> {
  const runtimeRootDirectory = resolveRuntimeDirectory(runtimeDirOverride);
  const { runtimeDirectory, hubJsonPath } = await resolveHubRuntimePaths(runtimeRootDirectory);

  let hubJsonText: string;
  try {
    hubJsonText = await fs.readFile(hubJsonPath, "utf-8");
  } catch {
    throw new Error(`未找到 hub.json：${hubJsonPath}`);
  }

  let payload: unknown;
  try {
    payload = JSON.parse(hubJsonText) as unknown;
  } catch {
    throw new Error(`hub.json 解析失败：${hubJsonPath}`);
  }

  const runtime = parseHubRuntime(payload, hubJsonPath);

  if (!path.isAbsolute(runtime.tokenFile)) {
    throw new Error(`hub.json.tokenFile 非法：${hubJsonPath}`);
  }

  let tokenText: string;
  try {
    tokenText = await fs.readFile(runtime.tokenFile, "utf-8");
  } catch {
    throw new Error(`未找到 token 文件：${runtime.tokenFile}`);
  }

  const token = tokenText.trim();
  if (!token) {
    throw new Error(`token 文件为空：${runtime.tokenFile}`);
  }

  return {
    runtimeDirectory,
    token,
    runtime,
    rpcEndpoint: `${runtime.httpBaseUrl}/rpc`,
    websocketEndpoint: runtime.wsUrl
  };
}

async function resolveHubRuntimePaths(runtimeRootDirectory: string): Promise<{
  runtimeDirectory: string;
  hubJsonPath: string;
}> {
  const standardRuntimeDirectory = path.join(runtimeRootDirectory, "runtime");
  const standardHubJsonPath = path.join(standardRuntimeDirectory, "hub.json");
  if (await fileExists(standardHubJsonPath)) {
    return {
      runtimeDirectory: standardRuntimeDirectory,
      hubJsonPath: standardHubJsonPath
    };
  }

  const legacyHubJsonPath = path.join(runtimeRootDirectory, "hub.json");
  return {
    runtimeDirectory: runtimeRootDirectory,
    hubJsonPath: legacyHubJsonPath
  };
}

function parseHubRuntime(payload: unknown, source: string): HubRuntime {
  if (!isRecord(payload)) {
    throw new Error(`hub.json 解析失败：${source}`);
  }

  const protocolVersion = readNumber(payload, "protocolVersion", source);
  if (protocolVersion !== 1) {
    throw new Error(`hub.json.protocolVersion 非法：${source}`);
  }

  const pid = readInteger(payload, "pid", source);
  if (pid < 1) {
    throw new Error(`hub.json.pid 非法：${source}`);
  }

  const httpBaseUrl = readString(payload, "httpBaseUrl", source);
  validateHttpBaseUrl(httpBaseUrl, source);

  const wsUrl = readString(payload, "wsUrl", source);
  validateWebSocketUrl(wsUrl, source);

  const tokenFile = readString(payload, "tokenFile", source);

  const startedAtUtcRaw = readString(payload, "startedAtUtc", source);
  const startedAtUtc = new Date(startedAtUtcRaw);
  if (Number.isNaN(startedAtUtc.getTime())) {
    throw new Error(`hub.json.startedAtUtc 非法：${source}`);
  }

  const runtimeTuning = parseRuntimeTuning(payload.runtimeTuning, source);

  const hubVersion = typeof payload.hubVersion === "string" && payload.hubVersion.trim()
    ? payload.hubVersion
    : undefined;

  return {
    protocolVersion,
    pid,
    httpBaseUrl,
    wsUrl,
    tokenFile,
    startedAtUtc,
    runtimeTuning,
    hubVersion
  };
}

function parseRuntimeTuning(payload: unknown, source: string): HubRuntimeTuning {
  if (!isRecord(payload)) {
    throw new Error(`hub.json.runtimeTuning 非法：${source}`);
  }

  const leaseSeconds = readInteger(payload, "leaseSeconds", source, "hub.json.runtimeTuning 非法");
  const onlineThresholdSeconds = readInteger(payload, "onlineThresholdSeconds", source, "hub.json.runtimeTuning 非法");
  const launchDedupeWindowSeconds = readInteger(payload, "launchDedupeWindowSeconds", source, "hub.json.runtimeTuning 非法");

  if (leaseSeconds < 1 || onlineThresholdSeconds < 1 || launchDedupeWindowSeconds < 1) {
    throw new Error(`hub.json.runtimeTuning 非法：${source}`);
  }

  return {
    leaseSeconds,
    onlineThresholdSeconds,
    launchDedupeWindowSeconds
  };
}

function validateHttpBaseUrl(value: string, source: string): void {
  if (!value || value.endsWith("/")) {
    throw new Error(`hub.json.httpBaseUrl 非法：${source}`);
  }

  let url: URL;
  try {
    url = new URL(value);
  } catch {
    throw new Error(`hub.json.httpBaseUrl 非法：${source}`);
  }

  if ((url.protocol !== "http:" && url.protocol !== "https:") || !isLoopbackHost(url.hostname)) {
    throw new Error(`hub.json.httpBaseUrl 非法：${source}`);
  }
}

function validateWebSocketUrl(value: string, source: string): void {
  if (!value || value.endsWith("/")) {
    throw new Error(`hub.json.wsUrl 非法：${source}`);
  }

  let url: URL;
  try {
    url = new URL(value);
  } catch {
    throw new Error(`hub.json.wsUrl 非法：${source}`);
  }

  if ((url.protocol !== "ws:" && url.protocol !== "wss:") || !isLoopbackHost(url.hostname)) {
    throw new Error(`hub.json.wsUrl 非法：${source}`);
  }
}

function isLoopbackHost(host: string): boolean {
  const normalized = host.replace(/^\[(.*)\]$/, "$1").toLowerCase();
  return normalized === "localhost" || normalized === "127.0.0.1" || normalized === "::1";
}

async function fileExists(target: string): Promise<boolean> {
  try {
    await fs.access(target);
    return true;
  } catch {
    return false;
  }
}

function readString(payload: Record<string, unknown>, key: string, source: string): string {
  const value = payload[key];
  if (typeof value !== "string" || !value.trim()) {
    throw new Error(`hub.json.${key} 非法：${source}`);
  }
  return value;
}

function readNumber(payload: Record<string, unknown>, key: string, source: string, overrideMessage?: string): number {
  const value = payload[key];
  if (typeof value !== "number" || Number.isNaN(value)) {
    const message = overrideMessage ? `${overrideMessage}：${source}` : `hub.json.${key} 非法：${source}`;
    throw new Error(message);
  }
  return value;
}

function readInteger(payload: Record<string, unknown>, key: string, source: string, overrideMessage?: string): number {
  const value = readNumber(payload, key, source, overrideMessage);
  if (!Number.isInteger(value)) {
    const message = overrideMessage ? `${overrideMessage}：${source}` : `hub.json.${key} 非法：${source}`;
    throw new Error(message);
  }

  return value;
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}
