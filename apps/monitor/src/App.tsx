import { startTransition, useEffect, useEffectEvent, useState } from "react";
import "./App.css";
import { AppShell } from "./components/AppShell";
import { useBootstrapFlow } from "./hooks/useBootstrapFlow";
import { useDefinitionEditor } from "./hooks/useDefinitionEditor";
import { useHostSession } from "./hooks/useHostSession";
import { useLogsPanel } from "./hooks/useLogsPanel";
import { writeFrontendLog } from "./lib/monitor-api";
import type { FrontendLogInput } from "./lib/models";
import { type RoutePage, readHashRoute, writeHashRoute } from "./lib/monitor-ui";

function App() {
  const [route, setRoute] = useState<RoutePage>(() => readHashRoute());
  const [nowTick, setNowTick] = useState(() => Date.now());

  const recordFrontendLog = useEffectEvent((entry: FrontendLogInput) => {
    void writeFrontendLog(entry).catch(() => {
      // 前端日志写入失败不应打断主流程。
    });
  });

  function navigateTo(nextRoute: RoutePage): void {
    writeHashRoute(nextRoute);
    startTransition(() => {
      setRoute(nextRoute);
    });
  }

  const {
    activeLogKind,
    logsBusy,
    logsError,
    openLog,
    refreshLogKind,
    selectLogKind,
    selectedLog,
    visibleLogs,
  } = useLogsPanel(route);

  const {
    bootstrap,
    bootstrapBusy,
    bootstrapError,
    handleLaunchHost,
    handleResumeDiscovery,
    handleSaveSettings,
    replaceBootstrap,
    settings,
    settingsBusy,
    settingsDirty,
    settingsDraft,
    settingsError,
    settingsFieldErrors,
    updateSettingsDraftField,
  } = useBootstrapFlow({
    onNavigate: navigateTo,
    recordFrontendLog,
    refreshLogKind,
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
    onNavigate: navigateTo,
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
    const handleHashChange = () => {
      const nextRoute = readHashRoute();
      if (nextRoute === "status" && !(bootstrap?.connection && bootstrap.phase === "host_available")) {
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
  }, [bootstrap?.connection, bootstrap?.phase]);

  const canOpenStatus = Boolean(bootstrap?.connection && bootstrap.phase === "host_available");
  const activeError = route === "bootstrap"
    ? bootstrapError
    : route === "settings"
      ? settingsError
      : route === "logs"
        ? logsError
        : definitionError ?? sessionError;

  return (
    <AppShell
      route={route}
      canOpenStatus={canOpenStatus}
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
      activeLogKind={activeLogKind}
      monitorLogDirectory={settings?.monitorLogDirectory ?? null}
      visibleLogs={visibleLogs}
      selectedLog={selectedLog}
      logsBusy={logsBusy}
      definitionDialog={definitionDialog}
      onNavigate={navigateTo}
      onResumeDiscovery={() => {
        void handleResumeDiscovery();
      }}
      onLaunchHost={() => {
        void handleLaunchHost();
      }}
      onChangeSettingsField={updateSettingsDraftField}
      onSaveSettings={() => {
        void handleSaveSettings();
      }}
      onRefreshLogs={() => {
        void refreshLogKind(activeLogKind, { autoSelect: true });
      }}
      onSelectLogKind={(kind) => {
        selectLogKind(kind);
      }}
      onOpenLog={(kind, fileName) => {
        void openLog(kind, fileName);
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
