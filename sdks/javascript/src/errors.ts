export interface DevHubCalleeError {
  code: number;
  message: string;
  data?: unknown;
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

    if (typeof code !== "number" || Number.isNaN(code) || typeof message !== "string" || !message) {
      return null;
    }

    return {
      code,
      message,
      data: callee.data
    };
  }
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}
