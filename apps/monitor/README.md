# DevHub Monitor

`apps/monitor/` 是 DevHub 的官方桌面 Monitor 工作区，使用 Tauri 2 提供运行时发现、Host 启动、日志目录访问，以及前端 WebView 与本机能力之间的桥接。

## 常用命令

- `npm install`
- `npm run tauri:dev`
- `npm run build:web`
- `npm test`
- `npm run test:native`
- `npm run tauri:check`
- `npm run verify`
- `python ../../scripts/release/package_monitor.py --release-id local-dry-run`
- `python ../../scripts/release/package_monitor.py --release-id local-dry-run --sdk-source local-src`

## 环境要求

- Node.js：`20.19+` 或 `22.12+`
- npm：使用仓库当前 lock 文件对应的 `npm ci`
- Rust：按 Tauri 2 官方要求安装稳定工具链

## 验证入口

推荐先安装本地依赖：

- `npm ci`

与仓库独立 Monitor workflow 对齐的验证命令：

- `npm run build:web`：构建前端并执行类型检查。
- `npm test`：执行前端侧边栏导航、主页 phase 切换、帮助/设置页面、Definition 页面工作流，以及基于真实 Host fixture 的前端回归。
- `npm run test:native`：执行 `src-tauri/` 原生后端单元测试。
- `npm run tauri:check`：执行 Tauri 原生侧非平台特定编译校验。
- `npm run verify`：串联上述全部验证入口。

仓库中的独立 `monitor.yml` workflow 会以 `DEVHUB_MONITOR_SDK_SOURCE=local-src` 运行上述验证，确保 Monitor 与当前分支的 JS SDK 源码保持一致。`apps/monitor/` 内的 `dev`、`build:web`、`test`、`verify`、`tauri:*` 等本地开发和验证命令在未设置环境变量时也默认解析 `local-src`；如需额外核对 release tarball 路径，可显式设置 `DEVHUB_MONITOR_SDK_SOURCE=release`。

对齐该 workflow 的本地验收入口：

- `DEVHUB_MONITOR_SDK_SOURCE=local-src npm run verify`

`DEVHUB_MONITOR_SDK_SOURCE=local-src` 的验证前提是：只在 `apps/monitor/` 执行 `npm ci`，也能完成 `build:web`、`test` 与 `verify`。该模式不要求额外执行 `npm --prefix sdks/javascript ci`，也不依赖预先存在的 `sdks/javascript/node_modules`。

## SDK 来源

- 默认 `local-src`：`@devhub/sdk` 与 `@devhub/sdk/runtime` 会分别解析到 `../../sdks/javascript/src/index.ts` 与 `../../sdks/javascript/src/runtime.ts`，适用于仓库内 Monitor 与 SDK 的源码联调、构建和验证。
- 可选 `release`：显式设置 `DEVHUB_MONITOR_SDK_SOURCE=release` 后，模块解析会回到 `package.json` 中声明的 GitHub Release tarball，适用于额外核对正式 SDK 包路径。
- `local-src` 只切换构建、测试、类型检查和本地打包时的模块解析来源，不修改 `package.json`、`package-lock.json` 或其他依赖声明文件；依赖声明本身仍保持 release tarball 形式，便于正式安装和打包。
- `local-src` 仍要求 `@devhub/sdk` 根入口保持浏览器 / WebView 安全；仅供 Node 使用的 `ws` 回退必须停留在运行时路径，不能在 Monitor 前端构建或类型检查阶段变成静态依赖。

## 目录说明

- `src/`：前端 WebView 工程；`App.tsx` 负责 `主页 / 测试 / 帮助 / 设置 / Definition` 多工作区状态编排，bootstrap / Host 会话 / 测试页状态 / Definition 编辑分别落在独立 hooks，壳层通过侧边栏驱动切换。
- `src-tauri/`：Rust 原生后端；Tauri command 只做参数校验与转发，设置、快照、discovery、Host 启动、日志写入与日志目录打开能力由独立服务协作。
- `@devhub/sdk`：前端 Host 通信依赖；依赖声明位于 `package.json`，本地开发和验证默认解析 `local-src`，需要时可显式切回 release tarball。

## 工作区概览

- `主页`：展示 Host 搜索状态、运行态摘要、App Definition 与 App 实例库存；当兼容状态为 `updateRecommended` 或 `unknown` 时显示非阻断版本提示，便于继续操作同时保留诊断信息。
- `测试`：显示当前 Host 的 RPC 地址，支持录入原始 JSON-RPC 文本、执行本地校验、发送请求、取消等待，并展示 Host 返回的原始响应文本；没有可用 Host 连接时仅展示不可用提示并禁用发送。
- `帮助`：展示 `Monitor` 版本、内置 `JS SDK` 版本、当前 `Host` 版本、兼容状态、日志目录与运行时路径辅助信息。
- `设置`：管理数据目录、Host 可执行文件路径与平台相关设置。
- `Definition`：承载新增、编辑、只读查看和缺失定义恢复等 Definition 工作流。

## 运行方式

- 开发态桌面运行：`npm run tauri:dev`
- 前端单独调试：`npm run dev`
- 生产发布优先入口：`python ../../scripts/release/package_monitor.py --release-id <release-id>`
- 本地 SDK 联调打包：`python ../../scripts/release/package_monitor.py --release-id <release-id> --sdk-source local-src`
- 底层 Tauri 构建命令：`npm run tauri:build`
- 安装包与桌面快捷方式按单实例运行；重复启动时会唤醒已有主窗口，不会创建新的 Monitor 进程。

`package_monitor.py` 会先校验 `package.json`、`package-lock.json`、`src-tauri/tauri.conf.json`、`src-tauri/Cargo.toml` 与共享版本元数据的一致性，再串联 `npm ci`、`npm run verify`、`npm run tauri:build`，并把 bundle 产物、校验日志、manifest 和 release notes 归档到 `artifacts/monitor/<release-id>/`。脚本支持 `--sdk-source {release,local-src}`；默认 `release` 用于正式打包校验，`--sdk-source local-src` 用于源码联调包，并会把当前生效的 `JS SDK` 版本写入产物说明。

当前 Monitor 的 CI 验证由独立的 `.github/workflows/monitor.yml` 承担；该 workflow 与 `package_monitor.py` 一样只服务于 Monitor 工作区验证，不接入 Host / SDK GitHub Release 自动发布链路。

Monitor 启动后会先扫描当前有效 `DEVHUB_DATA_DIR`，并持续自动搜索可用 Host。原生 discovery 会先确认 `runtime.protocolVersion=1` 与真实 `hub.ping` 成功，再优先调用 `hub.getVersion` 获取 Host 版本；仅在 `hub.getVersion` 返回 `method_not_found` 时回退到 `runtime.hubVersion`。只有确定兼容状态为 `incompatible` 时才会阻断发现态；`updateRecommended` 与 `unknown` 会继续允许前端建立会话，并在 `主页` 顶部和 `帮助` 工作区暴露诊断信息。若前端连接阶段再次得到保护性 `incompatible` 结果，当前 session 会被主动释放，并切回阻断式提示。若自动搜索约 3 秒后仍未发现可用 Host，`主页` 才会显示 `启动 Host`；若未配置 Host 可执行文件路径，则会引导用户进入 `设置` 工作区补全配置；若发现确定不兼容的 Host，则保留发现态并提供 `重新扫描` 与 `前往设置` 恢复动作。

读取 `settings.json` 时若发现损坏内容，Monitor 会先把原文件隔离为 `settings.json.corrupt-<timestamp>-<uuid>.bak`，随后回退默认设置继续启动，并在窗口顶部展示恢复告警。

## 日志与能力边界

- Host 日志固定来自 `<DEVHUB_DATA_DIR>/logs/`。
- Monitor 自身结构化日志写入 Monitor 本地数据目录下的 `logs/monitor-YYYYMMDD.jsonl`。
- 前端不提供内嵌日志列表或日志内容预览；需要排障时，通过 `帮助` 工作区直接调用系统外壳打开 Host / Monitor 日志目录。
- 前端在 `host_available` 后通过 `@devhub/sdk` 直接访问 Host `/rpc` 与 `/ws`，浏览器 / WebView 对 `/rpc` 的预检也由 Host 自身处理，不直接访问本地文件。
- 原生后端负责设置持久化、运行时发现、Host 启动、日志写入、日志目录打开和系统托盘，不代理 DevHub transport，也不直接依赖 `JS/TS SDK`。
- `DEVHUB_DATA_DIR` 覆盖值和 Host 可执行文件路径都只接受绝对路径；相对路径会在前端与原生命令层同时被拒绝。
- Windows 平台的设置工作区提供“隐藏 Host 命令行窗口”开关，默认启用。
