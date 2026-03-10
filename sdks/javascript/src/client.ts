import { discoverRuntime, RuntimeConnectionInfo } from "./runtime.js";
import {
  DevHubClientOptions,
  NormalizedDevHubClientOptions,
  normalizeClientOptions,
  validateClientOptions
} from "./models.js";

export class DevHubClient {
  readonly options: NormalizedDevHubClientOptions;
  readonly connection: RuntimeConnectionInfo;

  private constructor(options: NormalizedDevHubClientOptions, connection: RuntimeConnectionInfo) {
    this.options = options;
    this.connection = connection;
  }

  static async fromRuntime(options: DevHubClientOptions): Promise<DevHubClient> {
    const normalized = normalizeClientOptions(options);
    validateClientOptions(normalized);
    const connection = await discoverRuntime(normalized.runtimeDir);
    return new DevHubClient(normalized, connection);
  }
}

export class DevHubEventsClient {
  readonly options: NormalizedDevHubClientOptions;
  readonly connection: RuntimeConnectionInfo;

  private constructor(options: NormalizedDevHubClientOptions, connection: RuntimeConnectionInfo) {
    this.options = options;
    this.connection = connection;
  }

  static async fromRuntime(options: DevHubClientOptions): Promise<DevHubEventsClient> {
    const normalized = normalizeClientOptions(options);
    validateClientOptions(normalized);
    const connection = await discoverRuntime(normalized.runtimeDir);
    return new DevHubEventsClient(normalized, connection);
  }
}
