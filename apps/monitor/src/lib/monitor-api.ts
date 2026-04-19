import { invoke } from "@tauri-apps/api/core";
import type { RuntimeConnectionInfo, RuntimeResolver } from "@devhub/sdk/runtime";
import type {
  BootstrapSnapshot,
  FrontendLogInput,
  LaunchHostResult,
  LogKind,
  MonitorRuntimeConnectionInfo,
  MonitorSettings,
  SettingsSnapshot,
} from "./models";

export const BOOTSTRAP_STATE_CHANGED_EVENT = "devhub://bootstrap-state-changed";
export const SETTINGS_CHANGED_EVENT = "devhub://settings-changed";

export function toSdkRuntimeConnectionInfo(
  connection: MonitorRuntimeConnectionInfo,
): RuntimeConnectionInfo {
  return {
    runtimeDirectory: connection.runtimeDirectory,
    token: connection.token,
    rpcEndpoint: connection.rpcEndpoint,
    websocketEndpoint: connection.websocketEndpoint,
    runtime: {
      protocolVersion: connection.runtime.protocolVersion,
      pid: connection.runtime.pid,
      httpBaseUrl: connection.runtime.httpBaseUrl,
      wsUrl: connection.runtime.wsUrl,
      tokenFile: connection.runtime.tokenFile,
      startedAtUtc: new Date(connection.runtime.startedAtUtc),
      runtimeTuning: {
        leaseSeconds: connection.runtime.runtimeTuning.leaseSeconds,
        onlineThresholdSeconds: connection.runtime.runtimeTuning.onlineThresholdSeconds,
        launchDedupeWindowSeconds: connection.runtime.runtimeTuning.launchDedupeWindowSeconds,
      },
      hubVersion: connection.runtime.hubVersion ?? undefined,
    },
  };
}

export function createStaticRuntimeResolver(
  connection: MonitorRuntimeConnectionInfo,
): RuntimeResolver {
  const resolved = toSdkRuntimeConnectionInfo(connection);

  return {
    async resolve() {
      return resolved;
    },
  };
}

export function getBootstrapState(): Promise<BootstrapSnapshot> {
  return invoke("monitor_get_bootstrap_state");
}

export function getSettingsSnapshot(): Promise<SettingsSnapshot> {
  return invoke("monitor_get_settings_snapshot");
}

export function saveSettings(settings: MonitorSettings): Promise<SettingsSnapshot> {
  return invoke("monitor_save_settings", { settings });
}

export function requestHostLaunch(): Promise<LaunchHostResult> {
  return invoke("monitor_request_host_launch");
}

export function resumeDiscovery(reason?: string): Promise<BootstrapSnapshot> {
  return invoke("monitor_resume_discovery", { reason });
}

export function openLogDirectory(kind: LogKind): Promise<void> {
  return invoke("monitor_open_log_directory", { kind });
}

export function pickHostExecutablePath(currentPath?: string | null): Promise<string | null> {
  return invoke("monitor_pick_host_executable_path", { currentPath });
}

export function pickDataDirectory(currentPath?: string | null): Promise<string | null> {
  return invoke("monitor_pick_data_directory", { currentPath });
}

export function writeFrontendLog(entry: FrontendLogInput): Promise<void> {
  return invoke("monitor_write_frontend_log", { entry });
}
