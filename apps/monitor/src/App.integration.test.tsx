import { promises as fs } from "node:fs";
import { join } from "node:path";
import { render, screen, waitFor } from "@testing-library/react";
import { DevHubClient } from "@devhub/sdk";
import { afterAll, beforeAll, describe, expect, it, vi } from "vitest";
import { DevHubHostFixture } from "../../../sdks/javascript/tests/integration/host";
import App from "./App";
import type {
  BootstrapSnapshot,
  FrontendLogInput,
  LogFileInfo,
  LogKind,
  LogReadResult,
  MonitorRuntimeConnectionInfo,
  SettingsSnapshot,
} from "./lib/models";

const INSTANCE_PASSWORD = "monitor-integration-password";

const {
  listenMock,
  getBootstrapStateMock,
  getSettingsSnapshotMock,
  listLogsMock,
  readLogMock,
  requestHostLaunchMock,
  resumeDiscoveryMock,
  saveSettingsMock,
  writeFrontendLogMock,
} = vi.hoisted(() => ({
  listenMock: vi.fn(),
  getBootstrapStateMock: vi.fn<() => Promise<BootstrapSnapshot>>(),
  getSettingsSnapshotMock: vi.fn<() => Promise<SettingsSnapshot>>(),
  listLogsMock: vi.fn<(kind: LogKind) => Promise<LogFileInfo[]>>(),
  readLogMock: vi.fn<
    (request: { kind: LogKind; fileName: string }) => Promise<LogReadResult>
  >(),
  requestHostLaunchMock: vi.fn(),
  resumeDiscoveryMock: vi.fn(),
  saveSettingsMock: vi.fn(),
  writeFrontendLogMock: vi.fn<(entry: FrontendLogInput) => Promise<void>>(),
}));

vi.mock("@tauri-apps/api/event", () => ({
  listen: listenMock,
}));

vi.mock("./lib/monitor-api", async () => {
  const actual = await vi.importActual<typeof import("./lib/monitor-api")>("./lib/monitor-api");

  return {
    ...actual,
    getBootstrapState: getBootstrapStateMock,
    getSettingsSnapshot: getSettingsSnapshotMock,
    listLogs: listLogsMock,
    readLog: readLogMock,
    requestHostLaunch: requestHostLaunchMock,
    resumeDiscovery: resumeDiscoveryMock,
    saveSettings: saveSettingsMock,
    writeFrontendLog: writeFrontendLogMock,
  };
});

let host: DevHubHostFixture | undefined;
let originalWebSocket: typeof globalThis.WebSocket | undefined;

beforeAll(async () => {
  originalWebSocket = globalThis.WebSocket;
  Object.defineProperty(globalThis, "WebSocket", {
    configurable: true,
    value: undefined,
    writable: true,
  });

  host = await DevHubHostFixture.start();
  await host.writeDefinition({
    appId: "monitor.integration.app",
    displayName: "Monitor Integration App",
    description: "用于 Monitor 真实 Host 集成回归。",
  });
  const connection = await createConnection(getHost());

  const setupClient = await DevHubClient.fromRuntime({
    clientId: "monitor-integration-setup",
    dataDir: host.dataDirectory,
  }, {
    runtimeResolver: createSdkRuntimeResolver(connection),
  });

  try {
    await setupClient.registerInstance({
      instanceId: "monitor-integration-instance",
      appId: "monitor.integration.app",
      pid: process.pid,
      invoke: {
        poll: true,
        respond: true,
      },
    }, INSTANCE_PASSWORD);
  } finally {
    await setupClient.dispose();
  }

  getBootstrapStateMock.mockResolvedValue(createBootstrapSnapshot(connection));
  getSettingsSnapshotMock.mockResolvedValue(createSettingsSnapshot());
  listLogsMock.mockResolvedValue([]);
  readLogMock.mockResolvedValue({
    kind: "monitor",
    fileName: "monitor-latest.log",
    filePath: join(getHost().dataDirectory, "logs", "monitor-latest.log"),
    sizeBytes: 0,
    truncated: false,
    contents: "",
  });
  requestHostLaunchMock.mockResolvedValue({
    status: "started",
    effectiveDataDir: getHost().dataDirectory,
    dataDirSource: "settings_override",
    pid: null,
  });
  resumeDiscoveryMock.mockResolvedValue(
    createBootstrapSnapshot(connection, {
      generation: 2,
      phase: "scanning",
      connection: null,
    }),
  );
  saveSettingsMock.mockResolvedValue(createSettingsSnapshot());
  writeFrontendLogMock.mockResolvedValue(undefined);
  listenMock.mockImplementation(async () => () => {});
}, 120_000);

afterAll(async () => {
  await host?.close();
  Object.defineProperty(globalThis, "WebSocket", {
    configurable: true,
    value: originalWebSocket,
    writable: true,
  });
}, 120_000);

describe("Monitor App real-host integration", () => {
  it("loads state from a real host, reacts to definition events, and recovers after host termination", async () => {
    render(<App />);

    await screen.findByText("连接摘要", {}, { timeout: 15_000 });
    await screen.findByText("Monitor Integration App", {}, { timeout: 15_000 });
    await screen.findByText("monitor-integration-instance", {}, { timeout: 15_000 });

    const triggerClient = await DevHubClient.fromRuntime({
      clientId: "monitor-integration-trigger",
      dataDir: getHost().dataDirectory,
    }, {
      runtimeResolver: createSdkRuntimeResolver(await createConnection(getHost())),
    });

    try {
      await triggerClient.upsertDefinition({
        appId: "monitor.integration.extra",
        displayName: "Monitor Integration Extra",
        capabilities: {
          rpc: true,
        },
      });
    } finally {
      await triggerClient.dispose();
    }

    await screen.findByText("Monitor Integration Extra", {}, { timeout: 15_000 });

    await getHost().close();
    host = undefined;

    await waitFor(() => {
      expect(resumeDiscoveryMock).toHaveBeenCalledWith("host_session_terminated");
    }, { timeout: 15_000 });
    await screen.findByText("扫描与启动流程", {}, { timeout: 15_000 });
  }, 120_000);
});

function getHost(): DevHubHostFixture {
  if (!host) {
    throw new Error("Host fixture not started.");
  }

  return host;
}

async function createConnection(activeHost: DevHubHostFixture): Promise<MonitorRuntimeConnectionInfo> {
  const hubJsonPath = join(activeHost.runtimeDirectory, "hub.json");
  const payload = JSON.parse(await fs.readFile(hubJsonPath, "utf-8")) as {
    protocolVersion: number;
    pid: number;
    httpBaseUrl: string;
    wsUrl: string;
    tokenFile: string;
    startedAtUtc: string;
    runtimeTuning: {
      leaseSeconds: number;
      onlineThresholdSeconds: number;
      launchDedupeWindowSeconds: number;
    };
    hubVersion?: string;
  };
  const token = (await fs.readFile(payload.tokenFile, "utf-8")).trim();

  return {
    runtimeDirectory: activeHost.runtimeDirectory,
    token,
    rpcEndpoint: `${payload.httpBaseUrl}/rpc`,
    websocketEndpoint: payload.wsUrl,
    runtime: payload,
  };
}

function createBootstrapSnapshot(
  connection: MonitorRuntimeConnectionInfo,
  overrides: Partial<BootstrapSnapshot> = {},
): BootstrapSnapshot {
  return {
    generation: 1,
    phase: "host_available",
    effectiveDataDir: getHost().dataDirectory,
    dataDirSource: "settings_override",
    settings: {
      dataDirOverride: getHost().dataDirectory,
      hostExecutablePath: absoluteHostPlaceholder(),
    },
    hasConfiguredHostExecutable: true,
    connection,
    lastProblem: null,
    ...overrides,
  };
}

function createSettingsSnapshot(): SettingsSnapshot {
  return {
    settings: {
      dataDirOverride: getHost().dataDirectory,
      hostExecutablePath: absoluteHostPlaceholder(),
    },
    effectiveDataDir: getHost().dataDirectory,
    dataDirSource: "settings_override",
    settingsFilePath: join(getHost().dataDirectory, "monitor-settings.json"),
    monitorLogDirectory: join(getHost().dataDirectory, "logs"),
  };
}

function absoluteHostPlaceholder(): string {
  return process.platform === "win32"
    ? "C:\\devhub-tests\\DevHub.Host.exe"
    : "/tmp/devhub-tests/DevHub.Host";
}

function createSdkRuntimeResolver(connection: MonitorRuntimeConnectionInfo) {
  return {
    async resolve() {
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
    },
  };
}
