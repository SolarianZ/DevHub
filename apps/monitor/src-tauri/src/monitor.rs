use crate::discovery::{build_snapshot, DiscoveryCoordinator};
use crate::launch::HostLaunchService;
use crate::logging::{
    open_log_directory as open_log_directory_in_shell, resolve_log_directory, MonitorLogService,
};
use crate::models::{
    BootstrapPhase, BootstrapSnapshot, FrontendLogInput, HostLaunchStatus, LaunchHostResult,
    LogKind, MonitorLogLevel, MonitorProblem, MonitorSettings, MonitorStructuredLogRecord,
    SettingsSnapshot, EVENT_SETTINGS_CHANGED,
};
use crate::settings::SettingsService;
use crate::snapshot::SnapshotPublisher;
use anyhow::Result;
use chrono::Utc;
use serde_json::{Map, Value};
use std::path::PathBuf;
use tauri::{AppHandle, Emitter};

#[derive(Clone)]
pub struct MonitorCore {
    settings_service: SettingsService,
    log_service: MonitorLogService,
    snapshot_publisher: SnapshotPublisher,
    launch_service: HostLaunchService,
    discovery: DiscoveryCoordinator,
}

impl MonitorCore {
    pub fn new() -> Result<Self> {
        let settings_service = SettingsService::new()?;
        let log_service = MonitorLogService::new(settings_service.monitor_log_directory())?;
        let settings = settings_service.current();
        let resolved = settings_service.resolve_effective_data_dir();
        let initial_snapshot =
            build_snapshot(0, BootstrapPhase::Scanning, settings, resolved, None, None);
        let snapshot_publisher = SnapshotPublisher::new(initial_snapshot);
        let launch_service = HostLaunchService::default();
        let discovery = DiscoveryCoordinator::new(
            settings_service.clone(),
            snapshot_publisher.clone(),
            launch_service.clone(),
            log_service.clone(),
        );

        Ok(Self {
            settings_service,
            log_service,
            snapshot_publisher,
            launch_service,
            discovery,
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
        self.discovery.restart(app, "startup", None)?;
        Ok(())
    }

    pub fn should_exit(&self) -> bool {
        self.snapshot_publisher.should_exit()
    }

    pub fn request_exit(&self) {
        self.snapshot_publisher.request_exit();
    }

    pub fn get_bootstrap_state(&self) -> BootstrapSnapshot {
        self.snapshot_publisher.current()
    }

    pub fn get_settings_snapshot(&self) -> SettingsSnapshot {
        self.settings_service.snapshot()
    }

    pub fn save_settings(
        &self,
        app: AppHandle,
        settings: MonitorSettings,
    ) -> Result<SettingsSnapshot> {
        let snapshot = self.settings_service.save(settings)?;
        self.launch_service.finish_launch_attempt();
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
                    snapshot
                        .settings
                        .host_executable_path
                        .clone()
                        .map(Value::String)
                        .unwrap_or(Value::Null),
                ),
                (
                    "hideHostCommandLineWindow",
                    Value::Bool(snapshot.settings.hide_host_command_line_window),
                ),
            ])),
        )?;
        let _ = app.emit(EVENT_SETTINGS_CHANGED, snapshot.clone());
        self.discovery.restart(app, "settings_saved", None)?;
        Ok(snapshot)
    }

    pub fn request_host_launch(&self, app: AppHandle) -> Result<LaunchHostResult> {
        if !self.launch_service.begin_launch() {
            self.snapshot_publisher.update_current(&app, |snapshot| {
                snapshot.last_problem = Some(problem(
                    "launch_in_progress",
                    "当前已有 Host 启动流程在进行中。",
                ));
            });
            self.record_backend_log(
                MonitorLogLevel::Warn,
                "host",
                "launch",
                "launch_in_progress",
                Some("Host launch is already in progress."),
                None,
            )?;
            anyhow::bail!("当前已有 Host 启动流程在进行中。");
        }

        let settings = self.settings_service.current();
        let resolved = self.settings_service.resolve_effective_data_dir();
        let hide_host_command_line_window = settings.hide_host_command_line_window;

        let Some(host_path) = settings.host_executable_path.clone() else {
            self.launch_service.finish_launch_attempt();
            let generation = self.snapshot_publisher.advance_generation();
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
            self.snapshot_publisher.publish(&app, snapshot);
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

        self.discovery
            .restart(app.clone(), "launch_requested", None)?;

        let host_path = PathBuf::from(host_path);
        let pid = match self.launch_service.spawn_host(
            &host_path,
            &resolved.path,
            hide_host_command_line_window,
        ) {
            Ok(pid) => pid,
            Err(error) => {
                self.launch_service.finish_launch_attempt();
                self.record_backend_log(
                    MonitorLogLevel::Error,
                    "host",
                    "launch",
                    "failed",
                    Some(&error.to_string()),
                    Some(json_map(vec![
                        ("dataDir", Value::String(resolved.path.clone())),
                        (
                            "hostExecutablePath",
                            Value::String(host_path.display().to_string()),
                        ),
                        (
                            "hideHostCommandLineWindow",
                            Value::Bool(hide_host_command_line_window),
                        ),
                    ])),
                )?;
                return Err(error);
            }
        };

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
                (
                    "hideHostCommandLineWindow",
                    Value::Bool(hide_host_command_line_window),
                ),
                ("hostPid", Value::from(pid)),
            ])),
        )?;

        Ok(LaunchHostResult {
            status: HostLaunchStatus::Started,
            effective_data_dir: resolved.path,
            data_dir_source: resolved.source,
            pid: Some(pid),
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

        self.launch_service.finish_launch_attempt();
        self.discovery.restart(app, "manual_resume", last_problem)?;
        Ok(self.get_bootstrap_state())
    }

    pub fn open_log_directory(&self, kind: LogKind) -> Result<()> {
        let effective_data_dir =
            PathBuf::from(self.settings_service.resolve_effective_data_dir().path);
        let monitor_log_directory = self.log_service.log_directory().to_path_buf();
        let target_directory =
            resolve_log_directory(kind, &effective_data_dir, &monitor_log_directory);

        match open_log_directory_in_shell(kind, &effective_data_dir, &monitor_log_directory) {
            Ok(opened_directory) => {
                self.record_backend_log(
                    MonitorLogLevel::Info,
                    "support",
                    "open_log_directory",
                    "opened",
                    Some("Opened log directory."),
                    Some(json_map(vec![
                        ("kind", Value::String(kind.as_str().to_string())),
                        (
                            "path",
                            Value::String(opened_directory.display().to_string()),
                        ),
                    ])),
                )?;
                Ok(())
            }
            Err(error) => {
                self.record_backend_log(
                    MonitorLogLevel::Error,
                    "support",
                    "open_log_directory",
                    "failed",
                    Some(&error.to_string()),
                    Some(json_map(vec![
                        ("kind", Value::String(kind.as_str().to_string())),
                        (
                            "path",
                            Value::String(target_directory.display().to_string()),
                        ),
                    ])),
                )?;
                Err(error)
            }
        }
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

    fn record_backend_log(
        &self,
        level: MonitorLogLevel,
        category: &str,
        action: &str,
        result: &str,
        message: Option<&str>,
        context: Option<Map<String, Value>>,
    ) -> Result<()> {
        let effective_data_dir = self.snapshot_publisher.current().effective_data_dir;

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
