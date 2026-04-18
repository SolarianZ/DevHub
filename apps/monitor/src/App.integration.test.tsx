import { promises as fs } from "node:fs";
import { join } from "node:path";
import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
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
const PRIMARY_APP_ID = "monitor.integration.app";
const PRIMARY_INSTANCE_ID = "monitor-integration-instance";
const MISSING_APP_ID = "monitor.integration.missing";
const MISSING_INSTANCE_ID = "monitor-missing-instance";

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
    appId: PRIMARY_APP_ID,
    displayName: "Monitor Integration App",
    description: "用于 Monitor 真实 Host 集成回归。",
  });
  await host.writeDefinition({
    appId: MISSING_APP_ID,
    displayName: "Monitor Missing App",
    description: "用于缺失定义工作流。",
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
      instanceId: PRIMARY_INSTANCE_ID,
      appId: PRIMARY_APP_ID,
      pid: process.pid,
      invoke: {
        poll: true,
        respond: true,
      },
    }, INSTANCE_PASSWORD);

    await setupClient.registerInstance({
      instanceId: MISSING_INSTANCE_ID,
      appId: MISSING_APP_ID,
      pid: process.pid,
      invoke: {
        poll: true,
        respond: true,
      },
    }, INSTANCE_PASSWORD);

    await setupClient.deleteDefinition(MISSING_APP_ID);
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
  it("supports create, edit, view, and missing definition workflows against a real host", async () => {
    const connection = await createConnection(getHost());
    const restoreFetch = installBrowserStyleRpcFetch(connection.rpcEndpoint, "tauri://monitor-integration");

    try {
      render(<App />);

      await screen.findByRole("heading", { name: "主页" }, { timeout: 15_000 });
      await screen.findByText("Monitor Integration App", {}, { timeout: 15_000 });
      await screen.findByText(PRIMARY_INSTANCE_ID, {}, { timeout: 15_000 });
      await screen.findByText(MISSING_INSTANCE_ID, {}, { timeout: 15_000 });

      const user = userEvent.setup();

      await user.click(screen.getByRole("button", { name: "新增定义" }));
      await screen.findByRole("heading", { name: "新增 App Definition" }, { timeout: 15_000 });
      await user.type(screen.getByLabelText("App ID"), "monitor.integration.created");
      await user.type(screen.getByLabelText("显示名称"), "Monitor Created App");
      await user.type(screen.getByLabelText("描述"), "通过 Monitor UI 创建的定义。");
      await user.click(screen.getByRole("button", { name: "创建定义" }));

      await screen.findByRole("heading", { name: "主页" }, { timeout: 15_000 });
      await screen.findByText("Monitor Created App", {}, { timeout: 15_000 });

      const existingDefinitionRow = screen.getByText("Monitor Integration App").closest("article");
      if (!existingDefinitionRow) {
        throw new Error("Definition row not found.");
      }

      await user.click(within(existingDefinitionRow).getByRole("button", { name: "编辑" }));
      await screen.findByRole("heading", { name: "编辑 App Definition" }, { timeout: 15_000 });
      await user.clear(screen.getByLabelText("显示名称"));
      await user.type(screen.getByLabelText("显示名称"), "Monitor Integration App Updated");
      await user.click(screen.getByRole("button", { name: "保存修改" }));

      await screen.findByRole("heading", { name: "主页" }, { timeout: 15_000 });
      await screen.findByText("Monitor Integration App Updated", {}, { timeout: 15_000 });

      const primaryInstanceRow = screen.getByText(PRIMARY_INSTANCE_ID).closest("article");
      if (!primaryInstanceRow) {
        throw new Error("Primary instance row not found.");
      }

      await user.click(within(primaryInstanceRow).getByRole("button", { name: "查看定义" }));
      await screen.findByRole("heading", { name: "实例关联定义" }, { timeout: 15_000 });
      expect((screen.getByLabelText("显示名称") as HTMLInputElement).value).toBe("Monitor Integration App Updated");
      screen.getByText("只读模式不允许保存或删除。");
      await user.click(screen.getAllByRole("button", { name: "返回主页" })[0]);

      await screen.findByRole("heading", { name: "主页" }, { timeout: 15_000 });

      const missingInstanceRow = screen.getByText(MISSING_INSTANCE_ID).closest("article");
      if (!missingInstanceRow) {
        throw new Error("Missing instance row not found.");
      }

      await user.click(within(missingInstanceRow).getByRole("button", { name: "查看定义" }));
      await screen.findByRole("heading", { name: "定义不存在" }, { timeout: 15_000 });
      expect(screen.getAllByText(new RegExp(MISSING_INSTANCE_ID)).length).toBeGreaterThan(0);
      expect(screen.getAllByText(new RegExp(MISSING_APP_ID)).length).toBeGreaterThan(0);
      await user.click(screen.getAllByRole("button", { name: "返回主页" })[0]);

      await screen.findByRole("heading", { name: "主页" }, { timeout: 15_000 });

      await getHost().close();
      host = undefined;

      await waitFor(() => {
        expect(resumeDiscoveryMock).toHaveBeenCalledWith("host_session_terminated");
      }, { timeout: 15_000 });
      await screen.findByText("重新扫描", {}, { timeout: 15_000 });
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
