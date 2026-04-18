import { act, render, screen, waitFor } from "@testing-library/react";
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

function createConnection(): MonitorRuntimeConnectionInfo {
  return {
    runtimeDirectory: "/tmp/devhub/runtime",
    token: "test-token",
    rpcEndpoint: "http://127.0.0.1:4123/rpc",
    websocketEndpoint: "ws://127.0.0.1:4123/ws",
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
      hubVersion: "0.6.0",
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
      dataDirOverride: "/tmp/devhub",
      hostExecutablePath: "/tmp/DevHub.Host",
    },
    effectiveDataDir: "/tmp/devhub",
    dataDirSource: "settings_override",
    settingsFilePath: "/tmp/settings.json",
    monitorLogDirectory: "/tmp/monitor/logs",
  };
}

function createDefinition(overrides: Partial<AppDefinition> = {}): AppDefinition {
  return {
    appId: "demo.app",
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

beforeEach(() => {
  vi.clearAllMocks();

  listenMock.mockImplementation(async () => () => {});
  getBootstrapStateMock.mockResolvedValue(createBootstrapSnapshot());
  getSettingsSnapshotMock.mockResolvedValue(createSettingsSnapshot());
  openLogDirectoryMock.mockResolvedValue(undefined);
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
  it("promotes the single workspace from discovery into status and exposes top menus", async () => {
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

    await screen.findByText("连接 DevHub Host");
    expect(screen.getByRole("button", { name: "设置" })).toBeTruthy();
    expect(screen.getByRole("button", { name: "帮助" })).toBeTruthy();
    expect(screen.queryByRole("button", { name: "初始化" })).toBeNull();
    expect(screen.queryByRole("button", { name: "日志" })).toBeNull();

    await act(async () => {
      bootstrapListener?.({
        payload: createBootstrapSnapshot(),
      });
      await Promise.resolve();
    });

    await screen.findByText("应用定义");
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

    screen.getByText("Demo App");
    screen.getByText("instance-1");
  });

  it("returns to the discovery workspace when the host event stream terminates", async () => {
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

    await screen.findByText("连接 DevHub Host");
  });

  it("shows validation issues and blocks persistence when precheck fails", async () => {
    const invalidValidation: DefinitionValidationResult = {
      ok: true,
      valid: false,
      errors: [
        {
          path: "definition.appId",
          code: "required",
          message: "appId 不能为空。",
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

    await screen.findByText("应用定义");

    const user = userEvent.setup();
    await user.click(screen.getByRole("button", { name: "新增定义" }));
    await user.type(screen.getByLabelText("App ID"), "demo.app");
    await user.type(screen.getByLabelText("显示名称"), "Demo App");
    await user.click(screen.getByRole("button", { name: "创建定义" }));

    await screen.findByText("预校验未通过，请修正下列字段错误后再提交。");
    screen.getByText("appId 不能为空。");

    expect(hostClient.validateDefinition).toHaveBeenCalledTimes(1);
    expect(hostClient.upsertDefinition).not.toHaveBeenCalled();
  });

  it("opens log directories from help and keeps the current workspace visible on failure", async () => {
    getBootstrapStateMock.mockResolvedValue(
      createBootstrapSnapshot({
        phase: "scanning",
        connection: null,
      }),
    );

    render(<App />);

    await screen.findByText("连接 DevHub Host");

    const user = userEvent.setup();
    await user.click(screen.getByRole("button", { name: "帮助" }));
    screen.getByRole("menuitem", { name: "打开 Host 日志" });

    await user.click(screen.getByRole("menuitem", { name: "打开 Host 日志" }));
    await waitFor(() => {
      expect(openLogDirectoryMock).toHaveBeenCalledWith("host");
    });
    screen.getByText("连接 DevHub Host");

    openLogDirectoryMock.mockRejectedValueOnce(new Error("无法打开日志目录"));

    await user.click(screen.getByRole("button", { name: "帮助" }));
    await user.click(screen.getByRole("menuitem", { name: "打开 Monitor 日志" }));

    await screen.findByText("无法打开日志目录");
    screen.getByText("连接 DevHub Host");
  });

  it("opens settings from the menu and rejects relative paths before saving", async () => {
    getBootstrapStateMock.mockResolvedValue(
      createBootstrapSnapshot({
        phase: "scanning",
        connection: null,
      }),
    );

    render(<App />);

    await screen.findByText("连接 DevHub Host");

    const user = userEvent.setup();
    await user.click(screen.getByRole("button", { name: "设置" }));
    await screen.findByRole("dialog", { name: "Monitor 设置" });

    await user.clear(screen.getByLabelText("DEVHUB_DATA_DIR 覆盖值"));
    await user.type(screen.getByLabelText("DEVHUB_DATA_DIR 覆盖值"), "./relative-data");
    await user.click(screen.getByRole("button", { name: "保存设置" }));

    await screen.findByText("数据目录必须填写绝对路径。");
    expect(saveSettingsMock).not.toHaveBeenCalled();
  });

  it("keeps delete failures inside the definition workflow", async () => {
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

    const confirmSpy = vi.spyOn(window, "confirm").mockReturnValue(true);

    render(<App />);

    await screen.findByText("Demo App");

    const user = userEvent.setup();
    await user.click(screen.getByRole("button", { name: "编辑" }));
    await screen.findByText("编辑 App Definition");
    await user.click(screen.getByRole("button", { name: "删除定义" }));

    await screen.findAllByText("delete failed");
    expect(resumeDiscoveryMock).not.toHaveBeenCalled();

    confirmSpy.mockRestore();
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

    await screen.findByText("应用实例");

    const user = userEvent.setup();
    await user.click(screen.getByRole("button", { name: "查看定义" }));

    await screen.findByText("定义不存在");
    screen.getByText("只读模式不允许保存或删除。");
  });
});
