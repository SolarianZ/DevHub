# DevHub Monitor

`apps/monitor/` 是 DevHub 的官方桌面 Monitor 工作区，使用 Tauri 2 提供运行时发现、Host 启动、日志读取、系统托盘和前端 WebView 桥接能力。

## 常用命令

- `npm install`
- `npm run tauri:dev`
- `npm run build:web`
- `npm test`
- `npm run test:native`
- `npm run tauri:check`
- `npm run verify`
- `python ../../scripts/release/package_monitor.py --release-id local-dry-run`

## 环境要求

- Node.js：`20.19+` 或 `22.12+`
- npm：使用仓库当前 lock 文件对应的 `npm ci`
- Rust：按 Tauri 2 官方要求安装稳定工具链

## 验证入口

推荐先安装本地依赖：

- `npm --prefix ../../sdks/javascript ci`
- `npm ci`

与仓库 CI 对齐的验证命令：

- `npm run build:web`：构建前端、执行类型检查，并构建 `@devhub/sdk` 本地依赖。
- `npm test`：执行前端控制器测试、日志/定义工作流测试，以及基于真实 Host fixture 的前端回归。
- `npm run test:native`：执行 `src-tauri/` 原生后端单元测试。
- `npm run tauri:check`：执行 Tauri 原生侧非平台特定编译校验。
- `npm run verify`：串联上述全部验证入口。

## 目录说明

- `src/`：前端 WebView 工程；`App.tsx` 只负责路由与壳层装配，bootstrap / Host 会话 / 日志 / Definition 编辑分别落在独立 hooks。
- `src-tauri/`：Rust 原生后端；Tauri command 只做参数校验与转发，设置、快照、discovery、Host 启动和日志能力由独立服务协作。
- `../../sdks/javascript`：前端 Host 通信依赖来源，当前通过本地 `file:` 依赖映射为 `@devhub/sdk`。

## 运行方式

- 开发态桌面运行：`npm run tauri:dev`
- 前端单独调试：`npm run dev`
- 生产发布优先入口：`python ../../scripts/release/package_monitor.py --release-id <release-id>`
- 底层 Tauri 构建命令：`npm run tauri:build`

`package_monitor.py` 会先校验 `package.json`、`package-lock.json`、`src-tauri/tauri.conf.json` 与 `src-tauri/Cargo.toml` 的版本一致性，再串联 `npm ci`、`npm run verify`、`npm run tauri:build`，并把 bundle 产物、校验日志、manifest 和 release notes 归档到 `artifacts/monitor/<release-id>/`。

当前 Monitor 的一键发布脚本只负责本地打包，不接入仓库现有的 GitHub Release / CI 自动发布流程。

Monitor 启动后会先扫描当前有效 `DEVHUB_DATA_DIR`，只有在真实 `hub.ping` 校验成功后才切到状态页。若未配置 Host 可执行文件路径，启动请求会跳转到设置页而不是直接拉起进程。
设置页中的 `DEVHUB_DATA_DIR` 覆盖值和 Host 可执行文件路径都只接受绝对路径；相对路径会在前端与原生命令层同时被拒绝。

## 日志与能力边界

- Host 日志固定来自 `<DEVHUB_DATA_DIR>/logs/`。
- Monitor 自身结构化日志写入 Monitor 本地数据目录下的 `logs/monitor-YYYYMMDD.jsonl`；当前目录会显示在设置页的“Monitor 日志目录”字段中。
- 前端通过 `@devhub/sdk` 访问 Host RPC 与事件，不直接访问本地文件。
- 原生后端负责设置持久化、运行时发现、Host 启动、日志读写和系统托盘，不直接依赖 `JS/TS SDK`。
