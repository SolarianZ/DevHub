use crate::models::{
    DataDirSource, MonitorPlatform, MonitorSettings, ResolvedDataDir, SettingsSnapshot,
    DEVHUB_DATA_DIR_ENV,
};
use anyhow::{Context, Result};
use directories::ProjectDirs;
use std::env;
use std::fs;
use std::path::{Path, PathBuf};
use std::sync::{Arc, RwLock};

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
    current: Arc<RwLock<MonitorSettings>>,
}

impl SettingsStore {
    pub fn load(file_path: PathBuf) -> Result<Self> {
        let current = if file_path.exists() {
            let content = fs::read_to_string(&file_path)
                .with_context(|| format!("无法读取设置文件：{}", file_path.display()))?;
            normalize_settings(
                serde_json::from_str::<MonitorSettings>(&content)
                    .with_context(|| format!("设置文件 JSON 无法解析：{}", file_path.display()))?,
            )
        } else {
            MonitorSettings::default()
        };

        Ok(Self {
            file_path,
            current: Arc::new(RwLock::new(current)),
        })
    }

    pub fn current(&self) -> MonitorSettings {
        self.current.read().expect("settings lock poisoned").clone()
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
        fs::write(&self.file_path, payload)
            .with_context(|| format!("无法写入设置文件：{}", self.file_path.display()))?;

        *self.current.write().expect("settings lock poisoned") = normalized.clone();
        Ok(normalized)
    }

    pub fn snapshot(&self, paths: &MonitorPaths) -> SettingsSnapshot {
        let settings = self.current();
        let resolved = resolve_effective_data_dir(&settings);

        SettingsSnapshot {
            settings,
            platform: current_platform(),
            effective_data_dir: resolved.path,
            data_dir_source: resolved.source,
            settings_file_path: self.file_path.display().to_string(),
            monitor_log_directory: paths.monitor_log_directory.display().to_string(),
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
    use super::{resolve_effective_data_dir, SettingsStore};
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
        fs::write(
            &settings_file,
            settings_payload,
        )
        .expect("failed to write settings file");

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
}
