use serde::{Deserialize, Serialize};
use serde_json::{Map, Value};

pub const EVENT_BOOTSTRAP_STATE_CHANGED: &str = "devhub://bootstrap-state-changed";
pub const EVENT_SETTINGS_CHANGED: &str = "devhub://settings-changed";
pub const DEVHUB_DATA_DIR_ENV: &str = "DEVHUB_DATA_DIR";

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct MonitorSettings {
    pub data_dir_override: Option<String>,
    pub host_executable_path: Option<String>,
    #[serde(default = "default_hide_host_command_line_window")]
    pub hide_host_command_line_window: bool,
}

impl Default for MonitorSettings {
    fn default() -> Self {
        Self {
            data_dir_override: None,
            host_executable_path: None,
            hide_host_command_line_window: default_hide_host_command_line_window(),
        }
    }
}

fn default_hide_host_command_line_window() -> bool {
    true
}

#[derive(Debug, Clone, Copy, Serialize)]
#[serde(rename_all = "snake_case")]
#[allow(dead_code)]
pub enum MonitorPlatform {
    Windows,
    Macos,
    Linux,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "snake_case")]
pub enum DataDirSource {
    SettingsOverride,
    Environment,
    PlatformDefault,
}

#[derive(Debug, Clone)]
pub struct ResolvedDataDir {
    pub path: String,
    pub source: DataDirSource,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct SettingsSnapshot {
    pub revision: u64,
    pub settings: MonitorSettings,
    pub platform: MonitorPlatform,
    pub effective_data_dir: String,
    pub data_dir_source: DataDirSource,
    pub settings_file_path: String,
    pub monitor_log_directory: String,
    pub load_warning: Option<SettingsLoadWarning>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct SettingsLoadWarning {
    pub code: String,
    pub message: String,
    pub settings_file_path: String,
    pub backup_file_path: Option<String>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct MonitorProblem {
    pub code: String,
    pub message: String,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "snake_case")]
pub enum BootstrapPhase {
    Scanning,
    LaunchAvailable,
    SettingsRequired,
    HostIncompatible,
    HostAvailable,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct MonitorRuntimeTuning {
    pub lease_seconds: u32,
    pub online_threshold_seconds: u32,
    pub launch_dedupe_window_seconds: u32,
    pub launch_register_timeout_seconds: u32,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct MonitorHubRuntime {
    pub protocol_version: u32,
    pub pid: u32,
    pub http_base_url: String,
    pub ws_url: String,
    pub token_file: String,
    pub started_at_utc: String,
    pub runtime_tuning: MonitorRuntimeTuning,
    pub hub_version: Option<String>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct MonitorRuntimeConnectionInfo {
    pub runtime_directory: String,
    pub token: String,
    pub runtime: MonitorHubRuntime,
    pub rpc_endpoint: String,
    pub websocket_endpoint: String,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct BootstrapSnapshot {
    pub generation: u64,
    pub phase: BootstrapPhase,
    pub effective_data_dir: String,
    pub data_dir_source: DataDirSource,
    pub settings: MonitorSettings,
    pub has_configured_host_executable: bool,
    pub connection: Option<MonitorRuntimeConnectionInfo>,
    pub last_problem: Option<MonitorProblem>,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "snake_case")]
pub enum HostLaunchStatus {
    Started,
    SettingsRequired,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct LaunchHostResult {
    pub status: HostLaunchStatus,
    pub effective_data_dir: String,
    pub data_dir_source: DataDirSource,
    pub pid: Option<u32>,
}

#[derive(Debug, Clone, Copy, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "snake_case")]
pub enum LogKind {
    Host,
    Monitor,
}

impl LogKind {
    pub fn as_str(self) -> &'static str {
        match self {
            Self::Host => "host",
            Self::Monitor => "monitor",
        }
    }
}

#[derive(Debug, Clone, Copy, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum MonitorLogLevel {
    Trace,
    Debug,
    Info,
    Warn,
    Error,
}

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct MonitorStructuredLogRecord {
    pub timestamp_utc: String,
    pub level: MonitorLogLevel,
    pub category: String,
    pub action: String,
    pub result: String,
    pub message: Option<String>,
    pub data_dir: Option<String>,
    pub host_pid: Option<u32>,
    pub port: Option<u16>,
    pub app_id: Option<String>,
    pub instance_id: Option<String>,
    pub error_code: Option<String>,
    pub context: Option<Map<String, Value>>,
}

#[derive(Debug, Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct FrontendLogInput {
    pub level: MonitorLogLevel,
    pub category: String,
    pub action: String,
    pub result: String,
    pub message: Option<String>,
    pub context: Option<Map<String, Value>>,
}
