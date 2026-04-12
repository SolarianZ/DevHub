import { listen } from "@tauri-apps/api/event";
import {
  APP_DEFINITION_DELETED,
  APP_DEFINITION_UPSERTED,
  APP_INSTANCE_REGISTERED,
  APP_INSTANCE_UNREGISTERED,
  DevHubClient,
  DevHubEventsClient,
  DevHubRpcError,
  DevHubRpcErrorCode,
  type AppDefinition,
  type AppInstance,
} from "@devhub/sdk";
import { startTransition, useEffect, useEffectEvent, useRef, useState } from "react";
import "./App.css";
import {
  createEmptyDefinitionForm,
  definitionFormToModel,
  definitionToForm,
  mapValidationIssues,
  type DefinitionFormState,
  type DefinitionIssueMap,
} from "./lib/definition-form";
import {
  BOOTSTRAP_STATE_CHANGED_EVENT,
  SETTINGS_CHANGED_EVENT,
  createStaticRuntimeResolver,
  getBootstrapState,
  getSettingsSnapshot,
  listLogs,
  readLog,
  requestHostLaunch,
  resumeDiscovery,
  saveSettings,
  writeFrontendLog,
} from "./lib/monitor-api";
import type {
  BootstrapSnapshot,
  FrontendLogInput,
  LogFileInfo,
  LogKind,
  LogReadResult,
  MonitorRuntimeConnectionInfo,
  MonitorSettings,
  SettingsSnapshot,
} from "./lib/models";

const HTTP_CLIENT_ID = "devhub-monitor-ui";
const EVENTS_CLIENT_ID = "devhub-monitor-ui-events";
const INSTANCE_REFRESH_EVENT_TYPES = [APP_INSTANCE_REGISTERED, APP_INSTANCE_UNREGISTERED] as const;
const DEFINITION_REFRESH_EVENT_TYPES = [APP_DEFINITION_UPSERTED, APP_DEFINITION_DELETED] as const;
const ROUTE_SEQUENCE = ["bootstrap", "status", "settings", "logs"] as const;
const ONLINE_STATUS_REFRESH_MS = 15_000;

type RoutePage = (typeof ROUTE_SEQUENCE)[number];
type HostSessionStatus = "idle" | "connecting" | "connected" | "recovering";
type DefinitionDialogMode = "view" | "create" | "edit";

interface DefinitionDialogState {
  mode: DefinitionDialogMode;
  title: string;
  subtitle: string;
  form: DefinitionFormState;
  fieldErrors: DefinitionIssueMap;
  loading: boolean;
  saving: boolean;
  readOnly: boolean;
  missing: boolean;
  emptyStateMessage: string | null;
  submitError: string | null;
}

function App() {
  const [route, setRoute] = useState<RoutePage>(() => readHashRoute());
  const [bootstrap, setBootstrap] = useState<BootstrapSnapshot | null>(null);
  const [settings, setSettings] = useState<SettingsSnapshot | null>(null);
  const [settingsDraft, setSettingsDraft] = useState<MonitorSettings>({});
  const [settingsDirty, setSettingsDirty] = useState(false);
  const [hostLogs, setHostLogs] = useState<LogFileInfo[]>([]);
  const [monitorLogs, setMonitorLogs] = useState<LogFileInfo[]>([]);
  const [selectedLog, setSelectedLog] = useState<LogReadResult | null>(null);
  const [activeLogKind, setActiveLogKind] = useState<LogKind>("monitor");
  const [definitions, setDefinitions] = useState<AppDefinition[]>([]);
  const [instances, setInstances] = useState<AppInstance[]>([]);
  const [hostSessionStatus, setHostSessionStatus] = useState<HostSessionStatus>("idle");
  const [inventoryMessage, setInventoryMessage] = useState("等待发现可用的 DevHub Host。");
  const [shellBusy, setShellBusy] = useState(false);
  const [settingsBusy, setSettingsBusy] = useState(false);
  const [logsBusy, setLogsBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [definitionDialog, setDefinitionDialog] = useState<DefinitionDialogState | null>(null);
  const [nowTick, setNowTick] = useState(() => Date.now());

  const previousPhaseRef = useRef<BootstrapSnapshot["phase"] | null>(null);
  const settingsDirtyRef = useRef(false);
  const recoveryInFlightRef = useRef(false);
  const hostClientRef = useRef<DevHubClient | null>(null);
  const eventsClientRef = useRef<DevHubEventsClient | null>(null);
  const subscriptionIdRef = useRef<string | null>(null);

  settingsDirtyRef.current = settingsDirty;

  const recordFrontendLog = useEffectEvent((entry: FrontendLogInput) => {
    void writeFrontendLog(entry).catch(() => {
      // 前端日志写入失败不应打断主流程。
    });
  });

  async function disposeHostSession(): Promise<void> {
    const hostClient = hostClientRef.current;
    const eventsClient = eventsClientRef.current;
    const subscriptionId = subscriptionIdRef.current;

    hostClientRef.current = null;
    eventsClientRef.current = null;
    subscriptionIdRef.current = null;

    await disposeSessionResources(hostClient, eventsClient, subscriptionId);
  }

  function navigateTo(nextRoute: RoutePage): void {
    const resolved = nextRoute === "status" && !bootstrap?.connection ? "bootstrap" : nextRoute;
    writeHashRoute(resolved);
    startTransition(() => {
      setRoute(resolved);
    });
  }

  const refreshLogsView = useEffectEvent(async (kind: LogKind, options?: { autoOpen?: boolean }) => {
    setLogsBusy(true);

    try {
      const files = await listLogs(kind);

      startTransition(() => {
        if (kind === "monitor") {
          setMonitorLogs(files);
        } else {
          setHostLogs(files);
        }
      });

      const preferredFileName =
        selectedLog?.kind === kind
          ? selectedLog.fileName
          : options?.autoOpen || activeLogKind === kind
            ? files[0]?.name
            : undefined;
      const targetFile = preferredFileName
        ? files.find((item) => item.name === preferredFileName) ?? files[0]
        : undefined;

      if (!targetFile) {
        if (selectedLog?.kind === kind) {
          startTransition(() => {
            setSelectedLog(null);
          });
        }
        return;
      }

      const nextLog = await readLog({
        kind,
        fileName: targetFile.name,
      });

      startTransition(() => {
        setSelectedLog(nextLog);
      });
    } catch (refreshError) {
      setError(toErrorMessage(refreshError));
    } finally {
      setLogsBusy(false);
    }
  });

  const handleConnectionLoss = useEffectEvent(async (reason: string, lossError?: unknown) => {
    if (recoveryInFlightRef.current) {
      return;
    }

    recoveryInFlightRef.current = true;

    recordFrontendLog({
      level: "warn",
      category: "frontend.connection",
      action: "disconnect",
      result: "recovering",
      message: toErrorMessage(lossError),
      context: {
        reason,
        route,
      },
    });

    startTransition(() => {
      setDefinitionDialog(null);
      setDefinitions([]);
      setInstances([]);
      setHostSessionStatus("recovering");
      setInventoryMessage("当前 Host 会话已失效，正在返回初始化页并恢复扫描。");
    });

    await disposeHostSession();

    try {
      const snapshot = await resumeDiscovery(reason);

      writeHashRoute("bootstrap");
      startTransition(() => {
        setBootstrap(snapshot);
        setRoute("bootstrap");
      });
    } catch (resumeError) {
      setError(toErrorMessage(resumeError));
    } finally {
      recoveryInFlightRef.current = false;
    }
  });

  async function runHostAction<T>(
    action: string,
    execute: (client: DevHubClient) => Promise<T>,
  ): Promise<T> {
    const client = hostClientRef.current;
    if (!client) {
      throw new Error("当前没有可用的 DevHub Host 会话。");
    }

    try {
      return await execute(client);
    } catch (hostError) {
      if (shouldRecoverHostSession(hostError)) {
        await handleConnectionLoss(action, hostError);
      }

      throw hostError;
    }
  }

  useEffect(() => {
    const timer = window.setInterval(() => {
      startTransition(() => {
        setNowTick(Date.now());
      });
    }, ONLINE_STATUS_REFRESH_MS);

    return () => {
      window.clearInterval(timer);
    };
  }, []);

  useEffect(() => {
    const handleHashChange = () => {
      const nextRoute = readHashRoute();
      if (nextRoute === "status" && !bootstrap?.connection) {
        writeHashRoute("bootstrap");
        startTransition(() => {
          setRoute("bootstrap");
        });
        return;
      }

      startTransition(() => {
        setRoute(nextRoute);
      });
    };

    window.addEventListener("hashchange", handleHashChange);

    return () => {
      window.removeEventListener("hashchange", handleHashChange);
    };
  }, [bootstrap?.connection]);

  useEffect(() => {
    if (!bootstrap) {
      return;
    }

    const previousPhase = previousPhaseRef.current;

    if (bootstrap.phase === "settings_required") {
      navigateTo("settings");
    } else if (bootstrap.phase === "host_available" && previousPhase !== "host_available") {
      navigateTo("status");
    } else if (previousPhase === "host_available" && bootstrap.phase !== "host_available") {
      navigateTo("bootstrap");
    }

    previousPhaseRef.current = bootstrap.phase;
  }, [bootstrap?.generation, bootstrap?.phase]);

  useEffect(() => {
    let disposed = false;
    const unlistenCallbacks: Array<() => void> = [];

    async function initialize() {
      try {
        const [bootstrapSnapshot, settingsSnapshot] = await Promise.all([
          getBootstrapState(),
          getSettingsSnapshot(),
        ]);

        if (disposed) {
          return;
        }

        startTransition(() => {
          setBootstrap(bootstrapSnapshot);
          setSettings(settingsSnapshot);
          setSettingsDraft(settingsSnapshot.settings);
          setSettingsDirty(false);
        });

        await Promise.all([
          refreshLogsView("monitor", { autoOpen: true }),
          refreshLogsView("host"),
        ]);

        const offBootstrap = await listen<BootstrapSnapshot>(
          BOOTSTRAP_STATE_CHANGED_EVENT,
          (event) => {
            startTransition(() => {
              setBootstrap(event.payload);
            });
          },
        );
        const offSettings = await listen<SettingsSnapshot>(
          SETTINGS_CHANGED_EVENT,
          (event) => {
            startTransition(() => {
              setSettings(event.payload);

              if (!settingsDirtyRef.current) {
                setSettingsDraft(event.payload.settings);
                setSettingsDirty(false);
              }
            });
          },
        );

        if (disposed) {
          offBootstrap();
          offSettings();
          return;
        }

        unlistenCallbacks.push(offBootstrap, offSettings);

        recordFrontendLog({
          level: "info",
          category: "frontend.lifecycle",
          action: "initialize",
          result: "ready",
          message: "Monitor UI initialized.",
        });
      } catch (initializeError) {
        if (!disposed) {
          setError(toErrorMessage(initializeError));
        }
      }
    }

    void initialize();

    return () => {
      disposed = true;
      for (const dispose of unlistenCallbacks) {
        dispose();
      }
      void disposeHostSession();
    };
  }, []);

  useEffect(() => {
    if (!bootstrap?.connection || bootstrap.phase !== "host_available") {
      startTransition(() => {
        if (hostSessionStatus !== "recovering") {
          setHostSessionStatus("idle");
        }
      });
      void disposeHostSession();
      return;
    }

    let disposed = false;
    let localHostClient: DevHubClient | null = null;
    let localEventsClient: DevHubEventsClient | null = null;
    let localSubscriptionId: string | null = null;
    const connection = bootstrap.connection;
    const runtimeResolver = createStaticRuntimeResolver(connection);

    startTransition(() => {
      setHostSessionStatus("connecting");
      setInventoryMessage("正在连接 DevHub Host，并同步定义与实例清单。");
      setError(null);
    });

    async function connectHostSession() {
      try {
        const [hostClient, eventsClient] = await Promise.all([
          DevHubClient.fromRuntime(
            {
              clientId: HTTP_CLIENT_ID,
              requestTimeoutMs: 3_000,
            },
            {
              runtimeResolver,
            },
          ),
          DevHubEventsClient.fromRuntime(
            {
              clientId: EVENTS_CLIENT_ID,
              requestTimeoutMs: 3_000,
            },
            {
              runtimeResolver,
            },
          ),
        ]);

        if (disposed) {
          await disposeSessionResources(hostClient, eventsClient, null);
          return;
        }

        localHostClient = hostClient;
        localEventsClient = eventsClient;

        await eventsClient.authenticate();
        localSubscriptionId = await eventsClient.subscribe([
          ...DEFINITION_REFRESH_EVENT_TYPES,
          ...INSTANCE_REFRESH_EVENT_TYPES,
        ]);

        const [nextDefinitions, nextInstances] = await Promise.all([
          hostClient.listDefinitions(),
          hostClient.listInstances({
            includeAllScopes: true,
            includeOffline: true,
          }),
        ]);

        if (disposed) {
          await disposeSessionResources(hostClient, eventsClient, localSubscriptionId);
          return;
        }

        hostClientRef.current = hostClient;
        eventsClientRef.current = eventsClient;
        subscriptionIdRef.current = localSubscriptionId;

        startTransition(() => {
          setDefinitions(sortDefinitions(nextDefinitions));
          setInstances(sortInstances(nextInstances));
          setHostSessionStatus("connected");
          setInventoryMessage("状态页已连接到当前 DevHub Host。");
        });

        recordFrontendLog({
          level: "info",
          category: "frontend.connection",
          action: "connect",
          result: "connected",
          message: "DevHub Host session connected.",
          context: {
            port: getRuntimePort(connection),
            runtimeDirectory: connection.runtimeDirectory,
          },
        });

        for await (const event of eventsClient.readEvents()) {
          if (disposed) {
            return;
          }

          if (event.type === APP_DEFINITION_UPSERTED || event.type === APP_DEFINITION_DELETED) {
            const refreshedDefinitions = await hostClient.listDefinitions();
            if (disposed) {
              return;
            }

            startTransition(() => {
              setDefinitions(sortDefinitions(refreshedDefinitions));
            });

            recordFrontendLog({
              level: "info",
              category: "frontend.inventory",
              action: "refresh_definitions",
              result: event.type,
              context: {
                trigger: event.type,
              },
            });
            continue;
          }

          if (event.type === APP_INSTANCE_REGISTERED || event.type === APP_INSTANCE_UNREGISTERED) {
            const refreshedInstances = await hostClient.listInstances({
              includeAllScopes: true,
              includeOffline: true,
            });
            if (disposed) {
              return;
            }

            startTransition(() => {
              setInstances(sortInstances(refreshedInstances));
            });

            recordFrontendLog({
              level: "info",
              category: "frontend.inventory",
              action: "refresh_instances",
              result: event.type,
              context: {
                trigger: event.type,
              },
            });
          }
        }

        throw new Error("Host 事件流已终止。");
      } catch (connectError) {
        if (!disposed) {
          await handleConnectionLoss("host_session_terminated", connectError);
        }
      }
    }

    void connectHostSession();

    return () => {
      disposed = true;

      if (hostClientRef.current === localHostClient) {
        hostClientRef.current = null;
      }
      if (eventsClientRef.current === localEventsClient) {
        eventsClientRef.current = null;
      }
      if (subscriptionIdRef.current === localSubscriptionId) {
        subscriptionIdRef.current = null;
      }

      void disposeSessionResources(
        localHostClient,
        localEventsClient,
        localSubscriptionId,
      );
    };
  }, [bootstrap?.connection?.rpcEndpoint, bootstrap?.connection?.runtime.pid, bootstrap?.generation, bootstrap?.phase]);

  useEffect(() => {
    if (route === "logs") {
      void refreshLogsView(activeLogKind, { autoOpen: true });
    }
  }, [route, activeLogKind]);

  const visibleLogs = activeLogKind === "monitor" ? monitorLogs : hostLogs;
  const canOpenStatus = Boolean(bootstrap?.connection && bootstrap.phase === "host_available");

  async function handleResumeDiscovery() {
    setShellBusy(true);
    setError(null);

    try {
      const snapshot = await resumeDiscovery("frontend_manual_retry");
      writeHashRoute("bootstrap");
      startTransition(() => {
        setBootstrap(snapshot);
        setRoute("bootstrap");
      });

      recordFrontendLog({
        level: "info",
        category: "frontend.bootstrap",
        action: "resume_discovery",
        result: "requested",
      });

      await refreshLogsView("monitor", { autoOpen: route === "logs" && activeLogKind === "monitor" });
    } catch (resumeError) {
      setError(toErrorMessage(resumeError));
    } finally {
      setShellBusy(false);
    }
  }

  async function handleLaunchHost() {
    setShellBusy(true);
    setError(null);

    try {
      const result = await requestHostLaunch();

      recordFrontendLog({
        level: result.status === "settings_required" ? "warn" : "info",
        category: "frontend.bootstrap",
        action: "launch_host",
        result: result.status,
        context: {
          dataDir: result.effectiveDataDir,
          pid: result.pid ?? null,
        },
      });

      if (result.status === "settings_required") {
        navigateTo("settings");
      } else {
        navigateTo("bootstrap");
      }

      await Promise.all([
        refreshLogsView("monitor", { autoOpen: route === "logs" && activeLogKind === "monitor" }),
        refreshLogsView("host", { autoOpen: route === "logs" && activeLogKind === "host" }),
      ]);
    } catch (launchError) {
      setError(toErrorMessage(launchError));
    } finally {
      setShellBusy(false);
    }
  }

  async function handleSaveSettings() {
    setSettingsBusy(true);
    setError(null);

    const payload: MonitorSettings = {
      dataDirOverride: normalizeOptionalInput(settingsDraft.dataDirOverride),
      hostExecutablePath: normalizeOptionalInput(settingsDraft.hostExecutablePath),
    };

    recordFrontendLog({
      level: "info",
      category: "frontend.settings",
      action: "save",
      result: "requested",
      context: {
        dataDirOverride: payload.dataDirOverride ?? null,
        hostExecutablePath: payload.hostExecutablePath ?? null,
      },
    });

    try {
      const snapshot = await saveSettings(payload);

      startTransition(() => {
        setSettings(snapshot);
        setSettingsDraft(snapshot.settings);
        setSettingsDirty(false);
      });

      navigateTo("bootstrap");

      recordFrontendLog({
        level: "info",
        category: "frontend.settings",
        action: "save",
        result: "saved",
        context: {
          effectiveDataDir: snapshot.effectiveDataDir,
          dataDirSource: snapshot.dataDirSource,
        },
      });

      await refreshLogsView("monitor", { autoOpen: route === "logs" && activeLogKind === "monitor" });
    } catch (saveError) {
      recordFrontendLog({
        level: "error",
        category: "frontend.settings",
        action: "save",
        result: "failed",
        message: toErrorMessage(saveError),
      });
      setError(toErrorMessage(saveError));
    } finally {
      setSettingsBusy(false);
    }
  }

  function updateSettingsDraftField(field: keyof MonitorSettings, value: string) {
    startTransition(() => {
      setSettingsDraft((current) => ({
        ...current,
        [field]: value,
      }));
      setSettingsDirty(true);
    });
  }

  function closeDefinitionDialog() {
    startTransition(() => {
      setDefinitionDialog(null);
    });
  }

  function updateDefinitionField(field: keyof DefinitionFormState, value: string | boolean) {
    startTransition(() => {
      setDefinitionDialog((current) => {
        if (!current) {
          return current;
        }

        const nextForm = {
          ...current.form,
          [field]: value,
        };

        if (field === "enableLaunch" && value === false) {
          nextForm.launchExePath = "";
          nextForm.launchArgsTemplate = "";
          nextForm.launchWorkingDirectory = "";
          nextForm.launchDedupeKeyTemplate = "";
        }

        return {
          ...current,
          form: nextForm,
          fieldErrors: {},
          submitError: null,
        };
      });
    });
  }

  async function openCreateDefinitionDialog() {
    recordFrontendLog({
      level: "info",
      category: "frontend.definition",
      action: "open_create",
      result: "opened",
    });

    startTransition(() => {
      setDefinitionDialog({
        mode: "create",
        title: "新增 App Definition",
        subtitle: "提交前会先通过 hub.apps.validateDefinition 进行预校验。",
        form: createEmptyDefinitionForm(),
        fieldErrors: {},
        loading: false,
        saving: false,
        readOnly: false,
        missing: false,
        emptyStateMessage: null,
        submitError: null,
      });
    });
  }

  async function openEditDefinitionDialog(appId: string) {
    recordFrontendLog({
      level: "info",
      category: "frontend.definition",
      action: "open_edit",
      result: "requested",
      context: {
        appId,
      },
    });

    startTransition(() => {
      setDefinitionDialog({
        mode: "edit",
        title: "编辑 App Definition",
        subtitle: "编辑模式固定读取最新持久化定义，再允许保存或删除。",
        form: createMissingDefinitionForm(appId),
        fieldErrors: {},
        loading: true,
        saving: false,
        readOnly: false,
        missing: false,
        emptyStateMessage: null,
        submitError: null,
      });
    });

    try {
      const definition = await runHostAction("open_edit_definition", (client) =>
        client.getDefinition(appId),
      );

      startTransition(() => {
        setDefinitionDialog({
          mode: "edit",
          title: "编辑 App Definition",
          subtitle: "编辑模式固定读取最新持久化定义，再允许保存或删除。",
          form: definitionToForm(definition),
          fieldErrors: {},
          loading: false,
          saving: false,
          readOnly: false,
          missing: false,
          emptyStateMessage: null,
          submitError: null,
        });
      });
    } catch (dialogError) {
      if (dialogError instanceof DevHubRpcError && dialogError.is(DevHubRpcErrorCode.AppDefinitionNotFound)) {
        startTransition(() => {
          setDefinitionDialog({
            mode: "edit",
            title: "定义已不可用",
            subtitle: "该定义在打开编辑器前已被删除，当前窗口仅展示只读空状态。",
            form: createMissingDefinitionForm(appId),
            fieldErrors: {},
            loading: false,
            saving: false,
            readOnly: true,
            missing: true,
            emptyStateMessage: `定义 ${appId} 已不存在或已被删除。`,
            submitError: null,
          });
        });
        return;
      }

      setError(toErrorMessage(dialogError));
      closeDefinitionDialog();
    }
  }

  async function openInstanceDefinitionDialog(instance: AppInstance) {
    recordFrontendLog({
      level: "info",
      category: "frontend.definition",
      action: "open_view",
      result: "requested",
      context: {
        appId: instance.appId,
        instanceId: instance.instanceId,
      },
    });

    startTransition(() => {
      setDefinitionDialog({
        mode: "view",
        title: "实例关联定义",
        subtitle: `实例 ${instance.instanceId} 的定义详情只读展示。`,
        form: createMissingDefinitionForm(instance.appId),
        fieldErrors: {},
        loading: true,
        saving: false,
        readOnly: true,
        missing: false,
        emptyStateMessage: null,
        submitError: null,
      });
    });

    try {
      const definition = await runHostAction("open_view_definition", (client) =>
        client.getDefinition(instance.appId),
      );

      startTransition(() => {
        setDefinitionDialog({
          mode: "view",
          title: "实例关联定义",
          subtitle: `实例 ${instance.instanceId} 的定义详情只读展示。`,
          form: definitionToForm(definition),
          fieldErrors: {},
          loading: false,
          saving: false,
          readOnly: true,
          missing: false,
          emptyStateMessage: null,
          submitError: null,
        });
      });
    } catch (dialogError) {
      if (dialogError instanceof DevHubRpcError && dialogError.is(DevHubRpcErrorCode.AppDefinitionNotFound)) {
        startTransition(() => {
          setDefinitionDialog({
            mode: "view",
            title: "定义不存在",
            subtitle: `实例 ${instance.instanceId} 仍然保留注册记录，但对应定义已不可用。`,
            form: createMissingDefinitionForm(instance.appId),
            fieldErrors: {},
            loading: false,
            saving: false,
            readOnly: true,
            missing: true,
            emptyStateMessage: `实例 ${instance.instanceId} 对应的定义 ${instance.appId} 不存在或已被删除。`,
            submitError: null,
          });
        });
        return;
      }

      setError(toErrorMessage(dialogError));
      closeDefinitionDialog();
    }
  }

  async function handleDefinitionSubmit() {
    if (!definitionDialog || definitionDialog.readOnly) {
      return;
    }

    const candidateDefinition = definitionFormToModel(definitionDialog.form);

    startTransition(() => {
      setDefinitionDialog((current) =>
        current
          ? {
              ...current,
              saving: true,
              fieldErrors: {},
              submitError: null,
            }
          : current,
      );
    });

    recordFrontendLog({
      level: "info",
      category: "frontend.definition",
      action: "submit",
      result: "validating",
      context: {
        appId: candidateDefinition.appId,
        mode: definitionDialog.mode,
      },
    });

    try {
      const validation = await runHostAction("validate_definition", (client) =>
        client.validateDefinition(candidateDefinition),
      );

      if (!validation.valid) {
        const issues = mapValidationIssues(validation.errors);

        recordFrontendLog({
          level: "warn",
          category: "frontend.definition",
          action: "submit",
          result: "validation_failed",
          context: {
            appId: candidateDefinition.appId,
            errorCount: validation.errors.length,
          },
        });

        startTransition(() => {
          setDefinitionDialog((current) =>
            current
              ? {
                  ...current,
                  saving: false,
                  fieldErrors: issues,
                  submitError: "预校验未通过，请修正下列字段错误后再提交。",
                }
              : current,
          );
        });
        return;
      }

      const savedDefinition = await runHostAction("upsert_definition", (client) =>
        client.upsertDefinition(candidateDefinition),
      );

      recordFrontendLog({
        level: "info",
        category: "frontend.definition",
        action: "submit",
        result: "saved",
        context: {
          appId: savedDefinition.appId,
        },
      });

      startTransition(() => {
        setDefinitions((current) => upsertDefinition(current, savedDefinition));
        setDefinitionDialog(null);
      });
    } catch (submitError) {
      recordFrontendLog({
        level: "error",
        category: "frontend.definition",
        action: "submit",
        result: "failed",
        message: toErrorMessage(submitError),
        context: {
          appId: candidateDefinition.appId,
        },
      });

      startTransition(() => {
        setDefinitionDialog((current) =>
          current
            ? {
                ...current,
                saving: false,
                submitError: toErrorMessage(submitError),
              }
            : current,
        );
      });
    }
  }

  async function handleDefinitionDelete() {
    if (!definitionDialog || definitionDialog.mode !== "edit" || definitionDialog.readOnly) {
      return;
    }

    if (!window.confirm(`确认删除 App Definition “${definitionDialog.form.appId}” 吗？`)) {
      return;
    }

    startTransition(() => {
      setDefinitionDialog((current) =>
        current
          ? {
              ...current,
              saving: true,
              submitError: null,
            }
          : current,
      );
    });

    recordFrontendLog({
      level: "warn",
      category: "frontend.definition",
      action: "delete",
      result: "requested",
      context: {
        appId: definitionDialog.form.appId,
      },
    });

    try {
      await runHostAction("delete_definition", (client) =>
        client.deleteDefinition(definitionDialog.form.appId),
      );

      recordFrontendLog({
        level: "info",
        category: "frontend.definition",
        action: "delete",
        result: "deleted",
        context: {
          appId: definitionDialog.form.appId,
        },
      });

      startTransition(() => {
        setDefinitions((current) =>
          current.filter((definition) => definition.appId !== definitionDialog.form.appId),
        );
        setDefinitionDialog(null);
      });
    } catch (deleteError) {
      recordFrontendLog({
        level: "error",
        category: "frontend.definition",
        action: "delete",
        result: "failed",
        message: toErrorMessage(deleteError),
        context: {
          appId: definitionDialog.form.appId,
        },
      });

      startTransition(() => {
        setDefinitionDialog((current) =>
          current
            ? {
                ...current,
                saving: false,
                submitError: toErrorMessage(deleteError),
              }
            : current,
        );
      });
    }
  }

  return (
    <main className="app-shell">
      <header className="shell-header">
        <div className="shell-title">
          <p className="eyebrow">DevHub Monitor</p>
          <h1>Desktop workflow shell</h1>
          <p className="hero-copy">
            前端状态机、Host 连接层和桌面桥接已经合并到一个完整工作流里：初始化探测、
            状态盘点、定义管理、设置保存与日志排障都通过同一份 Monitor 前端闭环运行。
          </p>
        </div>
        <div className="shell-status">
          <span className={`phase phase-${bootstrap?.phase ?? "loading"}`}>
            {formatPhaseLabel(bootstrap?.phase)}
          </span>
          <span className={`session-badge session-${hostSessionStatus}`}>
            {formatSessionLabel(hostSessionStatus)}
          </span>
        </div>
      </header>

      <nav className="route-bar" aria-label="Monitor navigation">
        <RouteButton
          active={route === "bootstrap"}
          label="初始化"
          onClick={() => {
            navigateTo("bootstrap");
          }}
        />
        <RouteButton
          active={route === "status"}
          label="状态"
          disabled={!canOpenStatus}
          onClick={() => {
            navigateTo("status");
          }}
        />
        <RouteButton
          active={route === "settings"}
          label="设置"
          onClick={() => {
            navigateTo("settings");
          }}
        />
        <RouteButton
          active={route === "logs"}
          label="日志"
          onClick={() => {
            navigateTo("logs");
          }}
        />
      </nav>

      <section className="summary-grid">
        <article className="summary-card">
          <span className="summary-label">生效数据目录</span>
          <strong>{bootstrap?.effectiveDataDir ?? settings?.effectiveDataDir ?? "加载中"}</strong>
          <span className="summary-meta">
            来源：{formatDataDirSource(bootstrap?.dataDirSource ?? settings?.dataDirSource)}
          </span>
        </article>
        <article className="summary-card">
          <span className="summary-label">当前端口</span>
          <strong>{bootstrap?.connection ? getRuntimePort(bootstrap.connection) : "未连接"}</strong>
          <span className="summary-meta">
            {bootstrap?.connection?.rpcEndpoint ?? "等待验证可用 Host。"}
          </span>
        </article>
        <article className="summary-card">
          <span className="summary-label">定义 / 实例</span>
          <strong>
            {definitions.length} / {instances.length}
          </strong>
          <span className="summary-meta">{inventoryMessage}</span>
        </article>
      </section>

      {route === "bootstrap" ? (
        <BootstrapPage
          bootstrap={bootstrap}
          busy={shellBusy}
          onResumeDiscovery={handleResumeDiscovery}
          onLaunchHost={handleLaunchHost}
          onOpenSettings={() => {
            navigateTo("settings");
          }}
        />
      ) : null}

      {route === "status" ? (
        <StatusPage
          bootstrap={bootstrap}
          definitions={definitions}
          hostSessionStatus={hostSessionStatus}
          instances={instances}
          nowTick={nowTick}
          onAddDefinition={openCreateDefinitionDialog}
          onEditDefinition={openEditDefinitionDialog}
          onViewInstanceDefinition={openInstanceDefinitionDialog}
        />
      ) : null}

      {route === "settings" ? (
        <SettingsPage
          bootstrap={bootstrap}
          busy={settingsBusy}
          settings={settings}
          settingsDirty={settingsDirty}
          settingsDraft={settingsDraft}
          onChangeField={updateSettingsDraftField}
          onSave={handleSaveSettings}
        />
      ) : null}

      {route === "logs" ? (
        <LogsPage
          activeLogKind={activeLogKind}
          busy={logsBusy}
          monitorLogDirectory={settings?.monitorLogDirectory ?? null}
          selectedLog={selectedLog}
          visibleLogs={visibleLogs}
          onOpenLog={async (kind, fileName) => {
            setError(null);
            try {
              const result = await readLog({ kind, fileName });
              startTransition(() => {
                setSelectedLog(result);
              });
            } catch (readError) {
              setError(toErrorMessage(readError));
            }
          }}
          onRefresh={() => {
            void refreshLogsView(activeLogKind, { autoOpen: true });
          }}
          onSelectKind={(kind) => {
            startTransition(() => {
              setActiveLogKind(kind);
            });
            void refreshLogsView(kind, { autoOpen: true });
          }}
        />
      ) : null}

      {definitionDialog ? (
        <DefinitionDialog
          dialog={definitionDialog}
          onChangeField={updateDefinitionField}
          onClose={closeDefinitionDialog}
          onDelete={handleDefinitionDelete}
          onSubmit={handleDefinitionSubmit}
        />
      ) : null}

      {error ? <div className="error-banner">{error}</div> : null}
    </main>
  );
}

function BootstrapPage(props: {
  bootstrap: BootstrapSnapshot | null;
  busy: boolean;
  onResumeDiscovery: () => void;
  onLaunchHost: () => void;
  onOpenSettings: () => void;
}) {
  const { bootstrap, busy, onResumeDiscovery, onLaunchHost, onOpenSettings } = props;

  return (
    <section className="page-grid bootstrap-grid">
      <article className="panel panel-primary">
        <header className="panel-header">
          <div>
            <p className="eyebrow">Initialization</p>
            <h2>扫描与启动流程</h2>
          </div>
          <div className="button-row">
            <button type="button" onClick={onResumeDiscovery} disabled={busy}>
              重新扫描
            </button>
            <button type="button" onClick={onLaunchHost} disabled={busy || bootstrap?.phase === "host_available"}>
              启动 DevHub Host
            </button>
            <button type="button" className="button-secondary" onClick={onOpenSettings} disabled={busy}>
              打开设置
            </button>
          </div>
        </header>

        <div className="status-banner">
          <strong>{formatBootstrapHeadline(bootstrap?.phase)}</strong>
          <p>{formatBootstrapDescription(bootstrap)}</p>
        </div>

        <dl className="detail-list">
          <div>
            <dt>有效数据目录</dt>
            <dd>{bootstrap?.effectiveDataDir ?? "加载中"}</dd>
          </div>
          <div>
            <dt>目录来源</dt>
            <dd>{formatDataDirSource(bootstrap?.dataDirSource)}</dd>
          </div>
          <div>
            <dt>Host 可执行文件</dt>
            <dd>{bootstrap?.settings.hostExecutablePath ?? "未配置"}</dd>
          </div>
          <div>
            <dt>连接状态</dt>
            <dd>{bootstrap?.connection?.rpcEndpoint ?? "尚未发现可用 Host。"}</dd>
          </div>
        </dl>
      </article>

      <article className="panel">
        <header className="panel-header">
          <h2>流程断点</h2>
        </header>
        <div className="stack-list">
          <div className="stack-item">
            <strong>3 秒后显示启动按钮</strong>
            <p>当前 phase 为 `launch_available` 时，初始化页会继续扫描，但允许用户主动拉起 Host。</p>
          </div>
          <div className="stack-item">
            <strong>缺少 Host 路径自动转设置</strong>
            <p>原生层返回 `settings_required` 时，前端立即切到设置页，而不是继续发起启动。</p>
          </div>
          <div className="stack-item">
            <strong>断线回退初始化</strong>
            <p>状态页任何关键会话断开都会释放客户端并恢复扫描，避免停留在过期运行时上。</p>
          </div>
        </div>
      </article>

      <article className="panel">
        <header className="panel-header">
          <h2>最近问题</h2>
        </header>
        <p className="problem-card">
          {bootstrap?.lastProblem?.message ?? "当前没有记录到扫描或连接问题。"}
        </p>
      </article>
    </section>
  );
}

function StatusPage(props: {
  bootstrap: BootstrapSnapshot | null;
  definitions: AppDefinition[];
  hostSessionStatus: HostSessionStatus;
  instances: AppInstance[];
  nowTick: number;
  onAddDefinition: () => void;
  onEditDefinition: (appId: string) => void;
  onViewInstanceDefinition: (instance: AppInstance) => void;
}) {
  const {
    bootstrap,
    definitions,
    hostSessionStatus,
    instances,
    nowTick,
    onAddDefinition,
    onEditDefinition,
    onViewInstanceDefinition,
  } = props;

  return (
    <section className="page-grid status-grid">
      <article className="panel panel-primary">
        <header className="panel-header">
          <div>
            <p className="eyebrow">Status</p>
            <h2>连接摘要</h2>
          </div>
          <span className={`session-badge session-${hostSessionStatus}`}>
            {formatSessionLabel(hostSessionStatus)}
          </span>
        </header>

        <dl className="detail-list">
          <div>
            <dt>HTTP RPC</dt>
            <dd>{bootstrap?.connection?.rpcEndpoint ?? "未连接"}</dd>
          </div>
          <div>
            <dt>WebSocket</dt>
            <dd>{bootstrap?.connection?.websocketEndpoint ?? "未连接"}</dd>
          </div>
          <div>
            <dt>Host PID</dt>
            <dd>{bootstrap?.connection?.runtime.pid ?? "未连接"}</dd>
          </div>
          <div>
            <dt>运行时目录</dt>
            <dd>{bootstrap?.connection?.runtimeDirectory ?? "未连接"}</dd>
          </div>
        </dl>
      </article>

      <article className="panel panel-span">
        <header className="panel-header">
          <div>
            <p className="eyebrow">App Definitions</p>
            <h2>定义列表</h2>
          </div>
          <button type="button" onClick={onAddDefinition}>
            新增定义
          </button>
        </header>

        {definitions.length === 0 ? (
          <EmptyState
            title="当前没有已注册定义"
            description="连接建立后会拉取完整的 Definition 列表，后续变更会通过事件触发自动刷新。"
          />
        ) : (
          <div className="inventory-list">
            {definitions.map((definition) => (
              <article key={definition.appId} className="inventory-item">
                <div className="inventory-main">
                  <div className="inventory-heading">
                    <strong>{definition.displayName}</strong>
                    <span className="chip">{definition.appId}</span>
                  </div>
                  <p>{definition.description ?? "未提供描述。"}</p>
                  <div className="chip-row">
                    {formatDefinitionCapabilities(definition).map((item) => (
                      <span key={`${definition.appId}-${item}`} className="chip subtle-chip">
                        {item}
                      </span>
                    ))}
                  </div>
                </div>
                <div className="inventory-actions">
                  <button
                    type="button"
                    className="button-secondary"
                    onClick={() => {
                      onEditDefinition(definition.appId);
                    }}
                  >
                    编辑
                  </button>
                </div>
              </article>
            ))}
          </div>
        )}
      </article>

      <article className="panel panel-span">
        <header className="panel-header">
          <div>
            <p className="eyebrow">App Instances</p>
            <h2>跨 scope 实例</h2>
          </div>
          <span className="subtle">包含离线实例</span>
        </header>

        {instances.length === 0 ? (
          <EmptyState
            title="当前没有实例"
            description="状态页会通过 `includeAllScopes=true` 和 `includeOffline=true` 拉取完整实例镜像。"
          />
        ) : (
          <div className="inventory-list">
            {instances.map((instance) => {
              const offline = isInstanceOffline(instance, bootstrap?.connection, nowTick);

              return (
                <article key={instance.instanceId} className="inventory-item">
                  <div className="inventory-main">
                    <div className="inventory-heading">
                      <strong>{instance.instanceId}</strong>
                      <span className={`chip ${offline ? "chip-warning" : "chip-success"}`}>
                        {offline ? "离线" : "在线"}
                      </span>
                    </div>
                    <p>
                      {instance.appId} · scope {formatScope(instance.scope)} · PID {instance.pid}
                    </p>
                    <div className="chip-row">
                      <span className="chip subtle-chip">
                        最近心跳 {formatRelativeTime(instance.lastSeenUtc, nowTick)}
                      </span>
                      <span className="chip subtle-chip">
                        invoke: {instance.invoke.poll ? "poll" : "no-poll"} /{" "}
                        {instance.invoke.respond ? "respond" : "no-respond"}
                      </span>
                    </div>
                  </div>
                  <div className="inventory-actions">
                    <button
                      type="button"
                      className="button-secondary"
                      onClick={() => {
                        onViewInstanceDefinition(instance);
                      }}
                    >
                      查看定义
                    </button>
                  </div>
                </article>
              );
            })}
          </div>
        )}
      </article>
    </section>
  );
}

function SettingsPage(props: {
  bootstrap: BootstrapSnapshot | null;
  busy: boolean;
  settings: SettingsSnapshot | null;
  settingsDirty: boolean;
  settingsDraft: MonitorSettings;
  onChangeField: (field: keyof MonitorSettings, value: string) => void;
  onSave: () => void;
}) {
  const { bootstrap, busy, settings, settingsDirty, settingsDraft, onChangeField, onSave } = props;

  return (
    <section className="page-grid settings-grid">
      <article className="panel panel-primary">
        <header className="panel-header">
          <div>
            <p className="eyebrow">Settings</p>
            <h2>Monitor 运行时设置</h2>
          </div>
          <button type="button" onClick={onSave} disabled={busy || !settingsDirty}>
            保存设置
          </button>
        </header>

        <div className="form-grid">
          <label className="field">
            <span>DEVHUB_DATA_DIR 覆盖值</span>
            <input
              type="text"
              value={settingsDraft.dataDirOverride ?? ""}
              placeholder="留空表示使用环境变量或平台默认目录"
              onChange={(event) => {
                onChangeField("dataDirOverride", event.target.value);
              }}
            />
          </label>

          <label className="field">
            <span>Host 可执行文件路径</span>
            <input
              type="text"
              value={settingsDraft.hostExecutablePath ?? ""}
              placeholder="例如 host/src/DevHub.Host/bin/Release/net10.0/DevHub.Host"
              onChange={(event) => {
                onChangeField("hostExecutablePath", event.target.value);
              }}
            />
          </label>
        </div>
      </article>

      <article className="panel">
        <header className="panel-header">
          <h2>当前解析结果</h2>
        </header>
        <dl className="detail-list">
          <div>
            <dt>设置文件</dt>
            <dd>{settings?.settingsFilePath ?? "加载中"}</dd>
          </div>
          <div>
            <dt>生效数据目录</dt>
            <dd>{settings?.effectiveDataDir ?? bootstrap?.effectiveDataDir ?? "加载中"}</dd>
          </div>
          <div>
            <dt>目录来源</dt>
            <dd>{formatDataDirSource(settings?.dataDirSource ?? bootstrap?.dataDirSource)}</dd>
          </div>
          <div>
            <dt>Monitor 日志目录</dt>
            <dd>{settings?.monitorLogDirectory ?? "加载中"}</dd>
          </div>
        </dl>
      </article>
    </section>
  );
}

function LogsPage(props: {
  activeLogKind: LogKind;
  busy: boolean;
  monitorLogDirectory: string | null;
  selectedLog: LogReadResult | null;
  visibleLogs: LogFileInfo[];
  onOpenLog: (kind: LogKind, fileName: string) => void;
  onRefresh: () => void;
  onSelectKind: (kind: LogKind) => void;
}) {
  const {
    activeLogKind,
    busy,
    monitorLogDirectory,
    selectedLog,
    visibleLogs,
    onOpenLog,
    onRefresh,
    onSelectKind,
  } = props;

  return (
    <section className="page-grid logs-grid-page">
      <article className="panel panel-span">
        <header className="panel-header">
          <div>
            <p className="eyebrow">Logs</p>
            <h2>Host / Monitor 双视图</h2>
          </div>
          <div className="button-row">
            <button
              type="button"
              className={activeLogKind === "monitor" ? "active" : ""}
              onClick={() => {
                onSelectKind("monitor");
              }}
            >
              Monitor 日志
            </button>
            <button
              type="button"
              className={activeLogKind === "host" ? "active" : ""}
              onClick={() => {
                onSelectKind("host");
              }}
            >
              Host 日志
            </button>
            <button type="button" className="button-secondary" onClick={onRefresh} disabled={busy}>
              刷新
            </button>
          </div>
        </header>

        <div className="status-banner compact-banner">
          <strong>{activeLogKind === "monitor" ? "Monitor 结构化日志" : "Host 运行日志"}</strong>
          <p>
            {activeLogKind === "monitor"
              ? monitorLogDirectory ?? "加载中"
              : "当前有效 DEVHUB_DATA_DIR/logs/ 下的日志文件会显示在这里。"}
          </p>
        </div>

        <div className="logs-grid">
          <div className="log-list">
            {visibleLogs.length === 0 ? (
              <EmptyState
                title="当前没有可读取的日志文件"
                description="切换页面或执行启动、设置保存、定义管理等关键路径后，这里会出现最新日志。"
              />
            ) : (
              visibleLogs.map((file) => (
                <button
                  key={`${file.kind}-${file.name}`}
                  type="button"
                  className={`log-item ${
                    selectedLog?.kind === file.kind && selectedLog.fileName === file.name
                      ? "selected"
                      : ""
                  }`}
                  onClick={() => {
                    onOpenLog(file.kind, file.name);
                  }}
                >
                  <span>{file.name}</span>
                  <span className="subtle">{formatBytes(file.sizeBytes)}</span>
                </button>
              ))
            )}
          </div>

          <div className="log-content">
            <div className="log-meta">
              <strong>{selectedLog?.fileName ?? "未选择日志文件"}</strong>
              <span className="subtle">{selectedLog?.filePath ?? "请选择左侧日志。"}</span>
            </div>
            <pre>{selectedLog?.contents ?? "暂无内容。"}</pre>
          </div>
        </div>
      </article>
    </section>
  );
}

function DefinitionDialog(props: {
  dialog: DefinitionDialogState;
  onChangeField: (field: keyof DefinitionFormState, value: string | boolean) => void;
  onClose: () => void;
  onDelete: () => void;
  onSubmit: () => void;
}) {
  const { dialog, onChangeField, onClose, onDelete, onSubmit } = props;
  const submitLabel = dialog.mode === "create" ? "创建定义" : "保存修改";
  const canDelete = dialog.mode === "edit" && !dialog.readOnly && !dialog.missing;
  const disableInputs = dialog.readOnly || dialog.loading || dialog.saving;

  return (
    <div className="modal-backdrop" role="presentation">
      <section className="modal-card" role="dialog" aria-modal="true" aria-labelledby="definition-dialog-title">
        <header className="modal-header">
          <div>
            <p className="eyebrow">App Definition</p>
            <h2 id="definition-dialog-title">{dialog.title}</h2>
            <p className="modal-subtitle">{dialog.subtitle}</p>
          </div>
          <button type="button" className="button-secondary" onClick={onClose}>
            关闭
          </button>
        </header>

        {dialog.submitError ? <div className="inline-error">{dialog.submitError}</div> : null}

        {dialog.loading ? (
          <div className="empty-state modal-state">
            <h3>正在读取定义</h3>
            <p>状态页会优先拉取最新持久化定义，再决定是否进入编辑或只读查看。</p>
          </div>
        ) : dialog.missing ? (
          <div className="empty-state modal-state">
            <h3>定义不可用</h3>
            <p>{dialog.emptyStateMessage}</p>
          </div>
        ) : (
          <div className="modal-body">
            <div className="form-grid">
              <label className="field">
                <span>App ID</span>
                <input
                  type="text"
                  value={dialog.form.appId}
                  disabled={disableInputs || dialog.mode !== "create"}
                  onChange={(event) => {
                    onChangeField("appId", event.target.value);
                  }}
                />
                <FieldIssues issues={dialog.fieldErrors["definition.appId"]} />
              </label>

              <label className="field">
                <span>显示名称</span>
                <input
                  type="text"
                  value={dialog.form.displayName}
                  disabled={disableInputs}
                  onChange={(event) => {
                    onChangeField("displayName", event.target.value);
                  }}
                />
                <FieldIssues issues={dialog.fieldErrors["definition.displayName"]} />
              </label>

              <label className="field field-full">
                <span>描述</span>
                <textarea
                  rows={3}
                  value={dialog.form.description}
                  disabled={disableInputs}
                  onChange={(event) => {
                    onChangeField("description", event.target.value);
                  }}
                />
                <FieldIssues issues={dialog.fieldErrors["definition.description"]} />
              </label>
            </div>

            <section className="section-block">
              <header className="section-header">
                <h3>Capabilities</h3>
              </header>
              <div className="toggle-grid">
                <label className="toggle-item">
                  <input
                    type="checkbox"
                    checked={dialog.form.enableRpc}
                    disabled={disableInputs}
                    onChange={(event) => {
                      onChangeField("enableRpc", event.target.checked);
                    }}
                  />
                  <span>RPC</span>
                </label>
                <label className="toggle-item">
                  <input
                    type="checkbox"
                    checked={dialog.form.enableEvents}
                    disabled={disableInputs}
                    onChange={(event) => {
                      onChangeField("enableEvents", event.target.checked);
                    }}
                  />
                  <span>Events</span>
                </label>
              </div>
              <FieldIssues issues={dialog.fieldErrors["definition.capabilities"]} />
              <FieldIssues issues={dialog.fieldErrors["definition.capabilities.rpc"]} />
              <FieldIssues issues={dialog.fieldErrors["definition.capabilities.events"]} />
            </section>

            <section className="section-block">
              <header className="section-header">
                <h3>Launch</h3>
                <label className="toggle-item">
                  <input
                    type="checkbox"
                    checked={dialog.form.enableLaunch}
                    disabled={disableInputs}
                    onChange={(event) => {
                      onChangeField("enableLaunch", event.target.checked);
                    }}
                  />
                  <span>启用 launch 配置</span>
                </label>
              </header>
              {dialog.form.enableLaunch ? (
                <div className="form-grid">
                  <label className="field">
                    <span>exePath</span>
                    <input
                      type="text"
                      value={dialog.form.launchExePath}
                      disabled={disableInputs}
                      onChange={(event) => {
                        onChangeField("launchExePath", event.target.value);
                      }}
                    />
                    <FieldIssues issues={dialog.fieldErrors["definition.launch.exePath"]} />
                  </label>
                  <label className="field">
                    <span>argsTemplate</span>
                    <input
                      type="text"
                      value={dialog.form.launchArgsTemplate}
                      disabled={disableInputs}
                      onChange={(event) => {
                        onChangeField("launchArgsTemplate", event.target.value);
                      }}
                    />
                    <FieldIssues issues={dialog.fieldErrors["definition.launch.argsTemplate"]} />
                  </label>
                  <label className="field">
                    <span>workingDirectory</span>
                    <input
                      type="text"
                      value={dialog.form.launchWorkingDirectory}
                      disabled={disableInputs}
                      onChange={(event) => {
                        onChangeField("launchWorkingDirectory", event.target.value);
                      }}
                    />
                    <FieldIssues issues={dialog.fieldErrors["definition.launch.workingDirectory"]} />
                  </label>
                  <label className="field">
                    <span>dedupeKeyTemplate</span>
                    <input
                      type="text"
                      value={dialog.form.launchDedupeKeyTemplate}
                      disabled={disableInputs}
                      onChange={(event) => {
                        onChangeField("launchDedupeKeyTemplate", event.target.value);
                      }}
                    />
                    <FieldIssues issues={dialog.fieldErrors["definition.launch.dedupeKeyTemplate"]} />
                  </label>
                </div>
              ) : (
                <p className="subtle">未启用 launch 配置时，将不会向 Host 提交 launch 字段。</p>
              )}
              <FieldIssues issues={dialog.fieldErrors["definition.launch"]} />
            </section>
          </div>
        )}

        <footer className="modal-footer">
          {canDelete ? (
            <button type="button" className="button-danger" onClick={onDelete} disabled={dialog.saving}>
              删除定义
            </button>
          ) : (
            <span className="subtle">
              {dialog.readOnly ? "只读模式不允许保存或删除。" : "删除操作仅在编辑现有定义时可用。"}
            </span>
          )}
          <div className="button-row">
            <button type="button" className="button-secondary" onClick={onClose} disabled={dialog.saving}>
              {dialog.readOnly ? "关闭" : "取消"}
            </button>
            {!dialog.readOnly && !dialog.missing ? (
              <button type="button" onClick={onSubmit} disabled={dialog.saving}>
                {dialog.saving ? "处理中..." : submitLabel}
              </button>
            ) : null}
          </div>
        </footer>
      </section>
    </div>
  );
}

function RouteButton(props: {
  active: boolean;
  disabled?: boolean;
  label: string;
  onClick: () => void;
}) {
  const { active, disabled, label, onClick } = props;

  return (
    <button
      type="button"
      className={`route-pill ${active ? "active" : ""}`}
      onClick={onClick}
      disabled={disabled}
    >
      {label}
    </button>
  );
}

function FieldIssues(props: {
  issues?: readonly { code: string; message: string }[];
}) {
  const { issues } = props;

  if (!issues || issues.length === 0) {
    return null;
  }

  return (
    <div className="field-issues">
      {issues.map((issue) => (
        <p key={`${issue.code}-${issue.message}`}>{issue.message}</p>
      ))}
    </div>
  );
}

function EmptyState(props: {
  title: string;
  description: string;
}) {
  const { title, description } = props;

  return (
    <div className="empty-state">
      <h3>{title}</h3>
      <p>{description}</p>
    </div>
  );
}

function readHashRoute(): RoutePage {
  const raw = window.location.hash.replace(/^#\/?/, "").trim();
  return ROUTE_SEQUENCE.includes(raw as RoutePage) ? (raw as RoutePage) : "bootstrap";
}

function writeHashRoute(route: RoutePage): void {
  const nextHash = route === "bootstrap" ? "" : `#/${route}`;
  if (window.location.hash !== nextHash) {
    window.location.hash = nextHash;
  }
}

function normalizeOptionalInput(value?: string | null): string | null {
  const trimmed = value?.trim() ?? "";
  return trimmed ? trimmed : null;
}

function shouldRecoverHostSession(error: unknown): boolean {
  if (!(error instanceof DevHubRpcError)) {
    return true;
  }

  return (
    error.is(DevHubRpcErrorCode.Unauthorized)
    || error.is(DevHubRpcErrorCode.Forbidden)
    || error.is(DevHubRpcErrorCode.InternalError)
  );
}

async function disposeSessionResources(
  hostClient: DevHubClient | null,
  eventsClient: DevHubEventsClient | null,
  subscriptionId: string | null,
): Promise<void> {
  if (eventsClient && subscriptionId) {
    try {
      await eventsClient.unsubscribe(subscriptionId);
    } catch {
      // 连接已关闭时取消订阅可能失败，这里按幂等清理处理。
    }
  }

  const cleanupTasks: Promise<void>[] = [];
  if (eventsClient) {
    cleanupTasks.push(eventsClient.dispose());
  }
  if (hostClient) {
    cleanupTasks.push(hostClient.dispose());
  }

  if (cleanupTasks.length > 0) {
    await Promise.allSettled(cleanupTasks);
  }
}

function sortDefinitions(definitions: readonly AppDefinition[]): AppDefinition[] {
  return [...definitions].sort((left, right) =>
    left.appId.localeCompare(right.appId, "zh-CN"),
  );
}

function sortInstances(instances: readonly AppInstance[]): AppInstance[] {
  return [...instances].sort((left, right) => {
    const appCompare = left.appId.localeCompare(right.appId, "zh-CN");
    if (appCompare !== 0) {
      return appCompare;
    }

    const scopeCompare = formatScope(left.scope).localeCompare(formatScope(right.scope), "zh-CN");
    if (scopeCompare !== 0) {
      return scopeCompare;
    }

    return left.instanceId.localeCompare(right.instanceId, "zh-CN");
  });
}

function upsertDefinition(current: readonly AppDefinition[], nextDefinition: AppDefinition): AppDefinition[] {
  return sortDefinitions([
    ...current.filter((definition) => definition.appId !== nextDefinition.appId),
    nextDefinition,
  ]);
}

function createMissingDefinitionForm(appId: string): DefinitionFormState {
  const form = createEmptyDefinitionForm();
  form.appId = appId;
  form.enableRpc = false;
  return form;
}

function formatPhaseLabel(phase?: BootstrapSnapshot["phase"]): string {
  switch (phase) {
    case "scanning":
      return "正在扫描";
    case "launch_available":
      return "可启动 Host";
    case "settings_required":
      return "需要设置";
    case "host_available":
      return "Host 可用";
    default:
      return "初始化中";
  }
}

function formatSessionLabel(status: HostSessionStatus): string {
  switch (status) {
    case "connecting":
      return "连接中";
    case "connected":
      return "已连接";
    case "recovering":
      return "恢复中";
    default:
      return "未连接";
  }
}

function formatDataDirSource(source?: SettingsSnapshot["dataDirSource"]): string {
  switch (source) {
    case "settings_override":
      return "设置覆盖";
    case "environment":
      return "环境变量";
    case "platform_default":
      return "平台默认";
    default:
      return "加载中";
  }
}

function formatBootstrapHeadline(phase?: BootstrapSnapshot["phase"]): string {
  switch (phase) {
    case "launch_available":
      return "3 秒内未发现可用 Host，已经开放启动入口。";
    case "settings_required":
      return "缺少 Host 可执行文件路径，需要先完成设置。";
    case "host_available":
      return "已验证到可用 Host，前端会自动切入状态页。";
    case "scanning":
      return "正在持续扫描当前有效数据目录。";
    default:
      return "正在初始化 Monitor。";
  }
}

function formatBootstrapDescription(snapshot: BootstrapSnapshot | null): string {
  if (!snapshot) {
    return "正在读取 Monitor 原生后端的初始化状态。";
  }

  if (snapshot.phase === "host_available" && snapshot.connection) {
    return `已通过真实连通性校验确认 ${snapshot.connection.rpcEndpoint} 可用，接下来由前端接管 Host RPC 与事件连接。`;
  }

  if (snapshot.phase === "settings_required") {
    return "用户发起启动请求，但当前尚未配置 DevHub Host 可执行文件路径。";
  }

  if (snapshot.phase === "launch_available") {
    return "虽然初始化页显示了启动按钮，但后台扫描不会停止，一旦发现可用 Host 会立即切到状态页。";
  }

  return "原生后端会持续读取 hub.json 与 token，并通过真实 hub.ping 校验过滤掉过期运行时文件。";
}

function getRuntimePort(connection?: MonitorRuntimeConnectionInfo | null): string {
  if (!connection) {
    return "未连接";
  }

  try {
    return String(new URL(connection.rpcEndpoint).port || new URL(connection.runtime.httpBaseUrl).port);
  } catch {
    return connection.runtime.httpBaseUrl;
  }
}

function isInstanceOffline(
  instance: AppInstance,
  connection: MonitorRuntimeConnectionInfo | undefined | null,
  nowTick: number,
): boolean {
  const thresholdSeconds = connection?.runtime.runtimeTuning.onlineThresholdSeconds ?? 30;
  return nowTick - instance.lastSeenUtc.getTime() > thresholdSeconds * 1_000;
}

function formatScope(scope?: string | null): string {
  return scope && scope.trim() ? scope : "global";
}

function formatRelativeTime(value: Date, nowTick: number): string {
  const deltaMs = Math.max(0, nowTick - value.getTime());
  const deltaSeconds = Math.floor(deltaMs / 1_000);

  if (deltaSeconds < 60) {
    return `${deltaSeconds}s 前`;
  }

  const minutes = Math.floor(deltaSeconds / 60);
  if (minutes < 60) {
    return `${minutes}m 前`;
  }

  const hours = Math.floor(minutes / 60);
  return `${hours}h 前`;
}

function formatDefinitionCapabilities(definition: AppDefinition): string[] {
  const capabilities: string[] = [];

  if (definition.capabilities?.rpc ?? true) {
    capabilities.push("RPC");
  }
  if (definition.capabilities?.events) {
    capabilities.push("Events");
  }
  if (definition.launch?.exePath) {
    capabilities.push("Launch");
  }

  return capabilities.length > 0 ? capabilities : ["基础定义"];
}

function formatBytes(value: number): string {
  if (value < 1_024) {
    return `${value} B`;
  }

  if (value < 1_024 * 1_024) {
    return `${(value / 1_024).toFixed(1)} KB`;
  }

  return `${(value / (1_024 * 1_024)).toFixed(1)} MB`;
}

function toErrorMessage(error: unknown): string {
  if (typeof error === "object" && error && "message" in error && typeof error.message === "string") {
    return error.message;
  }

  return "发生未知错误。";
}

export default App;
