import { startTransition, useEffect, useEffectEvent, useState } from "react";
import "./App.css";
import { AppShell } from "./components/AppShell";
import { ConfirmDialog } from "./components/ConfirmDialog";
import { useBootstrapFlow } from "./hooks/useBootstrapFlow";
import { useConfirmDialog } from "./hooks/useConfirmDialog";
import { useDefinitionEditor } from "./hooks/useDefinitionEditor";
import { useHostSession } from "./hooks/useHostSession";
import {
  openLogDirectory,
  pickDataDirectory,
  pickHostExecutablePath,
  writeFrontendLog,
} from "./lib/monitor-api";
import type { AppDefinitionIdentity } from "@devhub/sdk";
import type { FrontendLogInput, LogKind } from "./lib/models";
import { type MonitorWorkspace, getHomeWorkspaceMode, toErrorMessage } from "./lib/monitor-ui";

function App() {
  const [activeWorkspace, setActiveWorkspace] = useState<MonitorWorkspace>("home");
  const [openingLogKind, setOpeningLogKind] = useState<LogKind | null>(null);
  const [shellError, setShellError] = useState<string | null>(null);
  const { closeDialog, confirm, dialog } = useConfirmDialog();

  const recordFrontendLog = useEffectEvent((entry: FrontendLogInput) => {
    void writeFrontendLog(entry).catch(() => {
      // 前端日志写入失败不应打断主流程。
    });
  });

  const {
    bootstrap,
    bootstrapBusy,
    bootstrapError,
    discardSettingsChanges,
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
    definitionDirty,
    definitionWorkspace,
    definitionError,
    handleDefinitionDelete,
    handleDefinitionSubmit,
    openCreateDefinitionWorkspace,
    openEditDefinitionWorkspace,
    openInstanceDefinitionWorkspace,
    updateDefinitionField,
  } = useDefinitionEditor({
    confirmAction: confirm,
    onRemoveDefinition: removeDefinitionFromState,
    onReplaceDefinition: replaceDefinitionInState,
    recordFrontendLog,
    runHostAction,
    sessionResetVersion,
  });

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

  const confirmDiscardWorkspace = useEffectEvent(async (workspace: MonitorWorkspace) => {
    switch (workspace) {
      case "settings":
        return confirm({
          message: "设置中的修改尚未保存，确认放弃并离开当前工作区吗？",
          variant: "danger",
        });
      case "definition":
        return confirm({
          message: "App Definition 中的修改尚未保存，确认放弃并离开当前工作区吗？",
          variant: "danger",
        });
      default:
        return true;
    }
  });

  const leaveCurrentWorkspace = useEffectEvent(async (nextWorkspace: MonitorWorkspace) => {
    if (nextWorkspace === activeWorkspace) {
      return true;
    }

    if (activeWorkspace === "settings") {
      if (settingsDirty && !(await confirmDiscardWorkspace("settings"))) {
        return false;
      }

      discardSettingsChanges();
      return true;
    }

    if (activeWorkspace === "definition") {
      if (definitionDirty && !(await confirmDiscardWorkspace("definition"))) {
        return false;
      }

      closeDefinitionWorkspace();
      return true;
    }

    return true;
  });

  const handleNavigateWorkspace = useEffectEvent(async (workspace: "home" | "help" | "settings") => {
    if (!(await leaveCurrentWorkspace(workspace))) {
      return;
    }

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

  const handleOpenDefinitionEdit = useEffectEvent((identity: AppDefinitionIdentity) => {
    startTransition(() => {
      setActiveWorkspace("definition");
    });
    void openEditDefinitionWorkspace(identity);
  });

  const handleOpenInstanceDefinition = useEffectEvent((instance: Parameters<typeof openInstanceDefinitionWorkspace>[0]) => {
    startTransition(() => {
      setActiveWorkspace("definition");
    });
    void openInstanceDefinitionWorkspace(instance);
  });

  const handleCloseDefinition = useEffectEvent(async () => {
    if (!(await leaveCurrentWorkspace("home"))) {
      return;
    }

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

  const handleSelectHostExecutablePath = useEffectEvent(async () => {
    setShellError(null);

    try {
      const selectedPath = await pickHostExecutablePath(settingsDraft.hostExecutablePath ?? null);
      if (!selectedPath) {
        return;
      }

      updateSettingsDraftField("hostExecutablePath", selectedPath);
    } catch (pickError) {
      setShellError(toErrorMessage(pickError));
    }
  });

  const handleSelectDataDirectory = useEffectEvent(async () => {
    setShellError(null);

    try {
      const selectedPath = await pickDataDirectory(settingsDraft.dataDirOverride ?? null);
      if (!selectedPath) {
        return;
      }

      updateSettingsDraftField("dataDirOverride", selectedPath);
    } catch (pickError) {
      setShellError(toErrorMessage(pickError));
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
    <>
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
        definitionWorkspace={definitionWorkspace}
        onNavigateWorkspace={(workspace) => {
          void handleNavigateWorkspace(workspace);
        }}
        onOpenLogDirectory={(kind) => {
          void handleOpenLogHelp(kind);
        }}
        onLaunchHost={() => {
          void handleLaunch();
        }}
        onResumeDiscovery={() => {
          void handleResumeDiscovery();
        }}
        onChangeSettingsField={updateSettingsDraftField}
        onSelectHostExecutablePath={() => {
          void handleSelectHostExecutablePath();
        }}
        onSelectDataDirectory={() => {
          void handleSelectDataDirectory();
        }}
        onSaveSettings={() => {
          void handleSaveSettings();
        }}
        onAddDefinition={() => {
          handleOpenDefinitionCreate();
        }}
        onEditDefinition={(identity) => {
          handleOpenDefinitionEdit(identity);
        }}
        onViewInstanceDefinition={(instance) => {
          handleOpenInstanceDefinition(instance);
        }}
        onChangeDefinitionField={updateDefinitionField}
        onCloseDefinitionWorkspace={() => {
          void handleCloseDefinition();
        }}
        onDeleteDefinition={() => {
          void handleDeleteDefinition();
        }}
        onSubmitDefinition={() => {
          void handleSubmitDefinition();
        }}
      />

      <ConfirmDialog dialog={dialog} onClose={closeDialog} />
    </>
  );
}

export default App;
