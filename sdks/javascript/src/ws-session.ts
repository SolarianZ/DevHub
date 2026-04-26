import { Mutex } from "./async-utils.js";
import type { AbandonedRequestFilter } from "./abandoned-request-filter.js";
import {
  DevHubConnectionError,
  normalizeConnectionError,
} from "./errors.js";
import type { JsonRpcEventSession } from "./events-client.js";
import {
  buildRpcError,
  createPendingRequest,
  createWebSocketRequestId,
  type PendingRequest,
  tryGetEventParams,
  tryGetResponse,
  validateIncomingEnvelope
} from "./jsonrpc.js";
import { ensureRecord, isRecord } from "./validation.js";

export interface JsonRpcWsSessionOptions {
  websocketEndpoint: string;
  requestTimeoutMs?: number;
  onEvent?: (params: Record<string, unknown>) => void;
  onTerminate?: (error?: Error) => void;
}

interface AbandonedRequestEntry {
  requestId: string;
  method: string;
  abandonedAt: number;
  appId?: string;
}

export class JsonRpcWsSession implements JsonRpcEventSession {
  private socket: WebSocketLike | null = null;
  private socketCleanup: Array<() => void> = [];
  private connectPromise: Promise<void> | null = null;
  private readonly pendingRequests = new Map<string, PendingRequest>();
  private readonly abandonedRequests = new Map<string, AbandonedRequestEntry>();
  private readonly sendLock = new Mutex();
  private socketOpen = false;
  private disposed = false;

  constructor(private readonly options: JsonRpcWsSessionOptions) {
  }

  async ensureConnected(): Promise<void> {
    this.throwIfDisposed();
    if (this.socket && this.socketOpen) {
      return;
    }

    try {
      if (!this.connectPromise) {
        this.connectPromise = this.connect();
      }

      await this.connectPromise;
    } finally {
      if (this.connectPromise && (this.socketOpen || this.socket === null)) {
        this.connectPromise = null;
      }
    }
  }

  async sendRequest(method: string, params?: Record<string, unknown>): Promise<Record<string, unknown>> {
    this.throwIfDisposed();
    if (!method || !method.trim()) {
      throw new Error("method cannot be empty.");
    }

    await this.ensureConnected();
    const socket = this.socket;
    if (!socket || !this.socketOpen) {
      throw new DevHubConnectionError({
        kind: "session_terminated",
        message: "WebSocket connection is not established."
      });
    }

    const requestId = createWebSocketRequestId();
    const payload: Record<string, unknown> = {
      jsonrpc: "2.0",
      id: requestId,
      method
    };

    if (params !== undefined) {
      payload.params = params;
    }

    const abandonedRequestAppId = tryExtractAppId(params);
    const waiter = createPendingRequest(this.options.requestTimeoutMs, () => {
      this.markPendingRequestAsAbandoned(requestId, method, abandonedRequestAppId);
    });
    this.pendingRequests.set(requestId, waiter);

    try {
      await this.sendLock.run(() => {
        socket.send(JSON.stringify(payload));
      });

      return await waiter.promise;
    } catch (error) {
      if (this.pendingRequests.delete(requestId) && waiter.timeoutId) {
        clearTimeout(waiter.timeoutId);
      }
      throw error;
    }
  }

  /**
   * 获取当前会话内匹配条件的已放弃请求数量。
   * 该操作只读取本地维护状态，不会触发网络交互或连接状态变化。
   */
  getAbandonedRequestCount(filter?: AbandonedRequestFilter): number {
    this.throwIfDisposed();
    const normalizedFilter = normalizeAbandonedRequestFilter(filter);
    const now = Date.now();
    let count = 0;

    for (const entry of this.abandonedRequests.values()) {
      if (matchesAbandonedRequest(entry, normalizedFilter, now)) {
        count += 1;
      }
    }

    return count;
  }

  /**
   * 清理当前会话内匹配条件的已放弃请求记录。
   * 该操作只修改本地维护状态，不会触发网络交互或连接状态变化。
   */
  clearAbandonedRequests(filter?: AbandonedRequestFilter): number {
    this.throwIfDisposed();
    const normalizedFilter = normalizeAbandonedRequestFilter(filter);
    const now = Date.now();
    let removed = 0;

    for (const [requestId, entry] of this.abandonedRequests) {
      if (!matchesAbandonedRequest(entry, normalizedFilter, now)) {
        continue;
      }

      this.abandonedRequests.delete(requestId);
      removed += 1;
    }

    return removed;
  }

  async disconnect(reason: string): Promise<void> {
    this.rejectPending(createSessionTerminatedError("WebSocket connection closed."));
    this.abandonedRequests.clear();
    await this.closeSocket(reason);
  }

  async dispose(reason = "client_dispose"): Promise<void> {
    if (this.disposed) {
      return;
    }

    this.disposed = true;
    await this.disconnect(reason);
  }

  private attachSocketHandlers(socket: WebSocketLike): void {
    this.socketCleanup.push(addSocketListener(socket, "open", () => {
      if (this.socket === socket) {
        this.socketOpen = true;
      }
    }));

    this.socketCleanup.push(addSocketListener(socket, "message", (event, ...args) => {
      void this.handleMessage(event, args);
    }));

    this.socketCleanup.push(addSocketListener(socket, "close", (event, ...args) => {
      if (this.socket === socket) {
        this.socketOpen = false;
      }
      this.terminate(resolveCloseError(event, args));
    }));

    this.socketCleanup.push(addSocketListener(socket, "error", (event) => {
      if (this.socket === socket) {
        this.socketOpen = false;
      }
      const error = createTransportError("WebSocket transport failed.", event);
      this.terminate(error);
    }));
  }

  private async handleMessage(event: unknown, args: readonly unknown[] = []): Promise<void> {
    try {
      const text = readMessageText(event, args);
      if (!text.trim()) {
        throw new Error("WebSocket JSON-RPC message cannot be blank.");
      }

      const payload = JSON.parse(text) as unknown;
      const root = ensureRecord(payload, "WebSocket JSON-RPC message");
      validateIncomingEnvelope(root);

      const response = tryGetResponse(root);
      if (response) {
        this.completePending(response.requestId, response.result, response.error);
        return;
      }

      const params = tryGetEventParams(root);
      if (params && this.options.onEvent) {
        this.options.onEvent(params);
        return;
      }

      throw new Error("WebSocket JSON-RPC message is not a supported response or hub.event notification.");
    } catch (error) {
      this.terminate(normalizeConnectionError(error, {
        kind: "invalid_response",
        message: error instanceof Error ? error.message : "WebSocket message handling failed."
      }));
    }
  }

  private completePending(
    requestId: string,
    result: Record<string, unknown>,
    error?: Record<string, unknown>
  ): void {
    const pending = this.pendingRequests.get(requestId);
    if (!pending) {
      if (this.abandonedRequests.has(requestId)) {
        return;
      }

      this.terminate(new DevHubConnectionError({
        kind: "invalid_response",
        message: "WebSocket JSON-RPC response id does not match any pending request."
      }));
      return;
    }

    this.pendingRequests.delete(requestId);
    if (pending.timeoutId) {
      clearTimeout(pending.timeoutId);
    }

    if (error) {
      pending.reject(buildRpcError(error, requestId));
      return;
    }

    pending.resolve(result);
  }

  private terminate(error?: Error): void {
    const hadSocket = this.socket !== null;
    const hadPending = this.pendingRequests.size > 0;
    const rejectionError = normalizeConnectionError(error, {
      kind: "session_terminated",
      message: "WebSocket connection closed."
    });

    this.rejectPending(rejectionError);
    this.abandonedRequests.clear();
    void this.closeSocket("connection_closed");

    if (hadSocket || hadPending) {
      this.options.onTerminate?.(rejectionError);
    }
  }

  private async closeSocket(reason: string): Promise<void> {
    const socket = this.socket;
    if (!socket) {
      return;
    }

    this.socket = null;
    this.socketOpen = false;
    this.cleanupSocketHandlers();

    try {
      socket.close(1000, reason);
    } catch {
    }
  }

  private cleanupSocketHandlers(): void {
    for (const cleanup of this.socketCleanup) {
      cleanup();
    }
    this.socketCleanup = [];
  }

  private rejectPending(error: Error): void {
    for (const pending of this.pendingRequests.values()) {
      if (pending.timeoutId) {
        clearTimeout(pending.timeoutId);
      }
      pending.reject(error);
    }

    this.pendingRequests.clear();
  }

  private throwIfDisposed(): void {
    if (this.disposed) {
      throw new Error("WebSocket session has been disposed.");
    }
  }

  private async connect(): Promise<void> {
    const ctor = await resolveWebSocketConstructor();
    const socket = new ctor(this.options.websocketEndpoint);
    this.socket = socket;
    this.socketOpen = false;
    this.attachSocketHandlers(socket);

    try {
      await waitForWebSocketOpen(socket, this.options.requestTimeoutMs);
      if (this.socket !== socket || !this.socketOpen) {
        throw new DevHubConnectionError({
          kind: "transport",
          message: "WebSocket connection is not established."
        });
      }
    } catch (error) {
      this.connectPromise = null;
      await this.disconnect("connect_failed");
      throw error;
    }

    this.connectPromise = null;
  }

  private markPendingRequestAsAbandoned(requestId: string, method: string, appId?: string): void {
    if (this.pendingRequests.delete(requestId)) {
      this.abandonedRequests.set(requestId, {
        requestId,
        method,
        abandonedAt: Date.now(),
        appId
      });
    }
  }
}

function normalizeAbandonedRequestFilter(filter?: AbandonedRequestFilter | null): AbandonedRequestFilter | undefined {
  if (filter === undefined || filter === null) {
    return undefined;
  }

  if (!isRecord(filter)) {
    throw new Error("filter 必须为对象。");
  }

  const filterRecord = filter as Record<string, unknown>;
  const normalizedFilter: AbandonedRequestFilter = {};
  const olderThanMs = filterRecord.olderThanMs;
  const appId = filterRecord.appId;
  const method = filterRecord.method;

  if (olderThanMs !== undefined) {
    if (typeof olderThanMs !== "number" || !Number.isFinite(olderThanMs) || olderThanMs < 0) {
      throw new Error("filter.olderThanMs 必须为大于等于 0 的有限数字。");
    }

    normalizedFilter.olderThanMs = olderThanMs;
  }

  if (appId !== undefined) {
    if (typeof appId !== "string" || !appId.trim()) {
      throw new Error("filter.appId 必须为非空字符串。");
    }

    normalizedFilter.appId = appId;
  }

  if (method !== undefined) {
    if (typeof method !== "string" || !method.trim()) {
      throw new Error("filter.method 必须为非空字符串。");
    }

    normalizedFilter.method = method;
  }

  return normalizedFilter;
}

function matchesAbandonedRequest(
  entry: AbandonedRequestEntry,
  filter: AbandonedRequestFilter | undefined,
  now: number
): boolean {
  if (!filter) {
    return true;
  }

  if (filter.olderThanMs !== undefined && now - entry.abandonedAt < filter.olderThanMs) {
    return false;
  }

  if (filter.appId !== undefined && entry.appId !== filter.appId) {
    return false;
  }

  if (filter.method !== undefined && entry.method !== filter.method) {
    return false;
  }

  return true;
}

function tryExtractAppId(params?: Record<string, unknown>): string | undefined {
  const appId = params?.appId;
  if (typeof appId !== "string" || !appId.trim()) {
    return undefined;
  }

  return appId;
}

interface WebSocketLike {
  send(data: string): void;
  close(code?: number, reason?: string): void;
  readyState?: number;
  addEventListener?: (type: string, listener: (event: unknown, ...args: unknown[]) => void) => void;
  removeEventListener?: (type: string, listener: (event: unknown, ...args: unknown[]) => void) => void;
  on?: (type: string, listener: (event: unknown, ...args: unknown[]) => void) => void;
  off?: (type: string, listener: (event: unknown, ...args: unknown[]) => void) => void;
}

type WebSocketConstructor = new (url: string) => WebSocketLike;
const TEXT_DECODER = new TextDecoder();
const WEB_SOCKET_MODULE_NAME = "ws";
let webSocketConstructorPromise: Promise<WebSocketConstructor> | undefined;

async function resolveWebSocketConstructor(): Promise<WebSocketConstructor> {
  const globalCandidate = (globalThis as { WebSocket?: unknown }).WebSocket;
  if (typeof globalCandidate === "function") {
    return globalCandidate as WebSocketConstructor;
  }

  webSocketConstructorPromise ??= loadWebSocketConstructor();
  return webSocketConstructorPromise;
}

async function loadWebSocketConstructor(): Promise<WebSocketConstructor> {
  try {
    const wsModule = await import(
      /* @vite-ignore */ WEB_SOCKET_MODULE_NAME
    );
    const ctor = (wsModule.WebSocket ?? wsModule.default ?? wsModule) as unknown;
    if (typeof ctor !== "function") {
      throw new Error("ws module did not export a WebSocket constructor.");
    }

    return ctor as WebSocketConstructor;
  } catch (error) {
    throw new Error("WebSocket 不可用。请安装 ws，或提供全局 WebSocket 实现。", { cause: error });
  }
}

function addSocketListener(
  socket: WebSocketLike,
  event: string,
  handler: (event: unknown, ...args: unknown[]) => void
): () => void {
  if (typeof socket.addEventListener === "function" && typeof socket.removeEventListener === "function") {
    socket.addEventListener(event, handler);
    return () => {
      socket.removeEventListener?.(event, handler);
    };
  }

  if (typeof socket.on === "function" && typeof socket.off === "function") {
    socket.on(event, handler);
    return () => {
      socket.off?.(event, handler);
    };
  }

  throw new Error("The current WebSocket implementation does not support event subscriptions.");
}

function waitForWebSocketOpen(socket: WebSocketLike, timeoutMs?: number): Promise<void> {
  if (socket.readyState === 1) {
    return Promise.resolve();
  }

  return new Promise((resolve, reject) => {
    const cleanup: Array<() => void> = [];

    const finish = (error?: unknown) => {
      for (const item of cleanup) {
        item();
      }

      if (error) {
        reject(error);
      } else {
        resolve();
      }
    };

    cleanup.push(addSocketListener(socket, "open", () => finish()));
    cleanup.push(addSocketListener(socket, "error", (event) => {
      finish(createTransportError("WebSocket connection failed.", event));
    }));
    cleanup.push(addSocketListener(socket, "close", () => {
      finish(createSessionTerminatedError("WebSocket connection closed."));
    }));

    if (timeoutMs && timeoutMs > 0) {
      const timeoutId = setTimeout(() => {
        finish(new DevHubConnectionError({
          kind: "timeout",
          message: "WebSocket connection timed out."
        }));
      }, timeoutMs);
      cleanup.push(() => clearTimeout(timeoutId));
    }
  });
}

function readMessageText(event: unknown, args: readonly unknown[] = []): string {
  const binaryHint = args[0];
  if (binaryHint === true) {
    throw new Error("WebSocket JSON-RPC message must be a text frame.");
  }

  if (typeof event === "string") {
    return event;
  }

  if (event instanceof ArrayBuffer) {
    if (binaryHint !== false) {
      throw new Error("WebSocket JSON-RPC message must be a text frame.");
    }

    return TEXT_DECODER.decode(event);
  }

  if (ArrayBuffer.isView(event)) {
    if (binaryHint !== false) {
      throw new Error("WebSocket JSON-RPC message must be a text frame.");
    }

    return TEXT_DECODER.decode(event);
  }

  if (isRecord(event) && "data" in event) {
    return readMessageText(event.data, args);
  }

  throw new Error("WebSocket JSON-RPC message must be a text frame.");
}

function resolveCloseError(event: unknown, args: unknown[]): DevHubConnectionError | undefined {
  if (typeof event === "number") {
    return event === 1000 ? undefined : createSessionTerminatedError("WebSocket connection closed.");
  }

  if (isRecord(event)) {
    if (event.wasClean === true) {
      return undefined;
    }

    if (typeof event.code === "number") {
      return event.code === 1000 ? undefined : createSessionTerminatedError("WebSocket connection closed.");
    }
  }

  if (typeof args[0] === "number" && args[0] === 1000) {
    return undefined;
  }

  return createSessionTerminatedError("WebSocket connection closed.");
}

function createTransportError(message: string, cause?: unknown): DevHubConnectionError {
  return normalizeConnectionError(cause, {
    kind: "transport",
    message
  });
}

function createSessionTerminatedError(message: string, cause?: unknown): DevHubConnectionError {
  return normalizeConnectionError(cause, {
    kind: "session_terminated",
    message
  });
}
