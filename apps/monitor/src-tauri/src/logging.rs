use crate::models::{LogKind, MonitorStructuredLogRecord};
use anyhow::{Context, Result};
use std::fs::{self, OpenOptions};
use std::io::Write;
use std::path::{Path, PathBuf};
use std::process::Command;
use std::sync::{Arc, Mutex};

#[derive(Clone)]
pub struct MonitorLogService {
    log_directory: PathBuf,
    write_lock: Arc<Mutex<()>>,
}

impl MonitorLogService {
    pub fn new(log_directory: PathBuf) -> Result<Self> {
        fs::create_dir_all(&log_directory)
            .with_context(|| format!("无法创建 Monitor 日志目录：{}", log_directory.display()))?;

        Ok(Self {
            log_directory,
            write_lock: Arc::new(Mutex::new(())),
        })
    }

    pub fn log_directory(&self) -> &Path {
        &self.log_directory
    }

    pub fn record(&self, record: MonitorStructuredLogRecord) -> Result<()> {
        let _guard = self.write_lock.lock().expect("monitor log lock poisoned");
        fs::create_dir_all(&self.log_directory).with_context(|| {
            format!(
                "无法确保 Monitor 日志目录存在：{}",
                self.log_directory.display()
            )
        })?;

        let file_path = self.current_log_file_path();
        let mut file = OpenOptions::new()
            .create(true)
            .append(true)
            .open(&file_path)
            .with_context(|| format!("无法打开 Monitor 日志文件：{}", file_path.display()))?;

        let line = serde_json::to_string(&record).context("无法序列化结构化日志。")?;
        writeln!(file, "{line}")
            .with_context(|| format!("无法写入 Monitor 日志：{}", file_path.display()))?;
        Ok(())
    }

    fn current_log_file_path(&self) -> PathBuf {
        let date = chrono::Utc::now().format("%Y%m%d");
        self.log_directory.join(format!("monitor-{date}.jsonl"))
    }
}

pub trait ShellOpener {
    fn open_directory(&self, directory: &Path) -> Result<()>;
}

#[derive(Debug, Clone, Copy)]
pub struct SystemShellOpener;

impl ShellOpener for SystemShellOpener {
    fn open_directory(&self, directory: &Path) -> Result<()> {
        let status = build_open_directory_command(directory)
            .status()
            .with_context(|| format!("无法调用系统外壳打开日志目录：{}", directory.display()))?;

        if !status.success() {
            anyhow::bail!(
                "系统未能打开日志目录：{}（退出码：{}）",
                directory.display(),
                status
            );
        }

        Ok(())
    }
}

pub fn resolve_log_directory(
    kind: LogKind,
    effective_data_dir: &Path,
    monitor_log_directory: &Path,
) -> PathBuf {
    match kind {
        LogKind::Monitor => monitor_log_directory.to_path_buf(),
        LogKind::Host => effective_data_dir.join("logs"),
    }
}

pub fn open_log_directory(
    kind: LogKind,
    effective_data_dir: &Path,
    monitor_log_directory: &Path,
) -> Result<PathBuf> {
    let directory = resolve_log_directory(kind, effective_data_dir, monitor_log_directory);
    open_directory_with(&SystemShellOpener, &directory)?;
    Ok(directory)
}

fn open_directory_with(opener: &dyn ShellOpener, directory: &Path) -> Result<()> {
    validate_log_directory(directory)?;
    opener.open_directory(directory)
}

fn validate_log_directory(directory: &Path) -> Result<()> {
    if !directory.exists() {
        anyhow::bail!("日志目录不存在：{}", directory.display());
    }

    if !directory.is_dir() {
        anyhow::bail!("日志目录无效：{}", directory.display());
    }

    Ok(())
}

#[cfg(target_os = "windows")]
fn build_open_directory_command(directory: &Path) -> Command {
    let mut command = Command::new("explorer");
    command.arg(directory);
    command
}

#[cfg(target_os = "macos")]
fn build_open_directory_command(directory: &Path) -> Command {
    let mut command = Command::new("open");
    command.arg(directory);
    command
}

#[cfg(all(not(target_os = "windows"), not(target_os = "macos")))]
fn build_open_directory_command(directory: &Path) -> Command {
    let mut command = Command::new("xdg-open");
    command.arg(directory);
    command
}

#[cfg(test)]
mod tests {
    use super::{open_directory_with, resolve_log_directory, LogKind, MonitorLogService, ShellOpener};
    use crate::models::{MonitorLogLevel, MonitorStructuredLogRecord};
    use anyhow::Result;
    use serde_json::json;
    use std::fs;
    use std::path::{Path, PathBuf};
    use std::sync::{Arc, Mutex};
    use std::time::{SystemTime, UNIX_EPOCH};

    fn create_temp_directory(name: &str) -> PathBuf {
        let suffix = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .expect("system clock before unix epoch")
            .as_nanos();
        let directory = std::env::temp_dir().join(format!("devhub-monitor-{name}-{suffix}"));
        fs::create_dir_all(&directory).expect("failed to create temp directory");
        directory
    }

    #[test]
    fn log_service_writes_monitor_logs() {
        let log_directory = create_temp_directory("logs");
        let service = MonitorLogService::new(log_directory.clone())
            .expect("failed to initialize log service");

        service
            .record(MonitorStructuredLogRecord {
                timestamp_utc: "2026-04-12T00:00:00Z".to_string(),
                level: MonitorLogLevel::Info,
                category: "test".to_string(),
                action: "record".to_string(),
                result: "ok".to_string(),
                message: Some("Monitor log entry".to_string()),
                data_dir: Some("/tmp/devhub".to_string()),
                host_pid: Some(1234),
                port: Some(4123),
                app_id: Some("demo.app".to_string()),
                instance_id: Some("instance-1".to_string()),
                error_code: None,
                context: Some(
                    [("reason".to_string(), json!("unit-test"))]
                        .into_iter()
                        .collect(),
                ),
            })
            .expect("failed to record log");

        let files = fs::read_dir(&log_directory)
            .expect("failed to read log directory")
            .collect::<std::result::Result<Vec<_>, _>>()
            .expect("failed to collect log files");

        assert_eq!(files.len(), 1);

        fs::remove_dir_all(log_directory).expect("failed to clean temp directory");
    }

    #[test]
    fn resolve_log_directory_uses_expected_paths() {
        let effective_data_dir = Path::new("/tmp/devhub-data");
        let monitor_log_directory = Path::new("/tmp/monitor-logs");

        assert_eq!(
            resolve_log_directory(LogKind::Host, effective_data_dir, monitor_log_directory),
            effective_data_dir.join("logs")
        );
        assert_eq!(
            resolve_log_directory(LogKind::Monitor, effective_data_dir, monitor_log_directory),
            monitor_log_directory
        );
    }

    #[test]
    fn open_directory_with_invokes_shell_for_existing_directory() {
        let directory = create_temp_directory("open-success");
        let opened_paths = Arc::new(Mutex::new(Vec::<PathBuf>::new()));
        let opener = RecordingShellOpener::success(opened_paths.clone());

        open_directory_with(&opener, &directory).expect("expected open success");

        let opened = opened_paths.lock().expect("open paths lock poisoned");
        assert_eq!(opened.as_slice(), &[directory.clone()]);

        fs::remove_dir_all(directory).expect("failed to clean temp directory");
    }

    #[test]
    fn open_directory_with_rejects_missing_directory_and_surfaces_shell_failures() {
        let missing_directory = std::env::temp_dir().join("devhub-monitor-missing-log-dir");
        let missing_error =
            open_directory_with(&RecordingShellOpener::success(Arc::new(Mutex::new(Vec::new()))), &missing_directory)
                .expect_err("expected missing directory error");
        assert!(missing_error.to_string().contains("日志目录不存在"));

        let directory = create_temp_directory("open-failure");
        let opener = RecordingShellOpener::failure("shell open failed");
        let open_error = open_directory_with(&opener, &directory).expect_err("expected shell failure");
        assert!(open_error.to_string().contains("shell open failed"));

        fs::remove_dir_all(directory).expect("failed to clean temp directory");
    }

    #[test]
    fn open_log_directory_returns_resolved_directory() {
        let effective_data_dir = create_temp_directory("runtime-data");
        let host_logs = effective_data_dir.join("logs");
        let monitor_logs = create_temp_directory("monitor-logs");
        fs::create_dir_all(&host_logs).expect("failed to create host logs");

        let host_path = open_log_directory_for_test(
            LogKind::Host,
            &effective_data_dir,
            &monitor_logs,
            &RecordingShellOpener::success(Arc::new(Mutex::new(Vec::new()))),
        )
        .expect("expected host log path");
        assert_eq!(host_path, host_logs);

        let monitor_path = open_log_directory_for_test(
            LogKind::Monitor,
            &effective_data_dir,
            &monitor_logs,
            &RecordingShellOpener::success(Arc::new(Mutex::new(Vec::new()))),
        )
        .expect("expected monitor log path");
        assert_eq!(monitor_path, monitor_logs);

        fs::remove_dir_all(effective_data_dir).expect("failed to clean temp directory");
        fs::remove_dir_all(monitor_path).expect("failed to clean temp directory");
    }

    fn open_log_directory_for_test(
        kind: LogKind,
        effective_data_dir: &Path,
        monitor_log_directory: &Path,
        opener: &dyn ShellOpener,
    ) -> Result<PathBuf> {
        let directory = resolve_log_directory(kind, effective_data_dir, monitor_log_directory);
        open_directory_with(opener, &directory)?;
        Ok(directory)
    }

    struct RecordingShellOpener {
        opened_paths: Arc<Mutex<Vec<PathBuf>>>,
        failure_message: Option<String>,
    }

    impl RecordingShellOpener {
        fn success(opened_paths: Arc<Mutex<Vec<PathBuf>>>) -> Self {
            Self {
                opened_paths,
                failure_message: None,
            }
        }

        fn failure(message: &str) -> Self {
            Self {
                opened_paths: Arc::new(Mutex::new(Vec::new())),
                failure_message: Some(message.to_string()),
            }
        }
    }

    impl ShellOpener for RecordingShellOpener {
        fn open_directory(&self, directory: &Path) -> Result<()> {
            if let Some(message) = &self.failure_message {
                anyhow::bail!(message.clone());
            }

            self.opened_paths
                .lock()
                .expect("open paths lock poisoned")
                .push(directory.to_path_buf());
            Ok(())
        }
    }
}
