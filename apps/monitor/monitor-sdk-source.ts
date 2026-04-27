import path from "node:path";
import { fileURLToPath } from "node:url";

export interface MonitorSdkSourceConfig {
  resolveAlias: Array<{ find: string; replacement: string }>;
  serverFsAllow: string[];
}

export function resolveMonitorSdkSourceConfig(): MonitorSdkSourceConfig {
  const monitorDir = fileURLToPath(new URL(".", import.meta.url));
  const sdkWorkspaceDir = path.resolve(monitorDir, "..", "..", "sdks", "javascript");

  return {
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
