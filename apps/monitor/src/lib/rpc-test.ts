export type RpcTestRequestId = string | number;

export type ValidatedRpcTestEnvelope = Record<string, unknown> & {
  jsonrpc: "2.0";
  id: RpcTestRequestId;
  method: string;
  params?: Record<string, unknown> | unknown[];
};

export type RpcTestValidationResult =
  | {
    ok: true;
    envelope: ValidatedRpcTestEnvelope;
    message: string;
  }
  | {
    ok: false;
    error: string;
  };

const VALIDATION_SUCCESS_MESSAGE = "当前请求文本已通过校验。";
const CANONICAL_IDENTIFIER_PATTERN = "^[A-Za-z0-9_](?:[A-Za-z0-9_.-]*[A-Za-z0-9_])?$";
const CANONICAL_IDENTIFIER_REGEX = new RegExp(CANONICAL_IDENTIFIER_PATTERN);
const INSTANCE_ID_MAX_LENGTH = 256;
const INVOCATION_METHODS_WITH_TARGET_INSTANCE_ID = new Set([
  "hub.invoke.notify",
  "hub.invoke.request",
]);

export function getRpcTestDraftPlaceholder(): string {
  return [
    "{",
    '  "jsonrpc": "2.0",',
    '  "id": "req-1",',
    '  "method": "hub.ping",',
    '  "params": {}',
    "}",
  ].join("\n");
}

export function validateRpcTestDraft(draft: string): RpcTestValidationResult {
  if (typeof draft !== "string" || !draft.trim()) {
    return {
      ok: false,
      error: "请求文本不能为空。",
    };
  }

  let root: unknown;
  try {
    root = JSON.parse(draft) as unknown;
  } catch {
    return {
      ok: false,
      error: "请求文本不是合法 JSON。",
    };
  }

  return validateRpcTestEnvelope(root);
}

export function validateRpcTestEnvelope(payload: unknown): RpcTestValidationResult {
  if (Array.isArray(payload)) {
    return {
      ok: false,
      error: "暂不支持批量请求数组。",
    };
  }

  if (!isRecord(payload)) {
    return {
      ok: false,
      error: "请求根节点必须是 JSON 对象。",
    };
  }

  if (payload.jsonrpc !== "2.0") {
    return {
      ok: false,
      error: "jsonrpc 必须等于 \"2.0\"。",
    };
  }

  if (!("id" in payload) || !isRequestId(payload.id)) {
    return {
      ok: false,
      error: "id 为必填项，且必须是字符串或数字。",
    };
  }

  if (typeof payload.method !== "string" || !payload.method.trim()) {
    return {
      ok: false,
      error: "method 必须是非空字符串。",
    };
  }

  const params = payload.params;
  if (params !== undefined && !Array.isArray(params) && !isRecord(params)) {
    return {
      ok: false,
      error: "params 存在时必须是对象或数组。",
    };
  }

  if (payload.method.startsWith("hub.") && Array.isArray(params)) {
    return {
      ok: false,
      error: "hub.* 方法必须使用对象 params。",
    };
  }

  if (INVOCATION_METHODS_WITH_TARGET_INSTANCE_ID.has(payload.method)) {
    const invocationTargetValidation = validateInvocationTargetInstanceId(params);
    if (!invocationTargetValidation.ok) {
      return invocationTargetValidation;
    }
  }

  return {
    ok: true,
    envelope: payload as ValidatedRpcTestEnvelope,
    message: VALIDATION_SUCCESS_MESSAGE,
  };
}

function validateInvocationTargetInstanceId(params: unknown): { ok: true } | { ok: false; error: string } {
  if (!isRecord(params)) {
    return { ok: true };
  }

  const target = params.target;
  if (!isRecord(target) || !Object.prototype.hasOwnProperty.call(target, "instanceId")) {
    return { ok: true };
  }

  const instanceId = target.instanceId;
  if (instanceId === null || instanceId === undefined) {
    return { ok: true };
  }

  if (typeof instanceId !== "string" || !instanceId.trim()) {
    return {
      ok: false,
      error: "target.instanceId 存在时必须是非空字符串或 null。",
    };
  }

  if (instanceId.length > INSTANCE_ID_MAX_LENGTH || !CANONICAL_IDENTIFIER_REGEX.test(instanceId)) {
    return {
      ok: false,
      error: `target.instanceId 必须匹配 ${CANONICAL_IDENTIFIER_PATTERN}，且长度不能超过 ${INSTANCE_ID_MAX_LENGTH}。`,
    };
  }

  return { ok: true };
}

function isRequestId(value: unknown): value is RpcTestRequestId {
  return typeof value === "string" || typeof value === "number";
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}
