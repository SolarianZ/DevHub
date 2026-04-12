use crate::logging::{list_log_files, read_log_file, MonitorLogService};
use crate::models::{
    BootstrapPhase, BootstrapSnapshot, FrontendLogInput, HostLaunchStatus, LaunchHostResult,
    LogFileInfo, LogKind, LogReadResult, MonitorLogLevel, MonitorProblem,
    MonitorRuntimeConnectionInfo, MonitorSettings, MonitorStructuredLogRecord, ReadLogRequest,
    SettingsSnapshot, EVENT_BOOTSTRAP_STATE_CHANGED, EVENT_SETTINGS_CHANGED,
};
use crate::runtime::{discover_runtime, port_from_runtime, verify_runtime};
use crate::settings::{resolve_effective_data_dir, MonitorPaths, SettingsStore};
use anyhow::{Context, Result};
use chrono::Utc;
use serde_json::{Map, Value};
use std::path::{Path, PathBuf};
use std::process::Command;
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::RwLock;
use tauri::{AppHandle, Emitter};
use tokio::time::{sleep, Duration, Instant};

const DISCOVERY_INTERVAL: Duration = Duration::from_millis(500);
const LAUNCH_ACTION_DELAY: Duration = Duration::from_secs(3);

#[derive(Clone)]
pub struct MonitorCore {
    paths: MonitorPaths,
    settings_store: SettingsStore,
    log_service: MonitorLogService,
    snapshot: std::sync::Arc<RwLock<BootstrapSnapshot>>,
    generation: std::sync::Arc<AtomicU64>,
    exit_requested: std::sync::Arc<AtomicBool>,
}

impl MonitorCore {
    pub fn new() -> Result<Self> {
        let paths = MonitorPaths::resolve()?;
        let settings_store = SettingsStore::load(paths.settings_file.clone())?;
        let log_service = MonitorLogService::new(paths.monitor_log_directory.clone())?;
        let settings = settings_store.current();
        let resolved = resolve_effective_data_dir(&settings);
        let initial_snapshot =
            build_snapshot(0, BootstrapPhase::Scanning, settings, resolved, None, None);

        Ok(Self {
            paths,
            settings_store,
            log_service,
            snapshot: std::sync::Arc::new(RwLock::new(initial_snapshot)),
            generation: std::sync::Arc::new(AtomicU64::new(0)),
            exit_requested: std::sync::Arc::new(AtomicBool::new(false)),
        })
    }

    pub fn initialize(&self, app: AppHandle) -> Result<()> {
        self.record_backend_log(
            MonitorLogLevel::Info,
            "lifecycle",
            "startup",
            "ready",
            Some("Monitor backend initialized."),
            None,
        )?;
        self.restart_discovery(app, "startup", None)?;
        Ok(())
    }

    pub fn should_exit(&self) -> bool {
        self.exit_requested.load(Ordering::SeqCst)
    }

    pub fn request_exit(&self) {
        self.exit_requested.store(true, Ordering::SeqCst);
    }

    pub fn get_bootstrap_state(&self) -> BootstrapSnapshot {
        self.snapshot
            .read()
            .expect("snapshot lock poisoned")
            .clone()
    }

    pub fn get_settings_snapshot(&self) -> SettingsSnapshot {
        self.settings_store.snapshot(&self.paths)
    }

    pub fn save_settings(
        &self,
        app: AppHandle,
        settings: MonitorSettings,
    ) -> Result<SettingsSnapshot> {
        let saved = self.settings_store.save(settings)?;
        let snapshot = self.get_settings_snapshot();
        self.record_backend_log(
            MonitorLogLevel::Info,
            "settings",
            "save",
            "saved",
            Some("Monitor settings saved."),
            Some(json_map(vec![
                (
                    "dataDir",
                    Value::String(snapshot.effective_data_dir.clone()),
                ),
                (
                    "hostExecutablePath",
                    saved
                        .host_executable_path
                        .clone()
                        .map(Value::String)
                        .unwrap_or(Value::Null),
                ),
            ])),
        )?;
        let _ = app.emit(EVENT_SETTINGS_CHANGED, snapshot.clone());
        self.restart_discovery(app, "settings_saved", None)?;
        Ok(snapshot)
    }

    pub fn request_host_launch(&self, app: AppHandle) -> Result<LaunchHostResult> {
        let settings = self.settings_store.current();
        let resolved = resolve_effective_data_dir(&settings);

        let Some(host_path) = settings.host_executable_path.clone() else {
            let generation = self.advance_generation();
            let snapshot = build_snapshot(
                generation,
                BootstrapPhase::SettingsRequired,
                settings,
                resolved.clone(),
                None,
                Some(problem(
                    "missing_host_executable",
                    "尚未配置 DevHub Host 可执行文件路径。",
                )),
            );
            self.publish_snapshot(&app, snapshot);
            self.record_backend_log(
                MonitorLogLevel::Warn,
                "host",
                "launch",
                "settings_required",
                Some("Host executable path is missing."),
                Some(json_map(vec![(
                    "dataDir",
                    Value::String(resolved.path.clone()),
                )])),
            )?;

            return Ok(LaunchHostResult {
                status: HostLaunchStatus::SettingsRequired,
                effective_data_dir: resolved.path,
                data_dir_source: resolved.source,
                pid: None,
            });
        };

        let host_path = PathBuf::from(host_path);
        if !host_path.is_file() {
            self.record_backend_log(
                MonitorLogLevel::Error,
                "host",
                "launch",
                "failed",
                Some("Configured Host executable path does not exist."),
                Some(json_map(vec![
                    ("dataDir", Value::String(resolved.path.clone())),
                    (
                        "hostExecutablePath",
                        Value::String(host_path.display().to_string()),
                    ),
                ])),
            )?;
            anyhow::bail!("Host 可执行文件不存在：{}", host_path.display());
        }

        self.restart_discovery(app.clone(), "launch_requested", None)?;

        let child = Command::new(&host_path)
            .env(crate::models::DEVHUB_DATA_DIR_ENV, &resolved.path)
            .spawn()
            .with_context(|| format!("启动 DevHub Host 失败：{}", host_path.display()))?;

        self.record_backend_log(
            MonitorLogLevel::Info,
            "host",
            "launch",
            "started",
            Some("DevHub Host launch requested."),
            Some(json_map(vec![
                ("dataDir", Value::String(resolved.path.clone())),
                (
                    "hostExecutablePath",
                    Value::String(host_path.display().to_string()),
                ),
                ("hostPid", Value::from(child.id())),
            ])),
        )?;

        Ok(LaunchHostResult {
            status: HostLaunchStatus::Started,
            effective_data_dir: resolved.path,
            data_dir_source: resolved.source,
            pid: Some(child.id()),
        })
    }

    pub fn resume_discovery(
        &self,
        app: AppHandle,
        reason: Option<String>,
    ) -> Result<BootstrapSnapshot> {
        let last_problem = reason.as_deref().map(|message| {
            problem(
                "connection_lost",
                format!("连接已失效，恢复扫描：{message}"),
            )
        });

        self.restart_discovery(app, "manual_resume", last_problem)?;
        Ok(self.get_bootstrap_state())
    }

    pub fn list_logs(&self, kind: LogKind) -> Result<Vec<LogFileInfo>> {
        let base_directory = self.log_base_directory(kind);
        list_log_files(&base_directory, kind)
    }

    pub fn read_log(&self, request: ReadLogRequest) -> Result<LogReadResult> {
        let base_directory = self.log_base_directory(request.kind);
        read_log_file(&base_directory, request.kind, &request.file_name)
    }

    pub fn record_frontend_log(&self, entry: FrontendLogInput) -> Result<()> {
        self.log_service.record(MonitorStructuredLogRecord {
            timestamp_utc: Utc::now().to_rfc3339(),
            level: entry.level,
            category: entry.category,
            action: entry.action,
            result: entry.result,
            message: entry.message,
            data_dir: None,
            host_pid: None,
            port: None,
            app_id: None,
            instance_id: None,
            error_code: None,
            context: entry.context,
        })
    }

    fn restart_discovery(
        &self,
        app: AppHandle,
        reason: &str,
        last_problem: Option<MonitorProblem>,
    ) -> Result<()> {
        let generation = self.advance_generation();
        let settings = self.settings_store.current();
        let resolved = resolve_effective_data_dir(&settings);
        let snapshot = build_snapshot(
            generation,
            BootstrapPhase::Scanning,
            settings,
            resolved.clone(),
            None,
            last_problem.clone(),
        );
        self.publish_snapshot(&app, snapshot);
        self.record_backend_log(
            MonitorLogLevel::Info,
            "discovery",
            "scan_start",
            "started",
            Some("Discovery scan started."),
            Some(json_map(vec![
                ("reason", Value::String(reason.to_string())),
                ("dataDir", Value::String(resolved.path.clone())),
            ])),
        )?;

        let state = self.clone();
        tauri::async_runtime::spawn(async move {
            if let Err(error) = state.run_discovery_loop(app, generation).await {
                let _ = state.record_backend_log(
                    MonitorLogLevel::Error,
                    "discovery",
                    "scan_loop",
                    "failed",
                    Some(&error.to_string()),
                    None,
                );
            }
        });

        Ok(())
    }

    async fn run_discovery_loop(&self, app: AppHandle, generation: u64) -> Result<()> {
        let started_at = Instant::now();
        let mut announced_launch_action = false;

        loop {
            if !self.is_current_generation(generation) {
                return Ok(());
            }

            let settings = self.settings_store.current();
            let resolved = resolve_effective_data_dir(&settings);

            let discovery_result = match discover_runtime(Path::new(&resolved.path)) {
                Ok(connection) => match verify_runtime(&connection).await {
                    Ok(()) => Ok(connection),
                    Err(error) => Err(error),
                },
                Err(error) => Err(error),
            };

            let last_failure = match discovery_result {
                Ok(connection) => {
                    if !self.is_current_generation(generation) {
                        return Ok(());
                    }

                    let port = port_from_runtime(&connection);
                    let snapshot = build_snapshot(
                        generation,
                        BootstrapPhase::HostAvailable,
                        settings,
                        resolved.clone(),
                        Some(connection.clone()),
                        None,
                    );
                    self.publish_snapshot(&app, snapshot);
                    self.record_backend_log(
                        MonitorLogLevel::Info,
                        "discovery",
                        "validate_host",
                        "available",
                        Some("A validated DevHub Host is available."),
                        Some(json_map(vec![
                            ("dataDir", Value::String(resolved.path)),
                            ("hostPid", Value::from(connection.runtime.pid)),
                            ("port", port.map(Value::from).unwrap_or(Value::Null)),
                        ])),
                    )?;
                    return Ok(());
                }
                Err(error) => error.to_string(),
            };

            if should_transition_to_launch_available(started_at.elapsed(), announced_launch_action)
            {
                announced_launch_action = true;
                let snapshot = build_launch_available_snapshot(
                    generation,
                    settings,
                    resolved.clone(),
                    last_failure.clone(),
                );
                self.publish_snapshot(&app, snapshot);
                self.record_backend_log(
                    MonitorLogLevel::Warn,
                    "discovery",
                    "launch_action",
                    "available",
                    Some("No validated DevHub Host found within the launch delay window."),
                    Some(json_map(vec![
                        ("dataDir", Value::String(resolved.path.clone())),
                        ("lastFailure", Value::String(last_failure.clone())),
                    ])),
                )?;
            }

            sleep(DISCOVERY_INTERVAL).await;
        }
    }

    fn advance_generation(&self) -> u64 {
        self.generation.fetch_add(1, Ordering::SeqCst) + 1
    }

    fn is_current_generation(&self, generation: u64) -> bool {
        self.generation.load(Ordering::SeqCst) == generation
    }

    fn publish_snapshot(&self, app: &AppHandle, snapshot: BootstrapSnapshot) {
        *self.snapshot.write().expect("snapshot lock poisoned") = snapshot.clone();
        let _ = app.emit(EVENT_BOOTSTRAP_STATE_CHANGED, snapshot);
    }

    fn record_backend_log(
        &self,
        level: MonitorLogLevel,
        category: &str,
        action: &str,
        result: &str,
        message: Option<&str>,
        context: Option<Map<String, Value>>,
    ) -> Result<()> {
        let effective_data_dir = self.get_bootstrap_state().effective_data_dir;

        self.log_service.record(MonitorStructuredLogRecord {
            timestamp_utc: Utc::now().to_rfc3339(),
            level,
            category: category.to_string(),
            action: action.to_string(),
            result: result.to_string(),
            message: message.map(str::to_string),
            data_dir: Some(effective_data_dir),
            host_pid: context
                .as_ref()
                .and_then(|map| map.get("hostPid"))
                .and_then(Value::as_u64)
                .map(|value| value as u32),
            port: context
                .as_ref()
                .and_then(|map| map.get("port"))
                .and_then(Value::as_u64)
                .map(|value| value as u16),
            app_id: context
                .as_ref()
                .and_then(|map| map.get("appId"))
                .and_then(Value::as_str)
                .map(str::to_string),
            instance_id: context
                .as_ref()
                .and_then(|map| map.get("instanceId"))
                .and_then(Value::as_str)
                .map(str::to_string),
            error_code: context
                .as_ref()
                .and_then(|map| map.get("errorCode"))
                .and_then(Value::as_str)
                .map(str::to_string),
            context,
        })
    }

    fn log_base_directory(&self, kind: LogKind) -> PathBuf {
        match kind {
            LogKind::Monitor => self.log_service.log_directory().to_path_buf(),
            LogKind::Host => {
                PathBuf::from(resolve_effective_data_dir(&self.settings_store.current()).path)
                    .join("logs")
            }
        }
    }
}

fn build_snapshot(
    generation: u64,
    phase: BootstrapPhase,
    settings: MonitorSettings,
    resolved: crate::models::ResolvedDataDir,
    connection: Option<MonitorRuntimeConnectionInfo>,
    last_problem: Option<MonitorProblem>,
) -> BootstrapSnapshot {
    BootstrapSnapshot {
        generation,
        phase,
        effective_data_dir: resolved.path,
        data_dir_source: resolved.source,
        has_configured_host_executable: settings.host_executable_path.is_some(),
        settings,
        connection,
        last_problem,
    }
}

fn should_transition_to_launch_available(elapsed: Duration, announced_launch_action: bool) -> bool {
    !announced_launch_action && elapsed >= LAUNCH_ACTION_DELAY
}

fn build_launch_available_snapshot(
    generation: u64,
    settings: MonitorSettings,
    resolved: crate::models::ResolvedDataDir,
    last_failure: String,
) -> BootstrapSnapshot {
    build_snapshot(
        generation,
        BootstrapPhase::LaunchAvailable,
        settings,
        resolved,
        None,
        Some(problem("host_unavailable", last_failure)),
    )
}

fn problem(code: impl Into<String>, message: impl Into<String>) -> MonitorProblem {
    MonitorProblem {
        code: code.into(),
        message: message.into(),
    }
}

fn json_map(entries: Vec<(&str, Value)>) -> Map<String, Value> {
    entries
        .into_iter()
        .map(|(key, value)| (key.to_string(), value))
        .collect()
}

#[cfg(test)]
mod tests {
    use super::{
        build_launch_available_snapshot, should_transition_to_launch_available, LAUNCH_ACTION_DELAY,
    };
    use crate::models::{BootstrapPhase, DataDirSource, MonitorSettings, ResolvedDataDir};
    use std::time::Duration;

    #[test]
    fn launch_action_becomes_available_after_delay() {
        assert!(!should_transition_to_launch_available(
            LAUNCH_ACTION_DELAY.saturating_sub(Duration::from_millis(1)),
            false
        ));
        assert!(should_transition_to_launch_available(
            LAUNCH_ACTION_DELAY,
            false
        ));
        assert!(!should_transition_to_launch_available(
            LAUNCH_ACTION_DELAY + Duration::from_secs(1),
            true
        ));
    }

    #[test]
    fn launch_available_snapshot_carries_failure_context() {
        let snapshot = build_launch_available_snapshot(
            7,
            MonitorSettings {
                data_dir_override: Some("/tmp/devhub".to_string()),
                host_executable_path: None,
            },
            ResolvedDataDir {
                path: "/tmp/devhub".to_string(),
                source: DataDirSource::SettingsOverride,
            },
            "hub.ping failed".to_string(),
        );

        assert!(matches!(snapshot.phase, BootstrapPhase::LaunchAvailable));
        assert_eq!(snapshot.generation, 7);
        assert_eq!(snapshot.effective_data_dir, "/tmp/devhub");
        let problem = snapshot.last_problem.expect("expected last problem");
        assert_eq!(problem.code, "host_unavailable");
        assert_eq!(problem.message, "hub.ping failed");
    }
}
