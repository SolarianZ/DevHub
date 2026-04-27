import path from "node:path";
import { fileURLToPath } from "node:url";

export const MONITOR_SDK_SOURCE_ENV = "DEVHUB_MONITOR_SDK_SOURCE";
export const RELEASE_MONITOR_SDK_SOURCE = "release";
export const LOCAL_MONITOR_SDK_SOURCE = "local-src";
export const DEFAULT_MONITOR_SDK_SOURCE = LOCAL_MONITOR_SDK_SOURCE;

export type MonitorSdkSource = typeof RELEASE_MONITOR_SDK_SOURCE | typeof LOCAL_MONITOR_SDK_SOURCE;

export interface MonitorSdkSourceConfig {
  source: MonitorSdkSource;
  isLocalSource: boolean;
  resolveAlias: Array<{ find: string; replacement: string }>;
  serverFsAllow: string[];
}

export function readMonitorSdkSource(env = process.env): MonitorSdkSource {
  const candidate = env[MONITOR_SDK_SOURCE_ENV]?.trim();
  if (!candidate) {
    return DEFAULT_MONITOR_SDK_SOURCE;
  }

  if (candidate === RELEASE_MONITOR_SDK_SOURCE || candidate === LOCAL_MONITOR_SDK_SOURCE) {
    return candidate;
  }

  throw new Error(
    `${MONITOR_SDK_SOURCE_ENV} 必须为 ${RELEASE_MONITOR_SDK_SOURCE} 或 ${LOCAL_MONITOR_SDK_SOURCE}，实际收到：${candidate}`
  );
}

export function resolveMonitorSdkSourceConfig(env = process.env): MonitorSdkSourceConfig {
  const source = readMonitorSdkSource(env);
  const monitorDir = fileURLToPath(new URL(".", import.meta.url));
  const sdkWorkspaceDir = path.resolve(monitorDir, "..", "..", "sdks", "javascript");

  if (source === RELEASE_MONITOR_SDK_SOURCE) {
    return {
      source,
      isLocalSource: false,
      resolveAlias: [],
      serverFsAllow: [monitorDir],
    };
  }

  return {
    source,
    isLocalSource: true,
    resolveAlias: [
      {
        find: "@devhub/sdk/runtime",
        replacement: path.resolve(sdkWorkspaceDir, "src", "runtime.ts"),
      },
      {
        find: "@devhub/sdk",
        replacement: path.resolve(sdkWorkspaceDir, "src", "index.ts"),
      },
    ],
    serverFsAllow: [monitorDir, sdkWorkspaceDir],
  };
}
