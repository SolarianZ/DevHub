use crate::backend_support::{json_map, problem, record_backend_log};
use crate::launch::{HostLaunchAttemptStatus, HostLaunchService};
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
    ) -> Result<u64> {
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

        Ok(generation)
    }

    async fn run_loop(&self, app: AppHandle, generation: u64) -> Result<()> {
        let started_at = Instant::now();
        let mut announced_launch_action = false;
        let mut last_incompatible_message: Option<String> = None;

        loop {
            if !self.snapshot_publisher.is_current_generation(generation) {
                return Ok(());
            }

            for timed_out_attempt in self.launch_service.take_timed_out_attempts() {
                record_backend_log(
                    &self.log_service,
                    timed_out_attempt.data_dir.clone(),
                    MonitorLogLevel::Warn,
                    "host",
                    "launch",
                    "timed_out",
                    Some("DevHub Host launch attempt timed out."),
                    Some(json_map(vec![
                        ("dataDir", Value::String(timed_out_attempt.data_dir.clone())),
                        (
                            "status",
                            Value::String(timed_out_attempt.status.as_str().to_string()),
                        ),
                        (
                            "requestedGeneration",
                            timed_out_attempt
                                .requested_by_generation
                                .map(Value::from)
                                .unwrap_or(Value::Null),
                        ),
                    ])),
                )?;
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

            match discovery_result {
                Ok(connection) => {
                    if !self.snapshot_publisher.is_current_generation(generation) {
                        return Ok(());
                    }

                    if let Some(incompatible_problem) = assess_runtime_compatibility(&connection) {
                        let incompatible_message = incompatible_problem.message.clone();
                        if last_incompatible_message.as_deref()
                            != Some(incompatible_message.as_str())
                        {
                            let snapshot = build_snapshot(
                                generation,
                                BootstrapPhase::HostIncompatible,
                                settings,
                                resolved.clone(),
                                None,
                                Some(incompatible_problem.clone()),
                            );
                            if !self
                                .snapshot_publisher
                                .publish_if_current(&app, generation, snapshot)
                            {
                                return Ok(());
                            }

                            record_backend_log(
                                &self.log_service,
                                resolved.path.clone(),
                                MonitorLogLevel::Warn,
                                "discovery",
                                "validate_host",
                                "incompatible",
                                Some(&incompatible_problem.message),
                                Some(json_map(vec![
                                    ("dataDir", Value::String(resolved.path.clone())),
                                    (
                                        "protocolVersion",
                                        Value::from(connection.runtime.protocol_version),
                                    ),
                                    (
                                        "hubVersion",
                                        connection
                                            .runtime
                                            .hub_version
                                            .clone()
                                            .map(Value::String)
                                            .unwrap_or(Value::Null),
                                    ),
                                ])),
                            )?;
                            last_incompatible_message = Some(incompatible_message);
                        }

                        sleep(DISCOVERY_INTERVAL).await;
                        continue;
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
                    if !self
                        .snapshot_publisher
                        .publish_if_current(&app, generation, snapshot)
                    {
                        return Ok(());
                    }

                    self.launch_service.finish_launch_attempt(
                        &resolved.path,
                        HostLaunchAttemptStatus::HostAvailable,
                    );
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
                Err(error) => {
                    last_incompatible_message = None;
                    let last_failure = error.to_string();

                    if should_transition_to_launch_available(
                        started_at.elapsed(),
                        announced_launch_action,
                    ) {
                        announced_launch_action = true;
                        let snapshot = build_launch_available_snapshot(
                            generation,
                            settings,
                            resolved.clone(),
                            last_failure.clone(),
                        );
                        if !self
                            .snapshot_publisher
                            .publish_if_current(&app, generation, snapshot)
                        {
                            return Ok(());
                        }
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
                    } else if matches!(
                        self.snapshot_publisher.current().phase,
                        BootstrapPhase::HostIncompatible
                    ) {
                        let snapshot = build_snapshot(
                            generation,
                            BootstrapPhase::Scanning,
                            settings,
                            resolved.clone(),
                            None,
                            None,
                        );
                        if !self
                            .snapshot_publisher
                            .publish_if_current(&app, generation, snapshot)
                        {
                            return Ok(());
                        }
                    }

                    sleep(DISCOVERY_INTERVAL).await;
                    continue;
                }
            }
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

fn assess_runtime_compatibility(
    connection: &MonitorRuntimeConnectionInfo,
) -> Option<MonitorProblem> {
    if connection.runtime.protocol_version != 1 {
        return Some(problem(
            "host_incompatible",
            format!(
                "当前 Monitor 仅支持 protocolVersion=1 且 hubVersion >= 0.7.0 的 DevHub Host。检测到 protocolVersion={}。",
                connection.runtime.protocol_version
            ),
        ));
    }

    let Some(hub_version) = connection.runtime.hub_version.as_deref() else {
        return Some(problem(
            "host_incompatible",
            "当前 Monitor 仅支持 protocolVersion=1 且 hubVersion >= 0.7.0 的 DevHub Host。当前 Host 缺少可解析的 hubVersion。",
        ));
    };

    let Some(parsed_hub_version) = parse_semantic_version(hub_version) else {
        return Some(problem(
            "host_incompatible",
            format!(
                "当前 Monitor 仅支持 protocolVersion=1 且 hubVersion >= 0.7.0 的 DevHub Host。检测到不可解析的 hubVersion={hub_version}。"
            ),
        ));
    };

    let minimum_supported = SemanticVersion {
        major: 0,
        minor: 7,
        patch: 0,
        prerelease: Vec::new(),
    };
    if compare_semantic_versions(&parsed_hub_version, &minimum_supported) < 0 {
        return Some(problem(
            "host_incompatible",
            format!(
                "当前 Monitor 仅支持 protocolVersion=1 且 hubVersion >= 0.7.0 的 DevHub Host。检测到 hubVersion={hub_version}。"
            ),
        ));
    }

    None
}

#[derive(Debug, Clone, PartialEq, Eq)]
struct SemanticVersion {
    major: u32,
    minor: u32,
    patch: u32,
    prerelease: Vec<SemanticVersionIdentifier>,
}

#[derive(Debug, Clone, PartialEq, Eq)]
enum SemanticVersionIdentifier {
    Numeric(u32),
    Text(String),
}

fn parse_semantic_version(value: &str) -> Option<SemanticVersion> {
    let trimmed = value.trim();
    if trimmed.is_empty() {
        return None;
    }

    let without_build = trimmed.split_once('+').map(|(head, _)| head).unwrap_or(trimmed);
    let (core, prerelease) = without_build
        .split_once('-')
        .map(|(head, tail)| (head, Some(tail)))
        .unwrap_or((without_build, None));

    let mut parts = core.split('.');
    let major = parts.next()?.parse::<u32>().ok()?;
    let minor = parts.next()?.parse::<u32>().ok()?;
    let patch = parts.next()?.parse::<u32>().ok()?;
    if parts.next().is_some() {
        return None;
    }

    let prerelease = match prerelease {
        Some(value) => value
            .split('.')
            .map(|identifier| {
                if identifier.is_empty() {
                    return None;
                }

                Some(
                    identifier
                        .parse::<u32>()
                        .map(SemanticVersionIdentifier::Numeric)
                        .unwrap_or_else(|_| SemanticVersionIdentifier::Text(identifier.to_string())),
                )
            })
            .collect::<Option<Vec<_>>>()?,
        None => Vec::new(),
    };

    Some(SemanticVersion {
        major,
        minor,
        patch,
        prerelease,
    })
}

fn compare_semantic_versions(left: &SemanticVersion, right: &SemanticVersion) -> i32 {
    match left.major.cmp(&right.major) {
        std::cmp::Ordering::Less => return -1,
        std::cmp::Ordering::Greater => return 1,
        std::cmp::Ordering::Equal => {}
    }

    match left.minor.cmp(&right.minor) {
        std::cmp::Ordering::Less => return -1,
        std::cmp::Ordering::Greater => return 1,
        std::cmp::Ordering::Equal => {}
    }

    match left.patch.cmp(&right.patch) {
        std::cmp::Ordering::Less => return -1,
        std::cmp::Ordering::Greater => return 1,
        std::cmp::Ordering::Equal => {}
    }

    if left.prerelease.is_empty() && right.prerelease.is_empty() {
        return 0;
    }

    if left.prerelease.is_empty() {
        return 1;
    }

    if right.prerelease.is_empty() {
        return -1;
    }

    let max_length = left.prerelease.len().max(right.prerelease.len());
    for index in 0..max_length {
        let left_identifier = left.prerelease.get(index);
        let right_identifier = right.prerelease.get(index);

        match (left_identifier, right_identifier) {
            (None, Some(_)) => return -1,
            (Some(_), None) => return 1,
            (None, None) => return 0,
            (Some(SemanticVersionIdentifier::Numeric(left_value)), Some(SemanticVersionIdentifier::Numeric(right_value))) => {
                match left_value.cmp(right_value) {
                    std::cmp::Ordering::Less => return -1,
                    std::cmp::Ordering::Greater => return 1,
                    std::cmp::Ordering::Equal => {}
                }
            }
            (Some(SemanticVersionIdentifier::Numeric(_)), Some(SemanticVersionIdentifier::Text(_))) => return -1,
            (Some(SemanticVersionIdentifier::Text(_)), Some(SemanticVersionIdentifier::Numeric(_))) => return 1,
            (Some(SemanticVersionIdentifier::Text(left_value)), Some(SemanticVersionIdentifier::Text(right_value))) => {
                match left_value.cmp(right_value) {
                    std::cmp::Ordering::Less => return -1,
                    std::cmp::Ordering::Greater => return 1,
                    std::cmp::Ordering::Equal => {}
                }
            }
        }
    }

    0
}

#[cfg(test)]
mod tests {
    use super::{
        assess_runtime_compatibility, build_launch_available_snapshot, compare_semantic_versions,
        parse_semantic_version, should_transition_to_launch_available, LAUNCH_ACTION_DELAY,
    };
    use crate::models::{
        BootstrapPhase, DataDirSource, MonitorRuntimeConnectionInfo, MonitorRuntimeTuning,
        MonitorSettings, ResolvedDataDir,
    };
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

    fn create_connection(protocol_version: u32, hub_version: Option<&str>) -> MonitorRuntimeConnectionInfo {
        MonitorRuntimeConnectionInfo {
            runtime_directory: "/tmp/devhub/runtime".to_string(),
            token: "secret".to_string(),
            rpc_endpoint: "http://127.0.0.1:4123/rpc".to_string(),
            websocket_endpoint: "ws://127.0.0.1:4123/ws".to_string(),
            runtime: crate::models::MonitorHubRuntime {
                protocol_version,
                pid: 4321,
                http_base_url: "http://127.0.0.1:4123".to_string(),
                ws_url: "ws://127.0.0.1:4123/ws".to_string(),
                token_file: "/tmp/devhub/runtime/token.txt".to_string(),
                started_at_utc: "2026-04-12T00:00:00Z".to_string(),
                runtime_tuning: MonitorRuntimeTuning {
                    lease_seconds: 30,
                    online_threshold_seconds: 15,
                    launch_dedupe_window_seconds: 5,
                },
                hub_version: hub_version.map(str::to_string),
            },
        }
    }

    #[test]
    fn compatibility_check_rejects_unsupported_protocol_or_hub_version() {
        let unsupported_protocol = assess_runtime_compatibility(&create_connection(2, Some("0.7.0")))
            .expect("expected protocol mismatch");
        assert!(unsupported_protocol.message.contains("protocolVersion=2"));

        let unsupported_hub = assess_runtime_compatibility(&create_connection(1, Some("0.6.9")))
            .expect("expected unsupported hub version");
        assert!(unsupported_hub.message.contains("hubVersion=0.6.9"));

        let missing_hub = assess_runtime_compatibility(&create_connection(1, None))
            .expect("expected missing hub version");
        assert!(missing_hub.message.contains("hubVersion"));
    }

    #[test]
    fn compatibility_check_accepts_supported_runtime() {
        assert!(assess_runtime_compatibility(&create_connection(1, Some("0.7.0"))).is_none());
        assert!(assess_runtime_compatibility(&create_connection(1, Some("0.7.0-rc.1"))).is_some());
    }

    #[test]
    fn semantic_version_parser_supports_prerelease_comparison() {
        let release = parse_semantic_version("0.7.0").expect("expected release version");
        let prerelease =
            parse_semantic_version("0.7.0-rc.1").expect("expected prerelease version");
        let newer = parse_semantic_version("0.7.1").expect("expected newer version");

        assert_eq!(compare_semantic_versions(&release, &prerelease), 1);
        assert_eq!(compare_semantic_versions(&prerelease, &release), -1);
        assert_eq!(compare_semantic_versions(&newer, &release), 1);
        assert!(parse_semantic_version("0.7").is_none());
    }
}
