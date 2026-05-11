import type { JsonValue } from "./models.js";

export interface DevHubCalleeError {
  code: number;
  message: string;
  data?: JsonValue;
}

export type DevHubConnectionErrorKind =
  | "timeout"
  | "transport"
  | "http_status"
  | "invalid_response"
  | "session_terminated";

export interface DevHubConnectionErrorInit {
  kind: DevHubConnectionErrorKind;
  message: string;
  cause?: unknown;
  requestId?: string;
  status?: number;
  statusText?: string;
  responseBody?: string;
}

export enum DevHubRpcErrorCode {
  ParseError = -32700,
  InvalidRequest = -32600,
  MethodNotFound = -32601,
  InvalidParams = -32602,
  InternalError = -32603,
  Unauthorized = -32001,
  Forbidden = -32002,
  InstanceNotFound = -32010,
  InvocationExpired = -32011,
  InvocationTimeout = -32012,
  AppDefinitionNotFound = -32014,
  LaunchFailed = -32020,
  DeliveryConflict = -32030,
  RateLimited = -32040,
  InvocationFailed = -32050,
  NotSupported = -32099
}

export interface DevHubRpcErrorInit {
  code: number;
  message: string;
  data?: unknown;
  requestId: string;
}

export class DevHubRpcError extends Error {
  readonly code: number;
  readonly data?: unknown;
  readonly requestId: string;

  constructor(init: DevHubRpcErrorInit) {
    super(init.message);
    this.name = "DevHubRpcError";
    this.code = init.code;
    this.data = init.data;
    this.requestId = init.requestId;
  }

  get knownCode(): DevHubRpcErrorCode | null {
    return isKnownCode(this.code) ? (this.code as DevHubRpcErrorCode) : null;
  }

  is(code: DevHubRpcErrorCode): boolean {
    return this.code === code;
  }

  get reason(): string | null {
    if (isRecord(this.data)) {
      const value = this.data.reason;
      return typeof value === "string" ? value : null;
    }
    return null;
  }

  get invocationId(): string | null {
    if (isRecord(this.data)) {
      const value = this.data.invocationId;
      return typeof value === "string" ? value : null;
    }
    return null;
  }

  get calleeError(): DevHubCalleeError | null {
    if (!isRecord(this.data) || !isRecord(this.data.calleeError)) {
      return null;
    }

    const callee = this.data.calleeError;
    const code = callee.code;
    const message = callee.message;

    if (!Number.isInteger(code) || typeof message !== "string" || !message.trim()) {
      return null;
    }

    return {
      code: code as number,
      message,
      ...("data" in callee ? { data: callee.data as JsonValue } : {})
    };
  }

  tryGetDataProperty(propertyName: string): unknown {
    if (typeof propertyName !== "string" || !propertyName.trim()) {
      throw new Error("propertyName 不能为空。");
    }

    if (isRecord(this.data) && propertyName in this.data) {
      return this.data[propertyName];
    }

    return undefined;
  }

  tryGetDataString(propertyName: string): string | null {
    const value = this.tryGetDataProperty(propertyName);
    return typeof value === "string" ? value : null;
  }
}

export class DevHubConnectionError extends Error {
  readonly kind: DevHubConnectionErrorKind;
  readonly cause?: unknown;
  readonly requestId?: string;
  readonly status?: number;
  readonly statusText?: string;
  readonly responseBody?: string;

  constructor(init: DevHubConnectionErrorInit) {
    super(init.message);
    this.name = "DevHubConnectionError";
    this.kind = init.kind;
    this.cause = init.cause;
    this.requestId = init.requestId;
    this.status = init.status;
    this.statusText = init.statusText;
    this.responseBody = init.responseBody;
  }
}

export function normalizeConnectionError(
  error: unknown,
  fallback: DevHubConnectionErrorInit,
): DevHubConnectionError {
  if (error instanceof DevHubConnectionError) {
    return error;
  }

  return new DevHubConnectionError({
    ...fallback,
    cause: fallback.cause ?? error,
  });
}

export function isAbortError(error: unknown): boolean {
  return typeof error === "object"
    && error !== null
    && "name" in error
    && error.name === "AbortError";
}

const KNOWN_CODES = new Set<number>([
  DevHubRpcErrorCode.ParseError,
  DevHubRpcErrorCode.InvalidRequest,
  DevHubRpcErrorCode.MethodNotFound,
  DevHubRpcErrorCode.InvalidParams,
  DevHubRpcErrorCode.InternalError,
  DevHubRpcErrorCode.Unauthorized,
  DevHubRpcErrorCode.Forbidden,
  DevHubRpcErrorCode.InstanceNotFound,
  DevHubRpcErrorCode.InvocationExpired,
  DevHubRpcErrorCode.InvocationTimeout,
  DevHubRpcErrorCode.AppDefinitionNotFound,
  DevHubRpcErrorCode.LaunchFailed,
  DevHubRpcErrorCode.DeliveryConflict,
  DevHubRpcErrorCode.RateLimited,
  DevHubRpcErrorCode.InvocationFailed,
  DevHubRpcErrorCode.NotSupported
]);

function isKnownCode(code: number): boolean {
  return KNOWN_CODES.has(code);
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}
