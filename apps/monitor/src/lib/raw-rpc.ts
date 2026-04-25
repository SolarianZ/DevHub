import type { MonitorRuntimeConnectionInfo } from "./models";
import {
  type RpcTestRequestId,
  type ValidatedRpcTestEnvelope,
  validateRpcTestEnvelope,
} from "./rpc-test";

const RPC_TEST_CLIENT_ID = "devhub-monitor-ui-rpc-test";
const DEFAULT_CLIENT_SESSION_ID_KEY = Symbol.for("@devhub/monitor/defaultRpcTestClientSessionId");

interface SendRawRpcRequestOptions {
  signal?: AbortSignal;
}

interface DefaultClientSessionState {
  [DEFAULT_CLIENT_SESSION_ID_KEY]?: string;
}

export async function sendRawRpcRequest(
  connection: MonitorRuntimeConnectionInfo,
  envelope: ValidatedRpcTestEnvelope,
  options: SendRawRpcRequestOptions = {},
): Promise<string> {
  if (!connection?.rpcEndpoint || !connection.token) {
    throw new Error("当前没有可用的 Host RPC 连接信息。");
  }

  const validation = validateRpcTestEnvelope(envelope);
  if (!validation.ok) {
    throw new Error(validation.error);
  }

  const response = await fetch(connection.rpcEndpoint, {
    method: "POST",
    headers: {
      "Content-Type": "application/json",
      Authorization: `Bearer ${connection.token}`,
      "X-DevHub-Protocol": String(connection.runtime.protocolVersion),
      "X-DevHub-ClientId": RPC_TEST_CLIENT_ID,
      "X-DevHub-ClientSessionId": getDefaultClientSessionId(),
    },
    body: JSON.stringify(validation.envelope),
    signal: options.signal,
  });

  const responseText = await response.text();
  if (!response.ok) {
    throw new Error(`Host RPC 请求失败：HTTP ${response.status} ${response.statusText}。`);
  }

  validateResponseEnvelope(responseText, validation.envelope.id);
  return responseText;
}

function validateResponseEnvelope(responseText: string, requestId: RpcTestRequestId): void {
  let payload: unknown;
  try {
    payload = JSON.parse(responseText) as unknown;
  } catch {
    throw new Error("Host 返回的响应不是合法 JSON。");
  }

  if (!isRecord(payload)) {
    throw new Error("Host 返回的响应根节点必须是 JSON 对象。");
  }

  if (payload.jsonrpc !== "2.0") {
    throw new Error("Host 返回的响应缺少合法的 jsonrpc 版本。");
  }

  if (!("id" in payload) || !isRequestId(payload.id)) {
    throw new Error("Host 返回的响应缺少合法的 id。");
  }

  if (String(payload.id) !== String(requestId)) {
    throw new Error("Host 返回的响应 id 与请求不匹配。");
  }

  const hasResult = Object.prototype.hasOwnProperty.call(payload, "result");
  const hasError = payload.error !== null && payload.error !== undefined;
  if (hasResult === hasError) {
    throw new Error("Host 返回的响应必须且只能包含 result 或 error。");
  }

  if (hasError && !isRecord(payload.error)) {
    throw new Error("Host 返回的错误响应格式无效。");
  }
}

function getDefaultClientSessionId(): string {
  const state = globalThis as typeof globalThis & DefaultClientSessionState;
  state[DEFAULT_CLIENT_SESSION_ID_KEY] ??= crypto.randomUUID();
  return state[DEFAULT_CLIENT_SESSION_ID_KEY];
}

function isRequestId(value: unknown): value is RpcTestRequestId {
  return typeof value === "string" || typeof value === "number";
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}
