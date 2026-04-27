use crate::models::{
    DataDirSource, MonitorPlatform, MonitorSettings, ResolvedDataDir, SettingsLoadWarning,
    SettingsSnapshot, DEVHUB_DATA_DIR_ENV,
};
use anyhow::{Context, Result};
use chrono::Utc;
use directories::ProjectDirs;
use std::env;
use std::fs;
use std::path::{Path, PathBuf};
use std::sync::{Arc, RwLock};
use uuid::Uuid;

#[derive(Debug, Clone)]
pub struct MonitorPaths {
    pub settings_file: PathBuf,
    pub monitor_log_directory: PathBuf,
}

impl MonitorPaths {
    pub fn resolve() -> Result<Self> {
        let project_dirs = ProjectDirs::from("io", "DevHub", "Monitor")
            .context("无法解析 DevHub Monitor 的本地目录。")?;

        let config_dir = project_dirs.config_dir().to_path_buf();
        let data_dir = project_dirs.data_local_dir().to_path_buf();
        let monitor_log_directory = data_dir.join("logs");

        fs::create_dir_all(&config_dir)
            .with_context(|| format!("无法创建 Monitor 配置目录：{}", config_dir.display()))?;
        fs::create_dir_all(&monitor_log_directory).with_context(|| {
            format!(
                "无法创建 Monitor 日志目录：{}",
                monitor_log_directory.display()
            )
        })?;

        Ok(Self {
            settings_file: config_dir.join("settings.json"),
            monitor_log_directory,
        })
    }
}

#[derive(Clone)]
pub struct SettingsStore {
    file_path: PathBuf,
    state: Arc<RwLock<SettingsState>>,
}

#[derive(Debug, Clone)]
struct SettingsState {
    settings: MonitorSettings,
    revision: u64,
    load_warning: Option<SettingsLoadWarning>,
}

impl SettingsStore {
    pub fn load(file_path: PathBuf) -> Result<Self> {
        let state = load_settings_state(&file_path)?;

        Ok(Self {
            file_path,
            state: Arc::new(RwLock::new(state)),
        })
    }

    pub fn current(&self) -> MonitorSettings {
        self.state
            .read()
            .expect("settings lock poisoned")
            .settings
            .clone()
    }

    pub fn save(&self, settings: MonitorSettings) -> Result<MonitorSettings> {
        let normalized = normalize_settings(settings);
        validate_optional_absolute_path(
            normalized.data_dir_override.as_deref(),
            "dataDirOverride",
        )?;
        validate_optional_absolute_path(
            normalized.host_executable_path.as_deref(),
            "hostExecutablePath",
        )?;

        if let Some(parent) = self.file_path.parent() {
            fs::create_dir_all(parent)
                .with_context(|| format!("无法创建设置父目录：{}", parent.display()))?;
        }

        let payload =
            serde_json::to_vec_pretty(&normalized).context("无法序列化 Monitor 设置。")?;
        atomic_write_settings_file(&self.file_path, &payload)?;

        let mut state = self.state.write().expect("settings lock poisoned");
        state.settings = normalized.clone();
        state.revision = state.revision.saturating_add(1);
        state.load_warning = None;
        Ok(normalized)
    }

    pub fn snapshot(&self, paths: &MonitorPaths) -> SettingsSnapshot {
        let state = self.state.read().expect("settings lock poisoned").clone();
        let resolved = resolve_effective_data_dir(&state.settings);

        SettingsSnapshot {
            revision: state.revision,
            settings: state.settings,
            platform: current_platform(),
            effective_data_dir: resolved.path,
            data_dir_source: resolved.source,
            settings_file_path: self.file_path.display().to_string(),
            monitor_log_directory: paths.monitor_log_directory.display().to_string(),
            load_warning: state.load_warning,
        }
    }
}

#[derive(Clone)]
pub struct SettingsService {
    paths: MonitorPaths,
    store: SettingsStore,
}

impl SettingsService {
    pub fn new() -> Result<Self> {
        let paths = MonitorPaths::resolve()?;
        let store = SettingsStore::load(paths.settings_file.clone())?;
        Ok(Self { paths, store })
    }

    pub fn current(&self) -> MonitorSettings {
        self.store.current()
    }

    pub fn snapshot(&self) -> SettingsSnapshot {
        self.store.snapshot(&self.paths)
    }

    pub fn save(&self, settings: MonitorSettings) -> Result<SettingsSnapshot> {
        self.store.save(settings)?;
        Ok(self.snapshot())
    }

    pub fn resolve_effective_data_dir(&self) -> ResolvedDataDir {
        resolve_effective_data_dir(&self.store.current())
    }

    pub fn monitor_log_directory(&self) -> PathBuf {
        self.paths.monitor_log_directory.clone()
    }
}

fn load_settings_state(file_path: &Path) -> Result<SettingsState> {
    if !file_path.exists() {
        return Ok(SettingsState {
            settings: MonitorSettings::default(),
            revision: 0,
            load_warning: None,
        });
    }

    let content = fs::read_to_string(file_path)
        .with_context(|| format!("无法读取设置文件：{}", file_path.display()))?;
    match serde_json::from_str::<MonitorSettings>(&content) {
        Ok(settings) => Ok(SettingsState {
            settings: normalize_settings(settings),
            revision: 0,
            load_warning: None,
        }),
        Err(_) => recover_from_corrupt_settings_file(file_path),
    }
}

fn recover_from_corrupt_settings_file(file_path: &Path) -> Result<SettingsState> {
    let backup_path = isolate_corrupt_settings_file(file_path)?;

    Ok(SettingsState {
        settings: MonitorSettings::default(),
        revision: 1,
        load_warning: Some(SettingsLoadWarning {
            code: "settings_recovered".to_string(),
            message: "检测到损坏的设置文件，已备份原文件并回退为默认设置。".to_string(),
            settings_file_path: file_path.display().to_string(),
            backup_file_path: Some(backup_path.display().to_string()),
        }),
    })
}

fn isolate_corrupt_settings_file(file_path: &Path) -> Result<PathBuf> {
    let backup_path = build_settings_backup_path(file_path);

    match fs::rename(file_path, &backup_path) {
        Ok(()) => Ok(backup_path),
        Err(_) => {
            fs::copy(file_path, &backup_path).with_context(|| {
                format!(
                    "无法备份损坏的设置文件：{} -> {}",
                    file_path.display(),
                    backup_path.display()
                )
            })?;
            let _ = fs::remove_file(file_path);
            Ok(backup_path)
        }
    }
}

fn build_settings_backup_path(file_path: &Path) -> PathBuf {
    let timestamp = Utc::now().format("%Y%m%dT%H%M%SZ");
    let file_name = file_path
        .file_name()
        .and_then(|name| name.to_str())
        .unwrap_or("settings.json");
    let backup_file_name = format!("{file_name}.corrupt-{timestamp}-{}.bak", Uuid::new_v4());

    file_path
        .parent()
        .unwrap_or_else(|| Path::new("."))
        .join(backup_file_name)
}

fn atomic_write_settings_file(file_path: &Path, payload: &[u8]) -> Result<()> {
    let temp_path = build_temp_settings_path(file_path);
    fs::write(&temp_path, payload)
        .with_context(|| format!("无法写入设置临时文件：{}", temp_path.display()))?;

    fs::rename(&temp_path, file_path).with_context(|| {
        format!(
            "无法以原子方式写入设置文件：{} -> {}",
            temp_path.display(),
            file_path.display()
        )
    })?;

    Ok(())
}

fn build_temp_settings_path(file_path: &Path) -> PathBuf {
    let file_name = file_path
        .file_name()
        .and_then(|name| name.to_str())
        .unwrap_or("settings.json");
    let temp_file_name = format!("{file_name}.{}.tmp", Uuid::new_v4());

    file_path
        .parent()
        .unwrap_or_else(|| Path::new("."))
        .join(temp_file_name)
}

fn current_platform() -> MonitorPlatform {
    #[cfg(target_os = "windows")]
    {
        return MonitorPlatform::Windows;
    }

    #[cfg(target_os = "macos")]
    {
        return MonitorPlatform::Macos;
    }

    #[cfg(not(any(target_os = "windows", target_os = "macos")))]
    {
        MonitorPlatform::Linux
    }
}

pub fn resolve_effective_data_dir(settings: &MonitorSettings) -> ResolvedDataDir {
    if let Some(override_path) = settings.data_dir_override.as_deref() {
        if is_absolute_path(override_path) {
            return ResolvedDataDir {
                path: override_path.trim().to_string(),
                source: DataDirSource::SettingsOverride,
            };
        }
    }

    if let Ok(environment_path) = env::var(DEVHUB_DATA_DIR_ENV) {
        let trimmed = environment_path.trim();
        if !trimmed.is_empty() {
            return ResolvedDataDir {
                path: make_absolute_path(trimmed),
                source: DataDirSource::Environment,
            };
        }
    }

    ResolvedDataDir {
        path: default_devhub_data_dir().display().to_string(),
        source: DataDirSource::PlatformDefault,
    }
}

fn normalize_settings(settings: MonitorSettings) -> MonitorSettings {
    MonitorSettings {
        data_dir_override: normalize_optional_path(settings.data_dir_override),
        host_executable_path: normalize_optional_path(settings.host_executable_path),
        hide_host_command_line_window: settings.hide_host_command_line_window,
    }
}

fn normalize_optional_path(value: Option<String>) -> Option<String> {
    let raw = value?;
    let trimmed = raw.trim();
    if trimmed.is_empty() {
        return None;
    }

    Some(trimmed.to_string())
}

fn make_absolute_path(input: &str) -> String {
    let path = PathBuf::from(input);
    let absolute = if path.is_absolute() {
        path
    } else {
        match env::current_dir() {
            Ok(current_dir) => current_dir.join(path),
            Err(_) => path,
        }
    };

    absolute.display().to_string()
}

fn default_devhub_data_dir() -> PathBuf {
    if cfg!(target_os = "windows") {
        let local_app_data = env::var("LOCALAPPDATA")
            .ok()
            .filter(|value| !value.trim().is_empty());
        let base = local_app_data
            .map(PathBuf::from)
            .or_else(user_home_directory)
            .unwrap_or_else(|| PathBuf::from("."));
        return base.join("DevHub");
    }

    if cfg!(target_os = "macos") {
        return user_home_directory()
            .unwrap_or_else(|| PathBuf::from("."))
            .join("Library")
            .join("Application Support")
            .join("DevHub");
    }

    let xdg_data_home = env::var("XDG_DATA_HOME")
        .ok()
        .filter(|value| !value.trim().is_empty());
    let base = xdg_data_home
        .map(PathBuf::from)
        .or_else(|| user_home_directory().map(|home| home.join(".local").join("share")))
        .unwrap_or_else(|| PathBuf::from("."));

    base.join("DevHub")
}

fn validate_optional_absolute_path(value: Option<&str>, field: &str) -> Result<()> {
    if let Some(value) = value {
        if !is_absolute_path(value) {
            anyhow::bail!("{field} 必须为绝对路径。");
        }
    }

    Ok(())
}

fn is_absolute_path(value: &str) -> bool {
    Path::new(value.trim()).is_absolute()
}

fn user_home_directory() -> Option<PathBuf> {
    env::var("HOME")
        .ok()
        .filter(|value| !value.trim().is_empty())
        .map(PathBuf::from)
}

#[cfg(test)]
mod tests {
    use super::{resolve_effective_data_dir, MonitorPaths, SettingsStore};
    use crate::models::{DataDirSource, MonitorSettings, DEVHUB_DATA_DIR_ENV};
    use serde_json::json;
    use std::fs;
    use std::path::PathBuf;
    use std::sync::Mutex;
    use std::time::{SystemTime, UNIX_EPOCH};

    static ENV_LOCK: Mutex<()> = Mutex::new(());

    fn create_temp_directory(name: &str) -> PathBuf {
        let suffix = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .expect("system clock before unix epoch")
            .as_nanos();
        let directory = std::env::temp_dir().join(format!("devhub-monitor-{name}-{suffix}"));
        fs::create_dir_all(&directory).expect("failed to create temp directory");
        directory
    }

    fn absolute_test_path(name: &str) -> String {
        if cfg!(windows) {
            format!(r"C:\devhub-tests\{name}")
        } else {
            format!("/tmp/{name}")
        }
    }

    #[test]
    fn resolve_effective_data_dir_prefers_absolute_settings_override_over_environment() {
        let _guard = ENV_LOCK.lock().expect("env lock poisoned");
        let env_path = absolute_test_path("from-env");
        let override_path = absolute_test_path("override");
        unsafe {
            std::env::set_var(DEVHUB_DATA_DIR_ENV, &env_path);
        }

        let resolved = resolve_effective_data_dir(&MonitorSettings {
            data_dir_override: Some(override_path.clone()),
            host_executable_path: None,
            hide_host_command_line_window: false,
        });

        assert!(matches!(resolved.source, DataDirSource::SettingsOverride));
        assert_eq!(resolved.path, override_path);

        unsafe {
            std::env::remove_var(DEVHUB_DATA_DIR_ENV);
        }
    }

    #[test]
    fn resolve_effective_data_dir_ignores_relative_settings_override() {
        let _guard = ENV_LOCK.lock().expect("env lock poisoned");
        let env_path = absolute_test_path("from-env");
        unsafe {
            std::env::set_var(DEVHUB_DATA_DIR_ENV, &env_path);
        }

        let resolved = resolve_effective_data_dir(&MonitorSettings {
            data_dir_override: Some("./override".to_string()),
            host_executable_path: None,
            hide_host_command_line_window: false,
        });

        assert!(matches!(resolved.source, DataDirSource::Environment));
        assert_eq!(resolved.path, env_path);

        unsafe {
            std::env::remove_var(DEVHUB_DATA_DIR_ENV);
        }
    }

    #[test]
    fn settings_store_save_preserves_absolute_paths_and_updates_snapshot() {
        let temp_directory = create_temp_directory("settings");
        let settings_file = temp_directory.join("settings.json");
        let log_directory = temp_directory.join("monitor-logs");
        fs::create_dir_all(&log_directory).expect("failed to create log directory");
        let data_dir = absolute_test_path("runtime-data");
        let host_path = absolute_test_path("host-bin");

        let store = SettingsStore::load(settings_file.clone()).expect("failed to load store");
        let saved = store
            .save(MonitorSettings {
                data_dir_override: Some(data_dir.clone()),
                host_executable_path: Some(host_path.clone()),
                hide_host_command_line_window: true,
            })
            .expect("failed to save settings");

        assert_eq!(
            saved.data_dir_override.expect("missing data dir override"),
            data_dir
        );
        assert_eq!(
            saved
                .host_executable_path
                .expect("missing host executable path"),
            host_path
        );
        assert!(saved.hide_host_command_line_window);

        let snapshot = store.snapshot(&super::MonitorPaths {
            settings_file: settings_file.clone(),
            monitor_log_directory: log_directory.clone(),
        });

        assert_eq!(
            snapshot.settings_file_path,
            settings_file.display().to_string()
        );
        assert_eq!(
            snapshot.monitor_log_directory,
            log_directory.display().to_string()
        );
        assert_eq!(
            snapshot.effective_data_dir,
            absolute_test_path("runtime-data")
        );

        fs::remove_dir_all(temp_directory).expect("failed to clean temp directory");
    }

    #[test]
    fn settings_store_save_rejects_relative_paths() {
        let temp_directory = create_temp_directory("settings-relative");
        let settings_file = temp_directory.join("settings.json");
        let store = SettingsStore::load(settings_file).expect("failed to load store");

        let error = store
            .save(MonitorSettings {
                data_dir_override: Some("./runtime-data".to_string()),
                host_executable_path: Some("/tmp/host/bin/DevHub.Host".to_string()),
                hide_host_command_line_window: false,
            })
            .expect_err("expected relative path error");

        assert!(error.to_string().contains("dataDirOverride 必须为绝对路径"));

        fs::remove_dir_all(temp_directory).expect("failed to clean temp directory");
    }

    #[test]
    fn settings_store_load_defaults_hide_host_command_line_window_to_true() {
        let temp_directory = create_temp_directory("settings-backward-compatible");
        let settings_file = temp_directory.join("settings.json");
        let data_dir = absolute_test_path("runtime-data");
        let host_path = absolute_test_path("host-bin");
        let settings_payload = serde_json::to_vec_pretty(&json!({
            "dataDirOverride": data_dir,
            "hostExecutablePath": host_path,
        }))
        .expect("failed to serialize settings file");
        fs::write(&settings_file, settings_payload).expect("failed to write settings file");

        let store = SettingsStore::load(settings_file).expect("failed to load store");
        let current = store.current();

        assert_eq!(
            current.data_dir_override.as_deref(),
            Some(data_dir.as_str())
        );
        assert_eq!(
            current.host_executable_path.as_deref(),
            Some(host_path.as_str())
        );
        assert!(current.hide_host_command_line_window);

        fs::remove_dir_all(temp_directory).expect("failed to clean temp directory");
    }

    #[test]
    fn settings_store_load_recovers_corrupt_file_with_backup_and_warning() {
        let temp_directory = create_temp_directory("settings-corrupt");
        let settings_file = temp_directory.join("settings.json");
        let log_directory = temp_directory.join("monitor-logs");
        fs::create_dir_all(&log_directory).expect("failed to create log directory");
        fs::write(&settings_file, "{ invalid json").expect("failed to write corrupt settings");

        let store = SettingsStore::load(settings_file.clone()).expect("failed to recover settings");
        let snapshot = store.snapshot(&MonitorPaths {
            settings_file: settings_file.clone(),
            monitor_log_directory: log_directory,
        });

        assert_eq!(snapshot.revision, 1);
        assert!(snapshot.settings.data_dir_override.is_none());
        assert!(snapshot.settings.host_executable_path.is_none());
        assert!(snapshot.settings.hide_host_command_line_window);

        let warning = snapshot.load_warning.expect("expected load warning");
        assert_eq!(warning.code, "settings_recovered");
        assert_eq!(
            warning.settings_file_path,
            settings_file.display().to_string()
        );

        let backup_path =
            PathBuf::from(warning.backup_file_path.expect("expected backup file path"));
        assert!(backup_path.exists());
        assert!(!settings_file.exists());
        assert_eq!(
            fs::read_to_string(&backup_path).expect("failed to read backup"),
            "{ invalid json"
        );

        fs::remove_dir_all(temp_directory).expect("failed to clean temp directory");
    }
}
