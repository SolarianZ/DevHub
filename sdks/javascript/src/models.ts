import { randomUUID } from "node:crypto";

export interface DevHubClientOptions {
  clientId: string;
  clientSessionId?: string;
  runtimeDir?: string;
  requestTimeoutMs?: number;
  protocolVersion?: number;
}

export interface NormalizedDevHubClientOptions {
  clientId: string;
  clientSessionId: string;
  runtimeDir?: string;
  requestTimeoutMs?: number;
  protocolVersion: number;
}

const UUID_REGEX = /^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i;

export function normalizeClientOptions(options: DevHubClientOptions): NormalizedDevHubClientOptions {
  const clientId = options.clientId ?? "";
  const clientSessionId = options.clientSessionId ?? randomUUID();
  const protocolVersion = options.protocolVersion ?? 1;

  return {
    clientId,
    clientSessionId,
    runtimeDir: options.runtimeDir,
    requestTimeoutMs: options.requestTimeoutMs,
    protocolVersion
  };
}

export function validateClientOptions(options: NormalizedDevHubClientOptions): void {
  if (!options.clientId || !options.clientId.trim()) {
    throw new Error("clientId 不能为空。");
  }

  if (!options.clientSessionId || !options.clientSessionId.trim()) {
    throw new Error("clientSessionId 不能为空。");
  }

  if (!UUID_REGEX.test(options.clientSessionId)) {
    throw new Error("clientSessionId 必须是有效的 UUID。");
  }

  if (options.protocolVersion !== 1) {
    throw new Error("当前仅支持协议版本 1。");
  }

  if (options.requestTimeoutMs !== undefined && options.requestTimeoutMs <= 0) {
    throw new Error("requestTimeoutMs 必须大于 0。");
  }
}
