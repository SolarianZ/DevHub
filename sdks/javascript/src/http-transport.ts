import {
  DevHubConnectionError,
  DevHubRpcError,
  isAbortError,
} from "./errors.js";
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
      const response = await sendHttpRequest(
        this.connection.rpcEndpoint,
        this.connection.token,
        this.options,
        payload,
        controller,
        requestId,
      );

      const body = await readResponseBody(response, controller, requestId);
      if (!response.ok) {
        throw new DevHubConnectionError({
          kind: "http_status",
          message: `HTTP request failed with status ${response.status} ${response.statusText}.`,
          requestId,
          status: response.status,
          statusText: response.statusText,
          responseBody: body
        });
      }

      let root: unknown;
      try {
        root = JSON.parse(body) as unknown;
      } catch (error) {
        throw new DevHubConnectionError({
          kind: "invalid_response",
          message: "Failed to parse JSON-RPC response.",
          cause: error,
          requestId
        });
      }

      try {
        return validateResponseEnvelope(root, requestId);
      } catch (error) {
        if (error instanceof DevHubRpcError) {
          throw error;
        }

        throw new DevHubConnectionError({
          kind: "invalid_response",
          message: error instanceof Error ? error.message : "Invalid JSON-RPC response.",
          cause: error,
          requestId
        });
      }
    } finally {
      if (timeoutId) {
        clearTimeout(timeoutId);
      }
    }
  }

  async dispose(): Promise<void> {
  }
}

async function sendHttpRequest(
  endpoint: string,
  token: string,
  options: NormalizedDevHubClientOptions,
  payload: Record<string, unknown>,
  controller: AbortController,
  requestId: string,
): Promise<Response> {
  try {
    return await fetch(endpoint, {
      method: "POST",
      headers: {
        "Content-Type": "application/json",
        Authorization: `Bearer ${token}`,
        "X-DevHub-Protocol": String(options.protocolVersion),
        "X-DevHub-ClientId": options.clientId,
        "X-DevHub-ClientSessionId": options.clientSessionId
      },
      body: JSON.stringify(payload),
      signal: controller.signal
    });
  } catch (error) {
    throw mapTransportFailure(error, controller, requestId);
  }
}

async function readResponseBody(
  response: Response,
  controller: AbortController,
  requestId: string,
): Promise<string> {
  try {
    return await response.text();
  } catch (error) {
    throw mapTransportFailure(error, controller, requestId);
  }
}

function mapTransportFailure(
  error: unknown,
  controller: AbortController,
  requestId: string,
): DevHubConnectionError {
  if (controller.signal.aborted || isAbortError(error)) {
    return new DevHubConnectionError({
      kind: "timeout",
      message: "HTTP request timed out.",
      cause: error,
      requestId
    });
  }

  return new DevHubConnectionError({
    kind: "transport",
    message: "HTTP transport failed.",
    cause: error,
    requestId
  });
}
