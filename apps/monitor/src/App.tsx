import { startTransition, useEffect, useEffectEvent, useState } from "react";
import "./App.css";
import { AppShell } from "./components/AppShell";
import { useBootstrapFlow } from "./hooks/useBootstrapFlow";
import { useDefinitionEditor } from "./hooks/useDefinitionEditor";
import { useHostSession } from "./hooks/useHostSession";
import { openLogDirectory, writeFrontendLog } from "./lib/monitor-api";
import type { FrontendLogInput, LogKind } from "./lib/models";
import { getPrimaryWorkspaceMode, toErrorMessage } from "./lib/monitor-ui";

function App() {
  const [nowTick, setNowTick] = useState(() => Date.now());
  const [settingsOpen, setSettingsOpen] = useState(false);
  const [helpMenuOpen, setHelpMenuOpen] = useState(false);
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
    closeDefinitionDialog,
    definitionDialog,
    definitionError,
    handleDefinitionDelete,
    handleDefinitionSubmit,
    openCreateDefinitionDialog,
    openEditDefinitionDialog,
    openInstanceDefinitionDialog,
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
        setSettingsOpen(true);
      });
    }
  }, [bootstrap?.phase]);

  const handleOpenSettings = useEffectEvent(() => {
    startTransition(() => {
      setHelpMenuOpen(false);
      setSettingsOpen(true);
    });
  });

  const handleCloseSettings = useEffectEvent(() => {
    startTransition(() => {
      setSettingsOpen(false);
    });
  });

  const handleToggleHelpMenu = useEffectEvent(() => {
    startTransition(() => {
      setHelpMenuOpen((current) => !current);
    });
  });

  const handleOpenLogHelp = useEffectEvent(async (kind: LogKind) => {
    startTransition(() => {
      setHelpMenuOpen(false);
    });
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
        setSettingsOpen(true);
      });
    }
  });

  const handleSaveSettings = useEffectEvent(async () => {
    const saved = await handleSaveSettingsRequest();
    if (saved) {
      startTransition(() => {
        setSettingsOpen(false);
      });
    }
  });

  const workspaceMode = getPrimaryWorkspaceMode(bootstrap);
  const activeError =
    shellError
    ?? definitionError
    ?? sessionError
    ?? settingsError
    ?? bootstrapError;

  useEffect(() => {
    if (workspaceMode === "status") {
      startTransition(() => {
        setHelpMenuOpen(false);
      });
    }
  }, [workspaceMode]);

  return (
    <AppShell
      workspaceMode={workspaceMode}
      helpMenuOpen={helpMenuOpen}
      openingLogKind={openingLogKind}
      settingsOpen={settingsOpen}
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
      definitionDialog={definitionDialog}
      onOpenSettings={handleOpenSettings}
      onCloseSettings={handleCloseSettings}
      onToggleHelpMenu={handleToggleHelpMenu}
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
        void openCreateDefinitionDialog();
      }}
      onEditDefinition={(appId) => {
        void openEditDefinitionDialog(appId);
      }}
      onViewInstanceDefinition={(instance) => {
        void openInstanceDefinitionDialog(instance);
      }}
      onChangeDefinitionField={updateDefinitionField}
      onCloseDefinitionDialog={closeDefinitionDialog}
      onDeleteDefinition={() => {
        void handleDefinitionDelete();
      }}
      onSubmitDefinition={() => {
        void handleDefinitionSubmit();
      }}
    />
  );
}

export default App;
