use crate::backend_support::{json_map, problem, record_backend_log};
use crate::launch::HostLaunchService;
use crate::logging::MonitorLogService;
use crate::models::{
    BootstrapPhase, BootstrapSnapshot, MonitorLogLevel, MonitorProblem,
    MonitorRuntimeConnectionInfo, MonitorSettings, ResolvedDataDir,
};
use crate::runtime::{discover_runtime, port_from_runtime, verify_runtime};
use crate::settings::SettingsService;
use crate::snapshot::SnapshotPublisher;
use anyhow::Result;
use serde_json::Value;
use std::path::Path;
use tauri::AppHandle;
use tokio::time::{sleep, Duration, Instant};

const DISCOVERY_INTERVAL: Duration = Duration::from_millis(500);
const LAUNCH_ACTION_DELAY: Duration = Duration::from_secs(3);

#[derive(Clone)]
pub struct DiscoveryCoordinator {
    settings_service: SettingsService,
    snapshot_publisher: SnapshotPublisher,
    launch_service: HostLaunchService,
    log_service: MonitorLogService,
}

impl DiscoveryCoordinator {
    pub fn new(
        settings_service: SettingsService,
        snapshot_publisher: SnapshotPublisher,
        launch_service: HostLaunchService,
        log_service: MonitorLogService,
    ) -> Self {
        Self {
            settings_service,
            snapshot_publisher,
            launch_service,
            log_service,
        }
    }

    pub fn restart(
        &self,
        app: AppHandle,
        reason: &str,
        last_problem: Option<MonitorProblem>,
    ) -> Result<()> {
        if reason != "launch_requested" {
            self.launch_service.finish_launch_attempt();
        }

        let generation = self.snapshot_publisher.advance_generation();
        let settings = self.settings_service.current();
        let resolved = self.settings_service.resolve_effective_data_dir();
        let snapshot = build_snapshot(
            generation,
            BootstrapPhase::Scanning,
            settings,
            resolved.clone(),
            None,
            last_problem.clone(),
        );
        self.snapshot_publisher.publish(&app, snapshot);
        record_backend_log(
            &self.log_service,
            self.snapshot_publisher.current().effective_data_dir,
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
            if let Err(error) = state.run_loop(app, generation).await {
                let _ = record_backend_log(
                    &state.log_service,
                    state.snapshot_publisher.current().effective_data_dir,
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

    async fn run_loop(&self, app: AppHandle, generation: u64) -> Result<()> {
        let started_at = Instant::now();
        let mut announced_launch_action = false;

        loop {
            if !self.snapshot_publisher.is_current_generation(generation) {
                return Ok(());
            }

            let settings = self.settings_service.current();
            let resolved = self.settings_service.resolve_effective_data_dir();

            let discovery_result = match discover_runtime(Path::new(&resolved.path)) {
                Ok(connection) => match verify_runtime(&connection).await {
                    Ok(()) => Ok(connection),
                    Err(error) => Err(error),
                },
                Err(error) => Err(error),
            };

            let last_failure = match discovery_result {
                Ok(connection) => {
                    if !self.snapshot_publisher.is_current_generation(generation) {
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
                    self.snapshot_publisher.publish(&app, snapshot);
                    self.launch_service.finish_launch_attempt();
                    record_backend_log(
                        &self.log_service,
                        self.snapshot_publisher.current().effective_data_dir,
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
                self.snapshot_publisher.publish(&app, snapshot);
                self.launch_service.finish_launch_attempt();
                record_backend_log(
                    &self.log_service,
                    self.snapshot_publisher.current().effective_data_dir,
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
}

pub fn build_snapshot(
    generation: u64,
    phase: BootstrapPhase,
    settings: MonitorSettings,
    resolved: ResolvedDataDir,
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

fn build_launch_available_snapshot(
    generation: u64,
    settings: MonitorSettings,
    resolved: ResolvedDataDir,
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

fn should_transition_to_launch_available(elapsed: Duration, announced_launch_action: bool) -> bool {
    !announced_launch_action && elapsed >= LAUNCH_ACTION_DELAY
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
                hide_host_command_line_window: false,
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
