import { promises as fs } from "node:fs";
import os from "node:os";
import path from "node:path";
import type { NormalizedDevHubClientOptions } from "./models.js";
import { parseDateTimeString } from "./validation.js";

export const DATA_DIR_ENV = "DEVHUB_DATA_DIR";

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
  resolve(options: Readonly<NormalizedDevHubClientOptions>): Promise<RuntimeConnectionInfo>;
}

export class FileSystemRuntimeResolver implements RuntimeResolver {
  async resolve(options: Readonly<NormalizedDevHubClientOptions>): Promise<RuntimeConnectionInfo> {
    return discoverRuntime(options.dataDir);
  }
}

export function resolveDataDirectory(dataDirOverride?: string): string {
  if (dataDirOverride && dataDirOverride.trim()) {
    return path.resolve(dataDirOverride);
  }

  const envDataDir = process.env[DATA_DIR_ENV];
  if (envDataDir && envDataDir.trim()) {
    return path.resolve(envDataDir);
  }

  const platform = process.platform;
  const home = os.homedir();

  if (platform === "win32") {
    const base = process.env.LOCALAPPDATA ?? path.join(home, "AppData", "Local");
    return path.resolve(base, "DevHub");
  }

  if (platform === "darwin") {
    return path.resolve(home, "Library", "Application Support", "DevHub");
  }

  const xdgDataHome = process.env.XDG_DATA_HOME;
  const base = xdgDataHome && xdgDataHome.trim() ? xdgDataHome : path.join(home, ".local", "share");
  return path.resolve(base, "DevHub");
}

export async function discoverRuntime(dataDirOverride?: string): Promise<RuntimeConnectionInfo> {
  const dataDirectory = resolveDataDirectory(dataDirOverride);
  const { runtimeDirectory, hubJsonPath } = await resolveHubRuntimePaths(dataDirectory);

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

async function resolveHubRuntimePaths(dataDirectory: string): Promise<{
  runtimeDirectory: string;
  hubJsonPath: string;
}> {
  const legacyHubJsonPath = path.join(dataDirectory, "hub.json");
  const legacyTokenFilePath = path.join(dataDirectory, "token.txt");
  const [hasLegacyHubJson, hasLegacyTokenFile] = await Promise.all([
    fileExists(legacyHubJsonPath),
    fileExists(legacyTokenFilePath)
  ]);

  if (hasLegacyHubJson || hasLegacyTokenFile) {
    throw new Error(
      `dataDir 必须指向数据根目录，不支持直接传入 runtime 子目录或旧版 hub.json/token.txt 直放布局：${dataDirectory}`
    );
  }

  return {
    runtimeDirectory: path.join(dataDirectory, "runtime"),
    hubJsonPath: path.join(dataDirectory, "runtime", "hub.json")
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
  const startedAtUtc = parseDateTimeString(startedAtUtcRaw, "hub.json.startedAtUtc");

  const runtimeTuning = parseRuntimeTuning(payload.runtimeTuning, source);

  const hubVersion = readOptionalString(payload, "hubVersion", source);

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

  if (url.username || url.password || url.pathname !== "/" || url.search || url.hash) {
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

function readOptionalString(payload: Record<string, unknown>, key: string, source: string): string | undefined {
  if (!(key in payload)) {
    return undefined;
  }

  const value = payload[key];
  if (value === undefined) {
    return undefined;
  }

  if (typeof value !== "string") {
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
