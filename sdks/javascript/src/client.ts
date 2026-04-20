import {
  createRuntimeView,
  type DevHubRuntimeView
} from "./runtime-view.js";
import type {
  RuntimeConnectionInfo,
  RuntimeResolver
} from "./runtime.js";
import { JsonRpcHttpTransport } from "./http-transport.js";
import {
  normalizeClientOptions,
  validateClientOptions
} from "./models.js";
import type {
  AppDefinition,
  AppDefinitionIdentity,
  AppInstance,
  AppInstanceRegistration,
  DefinitionValidationResult,
  DevHubClientOptions,
  InvokeRequest,
  JsonValue,
  LaunchRequest,
  LaunchResult,
  ListInstancesRequest,
  NotifyResult,
  PingResult,
  PollRequest,
  PollResult,
  RequestResult,
  RespondRequest,
  NormalizedDevHubClientOptions
} from "./models.js";
import {
  parseDefinitionResult,
  parseDefinitionValidationResult,
  parseDefinitionsResult,
  parseHeartbeatResult,
  parseInstancesResult,
  parseLaunchResult,
  parseNotifyResult,
  parsePingResult,
  parsePollResult,
  parseRegisterInstanceResult,
  parseRequestResult,
  parseUpsertDefinitionResult,
  parseVoidOkResult
} from "./parsers.js";
import {
  buildDeleteDefinitionParams,
  buildGetDefinitionParams,
  buildHeartbeatParams,
  buildInvokeParams,
  buildLaunchParams,
  buildListInstancesParams,
  buildPollParams,
  buildRegisterInstanceParams,
  buildRespondParams,
  buildUpsertDefinitionParams,
  buildUnregisterParams,
  buildValidateDefinitionParams
} from "./payloads.js";
import { getRuntimeResolver } from "./default-runtime-resolver.js";
import { ensureJsonValue } from "./validation.js";

export interface JsonRpcTransport {
  send(method: string, params?: Record<string, unknown> | null): Promise<Record<string, unknown>>;
  dispose?(): Promise<void> | void;
}

export type JsonRpcTransportFactory = (
  options: NormalizedDevHubClientOptions,
  connection: RuntimeConnectionInfo
) => JsonRpcTransport;

export interface DevHubClientDependencies {
  runtimeResolver?: RuntimeResolver;
  transportFactory?: JsonRpcTransportFactory;
}

export class DevHubClient {
  readonly options: NormalizedDevHubClientOptions;

  readonly #connection: RuntimeConnectionInfo;
  readonly #transport: JsonRpcTransport;
  #disposed = false;

  private constructor(
    options: NormalizedDevHubClientOptions,
    connection: RuntimeConnectionInfo,
    transport: JsonRpcTransport
  ) {
    this.options = options;
    this.#connection = connection;
    this.#transport = transport;
  }

  get runtime(): DevHubRuntimeView {
    return createRuntimeView(this.#connection.runtime);
  }

  static async fromRuntime(
    options: DevHubClientOptions,
    dependencies: DevHubClientDependencies = {}
  ): Promise<DevHubClient> {
    const normalized = normalizeClientOptions(options);
    validateClientOptions(normalized);
    const runtimeResolver = await getRuntimeResolver(dependencies.runtimeResolver);
    const connection = await runtimeResolver.resolve(normalized);
    const transport = dependencies.transportFactory?.(normalized, connection)
      ?? new JsonRpcHttpTransport(normalized, connection);
    return new DevHubClient(normalized, connection, transport);
  }

  async ping(echo?: JsonValue): Promise<PingResult> {
    this.throwIfDisposed();
    const params = echo === undefined ? undefined : { echo: ensureJsonValue(echo, "echo") };
    return parsePingResult(await this.#transport.send("hub.ping", params));
  }

  async listDefinitions(): Promise<AppDefinition[]> {
    this.throwIfDisposed();
    return parseDefinitionsResult(await this.#transport.send("hub.apps.listDefinitions"));
  }

  async getDefinition(identity: AppDefinitionIdentity): Promise<AppDefinition> {
    this.throwIfDisposed();
    return parseDefinitionResult(await this.#transport.send("hub.apps.getDefinition", buildGetDefinitionParams(identity)));
  }

  async validateDefinition(definition: AppDefinition): Promise<DefinitionValidationResult> {
    this.throwIfDisposed();
    return parseDefinitionValidationResult(
      await this.#transport.send("hub.apps.validateDefinition", buildValidateDefinitionParams(definition))
    );
  }

  async upsertDefinition(definition: AppDefinition): Promise<AppDefinition> {
    this.throwIfDisposed();
    return parseUpsertDefinitionResult(
      await this.#transport.send("hub.apps.upsertDefinition", buildUpsertDefinitionParams(definition))
    );
  }

  async deleteDefinition(identity: AppDefinitionIdentity): Promise<void> {
    this.throwIfDisposed();
    parseVoidOkResult(
      await this.#transport.send("hub.apps.deleteDefinition", buildDeleteDefinitionParams(identity)),
      "hub.apps.deleteDefinition.result"
    );
  }

  async registerInstance(instance: AppInstanceRegistration, password: string): Promise<AppInstance> {
    this.throwIfDisposed();
    return parseRegisterInstanceResult(
      await this.#transport.send("hub.apps.registerInstance", buildRegisterInstanceParams(instance, password))
    );
  }

  async heartbeat(instanceId: string): Promise<Date> {
    this.throwIfDisposed();
    return parseHeartbeatResult(await this.#transport.send("hub.apps.heartbeat", buildHeartbeatParams(instanceId)));
  }

  async unregisterInstance(instanceId: string, password: string): Promise<void> {
    this.throwIfDisposed();
    parseVoidOkResult(
      await this.#transport.send("hub.apps.unregisterInstance", buildUnregisterParams(instanceId, password)),
      "hub.apps.unregisterInstance.result"
    );
  }

  async listInstances(request?: ListInstancesRequest): Promise<AppInstance[]> {
    this.throwIfDisposed();
    return parseInstancesResult(await this.#transport.send("hub.apps.listInstances", buildListInstancesParams(request)));
  }

  async launch(request: LaunchRequest): Promise<LaunchResult> {
    this.throwIfDisposed();
    return parseLaunchResult(await this.#transport.send("hub.apps.launch", buildLaunchParams(request)));
  }

  async notify(request: InvokeRequest): Promise<NotifyResult> {
    this.throwIfDisposed();
    return parseNotifyResult(await this.#transport.send("hub.invoke.notify", buildInvokeParams(request, false)));
  }

  async request(request: InvokeRequest): Promise<RequestResult> {
    this.throwIfDisposed();
    return parseRequestResult(await this.#transport.send("hub.invoke.request", buildInvokeParams(request, true)));
  }

  async poll(request: PollRequest): Promise<PollResult> {
    this.throwIfDisposed();
    return parsePollResult(await this.#transport.send("hub.invoke.poll", buildPollParams(request)));
  }

  async respond(request: RespondRequest): Promise<void> {
    this.throwIfDisposed();
    parseVoidOkResult(
      await this.#transport.send("hub.invoke.respond", buildRespondParams(request)),
      "hub.invoke.respond.result"
    );
  }

  async dispose(): Promise<void> {
    if (this.#disposed) {
      return;
    }

    this.#disposed = true;
    await this.#transport.dispose?.();
  }

  private throwIfDisposed(): void {
    if (this.#disposed) {
      throw new Error("The client has been disposed.");
    }
  }
}

export { DevHubEventsClient } from "./events-client.js";
