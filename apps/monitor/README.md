# DevHub Monitor

`apps/monitor/` 是 DevHub 的官方桌面 Monitor 工作区，使用 Tauri 2 提供运行时发现、Host 启动、日志访问、系统托盘和前端 WebView 桥接能力。

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
- `npm test`：执行前端侧边栏导航、主页 phase 切换、帮助/设置页面、Definition 页面工作流，以及基于真实 Host fixture 的前端回归。
- `npm run test:native`：执行 `src-tauri/` 原生后端单元测试。
- `npm run tauri:check`：执行 Tauri 原生侧非平台特定编译校验。
- `npm run verify`：串联上述全部验证入口。

## 目录说明

- `src/`：前端 WebView 工程；`App.tsx` 负责 `主页 / 帮助 / 设置 / Definition` 多工作区状态编排，bootstrap / Host 会话 / Definition 编辑分别落在独立 hooks，壳层通过侧边栏驱动切换。
- `src-tauri/`：Rust 原生后端；Tauri command 只做参数校验与转发，设置、快照、discovery、Host 启动、日志写入与日志目录打开能力由独立服务协作。
- `../../sdks/javascript`：前端 Host 通信依赖来源，当前通过本地 `file:` 依赖映射为 `@devhub/sdk`。

## 主界面结构

- 左侧侧边栏提供 `主页`、`帮助`、`设置` 三个一级工作区入口，并支持折叠为图标模式；Definition 工作区由主页中的资产操作打开，不出现在一级导航中。
- `主页` 保持阶段驱动：未发现可用 Host 时展示发现 / 启动流程，发现并验证可用 Host 后在同一工作区内切换为连接摘要、应用定义和实例清单。
- `帮助` 是独立页面，集中提供 `打开 Host 日志` 与 `打开 Monitor 日志` 两个动作，并展示当前运行信息。
- `设置` 是独立页面，显示设置草稿、解析结果和字段级校验；保存成功后返回 `主页`，继续 discovery 或运行态流程。
- App Definition 通过独立页面工作流处理 `create / edit / view / missing` 四种状态；主页的 Definition 列表和 Instance 列表都会导航到该页面。
- `DEVHUB_DATA_DIR` 覆盖值和 Host 可执行文件路径都只接受绝对路径；相对路径会在前端与原生命令层同时被拒绝。

## 运行方式

- 开发态桌面运行：`npm run tauri:dev`
- 前端单独调试：`npm run dev`
- 生产发布优先入口：`python ../../scripts/release/package_monitor.py --release-id <release-id>`
- 底层 Tauri 构建命令：`npm run tauri:build`
- 安装包与桌面快捷方式按单实例运行；重复启动时会唤醒已有主窗口，不会创建新的 Monitor 进程。

`package_monitor.py` 会先校验 `package.json`、`package-lock.json`、`src-tauri/tauri.conf.json` 与 `src-tauri/Cargo.toml` 的版本一致性，再串联 `npm ci`、`npm run verify`、`npm run tauri:build`，并把 bundle 产物、校验日志、manifest 和 release notes 归档到 `artifacts/monitor/<release-id>/`。

当前 Monitor 的一键发布脚本只负责本地打包，不接入仓库现有的 GitHub Release / CI 自动发布流程。

Monitor 启动后会先扫描当前有效 `DEVHUB_DATA_DIR`，只有在真实 `hub.ping` 校验成功后才切换到运行态摘要。若未配置 Host 可执行文件路径，启动请求会保留当前壳层，并引导用户进入 `设置` 工作区补全配置。

## 日志与能力边界

- Host 日志固定来自 `<DEVHUB_DATA_DIR>/logs/`。
- Monitor 自身结构化日志写入 Monitor 本地数据目录下的 `logs/monitor-YYYYMMDD.jsonl`；当前目录会显示在 `设置` 工作区的“Monitor 日志目录”字段中。
- 前端不提供内嵌日志列表或日志内容预览；需要排障时，通过 `帮助` 工作区直接调用系统外壳打开 Host / Monitor 日志目录。
- 前端在 `host_available` 后通过 `@devhub/sdk` 直接访问 Host `/rpc` 与 `/ws`，浏览器 / WebView 对 `/rpc` 的预检也由 Host 自身处理，不直接访问本地文件。
- 原生后端负责设置持久化、运行时发现、Host 启动、日志写入、日志目录打开和系统托盘，不代理 DevHub transport，也不直接依赖 `JS/TS SDK`。
