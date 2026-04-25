import { listen } from "@tauri-apps/api/event";
import { startTransition, useEffect, useEffectEvent, useRef, useState } from "react";
import {
  BOOTSTRAP_STATE_CHANGED_EVENT,
  SETTINGS_CHANGED_EVENT,
  getBootstrapState,
  getSettingsSnapshot,
  requestHostLaunch,
  resumeDiscovery,
  saveSettings,
} from "../lib/monitor-api";
import type {
  BootstrapSnapshot,
  FrontendLogInput,
  LaunchHostResult,
  MonitorSettings,
  SettingsSnapshot,
} from "../lib/models";
import {
  areMonitorSettingsEqual,
  type SettingsFieldErrors,
  hasSettingsFieldErrors,
  normalizeOptionalInput,
  toErrorMessage,
  validateSettingsDraft,
} from "../lib/monitor-ui";

interface BootstrapFlowOptions {
  recordFrontendLog: (entry: FrontendLogInput) => void;
}

export function useBootstrapFlow(options: BootstrapFlowOptions) {
  const { recordFrontendLog } = options;

  const [bootstrap, setBootstrap] = useState<BootstrapSnapshot | null>(null);
  const [settings, setSettings] = useState<SettingsSnapshot | null>(null);
  const [settingsDraft, setSettingsDraft] = useState<MonitorSettings>({});
  const [settingsFieldErrors, setSettingsFieldErrors] = useState<SettingsFieldErrors>({});
  const [bootstrapBusy, setBootstrapBusy] = useState(false);
  const [settingsBusy, setSettingsBusy] = useState(false);
  const [bootstrapError, setBootstrapError] = useState<string | null>(null);
  const [settingsError, setSettingsError] = useState<string | null>(null);

  const settingsDirty = !areMonitorSettingsEqual(settingsDraft, settings?.settings);
  const settingsDirtyRef = useRef(false);
  const bootstrapGenerationRef = useRef(-1);
  const settingsRevisionRef = useRef(-1);
  settingsDirtyRef.current = settingsDirty;

  const applyBootstrapSnapshot = useEffectEvent((snapshot: BootstrapSnapshot) => {
    if (snapshot.generation < bootstrapGenerationRef.current) {
      return;
    }

    bootstrapGenerationRef.current = snapshot.generation;
    startTransition(() => {
      setBootstrap(snapshot);
    });
  });

  const applySettingsSnapshot = useEffectEvent((
    snapshot: SettingsSnapshot,
    options?: { resetDraft?: boolean },
  ) => {
    if (snapshot.revision < settingsRevisionRef.current) {
      return;
    }

    settingsRevisionRef.current = snapshot.revision;
    startTransition(() => {
      setSettings(snapshot);

      if (options?.resetDraft || !settingsDirtyRef.current) {
        setSettingsDraft(snapshot.settings);
        setSettingsFieldErrors({});
      }
    });
  });

  useEffect(() => {
    let disposed = false;
    const unlistenCallbacks: Array<() => void> = [];

    async function initialize() {
      try {
        const [offBootstrap, offSettings] = await Promise.all([
          listen<BootstrapSnapshot>(
            BOOTSTRAP_STATE_CHANGED_EVENT,
            (event) => {
              applyBootstrapSnapshot(event.payload);
            },
          ),
          listen<SettingsSnapshot>(
            SETTINGS_CHANGED_EVENT,
            (event) => {
              applySettingsSnapshot(event.payload);
            },
          ),
        ]);

        if (disposed) {
          offBootstrap();
          offSettings();
          return;
        }

        unlistenCallbacks.push(offBootstrap, offSettings);

        const [bootstrapSnapshot, settingsSnapshot] = await Promise.all([
          getBootstrapState(),
          getSettingsSnapshot(),
        ]);

        if (disposed) {
          return;
        }

        applyBootstrapSnapshot(bootstrapSnapshot);
        applySettingsSnapshot(settingsSnapshot, { resetDraft: true });
        setBootstrapError(null);

        recordFrontendLog({
          level: "info",
          category: "frontend.lifecycle",
          action: "initialize",
          result: "ready",
          message: "Monitor UI initialized.",
        });
      } catch (initializeError) {
        if (!disposed) {
          setBootstrapError(toErrorMessage(initializeError));
        }
      }
    }

    void initialize();

    return () => {
      disposed = true;
      for (const dispose of unlistenCallbacks) {
        dispose();
      }
    };
  }, []);

  const replaceBootstrap = useEffectEvent((snapshot: BootstrapSnapshot) => {
    applyBootstrapSnapshot(snapshot);
  });

  const handleResumeDiscovery = useEffectEvent(async () => {
    setBootstrapBusy(true);
    setBootstrapError(null);

    try {
      const snapshot = await resumeDiscovery("frontend_manual_retry");
      applyBootstrapSnapshot(snapshot);

      recordFrontendLog({
        level: "info",
        category: "frontend.bootstrap",
        action: "resume_discovery",
        result: "requested",
      });
    } catch (resumeError) {
      setBootstrapError(toErrorMessage(resumeError));
    } finally {
      setBootstrapBusy(false);
    }
  });

  const handleLaunchHost = useEffectEvent(async (): Promise<LaunchHostResult | null> => {
    setBootstrapBusy(true);
    setBootstrapError(null);

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
      return result;
    } catch (launchError) {
      setBootstrapError(toErrorMessage(launchError));
      return null;
    } finally {
      setBootstrapBusy(false);
    }
  });

  const updateSettingsDraftField = useEffectEvent((
    field: keyof MonitorSettings,
    value: MonitorSettings[keyof MonitorSettings],
  ) => {
    startTransition(() => {
      setSettingsDraft((current) => ({
        ...current,
        [field]: value,
      }));
      setSettingsFieldErrors((current) => {
        if (field === "dataDirOverride" || field === "hostExecutablePath") {
          return {
            ...current,
            [field]: null,
          };
        }

        return current;
      });
    });
  });

  const discardSettingsChanges = useEffectEvent(() => {
    startTransition(() => {
      setSettingsDraft(settings?.settings ?? {});
      setSettingsFieldErrors({});
      setSettingsError(null);
    });
  });

  const handleSaveSettings = useEffectEvent(async (): Promise<boolean> => {
    const validationErrors = validateSettingsDraft(settingsDraft);
    if (hasSettingsFieldErrors(validationErrors)) {
      startTransition(() => {
        setSettingsFieldErrors(validationErrors);
      });
      setSettingsError(null);

      recordFrontendLog({
        level: "warn",
        category: "frontend.settings",
        action: "save",
        result: "invalid",
      });
      return false;
    }

    setSettingsBusy(true);
    setSettingsError(null);

    const payload: MonitorSettings = {
      dataDirOverride: normalizeOptionalInput(settingsDraft.dataDirOverride),
      hostExecutablePath: normalizeOptionalInput(settingsDraft.hostExecutablePath),
      hideHostCommandLineWindow: settingsDraft.hideHostCommandLineWindow ?? true,
    };

    recordFrontendLog({
      level: "info",
      category: "frontend.settings",
      action: "save",
      result: "requested",
      context: {
        dataDirOverride: payload.dataDirOverride ?? null,
        hostExecutablePath: payload.hostExecutablePath ?? null,
        hideHostCommandLineWindow: payload.hideHostCommandLineWindow,
      },
    });

    try {
      const snapshot = await saveSettings(payload);
      applySettingsSnapshot(snapshot, { resetDraft: true });

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
      return true;
    } catch (saveError) {
      recordFrontendLog({
        level: "error",
        category: "frontend.settings",
        action: "save",
        result: "failed",
        message: toErrorMessage(saveError),
      });
      setSettingsError(toErrorMessage(saveError));
      return false;
    } finally {
      setSettingsBusy(false);
    }
  });

  return {
    bootstrap,
    bootstrapBusy,
    bootstrapError,
    handleLaunchHost,
    handleResumeDiscovery,
    handleSaveSettings,
    discardSettingsChanges,
    replaceBootstrap,
    settings,
    settingsBusy,
    settingsDirty,
    settingsDraft,
    settingsError,
    settingsFieldErrors,
    updateSettingsDraftField,
  };
}
