import net from "node:net";
import type { RuntimeConnectionInfo } from "./runtime.js";

export function validateRuntimeConnectionInfo(connection: RuntimeConnectionInfo, source = "runtimeResolver"): void {
  if (!isRecord(connection)) {
    throw new Error(`${source} 返回的运行时连接信息非法。`);
  }

  if (!isRecord(connection.runtime)) {
    throw new Error(`${source}.runtime 非法。`);
  }

  validateHttpBaseUrl(connection.runtime.httpBaseUrl, source);
  validateWebSocketUrl(connection.runtime.wsUrl, source);

  if (connection.rpcEndpoint !== `${connection.runtime.httpBaseUrl}/rpc`) {
    throw new Error(`${source}.rpcEndpoint 非法。`);
  }

  if (connection.websocketEndpoint !== connection.runtime.wsUrl) {
    throw new Error(`${source}.websocketEndpoint 非法。`);
  }
}

export function validateHttpBaseUrl(value: string, source: string): void {
  if (!value || value.trim() !== value || value.endsWith("/") || value.includes("?") || value.includes("#")) {
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

export function validateWebSocketUrl(value: string, source: string): void {
  if (!value || value.trim() !== value || value.endsWith("/") || value.includes("?") || value.includes("#")) {
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

  if (url.username || url.password || url.pathname !== "/ws" || url.search || url.hash) {
    throw new Error(`hub.json.wsUrl 非法：${source}`);
  }
}

function isLoopbackHost(host: string): boolean {
  const normalized = host.replace(/^\[(.*)\]$/, "$1").toLowerCase();
  if (normalized === "localhost" || normalized === "::1") {
    return true;
  }

  return net.isIP(normalized) === 4 && normalized.startsWith("127.");
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}
