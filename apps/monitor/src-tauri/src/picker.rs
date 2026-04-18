use rfd::FileDialog;
use std::path::PathBuf;

pub fn pick_host_executable_path(current_path: Option<&str>) -> Option<String> {
    let seed = resolve_file_dialog_seed(current_path);
    let mut dialog = FileDialog::new().set_title("选择 Host 可执行文件");

    #[cfg(target_os = "windows")]
    {
        dialog = dialog.add_filter("可执行文件", &["exe"]);
    }

    dialog = apply_file_dialog_seed(dialog, &seed);

    dialog.pick_file().map(|path| path.display().to_string())
}

pub fn pick_data_directory(current_path: Option<&str>) -> Option<String> {
    let mut dialog = FileDialog::new().set_title("选择 Host 数据目录");

    if let Some(directory) = resolve_directory_dialog_seed(current_path) {
        dialog = dialog.set_directory(directory);
    }

    dialog.pick_folder().map(|path| path.display().to_string())
}

#[derive(Debug, PartialEq, Eq)]
struct FileDialogSeed {
    directory: Option<PathBuf>,
    file_name: Option<String>,
}

fn resolve_file_dialog_seed(current_path: Option<&str>) -> FileDialogSeed {
    let Some(path) = normalize_absolute_path(current_path) else {
        return FileDialogSeed {
            directory: None,
            file_name: None,
        };
    };

    if path.is_dir() {
        return FileDialogSeed {
            directory: Some(path),
            file_name: None,
        };
    }

    FileDialogSeed {
        directory: path.parent().map(PathBuf::from),
        file_name: path
            .file_name()
            .map(|value| value.to_string_lossy().into_owned()),
    }
}

fn resolve_directory_dialog_seed(current_path: Option<&str>) -> Option<PathBuf> {
    let path = normalize_absolute_path(current_path)?;

    if path.is_file() {
        return path.parent().map(PathBuf::from);
    }

    Some(path)
}

fn normalize_absolute_path(current_path: Option<&str>) -> Option<PathBuf> {
    let trimmed = current_path?.trim();
    if trimmed.is_empty() {
        return None;
    }

    let path = PathBuf::from(trimmed);
    if !path.is_absolute() {
        return None;
    }

    Some(path)
}

fn apply_file_dialog_seed(mut dialog: FileDialog, seed: &FileDialogSeed) -> FileDialog {
    if let Some(directory) = seed.directory.as_deref() {
        dialog = dialog.set_directory(directory);
    }

    if let Some(file_name) = seed.file_name.as_deref() {
        dialog = dialog.set_file_name(file_name);
    }

    dialog
}

#[cfg(test)]
mod tests {
    use super::{resolve_directory_dialog_seed, resolve_file_dialog_seed, FileDialogSeed};
    use std::fs;
    use std::path::PathBuf;
    use std::time::{SystemTime, UNIX_EPOCH};

    #[test]
    fn resolve_file_dialog_seed_uses_parent_directory_and_file_name_for_absolute_file_path() {
        let seed = resolve_file_dialog_seed(Some(absolute_file_path()));

        assert_eq!(
            seed,
            FileDialogSeed {
                directory: Some(PathBuf::from(absolute_directory_path())),
                file_name: Some(file_name().to_string()),
            }
        );
    }

    #[test]
    fn resolve_file_dialog_seed_uses_existing_directory_as_is() {
        let directory = create_temp_directory();
        let directory_text = directory.display().to_string();
        let seed = resolve_file_dialog_seed(Some(&directory_text));

        assert_eq!(seed.directory, Some(directory.clone()));
        assert_eq!(seed.file_name, None);

        let _ = fs::remove_dir_all(directory);
    }

    #[test]
    fn resolve_file_dialog_seed_ignores_relative_paths() {
        let seed = resolve_file_dialog_seed(Some(relative_file_path()));

        assert_eq!(
            seed,
            FileDialogSeed {
                directory: None,
                file_name: None,
            }
        );
    }

    #[test]
    fn resolve_directory_dialog_seed_uses_file_parent_for_absolute_file_path() {
        let directory = create_temp_directory();
        let file_path = directory.join(file_name());
        fs::write(&file_path, b"test").expect("temp file should be created");
        let file_path_text = file_path.display().to_string();
        let seed = resolve_directory_dialog_seed(Some(&file_path_text));

        assert_eq!(seed, Some(directory.clone()));

        let _ = fs::remove_file(file_path);
        let _ = fs::remove_dir_all(directory);
    }

    #[test]
    fn resolve_directory_dialog_seed_keeps_absolute_directory_path() {
        let seed = resolve_directory_dialog_seed(Some(absolute_directory_path()));

        assert_eq!(seed, Some(PathBuf::from(absolute_directory_path())));
    }

    fn create_temp_directory() -> PathBuf {
        let unique = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .expect("system clock should be after unix epoch")
            .as_nanos();
        let directory = std::env::temp_dir().join(format!("devhub-monitor-picker-{unique}"));
        fs::create_dir_all(&directory).expect("temp directory should be created");
        directory
    }

    fn absolute_directory_path() -> &'static str {
        #[cfg(target_os = "windows")]
        {
            r"C:\DevHub\data"
        }

        #[cfg(not(target_os = "windows"))]
        {
            "/opt/devhub/data"
        }
    }

    fn absolute_file_path() -> &'static str {
        #[cfg(target_os = "windows")]
        {
            r"C:\DevHub\data\DevHub.Host.exe"
        }

        #[cfg(not(target_os = "windows"))]
        {
            "/opt/devhub/data/DevHub.Host"
        }
    }

    fn relative_file_path() -> &'static str {
        #[cfg(target_os = "windows")]
        {
            r".\DevHub.Host.exe"
        }

        #[cfg(not(target_os = "windows"))]
        {
            "./DevHub.Host"
        }
    }

    fn file_name() -> &'static str {
        #[cfg(target_os = "windows")]
        {
            "DevHub.Host.exe"
        }

        #[cfg(not(target_os = "windows"))]
        {
            "DevHub.Host"
        }
    }
}
