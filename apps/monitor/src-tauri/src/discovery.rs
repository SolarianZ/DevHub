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
use crate::versioning::{
    create_version_compatibility_result, VersionCompatibilityResult, VersionCompatibilityStatus,
    MONITOR_VERSION, SDK_VERSION,
};
use anyhow::{Context, Result};
use reqwest::Client;
use serde_json::Value;
use std::path::Path;
use tauri::AppHandle;
use tokio::time::{sleep, Duration, Instant};

const DISCOVERY_INTERVAL: Duration = Duration::from_millis(500);
const LAUNCH_ACTION_DELAY: Duration = Duration::from_secs(3);
const UNSUPPORTED_PROTOCOL_PROBE_TIMEOUT: Duration = Duration::from_millis(1500);

#[derive(Debug, Clone)]
enum RuntimeDiscoverySuccess {
    HostAvailable {
        connection: MonitorRuntimeConnectionInfo,
        compatibility: RuntimeCompatibilityAssessment,
    },
    HostIncompatible {
        connection: MonitorRuntimeConnectionInfo,
        compatibility: RuntimeCompatibilityAssessment,
    },
}

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
                Ok(connection) => {
                    match assess_discovered_runtime_before_verification(&connection) {
                        Some(compatibility) => {
                            match probe_unsupported_protocol_runtime(&connection).await {
                                Ok(()) => Ok(RuntimeDiscoverySuccess::HostIncompatible {
                                    connection,
                                    compatibility,
                                }),
                                Err(error) => Err(error),
                            }
                        }
                        None => match verify_runtime(&connection).await {
                            Ok(verification) => {
                                let compatibility = assess_runtime_compatibility_with_host_version(
                                    &connection,
                                    verification.host_version.as_deref(),
                                );

                                if compatibility.problem.is_some() {
                                    Ok(RuntimeDiscoverySuccess::HostIncompatible {
                                        connection,
                                        compatibility,
                                    })
                                } else {
                                    Ok(RuntimeDiscoverySuccess::HostAvailable {
                                        connection,
                                        compatibility,
                                    })
                                }
                            }
                            Err(error) => Err(error),
                        },
                    }
                }
                Err(error) => Err(error),
            };

            match discovery_result {
                Ok(RuntimeDiscoverySuccess::HostIncompatible {
                    connection,
                    compatibility,
                }) => {
                    if !self.snapshot_publisher.is_current_generation(generation) {
                        return Ok(());
                    }

                    if !self.publish_incompatible_runtime(
                        &app,
                        generation,
                        settings,
                        resolved,
                        &connection,
                        &compatibility,
                        &mut last_incompatible_message,
                    )? {
                        return Ok(());
                    }

                    sleep(DISCOVERY_INTERVAL).await;
                    continue;
                }
                Ok(RuntimeDiscoverySuccess::HostAvailable {
                    connection,
                    compatibility,
                }) => {
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
                            (
                                "hubVersion",
                                compatibility
                                    .result
                                    .host_version
                                    .clone()
                                    .map(Value::String)
                                    .unwrap_or(Value::Null),
                            ),
                            (
                                "compatibilityStatus",
                                Value::String(compatibility.result.status.as_str().to_string()),
                            ),
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

    fn publish_incompatible_runtime(
        &self,
        app: &AppHandle,
        generation: u64,
        settings: MonitorSettings,
        resolved: ResolvedDataDir,
        connection: &MonitorRuntimeConnectionInfo,
        compatibility: &RuntimeCompatibilityAssessment,
        last_incompatible_message: &mut Option<String>,
    ) -> Result<bool> {
        let Some(incompatible_problem) = compatibility.problem.as_ref() else {
            return Ok(true);
        };

        let incompatible_message = incompatible_problem.message.clone();
        if last_incompatible_message.as_deref() == Some(incompatible_message.as_str()) {
            return Ok(true);
        }

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
            .publish_if_current(app, generation, snapshot)
        {
            return Ok(false);
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
                ("dataDir", Value::String(resolved.path)),
                (
                    "protocolVersion",
                    Value::from(connection.runtime.protocol_version),
                ),
                (
                    "hubVersion",
                    compatibility
                        .result
                        .host_version
                        .clone()
                        .map(Value::String)
                        .unwrap_or(Value::Null),
                ),
                (
                    "compatibilityStatus",
                    Value::String(compatibility.result.status.as_str().to_string()),
                ),
            ])),
        )?;
        *last_incompatible_message = Some(incompatible_message);
        Ok(true)
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

async fn probe_unsupported_protocol_runtime(
    connection: &MonitorRuntimeConnectionInfo,
) -> Result<()> {
    probe_unsupported_protocol_runtime_with_client(
        connection,
        &build_unsupported_protocol_probe_client()?,
    )
    .await
}

fn build_unsupported_protocol_probe_client() -> Result<Client> {
    Client::builder()
        .timeout(UNSUPPORTED_PROTOCOL_PROBE_TIMEOUT)
        .build()
        .context("无法创建不兼容 Host 活性探测 HTTP 客户端。")
}

async fn probe_unsupported_protocol_runtime_with_client(
    connection: &MonitorRuntimeConnectionInfo,
    client: &Client,
) -> Result<()> {
    client
        .post(&connection.rpc_endpoint)
        .header("Content-Type", "application/json")
        .body(r#"{"jsonrpc":"2.0","id":"monitor-probe","method":"hub.ping","params":{}}"#)
        .send()
        .await
        .map(|_| ())
        .with_context(|| format!("不兼容 Host 活性探测失败：{}", connection.rpc_endpoint))
}

#[derive(Debug, Clone)]
struct RuntimeCompatibilityAssessment {
    result: VersionCompatibilityResult,
    problem: Option<MonitorProblem>,
}

#[cfg(test)]
fn assess_runtime_compatibility(
    connection: &MonitorRuntimeConnectionInfo,
) -> RuntimeCompatibilityAssessment {
    assess_runtime_compatibility_with_host_version(
        connection,
        connection.runtime.hub_version.as_deref(),
    )
}

fn assess_discovered_runtime_before_verification(
    connection: &MonitorRuntimeConnectionInfo,
) -> Option<RuntimeCompatibilityAssessment> {
    if connection.runtime.protocol_version == 1 {
        return None;
    }

    let compatibility = assess_runtime_compatibility_with_host_version(
        connection,
        connection.runtime.hub_version.as_deref(),
    );
    Some(compatibility)
}

fn assess_runtime_compatibility_with_host_version(
    connection: &MonitorRuntimeConnectionInfo,
    host_version: Option<&str>,
) -> RuntimeCompatibilityAssessment {
    if connection.runtime.protocol_version != 1 {
        return RuntimeCompatibilityAssessment {
            result: VersionCompatibilityResult {
                sdk_version: SDK_VERSION.to_string(),
                host_version: host_version.map(str::to_string),
                status: VersionCompatibilityStatus::Incompatible,
            },
            problem: Some(problem(
                "host_incompatible",
                format!(
                    "当前 Monitor 仅支持 protocolVersion=1 的 DevHub Host。检测到 protocolVersion={}。",
                    connection.runtime.protocol_version
                ),
            )),
        };
    }

    let result = create_version_compatibility_result(host_version);
    let problem = if matches!(result.status, VersionCompatibilityStatus::Incompatible) {
        Some(problem(
            "host_incompatible",
            format!(
                "当前 Monitor v{} 内置的 JS SDK 与 DevHub Host 版本不兼容。JS SDK={}; Host={}。",
                MONITOR_VERSION,
                result.sdk_version,
                result.host_version.as_deref().unwrap_or("未知")
            ),
        ))
    } else {
        None
    };

    RuntimeCompatibilityAssessment { result, problem }
}

#[cfg(test)]
mod tests {
    use super::{
        assess_discovered_runtime_before_verification, assess_runtime_compatibility,
        build_launch_available_snapshot, probe_unsupported_protocol_runtime_with_client,
        should_transition_to_launch_available, LAUNCH_ACTION_DELAY,
    };
    use crate::models::{
        BootstrapPhase, DataDirSource, MonitorRuntimeConnectionInfo, MonitorRuntimeTuning,
        MonitorSettings, ResolvedDataDir,
    };
    use crate::versioning::VersionCompatibilityStatus;
    use std::io::{Read, Write};
    use std::net::TcpListener;
    use std::sync::mpsc;
    use std::thread;
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

    fn create_connection(
        protocol_version: u32,
        hub_version: Option<&str>,
    ) -> MonitorRuntimeConnectionInfo {
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
                    launch_register_timeout_seconds: 45,
                },
                hub_version: hub_version.map(str::to_string),
            },
        }
    }

    #[test]
    fn compatibility_check_rejects_unsupported_protocol_or_major_mismatch() {
        let unsupported_protocol =
            assess_runtime_compatibility(&create_connection(2, Some("0.7.0")))
                .problem
                .expect("expected protocol mismatch");
        assert!(unsupported_protocol.message.contains("protocolVersion=2"));

        let unsupported_hub = assess_runtime_compatibility(&create_connection(1, Some("1.0.0")))
            .problem
            .expect("expected incompatible hub version");
        assert!(unsupported_hub.message.contains("JS SDK="));
        assert!(unsupported_hub.message.contains("Host=1.0.0"));
    }

    #[test]
    fn compatibility_precheck_rejects_unsupported_protocol_before_verification() {
        let compatibility =
            assess_discovered_runtime_before_verification(&create_connection(2, Some("0.7.0")))
                .expect("expected protocol mismatch");

        let problem = compatibility
            .problem
            .expect("expected incompatible problem");
        assert_eq!(problem.code, "host_incompatible");
        assert!(problem.message.contains("protocolVersion=2"));
        assert_eq!(
            compatibility.result.status,
            VersionCompatibilityStatus::Incompatible
        );
    }

    #[test]
    fn compatibility_precheck_allows_supported_protocol_to_continue_verification() {
        let compatibility =
            assess_discovered_runtime_before_verification(&create_connection(1, Some("1.0.0")));

        assert!(compatibility.is_none());
    }

    #[test]
    fn unsupported_protocol_probe_succeeds_when_endpoint_responds() {
        let (url, server) = spawn_probe_server();
        let mut connection = create_connection(2, Some("0.7.0"));
        connection.runtime.http_base_url = url;
        connection.rpc_endpoint = format!("{}/rpc", connection.runtime.http_base_url);

        let client = reqwest::Client::builder()
            .timeout(Duration::from_secs(2))
            .build()
            .expect("failed to build client");

        tauri::async_runtime::block_on(probe_unsupported_protocol_runtime_with_client(
            &connection,
            &client,
        ))
        .expect("expected active endpoint");
        server.join().expect("probe server failed");
    }

    #[test]
    fn unsupported_protocol_probe_fails_when_endpoint_is_unreachable() {
        let listener = TcpListener::bind("127.0.0.1:0").expect("failed to bind listener");
        let port = listener
            .local_addr()
            .expect("failed to read local address")
            .port();
        drop(listener);

        let mut connection = create_connection(2, Some("0.7.0"));
        connection.runtime.http_base_url = format!("http://127.0.0.1:{port}");
        connection.rpc_endpoint = format!("{}/rpc", connection.runtime.http_base_url);
        let client = reqwest::Client::builder()
            .timeout(Duration::from_millis(250))
            .build()
            .expect("failed to build client");

        let error = tauri::async_runtime::block_on(probe_unsupported_protocol_runtime_with_client(
            &connection,
            &client,
        ))
        .expect_err("expected inactive endpoint");

        assert!(error.to_string().contains("不兼容 Host 活性探测失败"));
    }

    #[test]
    fn compatibility_check_allows_unknown_and_update_recommended_hosts() {
        let unknown = assess_runtime_compatibility(&create_connection(1, None));
        assert!(unknown.problem.is_none());
        assert_eq!(unknown.result.status, VersionCompatibilityStatus::Unknown);

        let update_recommended = assess_runtime_compatibility(&create_connection(1, Some("0.8.1")));
        assert!(update_recommended.problem.is_none());
        assert_eq!(
            update_recommended.result.status,
            VersionCompatibilityStatus::UpdateRecommended
        );

        let compatible = assess_runtime_compatibility(&create_connection(1, Some("0.7.0-rc.1")));
        assert!(compatible.problem.is_none());
        assert_eq!(
            compatible.result.status,
            VersionCompatibilityStatus::Compatible
        );
    }

    fn spawn_probe_server() -> (String, thread::JoinHandle<()>) {
        let listener = TcpListener::bind("127.0.0.1:0").expect("failed to bind listener");
        let address = listener.local_addr().expect("failed to read local address");
        let (ready_sender, ready_receiver) = mpsc::channel();
        let handle = thread::spawn(move || {
            ready_sender.send(()).expect("failed to notify readiness");
            let (mut stream, _) = listener.accept().expect("failed to accept connection");
            let mut buffer = [0_u8; 1024];
            let _ = stream.read(&mut buffer);
            stream
                .write_all(b"HTTP/1.1 400 Bad Request\r\nContent-Length: 0\r\n\r\n")
                .expect("failed to write response");
        });
        ready_receiver.recv().expect("probe server did not start");

        (format!("http://{}", address), handle)
    }
}
