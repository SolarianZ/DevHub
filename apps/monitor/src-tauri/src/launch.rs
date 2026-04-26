use crate::models::DEVHUB_DATA_DIR_ENV;
use anyhow::{Context, Result};
use std::collections::HashMap;
use std::path::Path;
use std::process::Command;
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};

#[cfg(windows)]
use std::os::windows::process::CommandExt;

#[cfg(windows)]
const CREATE_NO_WINDOW: u32 = 0x0800_0000;
const DEFAULT_LAUNCH_ATTEMPT_TIMEOUT: Duration = Duration::from_secs(30);

#[derive(Clone)]
pub struct HostLaunchService {
    attempts: Arc<Mutex<HashMap<String, HostLaunchAttempt>>>,
    launch_attempt_timeout: Duration,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum HostLaunchAttemptStatus {
    SpawnFailed,
    HostAvailable,
    TimedOut,
}

impl HostLaunchAttemptStatus {
    pub fn as_str(self) -> &'static str {
        match self {
            Self::SpawnFailed => "spawn_failed",
            Self::HostAvailable => "host_available",
            Self::TimedOut => "timed_out",
        }
    }
}

#[derive(Debug, Clone)]
pub struct HostLaunchAttemptOutcome {
    pub data_dir: String,
    pub requested_by_generation: Option<u64>,
    pub status: HostLaunchAttemptStatus,
}

#[derive(Debug, Clone)]
struct HostLaunchAttempt {
    started_at: Instant,
    requested_by_generation: Option<u64>,
}

impl Default for HostLaunchService {
    fn default() -> Self {
        Self::with_timeout(DEFAULT_LAUNCH_ATTEMPT_TIMEOUT)
    }
}

impl HostLaunchService {
    pub fn with_timeout(launch_attempt_timeout: Duration) -> Self {
        Self {
            attempts: Arc::new(Mutex::new(HashMap::new())),
            launch_attempt_timeout,
        }
    }

    pub fn begin_launch(&self, data_dir: &str) -> bool {
        let mut attempts = self.attempts.lock().expect("launch attempts lock poisoned");
        prune_timed_out_attempts(&mut attempts, self.launch_attempt_timeout);

        if attempts.contains_key(data_dir) {
            return false;
        }

        attempts.insert(
            data_dir.to_string(),
            HostLaunchAttempt {
                started_at: Instant::now(),
                requested_by_generation: None,
            },
        );
        true
    }

    pub fn assign_generation(&self, data_dir: &str, generation: u64) {
        let mut attempts = self.attempts.lock().expect("launch attempts lock poisoned");
        if let Some(attempt) = attempts.get_mut(data_dir) {
            attempt.requested_by_generation = Some(generation);
        }
    }

    pub fn finish_launch_attempt(
        &self,
        data_dir: &str,
        status: HostLaunchAttemptStatus,
    ) -> Option<HostLaunchAttemptOutcome> {
        let mut attempts = self.attempts.lock().expect("launch attempts lock poisoned");
        attempts
            .remove(data_dir)
            .map(|attempt| HostLaunchAttemptOutcome {
                data_dir: data_dir.to_string(),
                requested_by_generation: attempt.requested_by_generation,
                status,
            })
    }

    pub fn take_timed_out_attempts(&self) -> Vec<HostLaunchAttemptOutcome> {
        let mut attempts = self.attempts.lock().expect("launch attempts lock poisoned");
        take_timed_out_attempts(&mut attempts, self.launch_attempt_timeout)
    }

    pub fn spawn_host(
        &self,
        host_path: &Path,
        data_dir: &str,
        hide_host_command_line_window: bool,
    ) -> Result<u32> {
        if !host_path.is_absolute() {
            anyhow::bail!("Host 可执行文件路径必须为绝对路径：{}", host_path.display());
        }

        if !host_path.is_file() {
            anyhow::bail!("Host 可执行文件不存在：{}", host_path.display());
        }

        let mut command = Command::new(host_path);
        command.env(DEVHUB_DATA_DIR_ENV, data_dir);
        apply_host_launch_options(&mut command, hide_host_command_line_window);

        let child = command
            .spawn()
            .with_context(|| format!("启动 DevHub Host 失败：{}", host_path.display()))?;

        Ok(child.id())
    }
}

#[cfg(windows)]
fn apply_host_launch_options(command: &mut Command, hide_host_command_line_window: bool) {
    if hide_host_command_line_window {
        command.creation_flags(CREATE_NO_WINDOW);
    }
}

#[cfg(not(windows))]
fn apply_host_launch_options(_command: &mut Command, _hide_host_command_line_window: bool) {}

fn prune_timed_out_attempts(attempts: &mut HashMap<String, HostLaunchAttempt>, timeout: Duration) {
    attempts.retain(|_, attempt| attempt.started_at.elapsed() < timeout);
}

fn take_timed_out_attempts(
    attempts: &mut HashMap<String, HostLaunchAttempt>,
    timeout: Duration,
) -> Vec<HostLaunchAttemptOutcome> {
    let expired_data_dirs = attempts
        .iter()
        .filter(|(_, attempt)| attempt.started_at.elapsed() >= timeout)
        .map(|(data_dir, _)| data_dir.clone())
        .collect::<Vec<_>>();

    expired_data_dirs
        .into_iter()
        .filter_map(|data_dir| {
            attempts
                .remove(&data_dir)
                .map(|attempt| HostLaunchAttemptOutcome {
                    data_dir,
                    requested_by_generation: attempt.requested_by_generation,
                    status: HostLaunchAttemptStatus::TimedOut,
                })
        })
        .collect()
}

#[cfg(test)]
mod tests {
    use super::{HostLaunchAttemptStatus, HostLaunchService};
    use std::time::Duration;

    #[test]
    fn begin_launch_blocks_only_the_same_data_dir_until_attempt_finishes() {
        let service = HostLaunchService::with_timeout(Duration::from_secs(30));

        assert!(service.begin_launch("/tmp/devhub-a"));
        assert!(!service.begin_launch("/tmp/devhub-a"));
        assert!(service.begin_launch("/tmp/devhub-b"));

        let outcome =
            service.finish_launch_attempt("/tmp/devhub-a", HostLaunchAttemptStatus::HostAvailable);

        assert_eq!(outcome.expect("expected outcome").data_dir, "/tmp/devhub-a");
        assert!(service.begin_launch("/tmp/devhub-a"));
    }

    #[test]
    fn timed_out_attempts_are_released() {
        let service = HostLaunchService::with_timeout(Duration::from_millis(10));

        assert!(service.begin_launch("/tmp/devhub-a"));
        std::thread::sleep(Duration::from_millis(20));

        let outcomes = service.take_timed_out_attempts();
        assert_eq!(outcomes.len(), 1);
        assert!(matches!(
            outcomes[0].status,
            HostLaunchAttemptStatus::TimedOut
        ));
        assert!(service.begin_launch("/tmp/devhub-a"));
    }
}
