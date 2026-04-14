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

## 验证入口

推荐先安装本地依赖：

- `npm --prefix ../../sdks/javascript ci`
- `npm ci`

与仓库 CI 对齐的验证命令：

- `npm run build:web`：构建前端、执行类型检查，并构建 `@devhub/sdk` 本地依赖。
- `npm test`：执行前端状态机、连接层和定义编辑流程测试。
- `npm run test:native`：执行 `src-tauri/` 原生后端单元测试。
- `npm run tauri:check`：执行 Tauri 原生侧非平台特定编译校验。
- `npm run verify`：串联上述全部验证入口。

## 目录说明

- `src/`：前端 WebView 工程与 Tauri bridge 调用入口。
- `src-tauri/`：Rust 原生后端，负责设置、扫描状态机、Host 启动、日志和托盘生命周期。
- `../../sdks/javascript`：前端 Host 通信依赖来源，当前通过本地 `file:` 依赖映射为 `@devhub/sdk`。

## 运行方式

- 开发态桌面运行：`npm run tauri:dev`
- 前端单独调试：`npm run dev`
- 生产构建入口：`npm run tauri:build`

Monitor 启动后会先扫描当前有效 `DEVHUB_DATA_DIR`，只有在真实 `hub.ping` 校验成功后才切到状态页。若未配置 Host 可执行文件路径，启动请求会跳转到设置页而不是直接拉起进程。

## 日志与能力边界

- Host 日志固定来自 `<DEVHUB_DATA_DIR>/logs/`。
- Monitor 自身结构化日志写入 Monitor 本地数据目录下的 `logs/monitor-YYYYMMDD.jsonl`；当前目录会显示在设置页的“Monitor 日志目录”字段中。
- 前端通过 `@devhub/sdk` 访问 Host RPC 与事件，不直接访问本地文件。
- 原生后端负责设置持久化、运行时发现、Host 启动、日志读写和系统托盘，不直接依赖 `JS/TS SDK`。
