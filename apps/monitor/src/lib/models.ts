export type DataDirSource = "settings_override" | "environment" | "platform_default";

export type BootstrapPhase =
  | "scanning"
  | "launch_available"
  | "settings_required"
  | "host_available";

export type HostLaunchStatus = "started" | "settings_required";

export type LogKind = "host" | "monitor";

export type MonitorLogLevel = "trace" | "debug" | "info" | "warn" | "error";

export interface MonitorSettings {
  dataDirOverride?: string | null;
  hostExecutablePath?: string | null;
}

export interface MonitorProblem {
  code: string;
  message: string;
}

export interface MonitorRuntimeTuning {
  leaseSeconds: number;
  onlineThresholdSeconds: number;
  launchDedupeWindowSeconds: number;
}

export interface MonitorHubRuntime {
  protocolVersion: number;
  pid: number;
  httpBaseUrl: string;
  wsUrl: string;
  tokenFile: string;
  startedAtUtc: string;
  runtimeTuning: MonitorRuntimeTuning;
  hubVersion?: string | null;
}

export interface MonitorRuntimeConnectionInfo {
  runtimeDirectory: string;
  token: string;
  runtime: MonitorHubRuntime;
  rpcEndpoint: string;
  websocketEndpoint: string;
}

export interface BootstrapSnapshot {
  generation: number;
  phase: BootstrapPhase;
  effectiveDataDir: string;
  dataDirSource: DataDirSource;
  settings: MonitorSettings;
  hasConfiguredHostExecutable: boolean;
  connection?: MonitorRuntimeConnectionInfo | null;
  lastProblem?: MonitorProblem | null;
}

export interface SettingsSnapshot {
  settings: MonitorSettings;
  effectiveDataDir: string;
  dataDirSource: DataDirSource;
  settingsFilePath: string;
  monitorLogDirectory: string;
}

export interface LaunchHostResult {
  status: HostLaunchStatus;
  effectiveDataDir: string;
  dataDirSource: DataDirSource;
  pid?: number | null;
}

export interface LogFileInfo {
  kind: LogKind;
  name: string;
  filePath: string;
  sizeBytes: number;
  modifiedAtUtc?: string | null;
}

export interface ReadLogRequest {
  kind: LogKind;
  fileName: string;
}

export interface LogReadResult {
  kind: LogKind;
  fileName: string;
  filePath: string;
  sizeBytes: number;
  truncated: boolean;
  contents: string;
}

export interface FrontendLogInput {
  level: MonitorLogLevel;
  category: string;
  action: string;
  result: string;
  message?: string | null;
  context?: Record<string, unknown>;
}
