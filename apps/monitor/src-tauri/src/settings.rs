use crate::models::{
    DataDirSource, MonitorSettings, ResolvedDataDir, SettingsSnapshot, DEVHUB_DATA_DIR_ENV,
};
use anyhow::{Context, Result};
use directories::ProjectDirs;
use std::env;
use std::fs;
use std::path::PathBuf;
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
            effective_data_dir: resolved.path,
            data_dir_source: resolved.source,
            settings_file_path: self.file_path.display().to_string(),
            monitor_log_directory: paths.monitor_log_directory.display().to_string(),
        }
    }
}

pub fn resolve_effective_data_dir(settings: &MonitorSettings) -> ResolvedDataDir {
    if let Some(override_path) = settings.data_dir_override.as_deref() {
        return ResolvedDataDir {
            path: make_absolute_path(override_path),
            source: DataDirSource::SettingsOverride,
        };
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
    }
}

fn normalize_optional_path(value: Option<String>) -> Option<String> {
    let raw = value?;
    let trimmed = raw.trim();
    if trimmed.is_empty() {
        return None;
    }

    Some(make_absolute_path(trimmed))
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

    #[test]
    fn resolve_effective_data_dir_prefers_settings_override_over_environment() {
        let _guard = ENV_LOCK.lock().expect("env lock poisoned");
        unsafe {
            std::env::set_var(DEVHUB_DATA_DIR_ENV, "/tmp/from-env");
        }

        let resolved = resolve_effective_data_dir(&MonitorSettings {
            data_dir_override: Some("./override".to_string()),
            host_executable_path: None,
        });

        assert!(matches!(resolved.source, DataDirSource::SettingsOverride));
        assert!(resolved.path.ends_with("/override"));

        unsafe {
            std::env::remove_var(DEVHUB_DATA_DIR_ENV);
        }
    }

    #[test]
    fn settings_store_save_normalizes_paths_and_updates_snapshot() {
        let temp_directory = create_temp_directory("settings");
        let settings_file = temp_directory.join("settings.json");
        let log_directory = temp_directory.join("monitor-logs");
        fs::create_dir_all(&log_directory).expect("failed to create log directory");

        let store = SettingsStore::load(settings_file.clone()).expect("failed to load store");
        let saved = store
            .save(MonitorSettings {
                data_dir_override: Some("./runtime-data".to_string()),
                host_executable_path: Some("./host/bin/DevHub.Host".to_string()),
            })
            .expect("failed to save settings");

        assert!(saved
            .data_dir_override
            .expect("missing data dir override")
            .ends_with("/runtime-data"));
        assert!(saved
            .host_executable_path
            .expect("missing host executable path")
            .ends_with("/host/bin/DevHub.Host"));

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
        assert!(snapshot.effective_data_dir.ends_with("/runtime-data"));

        fs::remove_dir_all(temp_directory).expect("failed to clean temp directory");
    }
}
