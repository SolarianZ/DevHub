import { promises as fs } from "node:fs";
import { join } from "node:path";
import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { DevHubClient } from "@devhub/sdk";
import { afterAll, beforeAll, beforeEach, describe, expect, it, vi } from "vitest";
import { DevHubHostFixture } from "../../../sdks/javascript/tests/integration/host";
import App from "./App";
import { deleteDefinitionCompat, registerInstanceCompat } from "./lib/sdk-compat";
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
const PRIMARY_SCOPED_SCOPE = "workspace-a";
const PRIMARY_SCOPED_INSTANCE_ID = "monitor-integration-instance-workspace-a";
const MISSING_APP_ID = "monitor.integration.missing";
const MISSING_INSTANCE_ID = "monitor-missing-instance";

const {
  listenMock,
  getBootstrapStateMock,
  getSettingsSnapshotMock,
  openLogDirectoryMock,
  pickDataDirectoryMock,
  pickHostExecutablePathMock,
  requestHostLaunchMock,
  resumeDiscoveryMock,
  saveSettingsMock,
  writeFrontendLogMock,
} = vi.hoisted(() => ({
  listenMock: vi.fn(),
  getBootstrapStateMock: vi.fn<() => Promise<BootstrapSnapshot>>(),
  getSettingsSnapshotMock: vi.fn<() => Promise<SettingsSnapshot>>(),
  openLogDirectoryMock: vi.fn<(kind: LogKind) => Promise<void>>(),
  pickDataDirectoryMock: vi.fn<(currentPath?: string | null) => Promise<string | null>>(),
  pickHostExecutablePathMock: vi.fn<(currentPath?: string | null) => Promise<string | null>>(),
  requestHostLaunchMock: vi.fn(),
  resumeDiscoveryMock: vi.fn(),
  saveSettingsMock: vi.fn(),
  writeFrontendLogMock: vi.fn<(entry: FrontendLogInput) => Promise<void>>(),
}));

function formatScopeLabel(scope?: string | null): string {
  return `scope：${scope && scope.length > 0 ? scope : "Global"}`;
}

function getDefinitionActionLabel(displayName: string, appId: string, scope?: string | null): string {
  return `编辑定义：${displayName}（${appId}，${formatScopeLabel(scope)}）`;
}

function getInstanceActionLabel(instanceId: string, appId: string, scope?: string | null): string {
  return `查看定义：${instanceId}（${appId}，${formatScopeLabel(scope)}）`;
}

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
    pickDataDirectory: pickDataDirectoryMock,
    pickHostExecutablePath: pickHostExecutablePathMock,
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
    scope: "",
    displayName: "Monitor Integration App",
    description: "用于 Monitor 真实 Host 集成回归。",
  });
  await host.writeDefinition({
    appId: PRIMARY_APP_ID,
    scope: PRIMARY_SCOPED_SCOPE,
    displayName: "Monitor Integration App Scoped",
    description: "用于 Monitor 多 scope Definition 回归。",
  });
  await host.writeDefinition({
    appId: MISSING_APP_ID,
    scope: "",
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
    await registerInstanceCompat(setupClient, {
      instanceId: PRIMARY_INSTANCE_ID,
      appId: PRIMARY_APP_ID,
      scope: "",
      pid: process.pid,
      invoke: {
        poll: true,
        respond: true,
      },
    }, INSTANCE_PASSWORD);

    await registerInstanceCompat(setupClient, {
      instanceId: PRIMARY_SCOPED_INSTANCE_ID,
      appId: PRIMARY_APP_ID,
      scope: PRIMARY_SCOPED_SCOPE,
      pid: process.pid,
      invoke: {
        poll: true,
        respond: true,
      },
    }, INSTANCE_PASSWORD);

    await registerInstanceCompat(setupClient, {
      instanceId: MISSING_INSTANCE_ID,
      appId: MISSING_APP_ID,
      scope: "",
      pid: process.pid,
      invoke: {
        poll: true,
        respond: true,
      },
    }, INSTANCE_PASSWORD);

    await deleteDefinitionCompat(setupClient, {
      appId: MISSING_APP_ID,
      scope: "",
    });
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

beforeEach(() => {
  pickDataDirectoryMock.mockResolvedValue(null);
  pickHostExecutablePathMock.mockResolvedValue(null);
});

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
      await screen.findAllByText("Monitor Integration App", {}, { timeout: 15_000 });

      const instancesSection = getInventorySection("App 实例");
      const definitionsSection = getInventorySection("App 定义");
      const primaryInstanceRow = getInventoryRowByActionLabel(
        instancesSection,
        getInstanceActionLabel(PRIMARY_INSTANCE_ID, PRIMARY_APP_ID, null),
      );
      within(primaryInstanceRow).getByText("Monitor Integration App");
      within(primaryInstanceRow).getByText(PRIMARY_APP_ID);
      within(primaryInstanceRow).getByText("scope：Global");
      within(primaryInstanceRow).getByText("用于 Monitor 真实 Host 集成回归。");

      const scopedInstanceRow = getInventoryRowByActionLabel(
        instancesSection,
        getInstanceActionLabel(PRIMARY_SCOPED_INSTANCE_ID, PRIMARY_APP_ID, PRIMARY_SCOPED_SCOPE),
      );
      within(scopedInstanceRow).getByText("Monitor Integration App Scoped");
      within(scopedInstanceRow).getByText(PRIMARY_APP_ID);
      within(scopedInstanceRow).getByText(`scope：${PRIMARY_SCOPED_SCOPE}`);
      within(scopedInstanceRow).getByText("用于 Monitor 多 scope Definition 回归。");

      const missingInstanceRow = getInventoryRowByActionLabel(
        instancesSection,
        getInstanceActionLabel(MISSING_INSTANCE_ID, MISSING_APP_ID, null),
      );
      expect(within(missingInstanceRow).getAllByText(MISSING_APP_ID)).toHaveLength(2);
      within(missingInstanceRow).getByText("scope：Global");
      within(missingInstanceRow).getByText("未提供 App 描述");

      const existingDefinitionRow = getInventoryRowByActionLabel(
        definitionsSection,
        getDefinitionActionLabel("Monitor Integration App", PRIMARY_APP_ID, null),
      );
      within(existingDefinitionRow).getByText("Monitor Integration App");
      within(existingDefinitionRow).getByText(PRIMARY_APP_ID);
      within(existingDefinitionRow).getByText("scope：Global");
      within(existingDefinitionRow).getByText("用于 Monitor 真实 Host 集成回归。");

      const scopedDefinitionRow = getInventoryRowByActionLabel(
        definitionsSection,
        getDefinitionActionLabel("Monitor Integration App Scoped", PRIMARY_APP_ID, PRIMARY_SCOPED_SCOPE),
      );
      within(scopedDefinitionRow).getByText("Monitor Integration App Scoped");
      within(scopedDefinitionRow).getByText(PRIMARY_APP_ID);
      within(scopedDefinitionRow).getByText(`scope：${PRIMARY_SCOPED_SCOPE}`);
      within(scopedDefinitionRow).getByText("用于 Monitor 多 scope Definition 回归。");

      const user = userEvent.setup();

      await user.click(screen.getByRole("button", { name: "新增定义" }));
      await screen.findByRole("heading", { name: "新增 App 定义" }, { timeout: 15_000 });
      await user.type(screen.getByLabelText("App ID"), "monitor.integration.created");
      await user.type(screen.getByLabelText("显示名称"), "Monitor Created App");
      await user.type(screen.getByLabelText("描述"), "通过 Monitor UI 创建的定义。");
      await user.click(screen.getByRole("button", { name: "创建定义" }));

      await screen.findByRole("heading", { name: "主页" }, { timeout: 15_000 });
      await screen.findByText("Monitor Created App", {}, { timeout: 15_000 });
      const createdDefinitionRow = getInventoryRowByActionLabel(
        getInventorySection("App 定义"),
        getDefinitionActionLabel("Monitor Created App", "monitor.integration.created", null),
      );
      within(createdDefinitionRow).getByText("scope：Global");

      const existingDefinitionRowAfterCreate = getInventoryRowByActionLabel(
        getInventorySection("App 定义"),
        getDefinitionActionLabel("Monitor Integration App", PRIMARY_APP_ID, null),
      );
      await user.click(within(existingDefinitionRowAfterCreate).getByRole("button", {
        name: getDefinitionActionLabel("Monitor Integration App", PRIMARY_APP_ID, null),
      }));
      await screen.findByRole("heading", { name: "编辑 App Definition" }, { timeout: 15_000 });
      expect((await findDefinitionInput("scope")).value).toBe("");
      const editDisplayNameInput = await findDefinitionInput("显示名称");
      await user.clear(editDisplayNameInput);
      await user.type(editDisplayNameInput, "Monitor Integration App Updated");
      await user.click(screen.getByRole("button", { name: "保存修改" }));

      await screen.findByRole("heading", { name: "主页" }, { timeout: 15_000 });
      await screen.findAllByText("Monitor Integration App Updated", {}, { timeout: 15_000 });

      const primaryInstanceRowAfterUpdate = getInventoryRowByActionLabel(
        getInventorySection("App 实例"),
        getInstanceActionLabel(PRIMARY_INSTANCE_ID, PRIMARY_APP_ID, null),
      );
      within(primaryInstanceRowAfterUpdate).getByText("Monitor Integration App Updated");
      within(primaryInstanceRowAfterUpdate).getByText(PRIMARY_APP_ID);
      within(primaryInstanceRowAfterUpdate).getByText("scope：Global");
      within(primaryInstanceRowAfterUpdate).getByText("用于 Monitor 真实 Host 集成回归。");

      await user.click(within(primaryInstanceRowAfterUpdate).getByRole("button", {
        name: getInstanceActionLabel(PRIMARY_INSTANCE_ID, PRIMARY_APP_ID, null),
      }));
      await screen.findByRole("heading", { name: "实例关联定义" }, { timeout: 15_000 });
      expect((await findDefinitionInput("scope")).value).toBe("");
      expect((await findDefinitionInput("显示名称")).value).toBe("Monitor Integration App Updated");
      screen.getByText("只读模式不允许保存或删除。");
      await user.click(screen.getByRole("button", { name: "返回主页" }));

      await screen.findByRole("heading", { name: "主页" }, { timeout: 15_000 });

      const scopedInstanceRowAfterUpdate = getInventoryRowByActionLabel(
        getInventorySection("App 实例"),
        getInstanceActionLabel(PRIMARY_SCOPED_INSTANCE_ID, PRIMARY_APP_ID, PRIMARY_SCOPED_SCOPE),
      );
      await user.click(within(scopedInstanceRowAfterUpdate).getByRole("button", {
        name: getInstanceActionLabel(PRIMARY_SCOPED_INSTANCE_ID, PRIMARY_APP_ID, PRIMARY_SCOPED_SCOPE),
      }));
      await screen.findByRole("heading", { name: "实例关联定义" }, { timeout: 15_000 });
      expect((await findDefinitionInput("scope")).value).toBe(PRIMARY_SCOPED_SCOPE);
      expect((await findDefinitionInput("显示名称")).value).toBe("Monitor Integration App Scoped");
      await user.click(screen.getByRole("button", { name: "返回主页" }));

      await screen.findByRole("heading", { name: "主页" }, { timeout: 15_000 });

      const missingInstanceRowAfterUpdate = getInventoryRowByActionLabel(
        getInventorySection("App 实例"),
        getInstanceActionLabel(MISSING_INSTANCE_ID, MISSING_APP_ID, null),
      );
      expect(within(missingInstanceRowAfterUpdate).getAllByText(MISSING_APP_ID)).toHaveLength(2);
      within(missingInstanceRowAfterUpdate).getByText("scope：Global");
      within(missingInstanceRowAfterUpdate).getByText("未提供 App 描述");

      await user.click(within(missingInstanceRowAfterUpdate).getByRole("button", {
        name: getInstanceActionLabel(MISSING_INSTANCE_ID, MISSING_APP_ID, null),
      }));
      await screen.findByRole("heading", { name: "定义不存在" }, { timeout: 15_000 });
      expect(screen.getAllByText(new RegExp(MISSING_INSTANCE_ID)).length).toBeGreaterThan(0);
      expect(screen.getAllByText(new RegExp(MISSING_APP_ID)).length).toBeGreaterThan(0);
      await user.click(screen.getByRole("button", { name: "返回主页" }));

      await screen.findByRole("heading", { name: "主页" }, { timeout: 15_000 });

      await getHost().close();
      host = undefined;

      await waitFor(() => {
        expect(resumeDiscoveryMock).toHaveBeenCalledWith("host_session_terminated");
      }, { timeout: 15_000 });
      await screen.findByText("正在搜索 DevHub Host", {}, { timeout: 15_000 });
    } finally {
      restoreFetch();
    }
  }, 120_000);

  it("confirms before discarding definition edits and returns home after save or delete", async () => {
    const guardAppId = "monitor.integration.guard";
    const guardDisplayName = "Monitor Guard App";
    const guardSavedDisplayName = "Monitor Guard App Saved";

    vi.clearAllMocks();
    if (!host) {
      host = await DevHubHostFixture.start();
    }
    listenMock.mockImplementation(async () => () => {});
    openLogDirectoryMock.mockResolvedValue(undefined);
    requestHostLaunchMock.mockResolvedValue({
      status: "started",
      effectiveDataDir: getHost().dataDirectory,
      dataDirSource: "settings_override",
      pid: null,
    });
    saveSettingsMock.mockResolvedValue(createSettingsSnapshot());
    writeFrontendLogMock.mockResolvedValue(undefined);

    await getHost().writeDefinition({
      appId: guardAppId,
      scope: "",
      displayName: guardDisplayName,
      description: "用于验证未保存离开保护和返回主页路径。",
    });

    const connection = await createConnection(getHost());
    getBootstrapStateMock.mockResolvedValue(createBootstrapSnapshot(connection));
    getSettingsSnapshotMock.mockResolvedValue(createSettingsSnapshot());
    resumeDiscoveryMock.mockResolvedValue(
      createBootstrapSnapshot(connection, {
        generation: 2,
        phase: "scanning",
        connection: null,
      }),
    );
    const restoreFetch = installBrowserStyleRpcFetch(connection.rpcEndpoint, "tauri://monitor-integration");

    try {
      render(<App />);

      await screen.findByRole("heading", { name: "主页" }, { timeout: 15_000 });
      await screen.findByText(guardDisplayName, {}, { timeout: 15_000 });

      const user = userEvent.setup();
      const guardDefinitionRow = getInventoryRowByActionLabel(
        getInventorySection("App 定义"),
        getDefinitionActionLabel(guardDisplayName, guardAppId, null),
      );
      await user.click(within(guardDefinitionRow).getByRole("button", {
        name: getDefinitionActionLabel(guardDisplayName, guardAppId, null),
      }));
      await screen.findByRole("heading", { name: "编辑 App Definition" }, { timeout: 15_000 });
      expect((await findDefinitionInput("scope")).value).toBe("");
      const draftDisplayNameInput = await findDefinitionInput("显示名称");
      await user.clear(draftDisplayNameInput);
      await user.type(draftDisplayNameInput, "Monitor Guard App Draft");

      await user.click(screen.getByRole("button", { name: "返回主页" }));
      await respondToConfirmDialog(user, "cancel", "App Definition 中的修改尚未保存，确认放弃并离开当前工作区吗？");
      await screen.findByRole("heading", { name: "编辑 App Definition" }, { timeout: 15_000 });
      expect((await findDefinitionInput("显示名称")).value).toBe("Monitor Guard App Draft");

      await user.click(screen.getByRole("button", { name: "返回主页" }));
      await respondToConfirmDialog(user, "confirm", "App Definition 中的修改尚未保存，确认放弃并离开当前工作区吗？");
      await screen.findByRole("heading", { name: "主页" }, { timeout: 15_000 });

      const guardDefinitionRowAfterDiscard = getInventoryRowByActionLabel(
        getInventorySection("App 定义"),
        getDefinitionActionLabel(guardDisplayName, guardAppId, null),
      );
      await user.click(within(guardDefinitionRowAfterDiscard).getByRole("button", {
        name: getDefinitionActionLabel(guardDisplayName, guardAppId, null),
      }));
      await screen.findByRole("heading", { name: "编辑 App Definition" }, { timeout: 15_000 });
      const savedDisplayNameInput = await findDefinitionInput("显示名称");
      expect(savedDisplayNameInput.value).toBe(guardDisplayName);

      await user.clear(savedDisplayNameInput);
      await user.type(savedDisplayNameInput, guardSavedDisplayName);
      await user.click(screen.getByRole("button", { name: "保存修改" }));

      await screen.findByRole("heading", { name: "主页" }, { timeout: 15_000 });
      await screen.findByText(guardSavedDisplayName, {}, { timeout: 15_000 });

      const guardDefinitionRowAfterSave = getInventoryRowByActionLabel(
        getInventorySection("App 定义"),
        getDefinitionActionLabel(guardSavedDisplayName, guardAppId, null),
      );
      await user.click(within(guardDefinitionRowAfterSave).getByRole("button", {
        name: getDefinitionActionLabel(guardSavedDisplayName, guardAppId, null),
      }));
      await screen.findByRole("heading", { name: "编辑 App Definition" }, { timeout: 15_000 });
      expect((await findDefinitionInput("scope")).value).toBe("");
      await findDefinitionInput("显示名称");

      await user.click(screen.getByRole("button", { name: "删除定义" }));
      await respondToConfirmDialog(user, "confirm", `确认删除 App Definition “${guardAppId}（scope：Global）” 吗？`);

      await screen.findByRole("heading", { name: "主页" }, { timeout: 15_000 });
      await waitFor(() => {
        expect(screen.queryByText(guardSavedDisplayName)).toBeNull();
      }, { timeout: 15_000 });
    } finally {
      restoreFetch();
    }
  }, 120_000);
});

function getInventorySection(title: "App 实例" | "App 定义"): HTMLElement {
  const trigger = screen.getByRole("button", { name: new RegExp(title) });
  const section = trigger.closest("section");
  if (!section) {
    throw new Error(`Inventory section ${title} not found.`);
  }
  return section;
}

function getInventoryRowByActionLabel(section: HTMLElement, actionLabel: string): HTMLElement {
  const actionButton = within(section).getByRole("button", { name: actionLabel });
  const row = actionButton.closest("article");
  if (!row) {
    throw new Error(`Inventory row for ${actionLabel} not found.`);
  }
  return row;
}

function getHost(): DevHubHostFixture {
  if (!host) {
    throw new Error("Host fixture not started.");
  }

  return host;
}

async function respondToConfirmDialog(
  user: ReturnType<typeof userEvent.setup>,
  action: "confirm" | "cancel",
  message?: string,
) {
  const dialog = await screen.findByRole("alertdialog", { name: "请注意" }, { timeout: 15_000 });
  if (message) {
    within(dialog).getByText(message);
  }

  await user.click(within(dialog).getByRole("button", {
    name: action === "confirm" ? "确定" : "取消",
  }));

  await waitFor(() => {
    expect(screen.queryByRole("alertdialog", { name: "请注意" })).toBeNull();
  }, { timeout: 15_000 });
}

async function findDefinitionInput(label: "App ID" | "scope" | "显示名称" | "描述"): Promise<HTMLInputElement | HTMLTextAreaElement> {
  const field = await screen.findByLabelText(label, {}, { timeout: 15_000 });
  if (!(field instanceof HTMLInputElement) && !(field instanceof HTMLTextAreaElement)) {
    throw new Error(`Field ${label} is not an input control.`);
  }
  return field;
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
      hideHostCommandLineWindow: true,
    },
    hasConfiguredHostExecutable: true,
    connection,
    lastProblem: null,
    ...overrides,
  };
}

function createSettingsSnapshot(): SettingsSnapshot {
  return {
    revision: 0,
    settings: {
      dataDirOverride: getHost().dataDirectory,
      hostExecutablePath: absoluteHostPlaceholder(),
      hideHostCommandLineWindow: true,
    },
    platform: "windows",
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
