# DevHub Monitor

`apps/monitor/` 是 DevHub 的官方桌面 Monitor 工作区，使用 Tauri 2 提供本地发现、Host 启动、日志读取、系统托盘和前端 WebView 桥接能力。

## 常用命令

- `npm install`
- `npm run tauri:dev`
- `npm run build`
- `npm run tauri:check`

## 目录说明

- `src/`：前端 WebView 工程与 Tauri bridge 调用入口。
- `src-tauri/`：Rust 原生后端，负责设置、扫描状态机、Host 启动、日志和托盘生命周期。
- `../../sdks/javascript`：前端 Host 通信依赖入口，当前通过本地 `file:` 依赖接入。
