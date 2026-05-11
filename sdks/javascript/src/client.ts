import {
  createRuntimeView,
  type DevHubRuntimeView
} from "./runtime-view.js";
import type {
  RuntimeConnectionInfo,
  RuntimeResolver
} from "./runtime.js";
import { validateRuntimeConnectionInfo } from "./runtime-validation.js";
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
  ListDefinitionsRequest,
  LaunchRequest,
  LaunchResult,
  ListInstancesRequest,
  NotifyResult,
  PingResult,
  PollRequest,
  PollResult,
  RegisterInstanceOptions,
  RegisteredAppInstance,
  RequestResult,
  RespondRequest,
  NormalizedDevHubClientOptions,
  VersionCompatibilityResult
} from "./models.js";
import {
  parseDefinitionResult,
  parseDefinitionValidationResult,
  parseDefinitionsResult,
  parseHeartbeatResult,
  parseHostVersionResult,
  parseInstanceResult,
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
  buildGetInstanceParams,
  buildHeartbeatParams,
  buildInvokeParams,
  buildLaunchParams,
  buildListDefinitionsParams,
  buildListInstancesParams,
  buildPollParams,
  buildRegisterInstanceParams,
  buildRespondParams,
  buildUpsertDefinitionParams,
  buildUnregisterParams,
  buildValidateDefinitionParams
} from "./payloads.js";
import { getRuntimeResolver } from "./default-runtime-resolver.js";
import { DevHubConnectionError } from "./errors.js";
import { ensureJsonValue } from "./validation.js";
import { checkVersionCompatibilityWithFallback } from "./versioning.js";

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
    validateRuntimeConnectionInfo(connection);
    const transport = dependencies.transportFactory?.(normalized, connection)
      ?? new JsonRpcHttpTransport(normalized, connection);
    return new DevHubClient(normalized, connection, transport);
  }

  async ping(echo?: JsonValue): Promise<PingResult> {
    this.throwIfDisposed();
    const params = echo === undefined ? undefined : { echo: ensureJsonValue(echo, "echo") };
    return await this.sendAndParse("hub.ping.result", params, "hub.ping", parsePingResult);
  }

  /**
   * 读取当前连接 Host 的运行时版本。
   */
  async getHostVersion(): Promise<string> {
    this.throwIfDisposed();
    return await this.sendAndParse("hub.getVersion.result", undefined, "hub.getVersion", parseHostVersionResult);
  }

  /**
   * 检查当前 SDK 与 Host 的版本兼容状态。
   * 优先调用 hub.getVersion；旧 Host 返回 method_not_found 时回退到 runtime.hubVersion。
   */
  async checkVersionCompatibility(): Promise<VersionCompatibilityResult> {
    return checkVersionCompatibilityWithFallback(
      () => this.getHostVersion(),
      this.#connection.runtime.hubVersion
    );
  }

  async listDefinitions(request: ListDefinitionsRequest): Promise<AppDefinition[]> {
    this.throwIfDisposed();
    return await this.sendAndParse(
      "hub.apps.listDefinitions.result",
      buildListDefinitionsParams(request),
      "hub.apps.listDefinitions",
      parseDefinitionsResult
    );
  }

  async getDefinition(identity: AppDefinitionIdentity): Promise<AppDefinition> {
    this.throwIfDisposed();
    return await this.sendAndParse(
      "hub.apps.getDefinition.result",
      buildGetDefinitionParams(identity),
      "hub.apps.getDefinition",
      parseDefinitionResult
    );
  }

  async validateDefinition(definition: AppDefinition): Promise<DefinitionValidationResult> {
    this.throwIfDisposed();
    return await this.sendAndParse(
      "hub.apps.validateDefinition.result",
      buildValidateDefinitionParams(definition),
      "hub.apps.validateDefinition",
      parseDefinitionValidationResult
    );
  }

  async upsertDefinition(definition: AppDefinition): Promise<AppDefinition> {
    this.throwIfDisposed();
    return await this.sendAndParse(
      "hub.apps.upsertDefinition.result",
      buildUpsertDefinitionParams(definition),
      "hub.apps.upsertDefinition",
      parseUpsertDefinitionResult
    );
  }

  async deleteDefinition(identity: AppDefinitionIdentity): Promise<void> {
    this.throwIfDisposed();
    await this.sendAndParse(
      "hub.apps.deleteDefinition.result",
      buildDeleteDefinitionParams(identity),
      "hub.apps.deleteDefinition",
      (payload) => parseVoidOkResult(payload, "hub.apps.deleteDefinition.result")
    );
  }

  async registerInstance(
    instance: AppInstanceRegistration,
    password: string,
    options?: RegisterInstanceOptions
  ): Promise<RegisteredAppInstance> {
    this.throwIfDisposed();
    return await this.sendAndParse(
      "hub.apps.registerInstance.result",
      buildRegisterInstanceParams(instance, password, options),
      "hub.apps.registerInstance",
      parseRegisterInstanceResult
    );
  }

  async heartbeat(instanceId: string, instanceSessionToken: string): Promise<Date> {
    this.throwIfDisposed();
    return await this.sendAndParse(
      "hub.apps.heartbeat.result",
      buildHeartbeatParams(instanceId, instanceSessionToken),
      "hub.apps.heartbeat",
      parseHeartbeatResult
    );
  }

  async unregisterInstance(instanceId: string, instanceSessionToken: string): Promise<void> {
    this.throwIfDisposed();
    await this.sendAndParse(
      "hub.apps.unregisterInstance.result",
      buildUnregisterParams(instanceId, instanceSessionToken),
      "hub.apps.unregisterInstance",
      (payload) => parseVoidOkResult(payload, "hub.apps.unregisterInstance.result")
    );
  }

  async listInstances(request: ListInstancesRequest): Promise<AppInstance[]> {
    this.throwIfDisposed();
    return await this.sendAndParse(
      "hub.apps.listInstances.result",
      buildListInstancesParams(request),
      "hub.apps.listInstances",
      parseInstancesResult
    );
  }

  async getInstance(instanceId: string): Promise<AppInstance> {
    this.throwIfDisposed();
    return await this.sendAndParse(
      "hub.apps.getInstance.result",
      buildGetInstanceParams(instanceId),
      "hub.apps.getInstance",
      parseInstanceResult
    );
  }

  async launch(request: LaunchRequest): Promise<LaunchResult> {
    this.throwIfDisposed();
    return await this.sendAndParse("hub.apps.launch.result", buildLaunchParams(request), "hub.apps.launch", parseLaunchResult);
  }

  async notify(request: InvokeRequest): Promise<NotifyResult> {
    this.throwIfDisposed();
    return await this.sendAndParse(
      "hub.invoke.notify.result",
      buildInvokeParams(request, false),
      "hub.invoke.notify",
      parseNotifyResult
    );
  }

  async request(request: InvokeRequest): Promise<RequestResult> {
    this.throwIfDisposed();
    return await this.sendAndParse(
      "hub.invoke.request.result",
      buildInvokeParams(request, true),
      "hub.invoke.request",
      parseRequestResult
    );
  }

  async poll(request: PollRequest): Promise<PollResult> {
    this.throwIfDisposed();
    return await this.sendAndParse("hub.invoke.poll.result", buildPollParams(request), "hub.invoke.poll", parsePollResult);
  }

  async respond(request: RespondRequest): Promise<void> {
    this.throwIfDisposed();
    await this.sendAndParse(
      "hub.invoke.respond.result",
      buildRespondParams(request),
      "hub.invoke.respond",
      (payload) => parseVoidOkResult(payload, "hub.invoke.respond.result")
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

  private async sendAndParse<TResult>(
    responseLocation: string,
    params: Record<string, unknown> | undefined,
    method: string,
    parser: (payload: Record<string, unknown>) => TResult
  ): Promise<TResult> {
    const payload = await this.#transport.send(method, params);

    try {
      return parser(payload);
    } catch (error) {
      throw toInvalidResponseError(error, responseLocation);
    }
  }
}

export { DevHubEventsClient } from "./events-client.js";

function toInvalidResponseError(error: unknown, location: string): DevHubConnectionError {
  if (error instanceof DevHubConnectionError) {
    return error;
  }

  return new DevHubConnectionError({
    kind: "invalid_response",
    message: error instanceof Error ? error.message : `${location} is invalid.`,
    cause: error
  });
}
