import { AsyncQueue } from "./async-utils.js";
import { DevHubConnectionError } from "./errors.js";
import {
  ensureSupportedEventType,
  type DevHubEventType
} from "./event-types.js";
import {
  normalizeClientOptions,
  validateClientOptions
} from "./models.js";
import type {
  AppDefinition,
  AppDefinitionIdentity,
  AppInstance,
  DevHubClientOptions,
  DevHubEvent,
  JsonValue,
  ListDefinitionsRequest,
  ListInstancesRequest,
  PingResult,
  NormalizedDevHubClientOptions,
  VersionCompatibilityResult
} from "./models.js";
import {
  parseAuthenticateResult,
  parseDefinitionResult,
  parseDefinitionsResult,
  parseEvent,
  parseHostVersionResult,
  parseInstanceResult,
  parseInstancesResult,
  parsePingResult,
  parseSubscriptionResult,
  parseUnsubscribeResult
} from "./parsers.js";
import {
  buildGetDefinitionParams,
  buildGetInstanceParams,
  buildListDefinitionsParams,
  buildListInstancesParams
} from "./payloads.js";
import { getRuntimeResolver } from "./default-runtime-resolver.js";
import { ensureJsonValue } from "./validation.js";
import {
  createRuntimeView,
  type DevHubRuntimeView
} from "./runtime-view.js";
import type { RuntimeConnectionInfo, RuntimeResolver } from "./runtime.js";
import type { AbandonedRequestFilter } from "./abandoned-request-filter.js";
import { JsonRpcWsSession, type JsonRpcWsSessionOptions } from "./ws-session.js";
import { checkVersionCompatibilityWithFallback } from "./versioning.js";

export type { AbandonedRequestFilter } from "./abandoned-request-filter.js";

export interface JsonRpcEventSession {
  ensureConnected(): Promise<void>;
  sendRequest(method: string, params?: Record<string, unknown>): Promise<Record<string, unknown>>;

  /**
   * 获取当前会话内匹配条件的已放弃请求数量。
   */
  getAbandonedRequestCount(filter?: AbandonedRequestFilter): number;

  /**
   * 清理当前会话内匹配条件的已放弃请求记录。
   */
  clearAbandonedRequests(filter?: AbandonedRequestFilter): number;
  disconnect(reason: string): Promise<void>;
  dispose(reason?: string): Promise<void>;
}

export type JsonRpcEventSessionFactory = (options: JsonRpcWsSessionOptions) => JsonRpcEventSession;

export interface DevHubEventsClientDependencies {
  runtimeResolver?: RuntimeResolver;
  sessionFactory?: JsonRpcEventSessionFactory;
}

export class DevHubEventsClient {
  readonly options: NormalizedDevHubClientOptions;
  #eventStream = createEventStreamGeneration();
  readonly #connection: RuntimeConnectionInfo;
  readonly #session: JsonRpcEventSession;
  #authenticated = false;
  #eventStreamAvailable = false;
  #eventStreamInvalidated = false;
  #activeReaderLease: EventReaderLease | null = null;
  #disposed = false;
  #terminationError: DevHubConnectionError | null = null;

  private constructor(
    options: NormalizedDevHubClientOptions,
    connection: RuntimeConnectionInfo,
    session: JsonRpcEventSession
  ) {
    this.options = options;
    this.#connection = connection;
    this.#session = session;
  }

  get runtime(): DevHubRuntimeView {
    return createRuntimeView(this.#connection.runtime);
  }

  /**
   * 获取当前会话内匹配条件的已放弃请求数量。
   * 该操作只读取本地维护状态，不会发送网络请求，也不会修改认证或订阅状态。
   */
  getAbandonedRequestCount(filter?: AbandonedRequestFilter): number {
    this.throwIfDisposed();
    return this.#session.getAbandonedRequestCount(filter);
  }

  /**
   * 清理当前会话内匹配条件的已放弃请求记录。
   * 该操作只修改本地维护状态，不会发送网络请求，也不会修改认证或订阅状态。
   */
  clearAbandonedRequests(filter?: AbandonedRequestFilter): number {
    this.throwIfDisposed();
    return this.#session.clearAbandonedRequests(filter);
  }

  static async fromRuntime(
    options: DevHubClientOptions,
    dependencies: DevHubEventsClientDependencies = {}
  ): Promise<DevHubEventsClient> {
    const normalized = normalizeClientOptions(options);
    validateClientOptions(normalized);
    const runtimeResolver = await getRuntimeResolver(dependencies.runtimeResolver);
    const connection = await runtimeResolver.resolve(normalized);
    let client: DevHubEventsClient | undefined;
    const sessionOptions: JsonRpcWsSessionOptions = {
      websocketEndpoint: connection.websocketEndpoint,
      requestTimeoutMs: normalized.requestTimeoutMs,
      onEvent: (params) => {
        if (client) {
          client.#eventStream.queue.push(parseEvent(params, "hub.event.params"));
        }
      },
      onTerminate: (error) => {
        client?.handleTermination(error);
      }
    };
    const session = dependencies.sessionFactory?.(sessionOptions) ?? new JsonRpcWsSession(sessionOptions);
    client = new DevHubEventsClient(normalized, connection, session);
    return client;
  }

  async authenticate(): Promise<void> {
    this.throwIfDisposed();
    if (this.#authenticated) {
      throw new Error("The events client is already authenticated.");
    }

    await this.#session.ensureConnected();

    try {
      const result = await this.#session.sendRequest("hub.ws.authenticate", {
        token: this.#connection.token,
        protocolVersion: this.options.protocolVersion,
        clientId: this.options.clientId,
        clientSessionId: this.options.clientSessionId
      });

      parseAuthenticateResult(result);
      this.invalidateReaderLease();
      this.#eventStream = createEventStreamGeneration();
      this.#authenticated = true;
      this.#eventStreamAvailable = true;
      this.#eventStreamInvalidated = false;
      this.#terminationError = null;
    } catch (error) {
      this.invalidateReaderLease();
      this.#authenticated = false;
      this.#eventStreamAvailable = false;
      this.#eventStreamInvalidated = true;
      this.#terminationError = createEventStreamTerminationError(error);
      await this.#session.disconnect("authenticate_failed");
      throw error;
    }
  }

  async subscribe(types?: readonly DevHubEventType[]): Promise<string> {
    this.ensureAuthenticated();
    return parseSubscriptionResult(await this.#session.sendRequest("hub.events.subscribe", buildSubscribeParams(types)));
  }

  async ping(echo?: JsonValue): Promise<PingResult> {
    this.ensureAuthenticated();
    const params = echo === undefined ? undefined : { echo: ensureJsonValue(echo, "echo") };
    return parsePingResult(await this.#session.sendRequest("hub.ping", params));
  }

  /**
   * 读取当前连接 Host 的运行时版本。
   * 该操作复用已认证 WebSocket 只读 RPC 通道。
   */
  async getHostVersion(): Promise<string> {
    this.ensureAuthenticated();
    return parseHostVersionResult(await this.#session.sendRequest("hub.getVersion"));
  }

  /**
   * 检查当前 SDK 与 Host 的版本兼容状态。
   * 优先调用 hub.getVersion；旧 Host 返回 method_not_found 时回退到 runtime.hubVersion。
   */
  async checkVersionCompatibility(): Promise<VersionCompatibilityResult> {
    this.ensureAuthenticated();
    return checkVersionCompatibilityWithFallback(
      () => this.getHostVersion(),
      this.#connection.runtime.hubVersion
    );
  }

  async listDefinitions(request: ListDefinitionsRequest): Promise<AppDefinition[]> {
    this.ensureAuthenticated();
    return parseDefinitionsResult(
      await this.#session.sendRequest("hub.apps.listDefinitions", buildListDefinitionsParams(request))
    );
  }

  async getDefinition(identity: AppDefinitionIdentity): Promise<AppDefinition> {
    this.ensureAuthenticated();
    return parseDefinitionResult(
      await this.#session.sendRequest("hub.apps.getDefinition", buildGetDefinitionParams(identity))
    );
  }

  async listInstances(request: ListInstancesRequest): Promise<AppInstance[]> {
    this.ensureAuthenticated();
    return parseInstancesResult(
      await this.#session.sendRequest("hub.apps.listInstances", buildListInstancesParams(request))
    );
  }

  async getInstance(instanceId: string): Promise<AppInstance> {
    this.ensureAuthenticated();
    return parseInstanceResult(
      await this.#session.sendRequest("hub.apps.getInstance", buildGetInstanceParams(instanceId))
    );
  }

  async unsubscribe(subscriptionId: string): Promise<void> {
    this.ensureAuthenticated();
    if (!subscriptionId || !subscriptionId.trim()) {
      throw new Error("subscriptionId cannot be empty.");
    }

    parseUnsubscribeResult(await this.#session.sendRequest("hub.events.unsubscribe", { subscriptionId }));
  }

  readEvents(): AsyncIterable<DevHubEvent> {
    this.ensureEventStreamAvailable();
    return {
      [Symbol.asyncIterator]: () => this.createEventIterator()
    };
  }

  async dispose(): Promise<void> {
    if (this.#disposed) {
      return;
    }

    this.#disposed = true;
    this.invalidateReaderLease();
    this.#authenticated = false;
    this.#eventStreamAvailable = false;
    this.#eventStreamInvalidated = true;
    this.#terminationError = createEventStreamTerminationError();
    this.#eventStream.queue.close();

    await this.#session.dispose("client_dispose");
  }

  private handleTermination(error?: Error): void {
    if (this.#disposed) {
      return;
    }

    this.#authenticated = false;
    this.#eventStreamAvailable = false;
    this.#eventStreamInvalidated = true;
    this.#terminationError = createEventStreamTerminationError(error);
    this.invalidateReaderLease();
    if (error instanceof DevHubConnectionError && error.kind === "invalid_response") {
      this.#eventStream.queue.close(error);
      return;
    }

    this.#eventStream.queue.close();
  }

  private ensureAuthenticated(): void {
    this.throwIfDisposed();
    if (!this.#authenticated) {
      throw new Error("The events client is not authenticated.");
    }
  }

  private ensureEventStreamAvailable(): void {
    this.throwIfDisposed();
    if (!this.#eventStreamAvailable) {
      if (this.#eventStreamInvalidated) {
        throw this.#terminationError ?? createEventStreamTerminationError();
      }

      this.ensureAuthenticated();
    }
  }

  private throwIfDisposed(): void {
    if (this.#disposed) {
      throw new Error("The events client has been disposed.");
    }
  }

  private createEventIterator(): AsyncIterableIterator<DevHubEvent> {
    this.ensureEventStreamAvailable();
    const eventStream = this.#eventStream;
    const readerLease = this.acquireReaderLease(eventStream.generationToken);

    const queueIterator = eventStream.queue[Symbol.asyncIterator]();
    let finished = false;
    const releaseReaderLease = () => {
      if (finished) {
        return;
      }

      finished = true;
      this.releaseReaderLease(readerLease);
    };

    return {
      next: async () => {
        if (finished) {
          return { value: undefined as unknown as DevHubEvent, done: true };
        }

        try {
          const result = await queueIterator.next();
          if (result.done) {
            releaseReaderLease();
          }

          return result;
        } catch (error) {
          releaseReaderLease();
          throw error;
        }
      },
      return: async (value?: DevHubEvent) => {
        releaseReaderLease();
        return {
          value: value as DevHubEvent,
          done: true
        };
      },
      throw: async (error?: unknown) => {
        releaseReaderLease();
        throw error;
      },
      [Symbol.asyncIterator]() {
        return this;
      }
    };
  }

  private acquireReaderLease(generationToken: symbol): EventReaderLease {
    if (this.#activeReaderLease) {
      throw new Error("Only one active readEvents() iterator is allowed per DevHubEventsClient instance.");
    }

    const lease = {
      generationToken,
      leaseToken: Symbol("event_reader_lease")
    };
    this.#activeReaderLease = lease;
    return lease;
  }

  private releaseReaderLease(lease: EventReaderLease): void {
    if (
      this.#activeReaderLease?.generationToken === lease.generationToken
      && this.#activeReaderLease.leaseToken === lease.leaseToken
    ) {
      this.#activeReaderLease = null;
    }
  }

  private invalidateReaderLease(): void {
    this.#activeReaderLease = null;
  }
}

interface EventStreamGeneration {
  queue: AsyncQueue<DevHubEvent>;
  generationToken: symbol;
}

interface EventReaderLease {
  generationToken: symbol;
  leaseToken: symbol;
}

function createEventStreamGeneration(): EventStreamGeneration {
  return {
    queue: new AsyncQueue<DevHubEvent>(),
    generationToken: Symbol("event_stream_generation")
  };
}

function buildSubscribeParams(types?: readonly DevHubEventType[]): Record<string, unknown> | undefined {
  if (types === undefined) {
    return undefined;
  }

  if (!Array.isArray(types)) {
    throw new Error("types must be a string array.");
  }

  if (types.some((item) => item === null || item === undefined)) {
    throw new Error("types cannot contain null or undefined.");
  }

  if (types.some((item) => typeof item !== "string")) {
    throw new Error("types can only contain strings.");
  }

  if (types.some((item) => !item || !item.trim())) {
    throw new Error("types cannot contain blank strings.");
  }

  const normalizedTypes = types.map((item, index) => ensureSupportedEventType(item, `types[${index}]`));

  if (types.length === 0) {
    return undefined;
  }

  return { types: normalizedTypes };
}

function createEventStreamTerminationError(error?: unknown): DevHubConnectionError {
  if (error instanceof DevHubConnectionError && error.kind === "invalid_response") {
    return error;
  }

  return new DevHubConnectionError({
    kind: "session_terminated",
    message: "The event stream is unavailable. Re-authenticate and subscribe again.",
    cause: error
  });
}
