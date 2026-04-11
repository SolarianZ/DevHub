import { listen } from "@tauri-apps/api/event";
import { startTransition, useEffect, useState } from "react";
import "./App.css";
import {
  BOOTSTRAP_STATE_CHANGED_EVENT,
  SETTINGS_CHANGED_EVENT,
  getBootstrapState,
  getSettingsSnapshot,
  listLogs,
  readLog,
  requestHostLaunch,
  resumeDiscovery,
  toSdkRuntimeConnectionInfo,
  writeFrontendLog,
} from "./lib/monitor-api";
import type {
  BootstrapSnapshot,
  LogFileInfo,
  LogKind,
  LogReadResult,
  SettingsSnapshot,
} from "./lib/models";

function App() {
  const [bootstrap, setBootstrap] = useState<BootstrapSnapshot | null>(null);
  const [settings, setSettings] = useState<SettingsSnapshot | null>(null);
  const [hostLogs, setHostLogs] = useState<LogFileInfo[]>([]);
  const [monitorLogs, setMonitorLogs] = useState<LogFileInfo[]>([]);
  const [selectedLog, setSelectedLog] = useState<LogReadResult | null>(null);
  const [activeLogKind, setActiveLogKind] = useState<LogKind>("monitor");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let disposed = false;
    const unlisten: Array<() => void> = [];

    async function initialize() {
      try {
        const [bootstrapState, settingsState, monitorLogFiles, hostLogFiles] = await Promise.all([
          getBootstrapState(),
          getSettingsSnapshot(),
          listLogs("monitor"),
          listLogs("host"),
        ]);

        if (disposed) {
          return;
        }

        setBootstrap(bootstrapState);
        setSettings(settingsState);
        setMonitorLogs(monitorLogFiles);
        setHostLogs(hostLogFiles);

        if (monitorLogFiles[0]) {
          setSelectedLog(await readLog({ kind: "monitor", fileName: monitorLogFiles[0].name }));
        }

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
            });
          },
        );

        unlisten.push(offBootstrap, offSettings);

        void writeFrontendLog({
          level: "info",
          category: "frontend.shell",
          action: "initialize",
          result: "ready",
          message: "Monitor bridge shell mounted.",
        });
      } catch (loadError) {
        setError(toErrorMessage(loadError));
      }
    }

    void initialize();

    return () => {
      disposed = true;
      for (const dispose of unlisten) {
        dispose();
      }
    };
  }, []);

  const sdkConnection = bootstrap?.connection
    ? toSdkRuntimeConnectionInfo(bootstrap.connection)
    : null;
  const visibleLogs = activeLogKind === "monitor" ? monitorLogs : hostLogs;

  async function refreshLogs(kind: LogKind) {
    setError(null);

    try {
      const files = await listLogs(kind);
      if (kind === "monitor") {
        setMonitorLogs(files);
      } else {
        setHostLogs(files);
      }

      if ((!selectedLog || selectedLog.kind !== kind) && files[0]) {
        setSelectedLog(await readLog({ kind, fileName: files[0].name }));
      }
    } catch (refreshError) {
      setError(toErrorMessage(refreshError));
    }
  }

  async function openLog(kind: LogKind, fileName: string) {
    setError(null);
    setActiveLogKind(kind);

    try {
      setSelectedLog(await readLog({ kind, fileName }));
    } catch (readError) {
      setError(toErrorMessage(readError));
    }
  }

  async function handleLaunchHost() {
    setBusy(true);
    setError(null);

    try {
      await requestHostLaunch();
      await Promise.all([refreshLogs("monitor"), refreshLogs("host")]);
    } catch (launchError) {
      setError(toErrorMessage(launchError));
    } finally {
      setBusy(false);
    }
  }

  async function handleResumeDiscovery() {
    setBusy(true);
    setError(null);

    try {
      const snapshot = await resumeDiscovery("frontend_manual_retry");
      setBootstrap(snapshot);
      await refreshLogs("monitor");
    } catch (resumeError) {
      setError(toErrorMessage(resumeError));
    } finally {
      setBusy(false);
    }
  }

  async function handleWriteSampleLog() {
    setBusy(true);
    setError(null);

    try {
      await writeFrontendLog({
        level: "info",
        category: "frontend.shell",
        action: "sample_log",
        result: "recorded",
        message: "User triggered a bridge smoke log.",
        context: {
          activePhase: bootstrap?.phase ?? "unknown",
          dataDir: bootstrap?.effectiveDataDir ?? null,
        },
      });
      await refreshLogs("monitor");
    } catch (logError) {
      setError(toErrorMessage(logError));
    } finally {
      setBusy(false);
    }
  }

  return (
    <main className="app-shell">
      <section className="hero">
        <p className="eyebrow">DevHub Monitor</p>
        <div className="hero-row">
          <div>
            <h1>Tauri bridge workspace</h1>
            <p className="hero-copy">
              当前页面是原生桥接调试面板，用于验证扫描状态机、设置解析、日志接口和
              JS SDK 接入边界。
            </p>
          </div>
          <div className="hero-actions">
            <button type="button" onClick={handleResumeDiscovery} disabled={busy}>
              重新扫描
            </button>
            <button
              type="button"
              onClick={handleLaunchHost}
              disabled={busy || bootstrap?.phase !== "launch_available"}
            >
              启动 DevHub Host
            </button>
            <button type="button" onClick={handleWriteSampleLog} disabled={busy}>
              写入前端日志
            </button>
          </div>
        </div>
      </section>

      <section className="content-grid">
        <article className="panel">
          <header className="panel-header">
            <h2>Bootstrap state</h2>
            <span className={`phase phase-${bootstrap?.phase ?? "loading"}`}>
              {bootstrap?.phase ?? "loading"}
            </span>
          </header>
          <dl className="detail-list">
            <div>
              <dt>Effective data dir</dt>
              <dd>{bootstrap?.effectiveDataDir ?? "加载中"}</dd>
            </div>
            <div>
              <dt>Source</dt>
              <dd>{bootstrap?.dataDirSource ?? "loading"}</dd>
            </div>
            <div>
              <dt>Host executable</dt>
              <dd>{bootstrap?.settings.hostExecutablePath ?? "未配置"}</dd>
            </div>
            <div>
              <dt>Launch entry visible</dt>
              <dd>{bootstrap?.phase === "launch_available" ? "是" : "否"}</dd>
            </div>
          </dl>
          <div className="status-block">
            <h3>Last problem</h3>
            <p>{bootstrap?.lastProblem?.message ?? "当前无错误。"}</p>
          </div>
          <div className="status-block">
            <h3>Validated runtime</h3>
            <p>{sdkConnection?.rpcEndpoint ?? "尚未发现可用 Host。"}</p>
            <p className="subtle">
              {sdkConnection
                ? `PID ${sdkConnection.runtime.pid} / WS ${sdkConnection.websocketEndpoint}`
                : "后续状态页会基于这份连接信息创建 DevHubClient 与 DevHubEventsClient。"}
            </p>
          </div>
        </article>

        <article className="panel">
          <header className="panel-header">
            <h2>Monitor settings</h2>
          </header>
          <dl className="detail-list">
            <div>
              <dt>Settings file</dt>
              <dd>{settings?.settingsFilePath ?? "加载中"}</dd>
            </div>
            <div>
              <dt>Saved DEVHUB_DATA_DIR</dt>
              <dd>{settings?.settings.dataDirOverride ?? "未覆盖"}</dd>
            </div>
            <div>
              <dt>Saved host executable</dt>
              <dd>{settings?.settings.hostExecutablePath ?? "未配置"}</dd>
            </div>
            <div>
              <dt>Monitor log directory</dt>
              <dd>{settings?.monitorLogDirectory ?? "加载中"}</dd>
            </div>
          </dl>
        </article>

        <article className="panel panel-logs">
          <header className="panel-header">
            <h2>Logs bridge</h2>
            <div className="tab-row">
              <button
                type="button"
                className={activeLogKind === "monitor" ? "active" : ""}
                onClick={() => {
                  setActiveLogKind("monitor");
                  void refreshLogs("monitor");
                }}
              >
                Monitor
              </button>
              <button
                type="button"
                className={activeLogKind === "host" ? "active" : ""}
                onClick={() => {
                  setActiveLogKind("host");
                  void refreshLogs("host");
                }}
              >
                Host
              </button>
            </div>
          </header>
          <div className="logs-grid">
            <div className="log-list">
              {visibleLogs.length === 0 ? (
                <p className="subtle">当前没有可读取的日志文件。</p>
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
                      void openLog(file.kind, file.name);
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
                <strong>{selectedLog?.fileName ?? "未选择日志"}</strong>
                <span className="subtle">{selectedLog?.filePath ?? "请选择左侧日志。"}</span>
              </div>
              <pre>{selectedLog?.contents ?? "暂无内容。"}</pre>
            </div>
          </div>
        </article>
      </section>

      {error ? <div className="error-banner">{error}</div> : null}
    </main>
  );
}

function formatBytes(value: number): string {
  if (value < 1024) {
    return `${value} B`;
  }

  if (value < 1024 * 1024) {
    return `${(value / 1024).toFixed(1)} KB`;
  }

  return `${(value / (1024 * 1024)).toFixed(1)} MB`;
}

function toErrorMessage(error: unknown): string {
  if (typeof error === "object" && error && "message" in error && typeof error.message === "string") {
    return error.message;
  }

  return "发生未知错误。";
}

export default App;
