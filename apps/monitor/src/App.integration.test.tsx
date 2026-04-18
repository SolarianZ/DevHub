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
  LogKind,
  MonitorRuntimeConnectionInfo,
  SettingsSnapshot,
} from "./lib/models";

const INSTANCE_PASSWORD = "monitor-integration-password";

const {
  listenMock,
  getBootstrapStateMock,
  getSettingsSnapshotMock,
  openLogDirectoryMock,
  requestHostLaunchMock,
  resumeDiscoveryMock,
  saveSettingsMock,
  writeFrontendLogMock,
} = vi.hoisted(() => ({
  listenMock: vi.fn(),
  getBootstrapStateMock: vi.fn<() => Promise<BootstrapSnapshot>>(),
  getSettingsSnapshotMock: vi.fn<() => Promise<SettingsSnapshot>>(),
  openLogDirectoryMock: vi.fn<(kind: LogKind) => Promise<void>>(),
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
    openLogDirectory: openLogDirectoryMock,
    requestHostLaunch: requestHostLaunchMock,
    resumeDiscovery: resumeDiscoveryMock,
    saveSettings: saveSettingsMock,
    writeFrontendLog: writeFrontendLogMock,
  };
});

let host: DevHubHostFixture | undefined;
let originalWebSocket: typeof globalThis.WebSocket | undefined;
let originalFetch: typeof globalThis.fetch | undefined;

beforeAll(async () => {
  originalWebSocket = globalThis.WebSocket;
  originalFetch = globalThis.fetch;
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
  openLogDirectoryMock.mockResolvedValue(undefined);
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
  if (originalFetch) {
    Object.defineProperty(globalThis, "fetch", {
      configurable: true,
      value: originalFetch,
      writable: true,
    });
  }
  Object.defineProperty(globalThis, "WebSocket", {
    configurable: true,
    value: originalWebSocket,
    writable: true,
  });
}, 120_000);

describe("Monitor App real-host integration", () => {
  it("keeps the product shell working across real host connect, refresh, and recovery", async () => {
    const connection = await createConnection(getHost());
    const restoreFetch = installBrowserStyleRpcFetch(connection.rpcEndpoint, "tauri://monitor-integration");

    try {
      render(<App />);

      await screen.findByRole("heading", { name: "应用定义" }, { timeout: 15_000 });
      await screen.findByText("Monitor Integration App", {}, { timeout: 15_000 });
      await screen.findByText("monitor-integration-instance", {}, { timeout: 15_000 });
      screen.getByRole("button", { name: "设置" });
      screen.getByRole("button", { name: "帮助" });
      expect(screen.queryByRole("button", { name: "日志" })).toBeNull();

      const triggerClient = await DevHubClient.fromRuntime({
        clientId: "monitor-integration-trigger",
        dataDir: getHost().dataDirectory,
      }, {
        runtimeResolver: createSdkRuntimeResolver(connection),
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
      await screen.findByText("连接 DevHub Host", {}, { timeout: 15_000 });
    } finally {
      restoreFetch();
    }
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

function installBrowserStyleRpcFetch(rpcEndpoint: string, origin: string): () => void {
  if (!globalThis.fetch) {
    throw new Error("global fetch is unavailable.");
  }

  const delegatedFetch = globalThis.fetch.bind(globalThis);
  const fetchMock = vi.spyOn(globalThis, "fetch").mockImplementation(async (input, init) => {
    const requestUrl = resolveRequestUrl(input);
    const requestMethod = resolveRequestMethod(input, init);

    if (requestUrl === rpcEndpoint && requestMethod === "POST") {
      const requestHeaders = new Headers(resolveRequestHeaders(input, init));
      const requestedHeaderNames = Array.from(requestHeaders.keys())
        .filter((name) => name.toLowerCase() !== "origin")
        .sort((left, right) => left.localeCompare(right));

      const preflightResponse = await delegatedFetch(rpcEndpoint, {
        method: "OPTIONS",
        headers: {
          Origin: origin,
          "Access-Control-Request-Method": "POST",
          "Access-Control-Request-Headers": requestedHeaderNames.join(", "),
        },
      });

      expect(preflightResponse.status).toBe(204);
      expect(preflightResponse.headers.get("access-control-allow-origin")).toBe(origin);
      expect((preflightResponse.headers.get("vary") ?? "").toLowerCase()).toContain("origin");
      expect((preflightResponse.headers.get("access-control-allow-methods") ?? "").toUpperCase()).toContain("POST");
      expect((preflightResponse.headers.get("access-control-allow-methods") ?? "").toUpperCase()).toContain("OPTIONS");

      const allowHeaders = (preflightResponse.headers.get("access-control-allow-headers") ?? "").toLowerCase();
      for (const headerName of requestedHeaderNames) {
        expect(allowHeaders).toContain(headerName.toLowerCase());
      }

      const postHeaders = new Headers(requestHeaders);
      postHeaders.set("Origin", origin);
      const postResponse = await delegatedFetch(rpcEndpoint, {
        ...init,
        method: "POST",
        headers: postHeaders,
      });

      expect(postResponse.headers.get("access-control-allow-origin")).toBe(origin);
      expect((postResponse.headers.get("vary") ?? "").toLowerCase()).toContain("origin");
      return postResponse;
    }

    return delegatedFetch(input, init);
  });

  return () => fetchMock.mockRestore();
}

function resolveRequestUrl(input: RequestInfo | URL): string {
  if (typeof input === "string") {
    return input;
  }

  if (input instanceof URL) {
    return input.toString();
  }

  return input.url;
}

function resolveRequestMethod(input: RequestInfo | URL, init?: RequestInit): string {
  if (init?.method) {
    return init.method.toUpperCase();
  }

  if (typeof Request !== "undefined" && input instanceof Request) {
    return input.method.toUpperCase();
  }

  return "GET";
}

function resolveRequestHeaders(input: RequestInfo | URL, init?: RequestInit): HeadersInit | undefined {
  if (init?.headers !== undefined) {
    return init.headers;
  }

  if (typeof Request !== "undefined" && input instanceof Request) {
    return input.headers;
  }

  return undefined;
}
