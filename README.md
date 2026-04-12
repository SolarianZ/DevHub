# DevHub

DevHub 是面向本机单用户场景的守护进程（Local Per-user Daemon），为各类工具提供实例注册、发现、调用编排与事件订阅能力。

本仓库包含协议规范、`.NET` 核心实现、宿主程序、多语言 SDK、官方桌面 `DevHub Monitor` 以及白盒/黑盒测试套件。所有公开行为、字段命名、状态转换、错误语义与序列化契约均以 [`docs/spec/Spec.md`](docs/spec/Spec.md) 为唯一权威标准。

完整文档导航见 [`docs/README.md`](docs/README.md)。

## 对外入口

### Host 上手

- [`docs/guides/getting-started/README.md`](docs/guides/getting-started/README.md)：面向首次使用 DevHub Host 的入口导航。
- [`docs/guides/getting-started/host-quickstart.md`](docs/guides/getting-started/host-quickstart.md)：前置条件、启动方式、`hub.json` / `tokenFile` 发现与最小 `hub.ping` 验证。
- [`docs/operations/部署与运行指南.md`](docs/operations/部署与运行指南.md)：当前可直接参考的启动、数据根目录与运行说明。

### 桌面 Monitor

- [`apps/monitor/README.md`](apps/monitor/README.md)：官方桌面 Monitor 的工作区结构、本地运行命令、验证入口与日志位置说明。
- [`docs/operations/部署与运行指南.md`](docs/operations/部署与运行指南.md)：Host 与 Monitor 的本地启动、数据目录与日志落点说明。
- [`docs/operations/运维排障手册.md`](docs/operations/运维排障手册.md)：Host / Monitor 日志定位与排障入口。

### SDK 接入

- [`docs/guides/sdk/README.md`](docs/guides/sdk/README.md)：官方 SDK 接入入口与语言路径导航。
- [`docs/guides/sdk/dotnet.md`](docs/guides/sdk/dotnet.md)：`.NET SDK` 接入指南。
- [`docs/guides/sdk/javascript.md`](docs/guides/sdk/javascript.md)：`JS/TS SDK` 接入指南。
- [`docs/guides/sdk/python.md`](docs/guides/sdk/python.md)：`Python SDK` 接入指南。
- [`sdks/dotnet/README.md`](sdks/dotnet/README.md)：`.NET SDK` 工作区与当前公开能力。
- [`sdks/javascript/README.md`](sdks/javascript/README.md)：`JS/TS SDK` 工作区与当前公开能力。
- [`sdks/python/README.md`](sdks/python/README.md)：`Python SDK` 工作区与当前公开能力。

### 无 SDK 接入

- [`docs/guides/无SDK接入指南.md`](docs/guides/无SDK接入指南.md)：面向直接对接原始协议的第三方开发者。

### 仓库改造与贡献

- [`docs/guides/contributor/README.md`](docs/guides/contributor/README.md)：仓库改造、贡献与治理入口。
- [`docs/guides/contributor/repository-contribution.md`](docs/guides/contributor/repository-contribution.md)：贡献流程、本地打包与发布准备入口。
- [`docs/guides/开发指南.md`](docs/guides/开发指南.md)：本地开发环境、构建、运行与验证流程。
- [`docs/architecture/DevHub协议与开发规划.md`](docs/architecture/DevHub协议与开发规划.md)：架构背景、里程碑与开发规划。
- [`docs/milestones/DevHub_M6任务文档.md`](docs/milestones/DevHub_M6任务文档.md)：当前阶段任务与验收边界。
- [`CONTRIBUTING.md`](CONTRIBUTING.md)：贡献与 PR 入口。
- [`CHANGELOG.md`](CHANGELOG.md)：变更跟踪入口。
- [`SECURITY.md`](SECURITY.md)：安全与支持入口。

### 发布与维护

- [`docs/operations/publishing/README.md`](docs/operations/publishing/README.md)：发布流程、资产约定、检查清单与 TODO 占位规范入口。
- [`docs/operations/publishing/release-process.md`](docs/operations/publishing/release-process.md)：preview / main / stable 发布流程。
- [`docs/operations/publishing/release-asset-layout.md`](docs/operations/publishing/release-asset-layout.md)：统一发布资产布局、命名规则与 manifest。
- [`docs/operations/publishing/release-checklist.md`](docs/operations/publishing/release-checklist.md)：发布前检查、发布后核验与 dry-run 入口。
- [`docs/operations/运维排障手册.md`](docs/operations/运维排障手册.md)：日志定位、常见故障与恢复步骤。

## 正式发布前的版本与安装说明

当正式 GitHub Release 资产、下载链接或安装命令尚未确定时，仓库文档统一使用显式 TODO 占位，并明确未来将由哪个发布资产替换；不会伪造未发布的版本号、下载地址或仓库外安装命令。占位规范见 [`docs/operations/publishing/README.md`](docs/operations/publishing/README.md)。

## 规范与验证参考

- [`docs/spec/Spec.md`](docs/spec/Spec.md)：公开协议与对外契约。
- [`docs/spec/schema/v1.0.1/README.md`](docs/spec/schema/v1.0.1/README.md)：版本化 Schema 包。
- [`docs/spec/protocol-examples/v1.0.1/README.md`](docs/spec/protocol-examples/v1.0.1/README.md)：HTTP / WebSocket 原始 JSON 示例。
- [`host/tests/README.md`](host/tests/README.md)：Python 黑盒测试夹具与执行方式。
- [`host/tests/conformance/README.md`](host/tests/conformance/README.md)：跨语言符合性向量、运行方式与失败快照说明。
