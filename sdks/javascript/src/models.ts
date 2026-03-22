import { randomUUID } from "node:crypto";
import type { DevHubCalleeError } from "./errors.js";
import type { DevHubEventType } from "./event-types.js";

export type JsonPrimitive = string | number | boolean | null;
export type JsonValue = JsonPrimitive | JsonObject | JsonValue[];
export type JsonObject = { [key: string]: JsonValue };

export interface DevHubClientOptions {
  clientId: string;
  clientSessionId?: string;
  dataDir?: string;
  requestTimeoutMs?: number;
  protocolVersion?: number;
}

export interface NormalizedDevHubClientOptions {
  clientId: string;
  clientSessionId: string;
  dataDir?: string;
  requestTimeoutMs?: number;
  protocolVersion: number;
}

export interface PingResult {
  ok: true;
  serverTimeUtc: Date;
  echo?: JsonValue;
}

export interface AppCapabilities {
  rpc?: boolean;
  events?: boolean;
}

export interface LaunchConfiguration {
  exePath?: string;
  argsTemplate?: string;
  workingDirectory?: string;
  dedupeKeyTemplate?: string;
}

export interface AppDefinition {
  appId: string;
  displayName: string;
  description?: string;
  capabilities?: AppCapabilities;
  launch?: LaunchConfiguration;
}

export interface InvokeCapability {
  poll: boolean;
  respond: boolean;
}

export interface AppInstance {
  instanceId: string;
  appId: string;
  scope?: string | null;
  pid: number;
  registeredAtUtc: Date;
  lastSeenUtc: Date;
  invoke: InvokeCapability;
  meta?: JsonObject;
}

export interface AppInstanceRegistration {
  instanceId: string;
  appId: string;
  scope?: string | null;
  pid: number;
  invoke: InvokeCapability;
  meta?: JsonObject;
}

export interface LaunchRequest {
  appId: string;
  scope?: string | null;
  dedupeKey?: string | null;
  waitForRegisterMs?: number | null;
}

export type LaunchStatus = "started" | "starting" | "already_running";

export interface LaunchResult {
  ok: true;
  status: LaunchStatus;
  pid?: number | null;
  launchId: string;
}

export interface ListInstancesRequest {
  appId?: string;
  scope?: string | null;
  includeOffline?: boolean;
  includeAllScopes?: boolean;
}

export interface InvocationTarget {
  scope?: string | null;
  instanceId?: string | null;
}

export interface InvocationOptions {
  ttlMs?: number | null;
  waitTimeoutMs?: number | null;
  queueIfOffline?: boolean | null;
  autoLaunch?: boolean | null;
}

export interface InvokeRequest {
  appId: string;
  target?: InvocationTarget;
  method: string;
  args?: JsonValue;
  options?: InvocationOptions;
}

export interface NotifyResult {
  ok: true;
  invocationId: string;
}

export interface RequestResult {
  ok: true;
  invocationId: string;
  value: JsonValue;
}

export interface PollRequest {
  instanceId: string;
  maxCount?: number | null;
  waitMs?: number | null;
}

export interface InvocationDelivery {
  leaseSeconds: number;
  attempt: number;
}

export interface InvocationCaller {
  clientId: string;
  clientSessionId: string;
}

export type InvocationKind = "request" | "notify";

export interface Invocation {
  invocationId: string;
  appId: string;
  target?: InvocationTarget;
  method: string;
  args?: JsonValue;
  kind: InvocationKind;
  createdAtUtc: Date;
  options?: InvocationOptions;
  delivery?: InvocationDelivery;
  caller: InvocationCaller;
}

export interface PollResult {
  ok: true;
  serverTimeUtc: Date;
  items: Invocation[];
}

export interface RespondRequest {
  instanceId: string;
  invocationId: string;
  value?: JsonValue;
  error?: DevHubCalleeError;
}

export interface DevHubEvent {
  subscriptionId: string;
  type: DevHubEventType;
  timeUtc: Date;
  payload?: JsonObject;
}

const UUID_REGEX = /^[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i;

export function normalizeClientOptions(options: DevHubClientOptions): NormalizedDevHubClientOptions {
  if (!options || typeof options !== "object" || Array.isArray(options)) {
    throw new Error("options 不能为空。");
  }

  const clientId = options.clientId ?? "";
  const clientSessionId = options.clientSessionId ?? randomUUID();
  const protocolVersion = options.protocolVersion ?? 1;

  return {
    clientId,
    clientSessionId,
    dataDir: options.dataDir,
    requestTimeoutMs: options.requestTimeoutMs,
    protocolVersion
  };
}

export function validateClientOptions(options: NormalizedDevHubClientOptions): void {
  if (typeof options.clientId !== "string" || !options.clientId.trim()) {
    throw new Error("clientId 不能为空。");
  }

  if (typeof options.clientSessionId !== "string" || !options.clientSessionId.trim()) {
    throw new Error("clientSessionId 不能为空。");
  }

  if (!UUID_REGEX.test(options.clientSessionId)) {
    throw new Error("clientSessionId 必须是有效的 UUID。");
  }

  if (options.protocolVersion !== 1) {
    throw new Error("当前仅支持协议版本 1。");
  }

  if (
    options.requestTimeoutMs !== undefined
    && (typeof options.requestTimeoutMs !== "number" || Number.isNaN(options.requestTimeoutMs) || !Number.isInteger(options.requestTimeoutMs) || options.requestTimeoutMs <= 0)
  ) {
    throw new Error("requestTimeoutMs 必须为大于 0 的整数。");
  }
}
