import { startTransition, useEffect, useEffectEvent, useState } from "react";
import "./App.css";
import { AppShell } from "./components/AppShell";
import { useBootstrapFlow } from "./hooks/useBootstrapFlow";
import { useDefinitionEditor } from "./hooks/useDefinitionEditor";
import { useHostSession } from "./hooks/useHostSession";
import { openLogDirectory, writeFrontendLog } from "./lib/monitor-api";
import type { FrontendLogInput, LogKind } from "./lib/models";
import { type MonitorWorkspace, getHomeWorkspaceMode, toErrorMessage } from "./lib/monitor-ui";

function App() {
  const [nowTick, setNowTick] = useState(() => Date.now());
  const [activeWorkspace, setActiveWorkspace] = useState<MonitorWorkspace>("home");
  const [openingLogKind, setOpeningLogKind] = useState<LogKind | null>(null);
  const [shellError, setShellError] = useState<string | null>(null);

  const recordFrontendLog = useEffectEvent((entry: FrontendLogInput) => {
    void writeFrontendLog(entry).catch(() => {
      // 前端日志写入失败不应打断主流程。
    });
  });

  const {
    bootstrap,
    bootstrapBusy,
    bootstrapError,
    handleLaunchHost,
    handleResumeDiscovery,
    handleSaveSettings: handleSaveSettingsRequest,
    replaceBootstrap,
    settings,
    settingsBusy,
    settingsDirty,
    settingsDraft,
    settingsError,
    settingsFieldErrors,
    updateSettingsDraftField,
  } = useBootstrapFlow({
    recordFrontendLog,
  });

  const {
    definitions,
    hostSessionStatus,
    instances,
    inventoryMessage,
    removeDefinitionFromState,
    replaceDefinitionInState,
    runHostAction,
    sessionError,
    sessionResetVersion,
  } = useHostSession({
    bootstrap,
    onReplaceBootstrap: replaceBootstrap,
    recordFrontendLog,
  });

  const {
    closeDefinitionWorkspace,
    definitionWorkspace,
    definitionError,
    handleDefinitionDelete,
    handleDefinitionSubmit,
    openCreateDefinitionWorkspace,
    openEditDefinitionWorkspace,
    openInstanceDefinitionWorkspace,
    updateDefinitionField,
  } = useDefinitionEditor({
    onRemoveDefinition: removeDefinitionFromState,
    onReplaceDefinition: replaceDefinitionInState,
    recordFrontendLog,
    runHostAction,
    sessionResetVersion,
  });

  useEffect(() => {
    const timer = window.setInterval(() => {
      startTransition(() => {
        setNowTick(Date.now());
      });
    }, 15_000);

    return () => {
      window.clearInterval(timer);
    };
  }, []);

  useEffect(() => {
    if (bootstrap?.phase === "settings_required") {
      startTransition(() => {
        setActiveWorkspace("settings");
      });
    }
  }, [bootstrap?.phase]);

  useEffect(() => {
    if (activeWorkspace === "definition" && !definitionWorkspace) {
      startTransition(() => {
        setActiveWorkspace("home");
      });
    }
  }, [activeWorkspace, definitionWorkspace]);

  const handleNavigateWorkspace = useEffectEvent((workspace: "home" | "help" | "settings") => {
    startTransition(() => {
      setActiveWorkspace(workspace);
    });
  });

  const handleOpenDefinitionCreate = useEffectEvent(() => {
    startTransition(() => {
      setActiveWorkspace("definition");
    });
    void openCreateDefinitionWorkspace();
  });

  const handleOpenDefinitionEdit = useEffectEvent((appId: string) => {
    startTransition(() => {
      setActiveWorkspace("definition");
    });
    void openEditDefinitionWorkspace(appId);
  });

  const handleOpenInstanceDefinition = useEffectEvent((instance: Parameters<typeof openInstanceDefinitionWorkspace>[0]) => {
    startTransition(() => {
      setActiveWorkspace("definition");
    });
    void openInstanceDefinitionWorkspace(instance);
  });

  const handleCloseDefinition = useEffectEvent(() => {
    closeDefinitionWorkspace();
    startTransition(() => {
      setActiveWorkspace("home");
    });
  });

  const handleOpenLogHelp = useEffectEvent(async (kind: LogKind) => {
    setShellError(null);
    setOpeningLogKind(kind);

    try {
      await openLogDirectory(kind);
      recordFrontendLog({
        level: "info",
        category: "frontend.support",
        action: "open_log_directory",
        result: "opened",
        context: {
          kind,
        },
      });
    } catch (openError) {
      const message = toErrorMessage(openError);
      setShellError(message);

      recordFrontendLog({
        level: "error",
        category: "frontend.support",
        action: "open_log_directory",
        result: "failed",
        message,
        context: {
          kind,
        },
      });
    } finally {
      setOpeningLogKind(null);
    }
  });

  const handleLaunch = useEffectEvent(async () => {
    setShellError(null);
    const result = await handleLaunchHost();
    if (result?.status === "settings_required") {
      startTransition(() => {
        setActiveWorkspace("settings");
      });
    }
  });

  const handleSaveSettings = useEffectEvent(async () => {
    const saved = await handleSaveSettingsRequest();
    if (saved) {
      startTransition(() => {
        setActiveWorkspace("home");
      });
    }
  });

  const handleSubmitDefinition = useEffectEvent(async () => {
    const saved = await handleDefinitionSubmit();
    if (saved) {
      startTransition(() => {
        setActiveWorkspace("home");
      });
    }
  });

  const handleDeleteDefinition = useEffectEvent(async () => {
    const deleted = await handleDefinitionDelete();
    if (deleted) {
      startTransition(() => {
        setActiveWorkspace("home");
      });
    }
  });

  const homeWorkspaceMode = getHomeWorkspaceMode(bootstrap);
  const activeError =
    shellError
    ?? definitionError
    ?? sessionError
    ?? settingsError
    ?? bootstrapError;

  return (
    <AppShell
      activeWorkspace={activeWorkspace}
      homeWorkspaceMode={homeWorkspaceMode}
      openingLogKind={openingLogKind}
      activeError={activeError}
      bootstrap={bootstrap}
      bootstrapBusy={bootstrapBusy}
      settings={settings}
      settingsDraft={settingsDraft}
      settingsDirty={settingsDirty}
      settingsFieldErrors={settingsFieldErrors}
      settingsBusy={settingsBusy}
      hostSessionStatus={hostSessionStatus}
      definitions={definitions}
      instances={instances}
      inventoryMessage={inventoryMessage}
      nowTick={nowTick}
      definitionWorkspace={definitionWorkspace}
      onNavigateWorkspace={handleNavigateWorkspace}
      onOpenLogDirectory={(kind) => {
        void handleOpenLogHelp(kind);
      }}
      onResumeDiscovery={() => {
        setShellError(null);
        void handleResumeDiscovery();
      }}
      onLaunchHost={() => {
        void handleLaunch();
      }}
      onChangeSettingsField={updateSettingsDraftField}
      onSaveSettings={() => {
        void handleSaveSettings();
      }}
      onAddDefinition={() => {
        handleOpenDefinitionCreate();
      }}
      onEditDefinition={(appId) => {
        handleOpenDefinitionEdit(appId);
      }}
      onViewInstanceDefinition={(instance) => {
        handleOpenInstanceDefinition(instance);
      }}
      onChangeDefinitionField={updateDefinitionField}
      onCloseDefinitionWorkspace={handleCloseDefinition}
      onDeleteDefinition={() => {
        void handleDeleteDefinition();
      }}
      onSubmitDefinition={() => {
        void handleSubmitDefinition();
      }}
    />
  );
}

export default App;
