use crate::models::{LogFileInfo, LogKind, LogReadResult, MonitorStructuredLogRecord};
use anyhow::{Context, Result};
use chrono::{DateTime, Utc};
use std::fs::{self, File, OpenOptions};
use std::io::{Read, Seek, SeekFrom, Write};
use std::path::{Path, PathBuf};
use std::sync::{Arc, Mutex};

const MAX_LOG_READ_BYTES: u64 = 256 * 1024;

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
        let date = Utc::now().format("%Y%m%d");
        self.log_directory.join(format!("monitor-{date}.jsonl"))
    }
}

pub fn list_log_files(base_directory: &Path, kind: LogKind) -> Result<Vec<LogFileInfo>> {
    if !base_directory.exists() {
        return Ok(Vec::new());
    }

    let mut files = Vec::new();
    for entry in fs::read_dir(base_directory)
        .with_context(|| format!("无法枚举日志目录：{}", base_directory.display()))?
    {
        let entry =
            entry.with_context(|| format!("读取日志目录项失败：{}", base_directory.display()))?;
        let metadata = entry
            .metadata()
            .with_context(|| format!("无法读取日志元数据：{}", entry.path().display()))?;

        if !metadata.is_file() {
            continue;
        }

        let modified_at_utc = metadata
            .modified()
            .ok()
            .map(DateTime::<Utc>::from)
            .map(|timestamp| timestamp.to_rfc3339());

        files.push(LogFileInfo {
            kind,
            name: entry.file_name().to_string_lossy().to_string(),
            file_path: entry.path().display().to_string(),
            size_bytes: metadata.len(),
            modified_at_utc,
        });
    }

    files.sort_by(|left, right| {
        right
            .modified_at_utc
            .cmp(&left.modified_at_utc)
            .then_with(|| left.name.cmp(&right.name))
    });

    Ok(files)
}

pub fn read_log_file(
    base_directory: &Path,
    kind: LogKind,
    file_name: &str,
) -> Result<LogReadResult> {
    let target_path = resolve_log_path(base_directory, file_name)?;
    let mut file = File::open(&target_path)
        .with_context(|| format!("无法打开日志文件：{}", target_path.display()))?;
    let metadata = file
        .metadata()
        .with_context(|| format!("无法读取日志文件元数据：{}", target_path.display()))?;

    let size_bytes = metadata.len();
    let truncated = size_bytes > MAX_LOG_READ_BYTES;
    let read_from = if truncated {
        size_bytes - MAX_LOG_READ_BYTES
    } else {
        0
    };

    file.seek(SeekFrom::Start(read_from))
        .with_context(|| format!("无法定位日志文件：{}", target_path.display()))?;

    let mut buffer = Vec::new();
    file.read_to_end(&mut buffer)
        .with_context(|| format!("无法读取日志文件：{}", target_path.display()))?;

    Ok(LogReadResult {
        kind,
        file_name: file_name.to_string(),
        file_path: target_path.display().to_string(),
        size_bytes,
        truncated,
        contents: String::from_utf8_lossy(&buffer).to_string(),
    })
}

fn resolve_log_path(base_directory: &Path, file_name: &str) -> Result<PathBuf> {
    if file_name.trim().is_empty() {
        anyhow::bail!("日志文件名不能为空。");
    }

    let candidate = Path::new(file_name);
    if candidate.components().count() != 1 {
        anyhow::bail!("日志文件名非法：{file_name}");
    }

    let resolved = base_directory.join(candidate);
    let canonical_parent = base_directory
        .canonicalize()
        .unwrap_or_else(|_| base_directory.to_path_buf());
    let resolved_parent = resolved
        .parent()
        .map(Path::to_path_buf)
        .unwrap_or_else(|| base_directory.to_path_buf());
    let canonical_resolved_parent = resolved_parent.canonicalize().unwrap_or(resolved_parent);

    if canonical_resolved_parent != canonical_parent {
        anyhow::bail!("日志文件路径越界：{file_name}");
    }

    Ok(resolved)
}
