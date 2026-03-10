import { randomUUID } from "node:crypto";
import { createRequire } from "node:module";
import { DevHubRpcError } from "./errors.js";
import {
  normalizeClientOptions,
  validateClientOptions,
} from "./models.js";
import type {
  AppDefinition,
  AppInstance,
  AppInstanceRegistration,
  DevHubClientOptions,
  DevHubEvent,
  Invocation,
  InvokeRequest,
  LaunchRequest,
  LaunchResult,
  ListInstancesRequest,
  NormalizedDevHubClientOptions,
  NotifyResult,
  PingResult,
  PollRequest,
  PollResult,
  RequestResult,
  RespondRequest,
  JsonObject,
  JsonValue
} from "./models.js";
import { discoverRuntime } from "./runtime.js";
import type { RuntimeConnectionInfo } from "./runtime.js";

const require = createRequire(import.meta.url);

export class DevHubClient {
  readonly options: NormalizedDevHubClientOptions;
  readonly connection: RuntimeConnectionInfo;

  private readonly transport: JsonRpcHttpTransport;

  private constructor(options: NormalizedDevHubClientOptions, connection: RuntimeConnectionInfo) {
    this.options = options;
    this.connection = connection;
    this.transport = new JsonRpcHttpTransport(options, connection);
  }

  get runtime() {
    return this.connection.runtime;
  }

  static async fromRuntime(options: DevHubClientOptions): Promise<DevHubClient> {
    const normalized = normalizeClientOptions(options);
    validateClientOptions(normalized);
    const connection = await discoverRuntime(normalized.runtimeDir);
    return new DevHubClient(normalized, connection);
  }

  async ping(echo?: JsonValue): Promise<PingResult> {
    const params = echo === undefined ? undefined : { echo };
    const result = await this.transport.send("hub.ping", params);
    const payload = ensureRecord(result, "hub.ping.result");
    ensureOk(payload, "hub.ping.result");
    const serverTimeUtc = readDate(payload, "hub.ping.result", "serverTimeUtc");
    const echoValue = payload.echo as JsonValue | undefined;
    return {
      ok: true,
      serverTimeUtc,
      echo: echoValue
    };
  }

  async listDefinitions(): Promise<AppDefinition[]> {
    const result = await this.transport.send("hub.apps.listDefinitions");
    const payload = ensureRecord(result, "hub.apps.listDefinitions.result");
    ensureOk(payload, "hub.apps.listDefinitions.result");
    const definitions = readArray(payload, "hub.apps.listDefinitions.result", "definitions");
    return definitions.map((item, index) => parseAppDefinition(item, `hub.apps.listDefinitions.result.definitions[${index}]`));
  }

  async getDefinition(appId: string): Promise<AppDefinition> {
    const result = await this.transport.send("hub.apps.getDefinition", buildGetDefinitionParams(appId));
    const payload = ensureRecord(result, "hub.apps.getDefinition.result");
    ensureOk(payload, "hub.apps.getDefinition.result");
    const definition = readObject(payload, "hub.apps.getDefinition.result", "definition");
    return parseAppDefinition(definition, "hub.apps.getDefinition.result.definition");
  }

  async registerInstance(instance: AppInstanceRegistration): Promise<AppInstance> {
    const result = await this.transport.send("hub.apps.registerInstance", buildRegisterInstanceParams(instance));
    const payload = ensureRecord(result, "hub.apps.registerInstance.result");
    ensureOk(payload, "hub.apps.registerInstance.result");
    const instancePayload = readObject(payload, "hub.apps.registerInstance.result", "instance");
    return parseAppInstance(instancePayload, "hub.apps.registerInstance.result.instance");
  }

  async heartbeat(instanceId: string): Promise<Date> {
    const result = await this.transport.send("hub.apps.heartbeat", buildHeartbeatParams(instanceId));
    const payload = ensureRecord(result, "hub.apps.heartbeat.result");
    ensureOk(payload, "hub.apps.heartbeat.result");
    return readDate(payload, "hub.apps.heartbeat.result", "lastSeenUtc");
  }

  async unregisterInstance(instanceId: string): Promise<void> {
    const result = await this.transport.send("hub.apps.unregisterInstance", buildUnregisterParams(instanceId));
    const payload = ensureRecord(result, "hub.apps.unregisterInstance.result");
    ensureOk(payload, "hub.apps.unregisterInstance.result");
  }

  async listInstances(request?: ListInstancesRequest): Promise<AppInstance[]> {
    const result = await this.transport.send("hub.apps.listInstances", buildListInstancesParams(request));
    const payload = ensureRecord(result, "hub.apps.listInstances.result");
    ensureOk(payload, "hub.apps.listInstances.result");
    const instances = readArray(payload, "hub.apps.listInstances.result", "instances");
    return instances.map((item, index) => parseAppInstance(item, `hub.apps.listInstances.result.instances[${index}]`));
  }

  async launch(request: LaunchRequest): Promise<LaunchResult> {
    const result = await this.transport.send("hub.apps.launch", buildLaunchParams(request));
    const payload = ensureRecord(result, "hub.apps.launch.result");
    ensureOk(payload, "hub.apps.launch.result");
    const status = readString(payload, "hub.apps.launch.result", "status");
    const launchId = readString(payload, "hub.apps.launch.result", "launchId");
    const pidValue = payload.pid;
    if (pidValue !== undefined && pidValue !== null && (typeof pidValue !== "number" || Number.isNaN(pidValue))) {
      throw new Error("hub.apps.launch.result 返回结果非法：pid 类型非法。");
    }

    if (status !== "started" && status !== "starting" && status !== "already_running") {
      throw new Error("hub.apps.launch.result 返回结果非法：status 取值不受支持。");
    }

    return {
      ok: true,
      status,
      pid: pidValue === undefined ? undefined : (pidValue as number | null),
      launchId
    };
  }

  async notify(request: InvokeRequest): Promise<NotifyResult> {
    const result = await this.transport.send("hub.invoke.notify", buildInvokeParams(request, false));
    const payload = ensureRecord(result, "hub.invoke.notify.result");
    ensureOk(payload, "hub.invoke.notify.result");
    const invocationId = readString(payload, "hub.invoke.notify.result", "invocationId");
    return {
      ok: true,
      invocationId
    };
  }

  async request(request: InvokeRequest): Promise<RequestResult> {
    const result = await this.transport.send("hub.invoke.request", buildInvokeParams(request, true));
    const payload = ensureRecord(result, "hub.invoke.request.result");
    ensureOk(payload, "hub.invoke.request.result");
    const invocationId = readString(payload, "hub.invoke.request.result", "invocationId");
    if (!("value" in payload)) {
      throw new Error("hub.invoke.request.result 返回结果非法：value 不能为空。");
    }

    return {
      ok: true,
      invocationId,
      value: payload.value as JsonValue
    };
  }

  async poll(request: PollRequest): Promise<PollResult> {
    const result = await this.transport.send("hub.invoke.poll", buildPollParams(request));
    const payload = ensureRecord(result, "hub.invoke.poll.result");
    ensureOk(payload, "hub.invoke.poll.result");
    const serverTimeUtc = readDate(payload, "hub.invoke.poll.result", "serverTimeUtc");
    const items = readArray(payload, "hub.invoke.poll.result", "items");
    const parsedItems = items.map((item, index) => parseInvocation(item, `hub.invoke.poll.result.items[${index}]`));
    return {
      ok: true,
      serverTimeUtc,
      items: parsedItems
    };
  }

  async respond(request: RespondRequest): Promise<void> {
    const result = await this.transport.send("hub.invoke.respond", buildRespondParams(request));
    const payload = ensureRecord(result, "hub.invoke.respond.result");
    ensureOk(payload, "hub.invoke.respond.result");
  }

  async dispose(): Promise<void> {
    return Promise.resolve();
  }
}

export class DevHubEventsClient {
  readonly options: NormalizedDevHubClientOptions;
  readonly connection: RuntimeConnectionInfo;

  private socket: WebSocketLike | null = null;
  private socketCleanup: Array<() => void> = [];
  private readonly pendingRequests = new Map<string, PendingRequest>();
  private readonly eventQueue = new AsyncQueue<DevHubEvent>();
  private readonly sendLock = new Mutex();
  private authenticated = false;
  private eventStreamAvailable = false;
  private disposed = false;

  private constructor(options: NormalizedDevHubClientOptions, connection: RuntimeConnectionInfo) {
    this.options = options;
    this.connection = connection;
  }

  get runtime() {
    return this.connection.runtime;
  }

  static async fromRuntime(options: DevHubClientOptions): Promise<DevHubEventsClient> {
    const normalized = normalizeClientOptions(options);
    validateClientOptions(normalized);
    const connection = await discoverRuntime(normalized.runtimeDir);
    return new DevHubEventsClient(normalized, connection);
  }

  async authenticate(): Promise<void> {
    this.throwIfDisposed();
    if (this.authenticated) {
      throw new Error("当前事件客户端已完成认证。");
    }

    await this.ensureConnected();

    try {
      const result = await this.sendRequest(
        "hub.ws.authenticate",
        {
          token: this.connection.token,
          protocolVersion: this.options.protocolVersion,
          clientId: this.options.clientId,
          clientSessionId: this.options.clientSessionId
        },
        false
      );

      const payload = ensureRecord(result, "hub.ws.authenticate.result");
      const ok = readBoolean(payload, "hub.ws.authenticate.result", "ok");
      const protocolVersion = readNumber(payload, "hub.ws.authenticate.result", "protocolVersion");
      if (!ok || protocolVersion !== 1) {
        throw new Error("hub.ws.authenticate 返回结果非法。");
      }

      this.authenticated = true;
      this.eventStreamAvailable = true;
    } catch (error) {
      await this.disposeConnection("authenticate_failed");
      throw error;
    }
  }

  async subscribe(types?: string[]): Promise<string> {
    this.ensureAuthenticated();

    let params: Record<string, unknown> | undefined;
    if (types !== undefined) {
      if (!Array.isArray(types)) {
        throw new Error("types 必须为字符串数组。");
      }

      if (types.some((item) => item === null || item === undefined)) {
        throw new Error("types 不能包含 null。");
      }

      if (types.some((item) => typeof item !== "string")) {
        throw new Error("types 只能包含字符串。");
      }

      if (types.some((item) => !item || !item.trim())) {
        throw new Error("types 不能包含空白字符串。");
      }

      if (types.length > 0) {
        params = { types };
      }
    }

    const result = await this.sendRequest("hub.events.subscribe", params, true);
    const payload = ensureRecord(result, "hub.events.subscribe.result");
    const ok = readBoolean(payload, "hub.events.subscribe.result", "ok");
    const subscriptionId = readString(payload, "hub.events.subscribe.result", "subscriptionId");
    if (!ok || !subscriptionId.trim()) {
      throw new Error("hub.events.subscribe 返回结果非法。");
    }

    return subscriptionId;
  }

  async unsubscribe(subscriptionId: string): Promise<void> {
    this.ensureAuthenticated();
    if (!subscriptionId || !subscriptionId.trim()) {
      throw new Error("subscriptionId 不能为空。");
    }

    const result = await this.sendRequest(
      "hub.events.unsubscribe",
      {
        subscriptionId
      },
      true
    );

    const payload = ensureRecord(result, "hub.events.unsubscribe.result");
    const ok = readBoolean(payload, "hub.events.unsubscribe.result", "ok");
    if (!ok) {
      throw new Error("hub.events.unsubscribe 返回结果非法。");
    }
  }

  readEvents(): AsyncIterable<DevHubEvent> {
    this.ensureEventStreamAvailable();
    return this.eventQueue;
  }

  async dispose(): Promise<void> {
    if (this.disposed) {
      return;
    }

    this.disposed = true;
    this.authenticated = false;
    this.eventStreamAvailable = false;

    const error = new Error("WebSocket 连接已关闭。");
    for (const pending of this.pendingRequests.values()) {
      pending.reject(error);
    }
    this.pendingRequests.clear();

    this.eventQueue.close();

    await this.disposeConnection("client_dispose");
  }

  private async ensureConnected(): Promise<void> {
    if (this.socket) {
      return;
    }

    const ctor = resolveWebSocketConstructor();
    const socket = new ctor(this.connection.websocketEndpoint);
    this.attachSocketHandlers(socket);
    await waitForWebSocketOpen(socket, this.options.requestTimeoutMs);

    this.socket = socket;
  }

  private attachSocketHandlers(socket: WebSocketLike): void {
    this.socketCleanup.push(addSocketListener(socket, "message", (event) => {
      void this.handleMessage(event);
    }));

    this.socketCleanup.push(addSocketListener(socket, "close", () => {
      this.terminate(new Error("WebSocket 连接已关闭。"));
    }));

    this.socketCleanup.push(addSocketListener(socket, "error", (event) => {
      const error = event instanceof Error ? event : new Error("WebSocket 发生错误。");
      this.terminate(error);
    }));
  }

  private async handleMessage(event: unknown): Promise<void> {
    let text: string | null = null;
    try {
      text = coerceMessageText(event);
      if (!text) {
        return;
      }

      const payload = JSON.parse(text) as unknown;
      const root = ensureRecord(payload, "WebSocket JSON-RPC 消息");
      validateIncomingEnvelope(root);

      const response = tryGetResponse(root);
      if (response) {
        this.completePending(response);
        return;
      }

      const params = tryGetEventParams(root);
      if (params) {
        const evt = parseEvent(params, "hub.event.params");
        this.eventQueue.push(evt);
      }
    } catch (error) {
      this.terminate(error instanceof Error ? error : new Error("WebSocket 消息处理失败。"));
    }
  }

  private completePending(response: ResponseEnvelope): void {
    const pending = this.pendingRequests.get(response.requestId);
    if (!pending) {
      return;
    }

    this.pendingRequests.delete(response.requestId);
    if (pending.timeoutId) {
      clearTimeout(pending.timeoutId);
    }

    if (response.error) {
      pending.reject(buildRpcError(response.error, response.requestId));
      return;
    }

    pending.resolve(response.result);
  }

  private async sendRequest(method: string, params: Record<string, unknown> | undefined, requireAuthenticated: boolean): Promise<Record<string, unknown>> {
    this.throwIfDisposed();
    if (requireAuthenticated) {
      this.ensureAuthenticated();
    }

    await this.ensureConnected();
    const socket = this.socket;
    if (!socket) {
      throw new Error("当前 WebSocket 尚未建立连接。");
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

    const json = JSON.stringify(payload);

    const waiter = createPendingRequest(this.options.requestTimeoutMs, () => {
      this.pendingRequests.delete(requestId);
    });
    this.pendingRequests.set(requestId, waiter);

    try {
      await this.sendLock.run(() => {
        socket.send(json);
      });

      return await waiter.promise;
    } catch (error) {
      this.pendingRequests.delete(requestId);
      throw error;
    }
  }

  private terminate(error?: Error): void {
    if (this.disposed) {
      return;
    }

    this.authenticated = false;
    this.eventStreamAvailable = false;

    const finalError = error ?? new Error("WebSocket 连接已关闭。");
    for (const pending of this.pendingRequests.values()) {
      pending.reject(finalError);
    }
    this.pendingRequests.clear();

    this.eventQueue.close(finalError);

    void this.disposeConnection("connection_closed");
  }

  private async disposeConnection(reason: string): Promise<void> {
    if (!this.socket) {
      return;
    }

    const socket = this.socket;
    this.socket = null;

    for (const cleanup of this.socketCleanup) {
      cleanup();
    }
    this.socketCleanup = [];

    try {
      socket.close(1000, reason);
    } catch {
    }
  }

  private ensureAuthenticated(): void {
    this.throwIfDisposed();
    if (!this.authenticated) {
      throw new Error("当前事件客户端尚未认证。");
    }
  }

  private ensureEventStreamAvailable(): void {
    this.throwIfDisposed();
    if (!this.eventStreamAvailable) {
      this.ensureAuthenticated();
    }
  }

  private throwIfDisposed(): void {
    if (this.disposed) {
      throw new Error("当前事件客户端已释放。");
    }
  }
}

class JsonRpcHttpTransport {
  private readonly options: NormalizedDevHubClientOptions;
  private readonly connection: RuntimeConnectionInfo;

  constructor(options: NormalizedDevHubClientOptions, connection: RuntimeConnectionInfo) {
    this.options = options;
    this.connection = connection;
  }

  async send(method: string, params?: Record<string, unknown> | null): Promise<Record<string, unknown>> {
    if (!method || !method.trim()) {
      throw new Error("method 不能为空。");
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
        throw new Error(`HTTP 请求失败：${response.status} ${response.statusText}，响应体：${body}`);
      }

      let root: unknown;
      try {
        root = JSON.parse(body) as unknown;
      } catch (error) {
        throw new Error("JSON-RPC 响应解析失败。", { cause: error });
      }

      return validateResponseEnvelope(root, requestId);
    } finally {
      if (timeoutId) {
        clearTimeout(timeoutId);
      }
    }
  }
}

interface PendingRequest {
  promise: Promise<Record<string, unknown>>;
  resolve: (value: Record<string, unknown>) => void;
  reject: (reason?: unknown) => void;
  timeoutId?: NodeJS.Timeout;
}

class AsyncQueue<T> implements AsyncIterable<T> {
  private readonly items: T[] = [];
  private readonly waiters: Array<{
    resolve: (value: IteratorResult<T>) => void;
    reject: (reason?: unknown) => void;
  }> = [];
  private closed = false;
  private error: Error | undefined;

  push(item: T): void {
    if (this.closed) {
      return;
    }

    const waiter = this.waiters.shift();
    if (waiter) {
      waiter.resolve({ value: item, done: false });
      return;
    }

    this.items.push(item);
  }

  close(error?: Error): void {
    if (this.closed) {
      return;
    }

    this.closed = true;
    this.error = error;

    while (this.waiters.length > 0) {
      const waiter = this.waiters.shift();
      if (!waiter) {
        break;
      }
      if (this.error) {
        waiter.reject(this.error);
      } else {
        waiter.resolve({ value: undefined as unknown as T, done: true });
      }
    }
  }

  async next(): Promise<IteratorResult<T>> {
    if (this.items.length > 0) {
      const value = this.items.shift()!;
      return { value, done: false };
    }

    if (this.closed) {
      if (this.error) {
        throw this.error;
      }

      return { value: undefined as unknown as T, done: true };
    }

    return new Promise<IteratorResult<T>>((resolve, reject) => {
      this.waiters.push({ resolve, reject });
    });
  }

  [Symbol.asyncIterator](): AsyncIterator<T> {
    return {
      next: () => this.next()
    };
  }
}

class Mutex {
  private current: Promise<void> = Promise.resolve();

  async run<T>(action: () => T | Promise<T>): Promise<T> {
    const previous = this.current;
    let release: () => void = () => {};
    this.current = new Promise<void>((resolve) => {
      release = resolve;
    });

    await previous;
    try {
      return await action();
    } finally {
      release();
    }
  }
}

interface ResponseEnvelope {
  requestId: string;
  result: Record<string, unknown>;
  error?: Record<string, unknown>;
}

interface WebSocketLike {
  send(data: string): void;
  close(code?: number, reason?: string): void;
  addEventListener?: (type: string, listener: (event: unknown, ...args: unknown[]) => void) => void;
  removeEventListener?: (type: string, listener: (event: unknown, ...args: unknown[]) => void) => void;
  on?: (type: string, listener: (event: unknown, ...args: unknown[]) => void) => void;
  off?: (type: string, listener: (event: unknown, ...args: unknown[]) => void) => void;
}

type WebSocketConstructor = new (url: string) => WebSocketLike;

function resolveWebSocketConstructor(): WebSocketConstructor {
  const globalCandidate = (globalThis as { WebSocket?: unknown }).WebSocket;
  if (typeof globalCandidate === "function") {
    return globalCandidate as WebSocketConstructor;
  }

  try {
    const wsModule = require("ws") as Record<string, unknown>;
    const ctor = (wsModule.WebSocket ?? wsModule.default ?? wsModule) as unknown;
    if (typeof ctor !== "function") {
      throw new Error("ws 模块未导出 WebSocket 构造函数。");
    }
    return ctor as WebSocketConstructor;
  } catch (error) {
    throw new Error("当前运行时不支持 WebSocket，请安装 ws 依赖或使用 Node.js 20+。", { cause: error });
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

  throw new Error("当前 WebSocket 实现不支持事件订阅。");
}

function waitForWebSocketOpen(socket: WebSocketLike, timeoutMs?: number): Promise<void> {
  return new Promise((resolve, reject) => {
    const cleanup = [] as Array<() => void>;

    const finish = (error?: Error) => {
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
      finish(event instanceof Error ? event : new Error("WebSocket 连接失败。"));
    }));
    cleanup.push(addSocketListener(socket, "close", () => {
      finish(new Error("WebSocket 连接已关闭。"));
    }));

    if (timeoutMs && timeoutMs > 0) {
      const timeoutId = setTimeout(() => {
        finish(new Error("WebSocket 连接超时。"));
      }, timeoutMs);
      cleanup.push(() => clearTimeout(timeoutId));
    }
  });
}

function coerceMessageText(event: unknown): string | null {
  if (typeof event === "string") {
    return event;
  }

  if (event instanceof ArrayBuffer) {
    return Buffer.from(event).toString("utf-8");
  }

  if (ArrayBuffer.isView(event)) {
    return Buffer.from(event.buffer).toString("utf-8");
  }

  if (Buffer.isBuffer(event)) {
    return event.toString("utf-8");
  }

  if (isRecord(event) && "data" in event) {
    const data = event.data as unknown;
    return coerceMessageText(data);
  }

  return null;
}

function createHttpRequestId(): string {
  return `req-${randomUUID().replace(/-/g, "")}`;
}

function createWebSocketRequestId(): string {
  return `ws-${randomUUID().replace(/-/g, "")}`;
}

function startTimeout(controller: AbortController, timeoutMs?: number): NodeJS.Timeout | undefined {
  if (!timeoutMs || timeoutMs <= 0) {
    return undefined;
  }

  return setTimeout(() => controller.abort(), timeoutMs);
}

function validateResponseEnvelope(payload: unknown, requestId: string): Record<string, unknown> {
  const root = ensureRecord(payload, "JSON-RPC 响应");
  const jsonrpc = root.jsonrpc;
  if (jsonrpc !== "2.0") {
    throw new Error("JSON-RPC 响应的 jsonrpc 版本非法。");
  }

  const responseId = readResponseId(root, "JSON-RPC 响应");
  if (responseId !== requestId) {
    throw new Error("JSON-RPC 响应的 id 与请求不匹配。");
  }

  const hasResult = "result" in root;
  const hasError = "error" in root && root.error !== null && root.error !== undefined;

  if (hasResult === hasError) {
    throw new Error("JSON-RPC 响应必须且只能包含 result 或 error。");
  }

  if (hasError) {
    throw buildRpcError(root.error as unknown, requestId);
  }

  const result = root.result as unknown;
  if (!isRecord(result)) {
    throw new Error("JSON-RPC result 必须为对象。");
  }

  return result;
}

function tryGetResponse(root: Record<string, unknown>): ResponseEnvelope | null {
  if (!("id" in root)) {
    return null;
  }

  const requestId = readResponseId(root, "WebSocket JSON-RPC 响应");
  const hasResult = "result" in root;
  const hasError = "error" in root && root.error !== null && root.error !== undefined;
  if (!hasResult && !hasError) {
    return null;
  }

  if (hasResult === hasError) {
    throw new Error("WebSocket JSON-RPC 响应必须且只能包含 result 或 error。");
  }

  if (hasResult) {
    const result = root.result as unknown;
    if (!isRecord(result)) {
      throw new Error("WebSocket JSON-RPC result 必须为对象。");
    }

    return {
      requestId,
      result
    };
  }

  const error = root.error as unknown;
  if (!isRecord(error)) {
    throw new Error("WebSocket JSON-RPC error 对象非法。");
  }

  return {
    requestId,
    result: {},
    error
  };
}

function tryGetEventParams(root: Record<string, unknown>): Record<string, unknown> | null {
  if (root.method !== "hub.event") {
    return null;
  }

  if ("id" in root) {
    throw new Error("hub.event 必须为通知，禁止包含 id。");
  }

  if ("result" in root || ("error" in root && root.error !== null && root.error !== undefined)) {
    throw new Error("hub.event 通知禁止包含 result 或 error。");
  }

  if (!("params" in root) || !isRecord(root.params)) {
    throw new Error("hub.event.params 非法。");
  }

  return root.params as Record<string, unknown>;
}

function validateIncomingEnvelope(root: Record<string, unknown>): void {
  if (root.jsonrpc !== "2.0") {
    throw new Error("WebSocket JSON-RPC 消息的 jsonrpc 版本非法。");
  }
}

function buildRpcError(payload: unknown, requestId: string): DevHubRpcError {
  if (!isRecord(payload)) {
    throw new Error("JSON-RPC error 对象非法。");
  }

  const code = payload.code;
  if (typeof code !== "number" || Number.isNaN(code)) {
    throw new Error("JSON-RPC error.code 非法。");
  }

  const message = payload.message;
  if (typeof message !== "string" || !message.trim()) {
    throw new Error("JSON-RPC error.message 非法。");
  }

  return new DevHubRpcError({
    code,
    message,
    data: payload.data,
    requestId
  });
}

function readResponseId(root: Record<string, unknown>, location: string): string {
  if (!("id" in root)) {
    throw new Error(`${location} 缺少 id 字段。`);
  }

  const id = root.id;
  if (typeof id === "string") {
    return id;
  }

  if (typeof id === "number") {
    return String(id);
  }

  throw new Error(`${location} 的 id 类型非法。`);
}

function createPendingRequest(timeoutMs: number | undefined, onTimeout: () => void): PendingRequest {
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
      reject(new Error("WebSocket 请求超时。"));
    }, timeoutMs);
  }

  return {
    promise,
    resolve,
    reject,
    timeoutId
  };
}

function buildGetDefinitionParams(appId: string): Record<string, unknown> {
  const normalizedAppId = ensureRequiredInputString(appId, "appId");

  return {
    appId: normalizedAppId
  };
}

function buildRegisterInstanceParams(instance: AppInstanceRegistration): Record<string, unknown> {
  if (!instance) {
    throw new Error("instance 不能为空。");
  }

  const instanceId = ensureRequiredInputString(instance.instanceId, "instanceId");
  const appId = ensureRequiredInputString(instance.appId, "appId");
  const scope = ensureOptionalInputStringOrNull(instance.scope, "scope");

  if (!instance.invoke) {
    throw new Error("invoke 不能为空。");
  }

  const poll = ensureInputBoolean(instance.invoke.poll, "invoke.poll");
  const respond = ensureInputBoolean(instance.invoke.respond, "invoke.respond");

  if (!Number.isInteger(instance.pid) || instance.pid < 1) {
    throw new Error("pid 必须大于等于 1。");
  }

  const payload: Record<string, unknown> = {
    instance: {
      instanceId,
      appId,
      pid: instance.pid,
      invoke: {
        poll,
        respond
      }
    }
  };

  if (scope !== undefined && scope !== null) {
    (payload.instance as Record<string, unknown>).scope = scope;
  }

  if (instance.meta !== undefined) {
    ensureJsonObject(instance.meta, "meta");
    (payload.instance as Record<string, unknown>).meta = instance.meta;
  }

  return payload;
}

function buildHeartbeatParams(instanceId: string): Record<string, unknown> {
  const normalizedInstanceId = ensureRequiredInputString(instanceId, "instanceId");

  return {
    instanceId: normalizedInstanceId
  };
}

function buildUnregisterParams(instanceId: string): Record<string, unknown> {
  const normalizedInstanceId = ensureRequiredInputString(instanceId, "instanceId");

  return {
    instanceId: normalizedInstanceId
  };
}

function buildListInstancesParams(request?: ListInstancesRequest): Record<string, unknown> | undefined {
  if (!request) {
    return undefined;
  }

  const payload: Record<string, unknown> = {};
  const appId = ensureOptionalInputString(request.appId, "appId", false);
  if (appId !== undefined) {
    payload.appId = appId;
  }

  const scope = ensureOptionalInputStringOrNull(request.scope, "scope");
  if (scope !== undefined) {
    payload.scope = scope;
  }

  const includeAllScopes = ensureOptionalInputBoolean(request.includeAllScopes, "includeAllScopes");
  if (includeAllScopes !== undefined) {
    payload.includeAllScopes = includeAllScopes;
  }

  const includeOffline = ensureOptionalInputBoolean(request.includeOffline, "includeOffline");
  if (includeOffline !== undefined) {
    payload.includeOffline = includeOffline;
  }

  return Object.keys(payload).length > 0 ? payload : undefined;
}

function buildLaunchParams(request: LaunchRequest): Record<string, unknown> {
  if (!request) {
    throw new Error("request 不能为空。");
  }

  const appId = ensureRequiredInputString(request.appId, "appId");
  const scope = ensureOptionalInputStringOrNull(request.scope, "scope");
  const dedupeKey = ensureOptionalInputStringOrNull(request.dedupeKey, "dedupeKey");
  const waitForRegisterMs = ensureOptionalInputIntegerAtLeast(
    request.waitForRegisterMs,
    "waitForRegisterMs",
    0,
    "waitForRegisterMs 必须为大于等于 0 的整数。"
  );

  const payload: Record<string, unknown> = {
    appId
  };

  if (scope !== undefined && scope !== null) {
    payload.scope = scope;
  }

  if (dedupeKey !== undefined && dedupeKey !== null) {
    payload.dedupeKey = dedupeKey;
  }

  if (waitForRegisterMs !== undefined) {
    payload.waitForRegisterMs = waitForRegisterMs;
  }

  return payload;
}

function buildInvokeParams(request: InvokeRequest, isRequest: boolean): Record<string, unknown> {
  if (!request) {
    throw new Error("request 不能为空。");
  }

  const appId = ensureRequiredInputString(request.appId, "appId");
  const method = ensureRequiredInputString(request.method, "method");
  const target = request.target;
  const targetScope = ensureOptionalInputStringOrNull(target?.scope, "target.scope");
  const targetInstanceId = ensureOptionalInputString(target?.instanceId, "target.instanceId", false, "target.instanceId 不能为空白字符串。", true);

  const ttlMs = ensureOptionalInputIntegerAtLeast(request.options?.ttlMs, "ttlMs", 1000, "ttlMs 必须大于等于 1000。")
    ?? (isRequest ? 300000 : 60000);
  const waitTimeoutMs = isRequest
    ? ensureOptionalInputIntegerAtLeast(request.options?.waitTimeoutMs, "waitTimeoutMs", 1, "waitTimeoutMs 必须大于等于 1。")
      ?? 120000
    : undefined;
  const queueIfOffline = ensureOptionalInputBoolean(request.options?.queueIfOffline, "queueIfOffline") ?? true;
  const autoLaunch = ensureOptionalInputBoolean(request.options?.autoLaunch, "autoLaunch")
    ?? (targetInstanceId === undefined || targetInstanceId === null);

  if (waitTimeoutMs !== undefined && waitTimeoutMs > ttlMs) {
    throw new Error("waitTimeoutMs 不能大于 ttlMs。");
  }

  if (targetInstanceId !== undefined && targetInstanceId !== null && autoLaunch) {
    throw new Error("指定 target.instanceId 时不能启用 autoLaunch。");
  }

  if (autoLaunch && !queueIfOffline) {
    throw new Error("启用 autoLaunch 时 queueIfOffline 必须为 true。");
  }

  const payload: Record<string, unknown> = {
    appId,
    method,
    args: request.args,
    options: {
      ttlMs,
      queueIfOffline,
      autoLaunch
    }
  };

  if (isRequest) {
    (payload.options as Record<string, unknown>).waitTimeoutMs = waitTimeoutMs;
  }

  if (target !== undefined) {
    payload.target = {
      scope: targetScope ?? null,
      instanceId: targetInstanceId ?? null
    };
  }

  return payload;
}

function buildPollParams(request: PollRequest): Record<string, unknown> {
  if (!request) {
    throw new Error("request 不能为空。");
  }

  const instanceId = ensureRequiredInputString(request.instanceId, "instanceId");

  const maxCount = ensureOptionalInputIntegerInRange(request.maxCount, "maxCount", 1, 100, "maxCount 必须位于 1..100。") ?? 10;
  const waitMs = ensureOptionalInputIntegerAtLeast(request.waitMs, "waitMs", 0, "waitMs 必须为大于等于 0 的整数。") ?? 25000;

  return {
    instanceId,
    maxCount,
    waitMs
  };
}

function buildRespondParams(request: RespondRequest): Record<string, unknown> {
  if (!request) {
    throw new Error("request 不能为空。");
  }

  const instanceId = ensureRequiredInputString(request.instanceId, "instanceId");
  const invocationId = ensureRequiredInputString(request.invocationId, "invocationId");

  const hasValue = request.value !== undefined;
  const hasError = request.error !== undefined;
  if (hasValue === hasError) {
    throw new Error("RespondRequest 必须且只能包含 value 或 error 之一。");
  }

  const errorCode = request.error ? ensureInputNumber(request.error.code, "error.code") : undefined;
  const errorMessage = request.error ? ensureRequiredInputString(request.error.message, "error.message") : undefined;

  const payload: Record<string, unknown> = {
    instanceId,
    invocationId
  };

  if (hasValue) {
    payload.value = request.value;
  } else {
    payload.error = {
      code: errorCode,
      message: errorMessage,
      data: request.error?.data
    };
  }

  return payload;
}

function parseAppDefinition(payload: unknown, location: string): AppDefinition {
  const record = ensureRecord(payload, location);
  const appId = readString(record, location, "appId");
  const displayName = readString(record, location, "displayName");
  const description = readOptionalString(record, location, "description");

  let capabilities: AppDefinition["capabilities"] = undefined;
  if ("capabilities" in record && record.capabilities !== null && record.capabilities !== undefined) {
    const capabilitiesPayload = ensureRecord(record.capabilities, `${location}.capabilities`);
    const rpc = readOptionalBoolean(capabilitiesPayload, `${location}.capabilities`, "rpc");
    const events = readOptionalBoolean(capabilitiesPayload, `${location}.capabilities`, "events");
    capabilities = { rpc, events };
  }

  let launch: AppDefinition["launch"] = undefined;
  if ("launch" in record && record.launch !== null && record.launch !== undefined) {
    const launchPayload = ensureRecord(record.launch, `${location}.launch`);
    const exePath = readString(launchPayload, `${location}.launch`, "exePath");
    const argsTemplate = readOptionalString(launchPayload, `${location}.launch`, "argsTemplate");
    const workingDirectory = readOptionalString(launchPayload, `${location}.launch`, "workingDirectory");
    const dedupeKeyTemplate = readOptionalString(launchPayload, `${location}.launch`, "dedupeKeyTemplate");
    launch = {
      exePath,
      argsTemplate,
      workingDirectory,
      dedupeKeyTemplate
    };
  }

  return {
    appId,
    displayName,
    description,
    capabilities,
    launch
  };
}

function parseAppInstance(payload: unknown, location: string): AppInstance {
  const record = ensureRecord(payload, location);
  const instanceId = readString(record, location, "instanceId");
  const appId = readString(record, location, "appId");
  const scope = readOptionalStringOrNull(record, location, "scope");
  const pid = readPositiveInt(record, location, "pid");
  const registeredAtUtc = readDate(record, location, "registeredAtUtc");
  const lastSeenUtc = readDate(record, location, "lastSeenUtc");

  const invokePayload = readObject(record, location, "invoke");
  const poll = readBoolean(invokePayload, `${location}.invoke`, "poll");
  const respond = readBoolean(invokePayload, `${location}.invoke`, "respond");

  let meta: JsonObject | undefined;
  if ("meta" in record && record.meta !== null && record.meta !== undefined) {
    meta = ensureRecord(record.meta, `${location}.meta`) as JsonObject;
  }

  return {
    instanceId,
    appId,
    scope,
    pid,
    registeredAtUtc,
    lastSeenUtc,
    invoke: {
      poll,
      respond
    },
    meta
  };
}

function parseInvocation(payload: unknown, location: string): Invocation {
  const record = ensureRecord(payload, location);
  const invocationId = readString(record, location, "invocationId");
  const appId = readString(record, location, "appId");
  const targetPayload = readObject(record, location, "target");
  const scope = readOptionalStringOrNull(targetPayload, `${location}.target`, "scope");
  const instanceId = readOptionalStringOrNull(targetPayload, `${location}.target`, "instanceId");
  const method = readString(record, location, "method");
  const kind = readString(record, location, "kind");
  if (kind !== "request" && kind !== "notify") {
    throw new Error(`${location} 返回结果非法：kind 取值不受支持。`);
  }

  const createdAtUtc = readDate(record, location, "createdAtUtc");

  let options: Invocation["options"] = undefined;
  if ("options" in record && record.options !== null && record.options !== undefined) {
    const optionsPayload = ensureRecord(record.options, `${location}.options`);
    const ttlMs = readOptionalIntAtLeast(optionsPayload, `${location}.options`, "ttlMs", 1000);
    const waitTimeoutMs = readOptionalIntAtLeast(optionsPayload, `${location}.options`, "waitTimeoutMs", 1);
    const queueIfOffline = readOptionalBoolean(optionsPayload, `${location}.options`, "queueIfOffline");
    const autoLaunch = readOptionalBoolean(optionsPayload, `${location}.options`, "autoLaunch");
    options = {
      ttlMs,
      waitTimeoutMs,
      queueIfOffline,
      autoLaunch
    };
  }

  let delivery: Invocation["delivery"] = undefined;
  if ("delivery" in record && record.delivery !== null && record.delivery !== undefined) {
    const deliveryPayload = ensureRecord(record.delivery, `${location}.delivery`);
    const leaseSeconds = readPositiveInt(deliveryPayload, `${location}.delivery`, "leaseSeconds");
    const attempt = readPositiveInt(deliveryPayload, `${location}.delivery`, "attempt");
    delivery = {
      leaseSeconds,
      attempt
    };
  }

  const callerPayload = readObject(record, location, "caller");
  const clientId = readString(callerPayload, `${location}.caller`, "clientId");
  const clientSessionId = readString(callerPayload, `${location}.caller`, "clientSessionId");

  return {
    invocationId,
    appId,
    target: {
      scope,
      instanceId
    },
    method,
    args: record.args as JsonValue | undefined,
    kind,
    createdAtUtc,
    options,
    delivery,
    caller: {
      clientId,
      clientSessionId
    }
  };
}

function parseEvent(payload: Record<string, unknown>, location: string): DevHubEvent {
  const subscriptionId = readString(payload, location, "subscriptionId");
  const type = readString(payload, location, "type");
  const timeUtc = readDate(payload, location, "timeUtc");

  let parsedPayload: JsonObject | undefined;
  if ("payload" in payload && payload.payload !== null && payload.payload !== undefined) {
    parsedPayload = ensureRecord(payload.payload, `${location}.payload`) as JsonObject;
  }

  return {
    subscriptionId,
    type,
    timeUtc,
    payload: parsedPayload
  };
}

function ensureJsonObject(value: unknown, propertyName: string): void {
  let parsed: unknown;
  try {
    parsed = JSON.parse(JSON.stringify(value));
  } catch (error) {
    throw new Error(`${propertyName} 必须可序列化为 JSON 对象。`, { cause: error });
  }

  if (!isRecord(parsed)) {
    throw new Error(`${propertyName} 必须序列化为 JSON 对象。`);
  }
}

function ensureOk(payload: Record<string, unknown>, location: string): void {
  const ok = readBoolean(payload, location, "ok");
  if (!ok) {
    throw new Error(`${location} 返回结果非法：ok 必须为 true。`);
  }
}

function ensureRecord(value: unknown, location: string): Record<string, unknown> {
  if (!isRecord(value)) {
    throw new Error(`${location} 返回结果非法：JSON 类型非法。`);
  }
  return value;
}

function readObject(payload: Record<string, unknown>, location: string, key: string): Record<string, unknown> {
  if (!(key in payload)) {
    throw new Error(`${location} 返回结果非法：${key} 不能为空。`);
  }

  const value = payload[key];
  if (!isRecord(value)) {
    throw new Error(`${location} 返回结果非法：${key} 类型非法。`);
  }

  return value;
}

function readArray(payload: Record<string, unknown>, location: string, key: string): unknown[] {
  if (!(key in payload)) {
    throw new Error(`${location} 返回结果非法：${key} 不能为空。`);
  }

  const value = payload[key];
  if (!Array.isArray(value)) {
    throw new Error(`${location} 返回结果非法：${key} 类型非法。`);
  }

  return value;
}

function readString(payload: Record<string, unknown>, location: string, key: string): string {
  if (!(key in payload)) {
    throw new Error(`${location} 返回结果非法：${key} 不能为空。`);
  }

  const value = payload[key];
  if (typeof value !== "string" || !value.trim()) {
    throw new Error(`${location} 返回结果非法：${key} 不能为空。`);
  }

  return value;
}

function readOptionalString(payload: Record<string, unknown>, location: string, key: string): string | undefined {
  if (!(key in payload)) {
    return undefined;
  }

  const value = payload[key];
  if (value === null || value === undefined) {
    return undefined;
  }

  if (typeof value !== "string") {
    throw new Error(`${location} 返回结果非法：${key} 类型非法。`);
  }

  return value;
}

function readOptionalStringOrNull(payload: Record<string, unknown>, location: string, key: string): string | null | undefined {
  if (!(key in payload)) {
    return undefined;
  }

  const value = payload[key];
  if (value === null) {
    return null;
  }

  if (typeof value !== "string") {
    throw new Error(`${location} 返回结果非法：${key} 类型非法。`);
  }

  return value;
}

function readBoolean(payload: Record<string, unknown>, location: string, key: string): boolean {
  if (!(key in payload)) {
    throw new Error(`${location} 返回结果非法：${key} 不能为空。`);
  }

  const value = payload[key];
  if (typeof value !== "boolean") {
    throw new Error(`${location} 返回结果非法：${key} 类型非法。`);
  }

  return value;
}

function readOptionalBoolean(payload: Record<string, unknown>, location: string, key: string): boolean | undefined {
  if (!(key in payload)) {
    return undefined;
  }

  const value = payload[key];
  if (value === null || value === undefined) {
    return undefined;
  }

  if (typeof value !== "boolean") {
    throw new Error(`${location} 返回结果非法：${key} 类型非法。`);
  }

  return value;
}

function readNumber(payload: Record<string, unknown>, location: string, key: string): number {
  if (!(key in payload)) {
    throw new Error(`${location} 返回结果非法：${key} 不能为空。`);
  }

  const value = payload[key];
  if (typeof value !== "number" || Number.isNaN(value)) {
    throw new Error(`${location} 返回结果非法：${key} 类型非法。`);
  }

  return value;
}

function readPositiveInt(payload: Record<string, unknown>, location: string, key: string): number {
  const value = readNumber(payload, location, key);
  if (!Number.isInteger(value) || value < 1) {
    throw new Error(`${location} 返回结果非法：${key} 必须大于等于 1。`);
  }

  return value;
}

function readOptionalIntAtLeast(payload: Record<string, unknown>, location: string, key: string, minimumValue: number): number | undefined {
  if (!(key in payload)) {
    return undefined;
  }

  const value = payload[key];
  if (typeof value !== "number" || Number.isNaN(value) || !Number.isInteger(value) || value < minimumValue) {
    throw new Error(`${location} 返回结果非法：${key} 必须大于等于 ${minimumValue}。`);
  }

  return value;
}

function readDate(payload: Record<string, unknown>, location: string, key: string): Date {
  const value = readString(payload, location, key);
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) {
    throw new Error(`${location} 返回结果非法：${key} 不能为空默认值。`);
  }
  return date;
}

function ensureRequiredInputString(value: unknown, propertyName: string): string {
  if (typeof value !== "string" || !value.trim()) {
    throw new Error(`${propertyName} 不能为空。`);
  }

  return value;
}

function ensureOptionalInputString(
  value: unknown,
  propertyName: string,
  allowEmpty: boolean,
  emptyMessage = `${propertyName} 不能为空。`,
  allowNull = false
): string | null | undefined {
  if (value === undefined) {
    return undefined;
  }

  if (value === null) {
    if (allowNull) {
      return null;
    }

    throw new Error(`${propertyName} 类型非法。`);
  }

  if (typeof value !== "string") {
    throw new Error(`${propertyName} 类型非法。`);
  }

  if (!allowEmpty && !value.trim()) {
    throw new Error(emptyMessage);
  }

  return value;
}

function ensureOptionalInputStringOrNull(value: unknown, propertyName: string): string | null | undefined {
  return ensureOptionalInputString(value, propertyName, true, `${propertyName} 不能为空。`, true);
}

function ensureInputBoolean(value: unknown, propertyName: string): boolean {
  if (typeof value !== "boolean") {
    throw new Error(`${propertyName} 必须为布尔值。`);
  }

  return value;
}

function ensureOptionalInputBoolean(value: unknown, propertyName: string): boolean | undefined {
  if (value === undefined || value === null) {
    return undefined;
  }

  return ensureInputBoolean(value, propertyName);
}

function ensureInputNumber(value: unknown, propertyName: string): number {
  if (typeof value !== "number" || Number.isNaN(value)) {
    throw new Error(`${propertyName} 类型非法。`);
  }

  return value;
}

function ensureOptionalInputIntegerAtLeast(
  value: unknown,
  propertyName: string,
  minimumValue: number,
  errorMessage: string
): number | undefined {
  if (value === undefined || value === null) {
    return undefined;
  }

  if (typeof value !== "number" || Number.isNaN(value) || !Number.isInteger(value) || value < minimumValue) {
    throw new Error(errorMessage);
  }

  return value;
}

function ensureOptionalInputIntegerInRange(
  value: unknown,
  propertyName: string,
  minimumValue: number,
  maximumValue: number,
  errorMessage: string
): number | undefined {
  if (value === undefined || value === null) {
    return undefined;
  }

  if (typeof value !== "number" || Number.isNaN(value) || !Number.isInteger(value) || value < minimumValue || value > maximumValue) {
    throw new Error(errorMessage);
  }

  return value;
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}
