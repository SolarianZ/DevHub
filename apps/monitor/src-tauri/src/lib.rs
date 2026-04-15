mod discovery;
mod launch;
mod logging;
mod models;
mod monitor;
mod runtime;
mod settings;
mod snapshot;

use crate::models::{
    BootstrapSnapshot, FrontendLogInput, LaunchHostResult, LogFileInfo, LogKind, LogReadResult,
    MonitorSettings, ReadLogRequest, SettingsSnapshot,
};
use crate::monitor::MonitorCore;
use anyhow::{Context, Result};
use serde::Serialize;
use std::path::Path;
use tauri::menu::{Menu, MenuItem};
use tauri::tray::TrayIconBuilder;
use tauri::{AppHandle, Manager, State, WindowEvent};

#[derive(Debug, Clone, Serialize)]
#[serde(rename_all = "camelCase")]
struct CommandError {
    code: String,
    message: String,
}

impl CommandError {
    fn new(code: impl Into<String>, message: impl Into<String>) -> Self {
        Self {
            code: code.into(),
            message: message.into(),
        }
    }

    fn internal(error: anyhow::Error) -> Self {
        Self::new("monitor_error", error.to_string())
    }
}

type CommandResult<T> = std::result::Result<T, CommandError>;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum TrayMenuAction {
    ShowMainWindow,
    Quit,
    Ignore,
}

#[tauri::command]
async fn monitor_get_bootstrap_state(
    state: State<'_, MonitorCore>,
) -> CommandResult<BootstrapSnapshot> {
    Ok(state.get_bootstrap_state())
}

#[tauri::command]
async fn monitor_get_settings_snapshot(
    state: State<'_, MonitorCore>,
) -> CommandResult<SettingsSnapshot> {
    Ok(state.get_settings_snapshot())
}

#[tauri::command]
async fn monitor_save_settings(
    app: AppHandle,
    state: State<'_, MonitorCore>,
    settings: MonitorSettings,
) -> CommandResult<SettingsSnapshot> {
    validate_optional_text(
        settings.data_dir_override.as_deref(),
        "dataDirOverride",
        4096,
    )?;
    validate_optional_text(
        settings.host_executable_path.as_deref(),
        "hostExecutablePath",
        4096,
    )?;
    validate_optional_absolute_path(settings.data_dir_override.as_deref(), "dataDirOverride")?;
    validate_optional_absolute_path(
        settings.host_executable_path.as_deref(),
        "hostExecutablePath",
    )?;

    state
        .save_settings(app, settings)
        .map_err(CommandError::internal)
}

#[tauri::command]
async fn monitor_request_host_launch(
    app: AppHandle,
    state: State<'_, MonitorCore>,
) -> CommandResult<LaunchHostResult> {
    state
        .request_host_launch(app)
        .map_err(CommandError::internal)
}

#[tauri::command]
async fn monitor_resume_discovery(
    app: AppHandle,
    state: State<'_, MonitorCore>,
    reason: Option<String>,
) -> CommandResult<BootstrapSnapshot> {
    validate_optional_text(reason.as_deref(), "reason", 512)?;
    state
        .resume_discovery(app, reason)
        .map_err(CommandError::internal)
}

#[tauri::command]
async fn monitor_list_logs(
    state: State<'_, MonitorCore>,
    kind: LogKind,
) -> CommandResult<Vec<LogFileInfo>> {
    state.list_logs(kind).map_err(CommandError::internal)
}

#[tauri::command]
async fn monitor_read_log(
    state: State<'_, MonitorCore>,
    request: ReadLogRequest,
) -> CommandResult<LogReadResult> {
    validate_required_text(&request.file_name, "fileName", 255)?;
    state.read_log(request).map_err(CommandError::internal)
}

#[tauri::command]
async fn monitor_write_frontend_log(
    state: State<'_, MonitorCore>,
    entry: FrontendLogInput,
) -> CommandResult<()> {
    validate_required_text(&entry.category, "category", 128)?;
    validate_required_text(&entry.action, "action", 128)?;
    validate_required_text(&entry.result, "result", 64)?;
    validate_optional_text(entry.message.as_deref(), "message", 4096)?;
    state
        .record_frontend_log(entry)
        .map_err(CommandError::internal)
}

#[cfg_attr(mobile, tauri::mobile_entry_point)]
pub fn run() {
    let monitor_state = MonitorCore::new().expect("failed to initialize monitor state");

    tauri::Builder::default()
        .manage(monitor_state)
        .setup(|app| setup_monitor(app).map_err(Into::into))
        .on_window_event(|window, event| {
            if let WindowEvent::CloseRequested { api, .. } = event {
                let state = window.app_handle().state::<MonitorCore>();
                if !should_hide_window_on_close(state.should_exit()) {
                    return;
                }

                api.prevent_close();
                let _ = window.hide();
            }
        })
        .invoke_handler(tauri::generate_handler![
            monitor_get_bootstrap_state,
            monitor_get_settings_snapshot,
            monitor_save_settings,
            monitor_request_host_launch,
            monitor_resume_discovery,
            monitor_list_logs,
            monitor_read_log,
            monitor_write_frontend_log
        ])
        .run(tauri::generate_context!())
        .expect("error while running DevHub Monitor");
}

fn setup_monitor(app: &mut tauri::App) -> Result<()> {
    create_tray(app)?;
    show_main_window(&app.handle())?;

    let state = app.state::<MonitorCore>();
    state.initialize(app.handle().clone())?;
    Ok(())
}

fn create_tray(app: &mut tauri::App) -> Result<()> {
    let show_item = MenuItem::with_id(app, "show_main_window", "显示主窗口", true, None::<&str>)
        .context("无法创建托盘菜单项：显示主窗口。")?;
    let quit_item = MenuItem::with_id(app, "quit_monitor", "退出 Monitor", true, None::<&str>)
        .context("无法创建托盘菜单项：退出 Monitor。")?;
    let menu = Menu::with_items(app, &[&show_item, &quit_item]).context("无法创建托盘菜单。")?;
    let icon = app
        .default_window_icon()
        .cloned()
        .context("无法读取默认窗口图标。")?;

    TrayIconBuilder::with_id("devhub-monitor-tray")
        .icon(icon)
        .menu(&menu)
        .on_menu_event(
            |app_handle, event| match tray_menu_action_from_id(event.id().as_ref()) {
                TrayMenuAction::ShowMainWindow => {
                    let _ = show_main_window(app_handle);
                }
                TrayMenuAction::Quit => {
                    let state = app_handle.state::<MonitorCore>();
                    state.request_exit();
                    app_handle.exit(0);
                }
                TrayMenuAction::Ignore => {}
            },
        )
        .build(app)
        .context("无法创建系统托盘。")?;

    Ok(())
}

fn show_main_window(app: &AppHandle) -> tauri::Result<()> {
    if let Some(window) = app.get_webview_window("main") {
        window.show()?;
        let _ = window.unminimize();
        let _ = window.set_focus();
    }

    Ok(())
}

fn should_hide_window_on_close(exit_requested: bool) -> bool {
    !exit_requested
}

fn tray_menu_action_from_id(id: &str) -> TrayMenuAction {
    match id {
        "show_main_window" => TrayMenuAction::ShowMainWindow,
        "quit_monitor" => TrayMenuAction::Quit,
        _ => TrayMenuAction::Ignore,
    }
}

fn validate_required_text(value: &str, field: &str, max_length: usize) -> CommandResult<()> {
    if value.trim().is_empty() {
        return Err(CommandError::new(
            "invalid_argument",
            format!("{field} 不能为空。"),
        ));
    }

    if value.len() > max_length {
        return Err(CommandError::new(
            "invalid_argument",
            format!("{field} 长度不能超过 {max_length}。"),
        ));
    }

    Ok(())
}

fn validate_optional_text(
    value: Option<&str>,
    field: &str,
    max_length: usize,
) -> CommandResult<()> {
    if let Some(value) = value {
        if value.len() > max_length {
            return Err(CommandError::new(
                "invalid_argument",
                format!("{field} 长度不能超过 {max_length}。"),
            ));
        }
    }

    Ok(())
}

fn validate_optional_absolute_path(value: Option<&str>, field: &str) -> CommandResult<()> {
    if let Some(value) = value {
        if !Path::new(value.trim()).is_absolute() {
            return Err(CommandError::new(
                "invalid_argument",
                format!("{field} 必须为绝对路径。"),
            ));
        }
    }

    Ok(())
}

#[cfg(test)]
mod tests {
    use super::{should_hide_window_on_close, tray_menu_action_from_id, TrayMenuAction};

    #[test]
    fn should_hide_window_when_close_requested_without_exit_flag() {
        assert!(should_hide_window_on_close(false));
        assert!(!should_hide_window_on_close(true));
    }

    #[test]
    fn tray_menu_ids_map_to_expected_actions() {
        assert_eq!(
            tray_menu_action_from_id("show_main_window"),
            TrayMenuAction::ShowMainWindow
        );
        assert_eq!(
            tray_menu_action_from_id("quit_monitor"),
            TrayMenuAction::Quit
        );
        assert_eq!(tray_menu_action_from_id("unknown"), TrayMenuAction::Ignore);
    }
}
