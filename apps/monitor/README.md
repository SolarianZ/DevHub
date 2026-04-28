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
- `python ../../scripts/release/package_monitor.py --release-id monitor-local-check --verify-only`

## 环境要求

- Node.js：`20.19+` 或 `22.12+`
- npm：使用仓库当前 lock 文件对应的 `npm ci`
- Rust：按 Tauri 2 官方要求安装稳定工具链

## 验证入口

推荐先安装本地依赖：

- `npm ci`

与主 CI `monitor-validation` job 对齐的验证命令：

- `npm run build:web`：构建前端并执行类型检查。
- `npm test`：执行前端侧边栏导航、主页 phase 切换、帮助/设置页面、Definition 页面工作流，以及基于真实 Host fixture 的前端回归。
- `npm run test:native`：执行 `src-tauri/` 原生后端单元测试。
- `npm run tauri:check`：执行 Tauri 原生侧非平台特定编译校验。
- `npm run verify`：串联上述全部验证入口。

仓库主 `ci.yml` 中的 `monitor-validation` job 运行上述验证，并执行 `python scripts/release/package_monitor.py --release-id monitor-ci --verify-only`。Monitor 的构建、测试、类型检查、版本元数据和打包脚本均解析当前仓库 `sdks/javascript` 源码。

对齐该 workflow 的本地验收入口：

- `npm run verify`

验证前提是：只在 `apps/monitor/` 执行 `npm ci`，也能完成 `build:web`、`test` 与 `verify`。该路径不要求额外执行 `npm --prefix sdks/javascript ci`，也不依赖预先存在的 `sdks/javascript/node_modules`。

## SDK 来源

- `@devhub/sdk` 解析到 `../../sdks/javascript/src/index.ts`。
- `@devhub/sdk/runtime` 解析到 `../../sdks/javascript/src/runtime.ts`。
- Vite、Vitest、TypeScript 类型检查、版本元数据同步与 Monitor 打包脚本使用同一源码来源。
- `@devhub/sdk` 根入口保持浏览器 / WebView 安全；仅供 Node 使用的能力通过 `@devhub/sdk/runtime` 子路径暴露。

## 目录说明

- `src/`：前端 WebView 工程；`App.tsx` 负责 `主页 / 测试 / 帮助 / 设置 / Definition` 多工作区状态编排，bootstrap / Host 会话 / 测试页状态 / Definition 编辑分别落在独立 hooks，壳层通过侧边栏驱动切换。
- `src-tauri/`：Rust 原生后端；Tauri command 只做参数校验与转发，设置、快照、discovery、Host 启动、日志写入与日志目录打开能力由独立服务协作。
- `@devhub/sdk`：前端 Host 通信公开入口，由构建工具映射到仓库内 `sdks/javascript/src/index.ts`。

## 工作区概览

- `主页`：展示 Host 搜索状态、运行态摘要、App Definition 与 App 实例库存；当兼容状态为 `updateRecommended` 或 `unknown` 时显示非阻断版本提示，便于继续操作同时保留诊断信息。
- `测试`：显示当前 Host 的 RPC 地址，支持录入原始 JSON-RPC 文本、执行本地校验、发送请求、取消等待，并展示 Host 返回的原始响应文本；没有可用 Host 连接时仅展示不可用提示并禁用发送。
- `帮助`：展示 `Monitor` 版本、内置 `JS SDK` 版本、当前 `Host` 版本、兼容状态、日志目录与运行时路径辅助信息。
- `设置`：管理数据目录、Host 可执行文件路径与平台相关设置。
- `Definition`：承载新增、编辑、只读查看和缺失定义恢复等 Definition 工作流。

## 运行方式

- 开发态桌面运行：`npm run tauri:dev`
- 前端单独调试：`npm run dev`
- Monitor 单平台组件脚本入口：`python ../../scripts/release/package_monitor.py --release-id <release-id>`
- preview/main 发布候选编排入口：`python ../../scripts/release/package_release.py --release-id <release-id> --channel preview|main-snapshot`
- 底层 Tauri 构建命令：`npm run tauri:build`
- 安装包与桌面快捷方式按单实例运行；重复启动时会唤醒已有主窗口，不会创建新的 Monitor 进程。

`package_monitor.py` 会先校验 `package.json`、`package-lock.json`、`src-tauri/tauri.conf.json`、`src-tauri/Cargo.toml` 与共享版本元数据的一致性，再串联 `npm ci`、`npm run verify`、`npm run tauri:build`，并把 bundle 产物、校验日志、manifest 和 release notes 归档到 `artifacts/monitor/<release-id>/`。产物 manifest 记录 Monitor 版本、仓库源码 JS SDK 版本、目标平台和 bundle 资产。命令行中出现 `--help` 时，脚本只输出能力与参数摘要，不执行版本检查、验证或打包逻辑；`--verify-only` 只执行版本与验证检查，不生成 bundle。

preview/main 发布链通过主 CI 验证 Monitor，并在 `.github/workflows/release-reusable.yml` 的 Linux、Windows、macOS 矩阵中生成 Monitor App 资产。稳定版发布资产集合维持 Host 与 SDK 资产。

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
