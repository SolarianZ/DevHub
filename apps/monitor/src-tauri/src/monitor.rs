use crate::backend_support::{json_map, problem, record_backend_log};
use crate::discovery::{build_snapshot, DiscoveryCoordinator};
use crate::launch::{HostLaunchAttemptStatus, HostLaunchService};
use crate::logging::{
    open_log_directory as open_log_directory_in_shell, resolve_log_directory, MonitorLogService,
};
use crate::models::{
    BootstrapPhase, BootstrapSnapshot, FrontendLogInput, HostLaunchStatus, LaunchHostResult,
    LogKind, MonitorLogLevel, MonitorSettings, MonitorStructuredLogRecord, SettingsSnapshot,
    EVENT_SETTINGS_CHANGED,
};
use crate::settings::SettingsService;
use crate::snapshot::SnapshotPublisher;
use anyhow::Result;
use chrono::Utc;
use serde_json::Value;
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
        let settings_snapshot = self.settings_service.snapshot();
        if let Some(load_warning) = settings_snapshot.load_warning.as_ref() {
            record_backend_log(
                &self.log_service,
                settings_snapshot.effective_data_dir.clone(),
                MonitorLogLevel::Warn,
                "settings",
                "load",
                "recovered",
                Some(&load_warning.message),
                Some(json_map(vec![
                    (
                        "settingsFilePath",
                        Value::String(load_warning.settings_file_path.clone()),
                    ),
                    (
                        "backupFilePath",
                        load_warning
                            .backup_file_path
                            .clone()
                            .map(Value::String)
                            .unwrap_or(Value::Null),
                    ),
                    ("settingsRevision", Value::from(settings_snapshot.revision)),
                ])),
            )?;
        }

        record_backend_log(
            &self.log_service,
            self.snapshot_publisher.current().effective_data_dir,
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
        record_backend_log(
            &self.log_service,
            self.snapshot_publisher.current().effective_data_dir,
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
                ("settingsRevision", Value::from(snapshot.revision)),
                (
                    "settingsLoadWarning",
                    snapshot
                        .load_warning
                        .as_ref()
                        .map(|warning| Value::String(warning.code.clone()))
                        .unwrap_or(Value::Null),
                ),
            ])),
        )?;
        let _ = app.emit(EVENT_SETTINGS_CHANGED, snapshot.clone());
        self.discovery.restart(app, "settings_saved", None)?;
        Ok(snapshot)
    }

    pub fn request_host_launch(&self, app: AppHandle) -> Result<LaunchHostResult> {
        let settings = self.settings_service.current();
        let resolved = self.settings_service.resolve_effective_data_dir();
        let hide_host_command_line_window = settings.hide_host_command_line_window;

        let Some(host_path) = settings.host_executable_path.clone() else {
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
            record_backend_log(
                &self.log_service,
                self.snapshot_publisher.current().effective_data_dir,
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

        if !self.launch_service.begin_launch(&resolved.path) {
            self.snapshot_publisher.update_current(&app, |snapshot| {
                snapshot.last_problem = Some(problem(
                    "launch_in_progress",
                    "当前已有 Host 启动流程在进行中。",
                ));
            });
            record_backend_log(
                &self.log_service,
                self.snapshot_publisher.current().effective_data_dir,
                MonitorLogLevel::Warn,
                "host",
                "launch",
                "launch_in_progress",
                Some("Host launch is already in progress."),
                Some(json_map(vec![(
                    "dataDir",
                    Value::String(resolved.path.clone()),
                )])),
            )?;
            anyhow::bail!("当前已有 Host 启动流程在进行中。");
        }

        let generation = match self
            .discovery
            .restart(app.clone(), "launch_requested", None)
        {
            Ok(generation) => generation,
            Err(error) => {
                self.launch_service
                    .finish_launch_attempt(&resolved.path, HostLaunchAttemptStatus::SpawnFailed);
                return Err(error);
            }
        };
        self.launch_service
            .assign_generation(&resolved.path, generation);

        let host_path = PathBuf::from(host_path);
        let pid = match self.launch_service.spawn_host(
            &host_path,
            &resolved.path,
            hide_host_command_line_window,
        ) {
            Ok(pid) => pid,
            Err(error) => {
                self.launch_service
                    .finish_launch_attempt(&resolved.path, HostLaunchAttemptStatus::SpawnFailed);
                record_backend_log(
                    &self.log_service,
                    self.snapshot_publisher.current().effective_data_dir,
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

        record_backend_log(
            &self.log_service,
            self.snapshot_publisher.current().effective_data_dir,
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
                record_backend_log(
                    &self.log_service,
                    self.snapshot_publisher.current().effective_data_dir,
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
                record_backend_log(
                    &self.log_service,
                    self.snapshot_publisher.current().effective_data_dir,
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
}
