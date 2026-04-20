import { AsyncQueue } from "./async-utils.js";
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
  ListInstancesRequest,
  PingResult,
  NormalizedDevHubClientOptions
} from "./models.js";
import {
  parseAuthenticateResult,
  parseDefinitionResult,
  parseDefinitionsResult,
  parseEvent,
  parseInstancesResult,
  parsePingResult,
  parseSubscriptionResult,
  parseUnsubscribeResult
} from "./parsers.js";
import {
  buildGetDefinitionParams,
  buildListInstancesParams
} from "./payloads.js";
import { getRuntimeResolver } from "./default-runtime-resolver.js";
import { ensureJsonValue } from "./validation.js";
import {
  createRuntimeView,
  type DevHubRuntimeView
} from "./runtime-view.js";
import type { RuntimeConnectionInfo, RuntimeResolver } from "./runtime.js";
import { JsonRpcWsSession, type JsonRpcWsSessionOptions } from "./ws-session.js";

export interface JsonRpcEventSession {
  ensureConnected(): Promise<void>;
  sendRequest(method: string, params?: Record<string, unknown>): Promise<Record<string, unknown>>;
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
  #eventQueue = new AsyncQueue<DevHubEvent>();
  readonly #connection: RuntimeConnectionInfo;
  readonly #session: JsonRpcEventSession;
  #authenticated = false;
  #eventStreamAvailable = false;
  #disposed = false;

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
          client.#eventQueue.push(parseEvent(params, "hub.event.params"));
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
      this.#eventQueue = new AsyncQueue<DevHubEvent>();
      this.#authenticated = true;
      this.#eventStreamAvailable = true;
    } catch (error) {
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

  async listDefinitions(): Promise<AppDefinition[]> {
    this.ensureAuthenticated();
    return parseDefinitionsResult(await this.#session.sendRequest("hub.apps.listDefinitions"));
  }

  async getDefinition(identity: AppDefinitionIdentity): Promise<AppDefinition> {
    this.ensureAuthenticated();
    return parseDefinitionResult(
      await this.#session.sendRequest("hub.apps.getDefinition", buildGetDefinitionParams(identity))
    );
  }

  async listInstances(request?: ListInstancesRequest): Promise<AppInstance[]> {
    this.ensureAuthenticated();
    return parseInstancesResult(
      await this.#session.sendRequest("hub.apps.listInstances", buildListInstancesParams(request))
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
    return this.#eventQueue;
  }

  async dispose(): Promise<void> {
    if (this.#disposed) {
      return;
    }

    this.#disposed = true;
    this.#authenticated = false;
    this.#eventStreamAvailable = false;
    this.#eventQueue.close();

    await this.#session.dispose("client_dispose");
  }

  private handleTermination(error?: Error): void {
    if (this.#disposed) {
      return;
    }

    this.#authenticated = false;
    this.#eventQueue.close(error);
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
      this.ensureAuthenticated();
    }
  }

  private throwIfDisposed(): void {
    if (this.#disposed) {
      throw new Error("The events client has been disposed.");
    }
  }
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
