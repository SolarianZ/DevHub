import type { HubRuntime } from "./runtime.js";

export interface DevHubRuntimeView {
  protocolVersion: number;
  pid: number;
  startedAtUtc: Date;
  hubVersion?: string;
}

export function createRuntimeView(runtime: HubRuntime): DevHubRuntimeView {
  return {
    protocolVersion: runtime.protocolVersion,
    pid: runtime.pid,
    startedAtUtc: new Date(runtime.startedAtUtc.getTime()),
    hubVersion: runtime.hubVersion
  };
}
