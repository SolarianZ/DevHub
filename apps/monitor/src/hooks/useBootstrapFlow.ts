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
  settingsDirtyRef.current = settingsDirty;

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
          setSettingsFieldErrors({});
        });

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
                setSettingsFieldErrors({});
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
    startTransition(() => {
      setBootstrap(snapshot);
    });
  });

  const handleResumeDiscovery = useEffectEvent(async () => {
    setBootstrapBusy(true);
    setBootstrapError(null);

    try {
      const snapshot = await resumeDiscovery("frontend_manual_retry");
      startTransition(() => {
        setBootstrap(snapshot);
      });

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

  const updateSettingsDraftField = useEffectEvent((field: keyof MonitorSettings, value: string) => {
    startTransition(() => {
      setSettingsDraft((current) => ({
        ...current,
        [field]: value,
      }));
      setSettingsFieldErrors((current) => ({
        ...current,
        [field]: null,
      }));
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
        setSettingsFieldErrors({});
      });

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
