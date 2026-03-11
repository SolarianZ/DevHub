import { JsonRpcHttpTransport } from "./http-transport.js";
import {
  normalizeClientOptions,
  validateClientOptions
} from "./models.js";
import type {
  AppDefinition,
  AppInstance,
  AppInstanceRegistration,
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
  parseDefinitionsResult,
  parseHeartbeatResult,
  parseInstancesResult,
  parseLaunchResult,
  parseNotifyResult,
  parsePingResult,
  parsePollResult,
  parseRegisterInstanceResult,
  parseRequestResult,
  parseVoidOkResult
} from "./parsers.js";
import {
  buildGetDefinitionParams,
  buildHeartbeatParams,
  buildInvokeParams,
  buildLaunchParams,
  buildListInstancesParams,
  buildPollParams,
  buildRegisterInstanceParams,
  buildRespondParams,
  buildUnregisterParams
} from "./payloads.js";
import { discoverRuntime } from "./runtime.js";
import { ensureJsonValue } from "./validation.js";
import type { RuntimeConnectionInfo } from "./runtime.js";

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
    const params = echo === undefined ? undefined : { echo: ensureJsonValue(echo, "echo") };
    return parsePingResult(await this.transport.send("hub.ping", params));
  }

  async listDefinitions(): Promise<AppDefinition[]> {
    return parseDefinitionsResult(await this.transport.send("hub.apps.listDefinitions"));
  }

  async getDefinition(appId: string): Promise<AppDefinition> {
    return parseDefinitionResult(await this.transport.send("hub.apps.getDefinition", buildGetDefinitionParams(appId)));
  }

  async registerInstance(instance: AppInstanceRegistration): Promise<AppInstance> {
    return parseRegisterInstanceResult(
      await this.transport.send("hub.apps.registerInstance", buildRegisterInstanceParams(instance))
    );
  }

  async heartbeat(instanceId: string): Promise<Date> {
    return parseHeartbeatResult(await this.transport.send("hub.apps.heartbeat", buildHeartbeatParams(instanceId)));
  }

  async unregisterInstance(instanceId: string): Promise<void> {
    parseVoidOkResult(
      await this.transport.send("hub.apps.unregisterInstance", buildUnregisterParams(instanceId)),
      "hub.apps.unregisterInstance.result"
    );
  }

  async listInstances(request?: ListInstancesRequest): Promise<AppInstance[]> {
    return parseInstancesResult(await this.transport.send("hub.apps.listInstances", buildListInstancesParams(request)));
  }

  async launch(request: LaunchRequest): Promise<LaunchResult> {
    return parseLaunchResult(await this.transport.send("hub.apps.launch", buildLaunchParams(request)));
  }

  async notify(request: InvokeRequest): Promise<NotifyResult> {
    return parseNotifyResult(await this.transport.send("hub.invoke.notify", buildInvokeParams(request, false)));
  }

  async request(request: InvokeRequest): Promise<RequestResult> {
    return parseRequestResult(await this.transport.send("hub.invoke.request", buildInvokeParams(request, true)));
  }

  async poll(request: PollRequest): Promise<PollResult> {
    return parsePollResult(await this.transport.send("hub.invoke.poll", buildPollParams(request)));
  }

  async respond(request: RespondRequest): Promise<void> {
    parseVoidOkResult(
      await this.transport.send("hub.invoke.respond", buildRespondParams(request)),
      "hub.invoke.respond.result"
    );
  }

  async dispose(): Promise<void> {
    return Promise.resolve();
  }
}

export { DevHubEventsClient } from "./events-client.js";
