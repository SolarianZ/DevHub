import type { NormalizedDevHubClientOptions } from "./models.js";
import { createHttpRequestId, startTimeout, validateResponseEnvelope } from "./jsonrpc.js";
import type { RuntimeConnectionInfo } from "./runtime.js";

export class JsonRpcHttpTransport {
  constructor(
    private readonly options: NormalizedDevHubClientOptions,
    private readonly connection: RuntimeConnectionInfo
  ) {
  }

  async send(method: string, params?: Record<string, unknown> | null): Promise<Record<string, unknown>> {
    if (!method || !method.trim()) {
      throw new Error("method cannot be empty.");
    }

    const requestId = createHttpRequestId();
    const payload: Record<string, unknown> = {
      jsonrpc: "2.0",
      id: requestId,
      method
    };
    if (params !== undefined) {
      payload.params = params;
    }

    const controller = new AbortController();
    const timeoutId = startTimeout(controller, this.options.requestTimeoutMs);

    try {
      const response = await fetch(this.connection.rpcEndpoint, {
        method: "POST",
        headers: {
          "Content-Type": "application/json",
          Authorization: `Bearer ${this.connection.token}`,
          "X-DevHub-Protocol": String(this.options.protocolVersion),
          "X-DevHub-ClientId": this.options.clientId,
          "X-DevHub-ClientSessionId": this.options.clientSessionId
        },
        body: JSON.stringify(payload),
        signal: controller.signal
      });

      const body = await response.text();
      if (!response.ok) {
        throw new Error(`HTTP request failed: ${response.status} ${response.statusText}; body: ${body}`);
      }

      let root: unknown;
      try {
        root = JSON.parse(body) as unknown;
      } catch (error) {
        throw new Error("Failed to parse JSON-RPC response.", { cause: error });
      }

      return validateResponseEnvelope(root, requestId);
    } finally {
      if (timeoutId) {
        clearTimeout(timeoutId);
      }
    }
  }

  async dispose(): Promise<void> {
  }
}
