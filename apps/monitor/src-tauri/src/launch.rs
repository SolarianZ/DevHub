use crate::models::DEVHUB_DATA_DIR_ENV;
use anyhow::{Context, Result};
use std::path::Path;
use std::process::Command;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::Arc;

#[cfg(windows)]
use std::os::windows::process::CommandExt;

#[cfg(windows)]
const CREATE_NO_WINDOW: u32 = 0x0800_0000;

#[derive(Clone, Default)]
pub struct HostLaunchService {
    launch_pending: Arc<AtomicBool>,
}

impl HostLaunchService {
    pub fn begin_launch(&self) -> bool {
        !self.launch_pending.swap(true, Ordering::SeqCst)
    }

    pub fn finish_launch_attempt(&self) {
        self.launch_pending.store(false, Ordering::SeqCst);
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

#[cfg(test)]
mod tests {
    use super::HostLaunchService;

    #[test]
    fn begin_launch_blocks_until_attempt_finishes() {
        let service = HostLaunchService::default();

        assert!(service.begin_launch());
        assert!(!service.begin_launch());

        service.finish_launch_attempt();

        assert!(service.begin_launch());
    }
}
