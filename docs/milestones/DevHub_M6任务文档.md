# DevHub M6 里程碑跟踪

本文档是 `m6` 分支的执行口径，负责记录 M6 的阶段拆解、验收标准、当前状态与遗留项。

参考入口：

- [Spec.md](../spec/Spec.md)：协议契约、字段命名、状态转换、错误语义与测试断言的唯一权威来源。
- [DevHub协议与开发规划.md](../architecture/DevHub协议与开发规划.md)：架构边界、设计取舍与长期演进原则。
- [开发指南.md](../guides/开发指南.md)：仓库开发、联调与最小验证流程。
- [部署与运行指南.md](../operations/部署与运行指南.md)：部署、运行与上线后检查。

## 1. 里程碑目标

- 收敛 Host 的 `DevHub.Core` / `DevHub.Host` 职责边界，让协议适配、连接会话、后台生命周期与运行时上下文回到 Host 层。
- 收敛 `.NET SDK`、`JS/TS SDK`、`Python SDK` 的稳定公共面，使公开能力面向客户端能力而不是底层 transport 或连接实现。
- 重新整理 `milestones/`、`architecture/`、`guides/`、`operations/` 与工作区 README 的权威分工，让文档体系可导航、可执行、可维护。
- 通过最小相关测试与 Host 黑盒 smoke 锁定本次收敛结果，确保进入后续发布准备前没有明显回归。

## 2. 阶段总览

| 阶段 | 范围 | 状态 | 验收要点 |
| --- | --- | --- | --- |
| Phase 1 | Host boundary convergence | 已完成 | Core transport-agnostic、Host 负责协议适配与会话交付、后台任务采用托管生命周期 |
| Phase 2 | SDK public surface convergence | 已完成 | 三套 SDK 收敛稳定公共面、共享会话/参数规则、测试与 README 同步更新 |
| Phase 3 | Documentation layering cleanup | 已完成 | 里程碑文档承载执行口径，架构文档只保留边界与取舍，稳定指南回到当前有效做法 |
| Phase 4 | Verification and closure | 已完成 | 最小相关 Host / SDK 测试与黑盒 smoke 已通过，并已记录实际验收结果 |

## 3. 分阶段拆解

### Phase 1. Host boundary convergence

**状态：已完成**

任务拆解：

- [x] 1.1 为定义、实例与调用链路引入 typed 应用服务/结果模型，并把 JSON-RPC 参数读取、错误码映射与响应组装下沉到 `DevHub.Host` 适配层。
- [x] 1.2 拆分 `HubEventBus` 的职责：让 `DevHub.Core` 只发布传输无关的领域事件，并在 `DevHub.Host` 中实现连接认证、订阅、待投递队列与 `hub.event` 通知桥接。
- [x] 1.3 将 `AppRegistry`、`InvocationTimeoutWorker` 等周期任务改造成 `IHostedService` / `BackgroundService`，同时提供 Host 内部运行时上下文供启动编排解析 `{httpBaseUrl}`。
- [x] 1.4 补齐 Host 白盒与黑盒回归验证，覆盖参数校验映射、事件交付、后台任务生命周期与运行时地址解析不回归。

阶段验收标准：

- `DevHub.Core` 对外只暴露 typed 应用服务、领域结果与传输无关的领域事件。
- `DevHub.Host` 独立承担 JSON-RPC 参数读取、错误码映射、响应组装、WebSocket 鉴权和连接级投递管理。
- 周期任务通过 ASP.NET Core 托管生命周期启动和停止，不依赖单例解析或构造函数副作用。
- Host 白盒与黑盒测试能覆盖以上边界，不依赖临时调试行为。

### Phase 2. SDK public surface convergence

**状态：已完成**

任务拆解：

- [x] 2.1 收敛 `.NET SDK` 的稳定公共面，移除或缩小低层 transport / session 暴露，并让默认依赖注入路径接入标准 `HttpClient` 管道。
- [x] 2.2 更新 `.NET SDK` 测试与 README，验证新的扩展 seam、默认 DI 行为和主要客户端能力保持可用。
- [x] 2.3 收敛 `JS/TS SDK` 的连接模型，隐藏 bearer token 与原始连接上下文，并为 `DevHubClient` / `DevHubEventsClient` 提供共享的默认 `clientSessionId`。
- [x] 2.4 更新 `JS/TS SDK` 的单元测试、类型测试与文档，覆盖脱敏运行时视图和默认会话一致性。
- [x] 2.5 重构 `Python SDK` 的 WebSocket 会话与共享 payload builder，使事件解析上移到传输层之外，并统一 HTTP / WS 的本地参数校验。
- [x] 2.6 更新 `Python SDK` 的单元测试与 README，验证传输/协议分层和共享参数构造规则。

阶段验收标准：

- 稳定公共面默认面向 `DevHubClient`、`DevHubEventsClient` 等能力入口，而不是默认暴露 transport / session 实现。
- 默认会话身份、参数构造与本地防御式校验在同语言的 HTTP / WS 路径之间保持一致。
- 三套 SDK 的 README、接入文档与测试基线与实际公开面一致。

### Phase 3. Documentation layering cleanup

**状态：已完成**

任务拆解：

- [x] 3.1 将 `docs/milestones/DevHub_M6任务文档.md` 重写为带阶段拆解、验收标准、状态与遗留项的实际里程碑跟踪文档。
- [x] 3.2 收敛 `docs/architecture/DevHub协议与开发规划.md` 的职责，只保留架构边界与设计取舍，并同步调整 `docs/README.md` 的权威入口说明。
- [x] 3.3 清理 `docs/guides/`、`docs/operations/` 与相关工作区 README 中的阶段性分支标签和串层引用，使稳定文档只描述当前有效做法。

阶段验收标准：

- `docs/milestones/` 承载里程碑的执行范围、状态、验收标准与遗留项，不再只是背景说明。
- `docs/architecture/` 只负责架构边界、设计取舍与演进原则；协议细节回指 `Spec.md`，阶段任务回指里程碑文档。
- `docs/README.md` 能直接区分 `spec/`、`milestones/`、`architecture/`、`guides/`、`operations/` 的权威职责。
- `docs/guides/`、`docs/operations/` 与相关工作区 README 不再维护 `适用分支`、`M6 期间` 等阶段标签；若涉及当前阶段执行范围，统一回指本文件。

### Phase 4. Verification and closure

**状态：已完成**

任务拆解：

- [x] 4.1 运行最小相关 `.NET`、JavaScript、Python 测试目标，确认 Host 与三套 SDK 的边界收敛没有引入回归。
- [x] 4.2 执行 `python3 host/tests/blackbox/test_runner.py --smoke --no-header` 冒烟验证，并记录本次 change 的实际验收命令与结果。

阶段验收标准：

- Host、`.NET SDK`、`JS/TS SDK`、`Python SDK` 的最小相关本地测试通过。
- Host 黑盒 smoke 通过，且使用的是与当前工作区一致的本地代码。
- 实际执行过的命令、结果与必要说明记录在本文件中，便于后续追溯。

## 4. 本次验收记录

执行日期：`2026-04-12`

| 命令 | 结果 |
| --- | --- |
| `dotnet test host/DevHub.slnx -c Release` | 通过。`DevHub.Tests` 265 passed，`DevHub.Host.Tests` 132 passed。 |
| `dotnet test sdks/dotnet/DevHub.DotNetSdk.slnx -c Release` | 通过。`.NET SDK` 单元测试 93 passed，集成测试 25 passed。 |
| `npm --prefix sdks/javascript test` | 通过。Vitest 共 13 个 test files、118 个 tests 全部通过。 |
| `python3 -m pytest sdks/python/tests` | 通过。共 175 个 tests 全部通过。 |
| `DEVHUB_DATA_DIR=/tmp/devhub-blackbox-smoke-address-m6-review-findings dotnet run --project host/src/DevHub.Host/DevHub.Host.csproj -c Release --no-build --no-launch-profile` | 通过。以独立数据根目录启动本地 Host，供 blackbox smoke 使用。 |
| `DEVHUB_DATA_DIR=/tmp/devhub-blackbox-smoke-address-m6-review-findings python3 host/tests/blackbox/test_runner.py --smoke --no-header` | 通过。14/14 tests passed，报告输出到 `temp/test_results.json` 与 `temp/test_results.txt`。 |

## 5. 遗留项

- 正式发布资产的版本号、下载链接、安装命令与包名仍由 `TODO(devhub-release)` 占位，待实际发布产物确定后再替换。
- 新的结构性治理任务应通过后续里程碑文档单独管理，不在本文件中并行追加新的执行范围。
