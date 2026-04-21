import { act, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import {
  DevHubRpcError,
  DevHubRpcErrorCode,
  type AppDefinition,
  type AppInstance,
  type DefinitionValidationResult,
} from "@devhub/sdk";
import { beforeEach, describe, expect, it, vi } from "vitest";
import App from "./App";
import packageManifest from "../package.json";
import type {
  BootstrapSnapshot,
  FrontendLogInput,
  LogKind,
  MonitorRuntimeConnectionInfo,
  SettingsSnapshot,
} from "./lib/models";

const {
  listenMock,
  createStaticRuntimeResolverMock,
  getBootstrapStateMock,
  getSettingsSnapshotMock,
  openLogDirectoryMock,
  pickDataDirectoryMock,
  pickHostExecutablePathMock,
  requestHostLaunchMock,
  resumeDiscoveryMock,
  saveSettingsMock,
  writeFrontendLogMock,
  hostClientFromRuntimeMock,
  eventsClientFromRuntimeMock,
} = vi.hoisted(() => ({
  listenMock: vi.fn(),
  createStaticRuntimeResolverMock: vi.fn(
    (connection: MonitorRuntimeConnectionInfo) => ({
      resolve: vi.fn().mockResolvedValue(connection),
    }),
  ),
  getBootstrapStateMock: vi.fn<() => Promise<BootstrapSnapshot>>(),
  getSettingsSnapshotMock: vi.fn<() => Promise<SettingsSnapshot>>(),
  openLogDirectoryMock: vi.fn<(kind: LogKind) => Promise<void>>(),
  pickDataDirectoryMock: vi.fn<(currentPath?: string | null) => Promise<string | null>>(),
  pickHostExecutablePathMock: vi.fn<(currentPath?: string | null) => Promise<string | null>>(),
  requestHostLaunchMock: vi.fn(),
  resumeDiscoveryMock: vi.fn(),
  saveSettingsMock: vi.fn(),
  writeFrontendLogMock: vi.fn<(entry: FrontendLogInput) => Promise<void>>(),
  hostClientFromRuntimeMock: vi.fn(),
  eventsClientFromRuntimeMock: vi.fn(),
}));

vi.mock("@tauri-apps/api/event", () => ({
  listen: listenMock,
}));

vi.mock("./lib/monitor-api", () => ({
  BOOTSTRAP_STATE_CHANGED_EVENT: "devhub://bootstrap-state-changed",
  SETTINGS_CHANGED_EVENT: "devhub://settings-changed",
  createStaticRuntimeResolver: createStaticRuntimeResolverMock,
  getBootstrapState: getBootstrapStateMock,
  getSettingsSnapshot: getSettingsSnapshotMock,
  openLogDirectory: openLogDirectoryMock,
  pickDataDirectory: pickDataDirectoryMock,
  pickHostExecutablePath: pickHostExecutablePathMock,
  requestHostLaunch: requestHostLaunchMock,
  resumeDiscovery: resumeDiscoveryMock,
  saveSettings: saveSettingsMock,
  writeFrontendLog: writeFrontendLogMock,
}));

vi.mock("@devhub/sdk", async () => {
  const actual = await vi.importActual<typeof import("@devhub/sdk")>("@devhub/sdk");

  return {
    ...actual,
    DevHubClient: {
      fromRuntime: hostClientFromRuntimeMock,
    },
    DevHubEventsClient: {
      fromRuntime: eventsClientFromRuntimeMock,
    },
  };
});

function createConnection(overrides: Partial<MonitorRuntimeConnectionInfo> = {}): MonitorRuntimeConnectionInfo {
  const runtimeOverrides = overrides.runtime ?? {};

  return {
    runtimeDirectory: overrides.runtimeDirectory ?? "/tmp/devhub/runtime",
    token: overrides.token ?? "test-token",
    rpcEndpoint: overrides.rpcEndpoint ?? "http://127.0.0.1:4123/rpc",
    websocketEndpoint: overrides.websocketEndpoint ?? "ws://127.0.0.1:4123/ws",
    runtime: {
      protocolVersion: 1,
      pid: 4321,
      httpBaseUrl: "http://127.0.0.1:4123",
      wsUrl: "ws://127.0.0.1:4123/ws",
      tokenFile: "/tmp/devhub/runtime/token.txt",
      startedAtUtc: "2026-04-12T02:03:04Z",
      runtimeTuning: {
        leaseSeconds: 30,
        onlineThresholdSeconds: 15,
        launchDedupeWindowSeconds: 5,
      },
      hubVersion: "0.7.0",
      ...runtimeOverrides,
    },
  };
}

function createBootstrapSnapshot(
  overrides: Partial<BootstrapSnapshot> = {},
): BootstrapSnapshot {
  const connection = overrides.connection === undefined ? createConnection() : overrides.connection;

  return {
    generation: 1,
    phase: "host_available",
    effectiveDataDir: "/tmp/devhub",
    dataDirSource: "settings_override",
    settings: {
      dataDirOverride: "/tmp/devhub",
      hostExecutablePath: "/tmp/DevHub.Host",
      hideHostCommandLineWindow: true,
    },
    hasConfiguredHostExecutable: true,
    connection,
    lastProblem: null,
    ...overrides,
  };
}

function createSettingsSnapshot(overrides: Partial<SettingsSnapshot> = {}): SettingsSnapshot {
  return {
    settings: {
      dataDirOverride: "/tmp/devhub",
      hostExecutablePath: "/tmp/DevHub.Host",
      hideHostCommandLineWindow: true,
    },
    platform: "windows",
    effectiveDataDir: "/tmp/devhub",
    dataDirSource: "settings_override",
    settingsFilePath: "/tmp/settings.json",
    monitorLogDirectory: "/tmp/monitor/logs",
    ...overrides,
  };
}

function createDefinition(overrides: Partial<AppDefinition> = {}): AppDefinition {
  return {
    appId: "demo.app",
    scope: null,
    displayName: "Demo App",
    description: "Demo description",
    capabilities: {
      rpc: true,
      events: true,
    },
    ...overrides,
  };
}

function createInstance(overrides: Partial<AppInstance> = {}): AppInstance {
  return {
    instanceId: "instance-1",
    appId: "demo.app",
    scope: null,
    pid: 1001,
    registeredAtUtc: new Date("2026-04-12T02:03:04Z"),
    lastSeenUtc: new Date(),
    invoke: {
      poll: true,
      respond: true,
    },
    ...overrides,
  };
}

function formatScopeLabel(scope?: string | null): string {
  return `scope：${scope ?? "Global"}`;
}

function getDefinitionActionLabel(definition: Pick<AppDefinition, "appId" | "scope" | "displayName">): string {
  return `编辑定义：${definition.displayName}（${definition.appId}，${formatScopeLabel(definition.scope)}）`;
}

function getInstanceActionLabel(instance: Pick<AppInstance, "instanceId" | "appId" | "scope">): string {
  return `查看定义：${instance.instanceId}（${instance.appId}，${formatScopeLabel(instance.scope)}）`;
}

function createPendingEventStream(): AsyncIterable<unknown> {
  return {
    [Symbol.asyncIterator]() {
      return {
        next: () =>
          new Promise<IteratorResult<unknown>>(() => {
            // 保持连接不断开，供测试检查当前会话状态。
          }),
      };
    },
  };
}

function createFailingEventStream(error: Error): AsyncIterable<unknown> {
  return {
    async *[Symbol.asyncIterator]() {
      await Promise.resolve();
      throw error;
    },
  };
}

async function respondToConfirmDialog(
  user: ReturnType<typeof userEvent.setup>,
  action: "confirm" | "cancel",
  message?: string,
) {
  const dialog = await screen.findByRole("alertdialog", { name: "请注意" });
  if (message) {
    within(dialog).getByText(message);
  }

  await user.click(within(dialog).getByRole("button", {
    name: action === "confirm" ? "确定" : "取消",
  }));

  await waitFor(() => {
    expect(screen.queryByRole("alertdialog", { name: "请注意" })).toBeNull();
  });
}

beforeEach(() => {
  vi.clearAllMocks();

  listenMock.mockImplementation(async () => () => {});
  getBootstrapStateMock.mockResolvedValue(createBootstrapSnapshot());
  getSettingsSnapshotMock.mockResolvedValue(createSettingsSnapshot());
  openLogDirectoryMock.mockResolvedValue(undefined);
  pickDataDirectoryMock.mockResolvedValue(null);
  pickHostExecutablePathMock.mockResolvedValue(null);
  requestHostLaunchMock.mockResolvedValue({
    status: "started",
    effectiveDataDir: "/tmp/devhub",
    dataDirSource: "settings_override",
    pid: 4321,
  });
  resumeDiscoveryMock.mockResolvedValue(
    createBootstrapSnapshot({
      generation: 2,
      phase: "scanning",
      connection: null,
    }),
  );
  saveSettingsMock.mockResolvedValue(createSettingsSnapshot());
  writeFrontendLogMock.mockResolvedValue(undefined);
});

describe("Monitor App", () => {
  it("keeps home mounted while discovery promotes into status and sidebar navigation stays available", async () => {
    let bootstrapListener:
      | ((event: { payload: BootstrapSnapshot }) => void)
      | undefined;
    listenMock.mockImplementation(async (eventName, callback) => {
      if (eventName === "devhub://bootstrap-state-changed") {
        bootstrapListener = callback as (event: { payload: BootstrapSnapshot }) => void;
      }

      return () => {};
    });

    getBootstrapStateMock.mockResolvedValue(
      createBootstrapSnapshot({
        phase: "scanning",
        connection: null,
      }),
    );

    const definition = createDefinition();
    const instance = createInstance();
    const hostClient = {
      listDefinitions: vi.fn().mockResolvedValue([definition]),
      listInstances: vi.fn().mockResolvedValue([instance]),
      getDefinition: vi.fn(),
      validateDefinition: vi.fn(),
      upsertDefinition: vi.fn(),
      deleteDefinition: vi.fn(),
      dispose: vi.fn().mockResolvedValue(undefined),
    };
    const eventsClient = {
      authenticate: vi.fn().mockResolvedValue(undefined),
      subscribe: vi.fn().mockResolvedValue("sub-1"),
      unsubscribe: vi.fn().mockResolvedValue(undefined),
      readEvents: vi.fn().mockReturnValue(createPendingEventStream()),
      dispose: vi.fn().mockResolvedValue(undefined),
    };

    hostClientFromRuntimeMock.mockResolvedValue(hostClient);
    eventsClientFromRuntimeMock.mockResolvedValue(eventsClient);

    render(<App />);

    await screen.findByRole("heading", { name: "主页" });
    screen.getByRole("button", { name: "主页" });
    screen.getByRole("button", { name: "帮助" });
    screen.getByRole("button", { name: "设置" });
    expect(screen.getByRole("button", { name: "设置" }).querySelector("svg")?.classList.contains("settings-icon")).toBe(true);
    screen.getByText("正在搜索 DevHub Host");
    expect(screen.queryByRole("button", { name: "启动 Host" })).toBeNull();
    expect(screen.queryByRole("button", { name: "重新扫描" })).toBeNull();

    await act(async () => {
      bootstrapListener?.({
        payload: createBootstrapSnapshot(),
      });
      await Promise.resolve();
    });

    await screen.findAllByText("Demo App");

    const instancesSection = getInventorySection("App 实例");
    const instanceRow = getInventoryRowByActionLabel(
      instancesSection,
      getInstanceActionLabel(instance),
    );
    within(instanceRow).getByText("Demo App");
    within(instanceRow).getByText("demo.app");
    within(instanceRow).getByText("scope：Global");
    within(instanceRow).getByText("Demo description");

    const definitionsSection = getInventorySection("App 定义");
    const definitionRow = getInventoryRowByActionLabel(
      definitionsSection,
      getDefinitionActionLabel(definition),
    );
    within(definitionRow).getByText("Demo App");
    within(definitionRow).getByText("demo.app");
    within(definitionRow).getByText("scope：Global");
    within(definitionRow).getByText("Demo description");
    expect(within(definitionsSection).getByRole("button", { name: "新增定义" }).className)
      .toContain("icon-button-prominent");

    const user = userEvent.setup();
    await user.click(screen.getByRole("button", { name: "帮助" }));
    await screen.findByRole("heading", { name: "帮助" });
    screen.getByRole("button", { name: "打开 Host 日志" });
    screen.getByText(`Monitor v${packageManifest.version}`);

    await user.click(screen.getByRole("button", { name: "设置" }));
    await screen.findByRole("heading", { name: "设置" });

    await user.click(screen.getByRole("button", { name: "主页" }));
    await screen.findByRole("heading", { name: "主页" });
    expect(screen.getAllByText("Demo App").length).toBeGreaterThan(0);

    expect(hostClientFromRuntimeMock).toHaveBeenCalledTimes(1);
    expect(eventsClientFromRuntimeMock).toHaveBeenCalledTimes(1);
    expect(eventsClient.authenticate).toHaveBeenCalledTimes(1);
    expect(eventsClient.subscribe).toHaveBeenCalledWith([
      "app.definition.upserted",
      "app.definition.deleted",
      "app.instance.registered",
      "app.instance.unregistered",
    ]);

    await waitFor(() => {
      expect(hostClient.listDefinitions).toHaveBeenCalledTimes(1);
      expect(hostClient.listInstances).toHaveBeenCalledWith({
        includeAllScopes: true,
        includeOffline: true,
      });
    });
  });

  it("renders shared inventory metadata rows with tooltip titles and missing-definition fallback", async () => {
    const longDisplayName = "Monitor Inventory Display Name With Long Overflow";
    const longAppId = "monitor.inventory.long.app.identifier";
    const longDescription = "用于验证库存条目长文本悬停全文展示和单行布局。";
    const orphanAppId = "monitor.inventory.orphan";
    const orphanInstanceId = "orphan-instance";
    const hostClient = {
      listDefinitions: vi.fn().mockResolvedValue([
        createDefinition({
          appId: longAppId,
          displayName: longDisplayName,
          description: longDescription,
        }),
      ]),
      listInstances: vi.fn().mockResolvedValue([
        createInstance({
          instanceId: "long-instance",
          appId: longAppId,
        }),
        createInstance({
          instanceId: orphanInstanceId,
          appId: orphanAppId,
        }),
      ]),
      getDefinition: vi.fn(),
      validateDefinition: vi.fn(),
      upsertDefinition: vi.fn(),
      deleteDefinition: vi.fn(),
      dispose: vi.fn().mockResolvedValue(undefined),
    };
    const eventsClient = {
      authenticate: vi.fn().mockResolvedValue(undefined),
      subscribe: vi.fn().mockResolvedValue("sub-1"),
      unsubscribe: vi.fn().mockResolvedValue(undefined),
      readEvents: vi.fn().mockReturnValue(createPendingEventStream()),
      dispose: vi.fn().mockResolvedValue(undefined),
    };

    hostClientFromRuntimeMock.mockResolvedValue(hostClient);
    eventsClientFromRuntimeMock.mockResolvedValue(eventsClient);

    render(<App />);

    await screen.findAllByText(longDisplayName);

    const instancesSection = getInventorySection("App 实例");
    const mappedInstanceRow = getInventoryRowByActionLabel(
      instancesSection,
      getInstanceActionLabel(createInstance({
        instanceId: "long-instance",
        appId: longAppId,
      })),
    );
    expect(within(mappedInstanceRow).getByText(longDisplayName).getAttribute("title")).toBe(longDisplayName);
    expect(within(mappedInstanceRow).getByText(longAppId).getAttribute("title")).toBe(longAppId);
    expect(within(mappedInstanceRow).getByText(longDescription).getAttribute("title")).toBe(longDescription);
    within(mappedInstanceRow).getByText("scope：Global");

    const orphanInstanceRow = getInventoryRowByActionLabel(
      instancesSection,
      getInstanceActionLabel(createInstance({
        instanceId: orphanInstanceId,
        appId: orphanAppId,
      })),
    );
    expect(within(orphanInstanceRow).getAllByText(orphanAppId)).toHaveLength(2);
    within(orphanInstanceRow).getByText("scope：Global");
    expect(within(orphanInstanceRow).getByText("未提供 App 描述").getAttribute("title"))
      .toBe("未提供 App 描述");

    const definitionsSection = getInventorySection("App 定义");
    const definitionRow = getInventoryRowByActionLabel(
      definitionsSection,
      getDefinitionActionLabel(createDefinition({
        appId: longAppId,
        displayName: longDisplayName,
      })),
    );
    expect(within(definitionRow).getByText(longDisplayName).getAttribute("title")).toBe(longDisplayName);
    expect(within(definitionRow).getByText(longAppId).getAttribute("title")).toBe(longAppId);
    within(definitionRow).getByText("scope：Global");
    expect(within(definitionRow).getByText(longDescription).getAttribute("title")).toBe(longDescription);
  });

  it("keys definition edit, view, and delete flows by appId plus scope", async () => {
    const globalDefinition = createDefinition({
      displayName: "Demo App Global",
      description: "Global definition",
    });
    const scopedDefinition = createDefinition({
      scope: "workspace-a",
      displayName: "Demo App Scoped",
      description: "Scoped definition",
    });
    const globalInstance = createInstance();
    const scopedInstance = createInstance({
      instanceId: "instance-workspace-a",
      scope: "workspace-a",
    });
    const hostClient = {
      listDefinitions: vi.fn().mockResolvedValue([globalDefinition, scopedDefinition]),
      listInstances: vi.fn().mockResolvedValue([globalInstance, scopedInstance]),
      getDefinition: vi.fn().mockImplementation(async (identity: { appId: string; scope: string | null }) => {
        return identity.scope === "workspace-a" ? scopedDefinition : globalDefinition;
      }),
      validateDefinition: vi.fn(),
      upsertDefinition: vi.fn(),
      deleteDefinition: vi.fn().mockResolvedValue(undefined),
      dispose: vi.fn().mockResolvedValue(undefined),
    };
    const eventsClient = {
      authenticate: vi.fn().mockResolvedValue(undefined),
      subscribe: vi.fn().mockResolvedValue("sub-1"),
      unsubscribe: vi.fn().mockResolvedValue(undefined),
      readEvents: vi.fn().mockReturnValue(createPendingEventStream()),
      dispose: vi.fn().mockResolvedValue(undefined),
    };

    hostClientFromRuntimeMock.mockResolvedValue(hostClient);
    eventsClientFromRuntimeMock.mockResolvedValue(eventsClient);

    render(<App />);

    await screen.findAllByText("Demo App Global");
    await screen.findAllByText("Demo App Scoped");

    const instancesSection = getInventorySection("App 实例");
    const scopedInstanceRow = getInventoryRowByActionLabel(
      instancesSection,
      getInstanceActionLabel(scopedInstance),
    );
    within(scopedInstanceRow).getByText("scope：workspace-a");

    const user = userEvent.setup();
    await user.click(within(scopedInstanceRow).getByRole("button", {
      name: getInstanceActionLabel(scopedInstance),
    }));

    await screen.findByRole("heading", { name: "实例关联定义" });
    expect(hostClient.getDefinition).toHaveBeenCalledWith({
      appId: "demo.app",
      scope: "workspace-a",
    });
    expect((screen.getByLabelText("scope") as HTMLInputElement).value).toBe("workspace-a");

    await user.click(screen.getByRole("button", { name: "返回主页" }));
    await screen.findByRole("heading", { name: "主页" });

    const definitionsSection = getInventorySection("App 定义");
    const globalDefinitionRow = getInventoryRowByActionLabel(
      definitionsSection,
      getDefinitionActionLabel(globalDefinition),
    );
    within(globalDefinitionRow).getByText("scope：Global");

    const scopedDefinitionRow = getInventoryRowByActionLabel(
      definitionsSection,
      getDefinitionActionLabel(scopedDefinition),
    );
    within(scopedDefinitionRow).getByText("scope：workspace-a");

    await user.click(within(scopedDefinitionRow).getByRole("button", {
      name: getDefinitionActionLabel(scopedDefinition),
    }));

    await screen.findByRole("heading", { name: "编辑 App Definition" });
    expect(hostClient.getDefinition).toHaveBeenLastCalledWith({
      appId: "demo.app",
      scope: "workspace-a",
    });
    expect((screen.getByLabelText("scope") as HTMLInputElement).value).toBe("workspace-a");

    await user.click(screen.getByRole("button", { name: "删除定义" }));
    await respondToConfirmDialog(user, "confirm", "确认删除 App Definition “demo.app（scope：workspace-a）” 吗？");

    await screen.findByRole("heading", { name: "主页" });
    expect(hostClient.deleteDefinition).toHaveBeenCalledWith({
      appId: "demo.app",
      scope: "workspace-a",
    });
    expect(screen.queryByText("Demo App Scoped")).toBeNull();
    expect(screen.getAllByText("Demo App Global").length).toBeGreaterThan(0);
  });

  it("keeps Global and literal global definition identities distinct in the inventory", async () => {
    const globalDefinition = createDefinition({
      displayName: "Demo App Global",
      description: "Global definition",
    });
    const literalGlobalDefinition = createDefinition({
      scope: "global",
      displayName: "Demo App Literal Global",
      description: "Literal global definition",
    });
    const hostClient = {
      listDefinitions: vi.fn().mockResolvedValue([globalDefinition, literalGlobalDefinition]),
      listInstances: vi.fn().mockResolvedValue([]),
      getDefinition: vi.fn().mockImplementation(async (identity: { appId: string; scope: string | null }) => {
        return identity.scope === "global" ? literalGlobalDefinition : globalDefinition;
      }),
      validateDefinition: vi.fn(),
      upsertDefinition: vi.fn(),
      deleteDefinition: vi.fn(),
      dispose: vi.fn().mockResolvedValue(undefined),
    };
    const eventsClient = {
      authenticate: vi.fn().mockResolvedValue(undefined),
      subscribe: vi.fn().mockResolvedValue("sub-1"),
      unsubscribe: vi.fn().mockResolvedValue(undefined),
      readEvents: vi.fn().mockReturnValue(createPendingEventStream()),
      dispose: vi.fn().mockResolvedValue(undefined),
    };

    hostClientFromRuntimeMock.mockResolvedValue(hostClient);
    eventsClientFromRuntimeMock.mockResolvedValue(eventsClient);

    render(<App />);

    await screen.findAllByText("Demo App Global");
    await screen.findAllByText("Demo App Literal Global");

    const definitionsSection = getInventorySection("App 定义");
    const globalDefinitionRow = getInventoryRowByActionLabel(
      definitionsSection,
      getDefinitionActionLabel(globalDefinition),
    );
    within(globalDefinitionRow).getByText("scope：Global");

    const literalGlobalDefinitionRow = getInventoryRowByActionLabel(
      definitionsSection,
      getDefinitionActionLabel(literalGlobalDefinition),
    );
    within(literalGlobalDefinitionRow).getByText("scope：global");

    const user = userEvent.setup();
    await user.click(within(literalGlobalDefinitionRow).getByRole("button", {
      name: getDefinitionActionLabel(literalGlobalDefinition),
    }));

    await screen.findByRole("heading", { name: "编辑 App Definition" });
    expect(hostClient.getDefinition).toHaveBeenLastCalledWith({
      appId: "demo.app",
      scope: "global",
    });
    expect((screen.getByLabelText("scope") as HTMLInputElement).value).toBe("global");
  });

  it("rejects unsupported hosts before opening the connected inventory workflow", async () => {
    getBootstrapStateMock.mockResolvedValue(
      createBootstrapSnapshot({
        connection: createConnection({
          runtime: {
            ...createConnection().runtime,
            hubVersion: "0.6.9",
          },
        }),
      }),
    );

    render(<App />);

    await screen.findByRole("heading", { name: "主页" });
    await screen.findByText("当前 Host 版本不受支持");
    await screen.findByText(/hubVersion=0\.6\.9/);

    expect(hostClientFromRuntimeMock).not.toHaveBeenCalled();
    expect(eventsClientFromRuntimeMock).not.toHaveBeenCalled();
    expect(screen.queryByText("App 定义")).toBeNull();
    expect(screen.queryByText("App 实例")).toBeNull();
  });

  it("returns home to discovery when the host event stream terminates", async () => {
    const hostClient = {
      listDefinitions: vi.fn().mockResolvedValue([]),
      listInstances: vi.fn().mockResolvedValue([]),
      getDefinition: vi.fn(),
      validateDefinition: vi.fn(),
      upsertDefinition: vi.fn(),
      deleteDefinition: vi.fn(),
      dispose: vi.fn().mockResolvedValue(undefined),
    };
    const eventsClient = {
      authenticate: vi.fn().mockResolvedValue(undefined),
      subscribe: vi.fn().mockResolvedValue("sub-1"),
      unsubscribe: vi.fn().mockResolvedValue(undefined),
      readEvents: vi.fn().mockReturnValue(createFailingEventStream(new Error("socket closed"))),
      dispose: vi.fn().mockResolvedValue(undefined),
    };

    hostClientFromRuntimeMock.mockResolvedValue(hostClient);
    eventsClientFromRuntimeMock.mockResolvedValue(eventsClient);

    render(<App />);

    await waitFor(() => {
      expect(resumeDiscoveryMock).toHaveBeenCalledWith("host_session_terminated");
    });

    await screen.findByRole("heading", { name: "主页" });
    screen.getByText("正在搜索 DevHub Host");
    expect(screen.queryByRole("button", { name: "重新扫描" })).toBeNull();
  });

  it("keeps the searching copy in launch-available mode and only then shows the launch action", async () => {
    getBootstrapStateMock.mockResolvedValue(
      createBootstrapSnapshot({
        phase: "launch_available",
        connection: null,
      }),
    );

    render(<App />);

    await screen.findByRole("heading", { name: "主页" });
    await screen.findByRole("button", { name: "启动 Host" });
    screen.getByText("正在搜索 DevHub Host");
    expect(screen.queryByRole("button", { name: "重新扫描" })).toBeNull();
    expect(screen.queryByText("尚未连接到 DevHub Host")).toBeNull();
  });

  it("opens log directories from help and keeps the help workspace visible on failure", async () => {
    getBootstrapStateMock.mockResolvedValue(
      createBootstrapSnapshot({
        phase: "scanning",
        connection: null,
      }),
    );

    render(<App />);

    await screen.findByRole("heading", { name: "主页" });

    const user = userEvent.setup();
    await user.click(screen.getByRole("button", { name: "帮助" }));
    await screen.findByRole("heading", { name: "帮助" });
    screen.getByText(`Monitor v${packageManifest.version}`);

    await user.click(screen.getByRole("button", { name: "打开 Host 日志" }));
    await waitFor(() => {
      expect(openLogDirectoryMock).toHaveBeenCalledWith("host");
    });
    screen.getByRole("heading", { name: "帮助" });

    openLogDirectoryMock.mockRejectedValueOnce(new Error("无法打开日志目录"));

    await user.click(screen.getByRole("button", { name: "打开 Monitor 日志" }));

    await screen.findByText("无法打开日志目录");
    screen.getByRole("heading", { name: "帮助" });
  });

  it("returns to home after saving settings from the dedicated workspace", async () => {
    getBootstrapStateMock.mockResolvedValue(
      createBootstrapSnapshot({
        phase: "scanning",
        connection: null,
      }),
    );

    render(<App />);

    await screen.findByRole("heading", { name: "主页" });

    const user = userEvent.setup();
    await user.click(screen.getByRole("button", { name: "设置" }));
    await screen.findByRole("heading", { name: "设置" });

    await user.clear(screen.getByLabelText("Host 可执行文件路径"));
    await user.type(screen.getByLabelText("Host 可执行文件路径"), "/tmp/alt-host");
    await user.click(screen.getByLabelText("隐藏 Host 命令行窗口"));
    await user.click(screen.getByRole("button", { name: "保存设置" }));

    await waitFor(() => {
      expect(saveSettingsMock).toHaveBeenCalledWith({
        dataDirOverride: "/tmp/devhub",
        hostExecutablePath: "/tmp/alt-host",
        hideHostCommandLineWindow: false,
      });
    });

    await screen.findByRole("heading", { name: "主页" });
  });

  it("keeps the host command line window option checked by default on windows", async () => {
    getBootstrapStateMock.mockResolvedValue(
      createBootstrapSnapshot({
        phase: "scanning",
        connection: null,
      }),
    );

    render(<App />);

    await screen.findByRole("heading", { name: "主页" });

    const user = userEvent.setup();
    await user.click(screen.getByRole("button", { name: "设置" }));
    await screen.findByRole("heading", { name: "设置" });

    expect((screen.getByLabelText("隐藏 Host 命令行窗口") as HTMLInputElement).checked).toBe(true);
  });

  it("hides the host command line window option on non-windows platforms", async () => {
    getBootstrapStateMock.mockResolvedValue(
      createBootstrapSnapshot({
        phase: "scanning",
        connection: null,
      }),
    );
    getSettingsSnapshotMock.mockResolvedValue(createSettingsSnapshot({
      platform: "macos",
    }));

    render(<App />);

    await screen.findByRole("heading", { name: "主页" });

    const user = userEvent.setup();
    await user.click(screen.getByRole("button", { name: "设置" }));
    await screen.findByRole("heading", { name: "设置" });

    expect(screen.queryByLabelText("隐藏 Host 命令行窗口")).toBeNull();
  });

  it("fills settings fields from the native file and directory pickers", async () => {
    getBootstrapStateMock.mockResolvedValue(
      createBootstrapSnapshot({
        phase: "scanning",
        connection: null,
      }),
    );
    pickHostExecutablePathMock.mockResolvedValue("/tmp/picked-host");
    pickDataDirectoryMock.mockResolvedValue("/tmp/picked-data");

    render(<App />);

    await screen.findByRole("heading", { name: "主页" });

    const user = userEvent.setup();
    await user.click(screen.getByRole("button", { name: "设置" }));
    await screen.findByRole("heading", { name: "设置" });

    await user.click(screen.getByRole("button", { name: "选择 Host 可执行文件" }));
    await user.click(screen.getByRole("button", { name: "选择 Host 数据目录" }));

    expect(pickHostExecutablePathMock).toHaveBeenCalledWith("/tmp/DevHub.Host");
    expect(pickDataDirectoryMock).toHaveBeenCalledWith("/tmp/devhub");
    expect((screen.getByLabelText("Host 可执行文件路径") as HTMLInputElement).value).toBe("/tmp/picked-host");
    expect((screen.getByLabelText("Host 数据目录") as HTMLInputElement).value).toBe("/tmp/picked-data");
  });

  it("prompts before leaving settings with unsaved changes and discards the draft after confirmation", async () => {
    getBootstrapStateMock.mockResolvedValue(
      createBootstrapSnapshot({
        phase: "scanning",
        connection: null,
      }),
    );

    render(<App />);

    await screen.findByRole("heading", { name: "主页" });

    const user = userEvent.setup();
    await user.click(screen.getByRole("button", { name: "设置" }));
    await screen.findByRole("heading", { name: "设置" });

    const hostPathInput = screen.getByLabelText("Host 可执行文件路径");
    await user.clear(hostPathInput);
    await user.type(hostPathInput, "/tmp/alt-host");

    await user.click(screen.getByRole("button", { name: "帮助" }));
    await respondToConfirmDialog(user, "cancel", "设置中的修改尚未保存，确认放弃并离开当前工作区吗？");
    await screen.findByRole("heading", { name: "设置" });
    expect((screen.getByLabelText("Host 可执行文件路径") as HTMLInputElement).value).toBe("/tmp/alt-host");

    await user.click(screen.getByRole("button", { name: "帮助" }));
    await respondToConfirmDialog(user, "confirm", "设置中的修改尚未保存，确认放弃并离开当前工作区吗？");
    await screen.findByRole("heading", { name: "帮助" });

    await user.click(screen.getByRole("button", { name: "设置" }));
    await screen.findByRole("heading", { name: "设置" });
    expect((screen.getByLabelText("Host 可执行文件路径") as HTMLInputElement).value).toBe("/tmp/DevHub.Host");
  });

  it("keeps the home workspace visible without empty inventories while the host session is still connecting", async () => {
    const pendingConnection = new Promise<never>(() => {});
    hostClientFromRuntimeMock.mockImplementation(() => pendingConnection);
    eventsClientFromRuntimeMock.mockImplementation(() => pendingConnection);

    render(<App />);

    await screen.findByRole("heading", { name: "主页" });
    await screen.findByText("http://127.0.0.1:4123/rpc");

    expect(screen.queryByText("App 定义")).toBeNull();
    expect(screen.queryByText("App 实例")).toBeNull();
    expect(screen.queryByText("当前没有 App 定义")).toBeNull();
    expect(screen.queryByText("当前没有 App 实例")).toBeNull();
    expect(screen.queryByRole("button", { name: "新增定义" })).toBeNull();
  });

  it("adds the collapsed class to sidebar buttons after collapsing the sidebar", async () => {
    getBootstrapStateMock.mockResolvedValue(
      createBootstrapSnapshot({
        phase: "scanning",
        connection: null,
      }),
    );

    render(<App />);

    await screen.findByRole("heading", { name: "主页" });

    const user = userEvent.setup();
    await user.click(screen.getByRole("button", { name: "折叠侧边栏" }));

    expect(screen.getByRole("button", { name: "主页" }).className).toContain("collapsed");
    expect(screen.getByRole("button", { name: "帮助" }).className).toContain("collapsed");
    expect(screen.getByRole("button", { name: "设置" }).className).toContain("collapsed");
    screen.getByRole("button", { name: "展开侧边栏" });
  });

  it("shows inline validation and stays in the definition workspace when precheck fails", async () => {
    const invalidValidation: DefinitionValidationResult = {
      ok: true,
      valid: false,
      errors: [
        {
          path: "definition.scope",
          code: "format",
          message: "scope 格式不合法。",
        },
      ],
    };
    const hostClient = {
      listDefinitions: vi.fn().mockResolvedValue([]),
      listInstances: vi.fn().mockResolvedValue([]),
      getDefinition: vi.fn(),
      validateDefinition: vi.fn().mockResolvedValue(invalidValidation),
      upsertDefinition: vi.fn(),
      deleteDefinition: vi.fn(),
      dispose: vi.fn().mockResolvedValue(undefined),
    };
    const eventsClient = {
      authenticate: vi.fn().mockResolvedValue(undefined),
      subscribe: vi.fn().mockResolvedValue("sub-1"),
      unsubscribe: vi.fn().mockResolvedValue(undefined),
      readEvents: vi.fn().mockReturnValue(createPendingEventStream()),
      dispose: vi.fn().mockResolvedValue(undefined),
    };

    hostClientFromRuntimeMock.mockResolvedValue(hostClient);
    eventsClientFromRuntimeMock.mockResolvedValue(eventsClient);

    render(<App />);

    await screen.findByRole("button", { name: "新增定义" });

    const user = userEvent.setup();
    await user.click(screen.getByRole("button", { name: "新增定义" }));
    await screen.findByRole("heading", { name: "新增 App 定义" });
    await user.type(screen.getByLabelText("App ID"), "demo.app");
    await user.type(screen.getByLabelText("scope"), "invalid scope");
    await user.type(screen.getByLabelText("显示名称"), "Demo App");
    await user.click(screen.getByRole("button", { name: "创建定义" }));

    await screen.findByText("预校验未通过，请修正下列字段错误后再提交。");
    screen.getByText("scope 格式不合法。");
    screen.getByRole("heading", { name: "新增 App 定义" });
    expect((screen.getByLabelText("scope") as HTMLInputElement).value).toBe("invalid scope");

    expect(hostClient.validateDefinition).toHaveBeenCalledTimes(1);
    expect(hostClient.upsertDefinition).not.toHaveBeenCalled();
  });

  it("keeps delete failures inside the definition workspace", async () => {
    const definition = createDefinition();
    const hostClient = {
      listDefinitions: vi.fn().mockResolvedValue([definition]),
      listInstances: vi.fn().mockResolvedValue([]),
      getDefinition: vi.fn().mockResolvedValue(definition),
      validateDefinition: vi.fn(),
      upsertDefinition: vi.fn(),
      deleteDefinition: vi.fn().mockRejectedValue(new Error("delete failed")),
      dispose: vi.fn().mockResolvedValue(undefined),
    };
    const eventsClient = {
      authenticate: vi.fn().mockResolvedValue(undefined),
      subscribe: vi.fn().mockResolvedValue("sub-1"),
      unsubscribe: vi.fn().mockResolvedValue(undefined),
      readEvents: vi.fn().mockReturnValue(createPendingEventStream()),
      dispose: vi.fn().mockResolvedValue(undefined),
    };

    hostClientFromRuntimeMock.mockResolvedValue(hostClient);
    eventsClientFromRuntimeMock.mockResolvedValue(eventsClient);

    render(<App />);

    await screen.findByText("Demo App");

    const user = userEvent.setup();
    await user.click(screen.getByRole("button", { name: getDefinitionActionLabel(definition) }));
    await screen.findByRole("heading", { name: "编辑 App Definition" });
    await screen.findByLabelText("显示名称");
    await user.click(screen.getByRole("button", { name: "删除定义" }));
    await respondToConfirmDialog(user, "confirm", "确认删除 App Definition “demo.app（scope：Global）” 吗？");

    await screen.findAllByText("delete failed");
    screen.getByRole("heading", { name: "编辑 App Definition" });
    expect(resumeDiscoveryMock).not.toHaveBeenCalled();
  });

  it("keeps missing definitions in read-only mode when an instance link is stale", async () => {
    const hostClient = {
      listDefinitions: vi.fn().mockResolvedValue([]),
      listInstances: vi.fn().mockResolvedValue([createInstance()]),
      getDefinition: vi
        .fn()
        .mockRejectedValue(
          new DevHubRpcError({
            code: DevHubRpcErrorCode.AppDefinitionNotFound,
            message: "definition missing",
            requestId: "get-definition-1",
          }),
        ),
      validateDefinition: vi.fn(),
      upsertDefinition: vi.fn(),
      deleteDefinition: vi.fn(),
      dispose: vi.fn().mockResolvedValue(undefined),
    };
    const eventsClient = {
      authenticate: vi.fn().mockResolvedValue(undefined),
      subscribe: vi.fn().mockResolvedValue("sub-1"),
      unsubscribe: vi.fn().mockResolvedValue(undefined),
      readEvents: vi.fn().mockReturnValue(createPendingEventStream()),
      dispose: vi.fn().mockResolvedValue(undefined),
    };

    hostClientFromRuntimeMock.mockResolvedValue(hostClient);
    eventsClientFromRuntimeMock.mockResolvedValue(eventsClient);

    render(<App />);

    const instance = createInstance();
    await screen.findByRole("button", { name: getInstanceActionLabel(instance) });

    const instancesSection = getInventorySection("App 实例");
    const missingInstanceRow = getInventoryRowByActionLabel(
      instancesSection,
      getInstanceActionLabel(instance),
    );
    expect(within(missingInstanceRow).getAllByText("demo.app")).toHaveLength(2);
    within(missingInstanceRow).getByText("scope：Global");
    within(missingInstanceRow).getByText("未提供 App 描述");

    const user = userEvent.setup();
    await user.click(within(missingInstanceRow).getByRole("button", {
      name: getInstanceActionLabel(instance),
    }));

    await screen.findByRole("heading", { name: "定义不存在" });
    screen.getByText("只读模式不允许保存或删除。");
  });
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
