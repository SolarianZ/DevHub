import { DevHubRpcError } from "./errors.js";
import { ensureRecord, isRecord } from "./validation.js";
import { createRandomUuid } from "./web-crypto.js";

export interface ResponseEnvelope {
  requestId: string;
  result: Record<string, unknown>;
  error?: Record<string, unknown>;
}

export interface PendingRequest {
  promise: Promise<Record<string, unknown>>;
  resolve: (value: Record<string, unknown>) => void;
  reject: (reason?: unknown) => void;
  timeoutId?: NodeJS.Timeout;
}

export function createHttpRequestId(): string {
  return `req-${createRandomUuid().replace(/-/g, "")}`;
}

export function createWebSocketRequestId(): string {
  return `ws-${createRandomUuid().replace(/-/g, "")}`;
}

export function startTimeout(controller: AbortController, timeoutMs?: number): NodeJS.Timeout | undefined {
  if (!timeoutMs || timeoutMs <= 0) {
    return undefined;
  }

  return setTimeout(() => controller.abort(), timeoutMs);
}

export function validateResponseEnvelope(payload: unknown, requestId: string): Record<string, unknown> {
  const root = ensureRecord(payload, "JSON-RPC response");
  if (root.jsonrpc !== "2.0") {
    throw new Error("JSON-RPC response has an invalid jsonrpc version.");
  }

  const responseId = readResponseId(root, "JSON-RPC response");
  if (responseId !== requestId) {
    throw new Error("JSON-RPC response id does not match the request id.");
  }

  const hasResult = "result" in root;
  const hasError = "error" in root && root.error !== null && root.error !== undefined;
  if (hasResult === hasError) {
    throw new Error("JSON-RPC response must contain exactly one of result or error.");
  }

  if (hasError) {
    throw buildRpcError(root.error as unknown, requestId);
  }

  const result = root.result as unknown;
  if (!isRecord(result)) {
    throw new Error("JSON-RPC result must be an object.");
  }

  return result;
}

export function tryGetResponse(root: Record<string, unknown>): ResponseEnvelope | null {
  if (!("id" in root)) {
    return null;
  }

  const requestId = readResponseId(root, "WebSocket JSON-RPC response");
  const hasResult = "result" in root;
  const hasError = "error" in root && root.error !== null && root.error !== undefined;
  if (!hasResult && !hasError) {
    return null;
  }

  if (hasResult === hasError) {
    throw new Error("WebSocket JSON-RPC response must contain exactly one of result or error.");
  }

  if (hasResult) {
    const result = root.result as unknown;
    if (!isRecord(result)) {
      throw new Error("WebSocket JSON-RPC result must be an object.");
    }

    return {
      requestId,
      result
    };
  }

  const error = root.error as unknown;
  if (!isRecord(error)) {
    throw new Error("WebSocket JSON-RPC error must be an object.");
  }

  return {
    requestId,
    result: {},
    error
  };
}

export function tryGetEventParams(root: Record<string, unknown>): Record<string, unknown> | null {
  if (root.method !== "hub.event") {
    return null;
  }

  if ("id" in root) {
    throw new Error("hub.event must be delivered as a notification.");
  }

  if ("result" in root || ("error" in root && root.error !== null && root.error !== undefined)) {
    throw new Error("hub.event notification must not contain result or error.");
  }

  if (!("params" in root) || !isRecord(root.params)) {
    throw new Error("hub.event.params must be an object.");
  }

  return root.params as Record<string, unknown>;
}

export function validateIncomingEnvelope(root: Record<string, unknown>): void {
  if (root.jsonrpc !== "2.0") {
    throw new Error("WebSocket JSON-RPC message has an invalid jsonrpc version.");
  }
}

export function buildRpcError(payload: unknown, requestId: string): DevHubRpcError {
  if (!isRecord(payload)) {
    throw new Error("JSON-RPC error must be an object.");
  }

  const code = payload.code;
  if (!Number.isInteger(code)) {
    throw new Error("JSON-RPC error.code must be an integer.");
  }

  const message = payload.message;
  if (typeof message !== "string" || !message.trim()) {
    throw new Error("JSON-RPC error.message must be a non-empty string.");
  }

  const data = payload.data;
  if (data !== undefined && !isRecord(data)) {
    throw new Error("JSON-RPC error.data must be an object when present.");
  }

  return new DevHubRpcError({
    code: code as number,
    message,
    data,
    requestId
  });
}

export function readResponseId(root: Record<string, unknown>, location: string): string {
  if (!("id" in root)) {
    throw new Error(`${location} is missing id.`);
  }

  const id = root.id;
  if (typeof id === "string") {
    return id;
  }

  if (typeof id === "number") {
    return String(id);
  }

  throw new Error(`${location} has an invalid id type.`);
}

export function createPendingRequest(timeoutMs: number | undefined, onTimeout: () => void): PendingRequest {
  let resolve: (value: Record<string, unknown>) => void = () => {};
  let reject: (reason?: unknown) => void = () => {};

  const promise = new Promise<Record<string, unknown>>((resolveFn, rejectFn) => {
    resolve = resolveFn;
    reject = rejectFn;
  });

  let timeoutId: NodeJS.Timeout | undefined;
  if (timeoutMs && timeoutMs > 0) {
    timeoutId = setTimeout(() => {
      onTimeout();
      reject(new Error("WebSocket request timed out."));
    }, timeoutMs);
  }

  return {
    promise,
    resolve,
    reject,
    timeoutId
  };
}
