import { AsyncQueue } from "./async-utils.js";
import {
  normalizeClientOptions,
  validateClientOptions
} from "./models.js";
import type {
  DevHubClientOptions,
  DevHubEvent,
  NormalizedDevHubClientOptions
} from "./models.js";
import {
  parseAuthenticateResult,
  parseEvent,
  parseSubscriptionResult,
  parseUnsubscribeResult
} from "./parsers.js";
import { discoverRuntime } from "./runtime.js";
import type { RuntimeConnectionInfo } from "./runtime.js";
import { JsonRpcWsSession } from "./ws-session.js";

export class DevHubEventsClient {
  readonly options: NormalizedDevHubClientOptions;
  readonly connection: RuntimeConnectionInfo;

  private readonly eventQueue = new AsyncQueue<DevHubEvent>();
  private readonly session: JsonRpcWsSession;
  private authenticated = false;
  private eventStreamAvailable = false;
  private disposed = false;

  private constructor(options: NormalizedDevHubClientOptions, connection: RuntimeConnectionInfo) {
    this.options = options;
    this.connection = connection;
    this.session = new JsonRpcWsSession({
      websocketEndpoint: connection.websocketEndpoint,
      requestTimeoutMs: options.requestTimeoutMs,
      onEvent: (params) => {
        this.eventQueue.push(parseEvent(params, "hub.event.params"));
      },
      onTerminate: (error) => {
        this.handleTermination(error);
      }
    });
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
      throw new Error("The events client is already authenticated.");
    }

    await this.session.ensureConnected();

    try {
      const result = await this.session.sendRequest("hub.ws.authenticate", {
        token: this.connection.token,
        protocolVersion: this.options.protocolVersion,
        clientId: this.options.clientId,
        clientSessionId: this.options.clientSessionId
      });

      parseAuthenticateResult(result);
      this.authenticated = true;
      this.eventStreamAvailable = true;
    } catch (error) {
      await this.session.disconnect("authenticate_failed");
      throw error;
    }
  }

  async subscribe(types?: string[]): Promise<string> {
    this.ensureAuthenticated();
    return parseSubscriptionResult(await this.session.sendRequest("hub.events.subscribe", buildSubscribeParams(types)));
  }

  async unsubscribe(subscriptionId: string): Promise<void> {
    this.ensureAuthenticated();
    if (!subscriptionId || !subscriptionId.trim()) {
      throw new Error("subscriptionId cannot be empty.");
    }

    parseUnsubscribeResult(await this.session.sendRequest("hub.events.unsubscribe", { subscriptionId }));
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
    this.eventQueue.close();

    await this.session.dispose("client_dispose");
  }

  private handleTermination(error: Error): void {
    if (this.disposed) {
      return;
    }

    this.authenticated = false;
    this.eventQueue.close(error);
  }

  private ensureAuthenticated(): void {
    this.throwIfDisposed();
    if (!this.authenticated) {
      throw new Error("The events client is not authenticated.");
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
      throw new Error("The events client has been disposed.");
    }
  }
}

function buildSubscribeParams(types?: string[]): Record<string, unknown> | undefined {
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

  return types.length > 0 ? { types } : undefined;
}
