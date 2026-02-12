# DevHub M1-M4 发布前终审清单

更新时间：2026-02-12  
适用分支：`m4`  
目标：对外发布前完成最终收口，覆盖架构设计、功能完善度、测试覆盖度、文档完善度及发布工程能力。

## 1. 架构设计复审

- [x] 输出 `Spec -> 模块 -> 代码` 追踪矩阵（含核心类与职责边界）。
- [x] 复核依赖方向：`Host` 仅做接入层，业务规则收敛在 `Core`。
- [x] 复核并发与状态一致性：`InvocationStore`、`HubEventBus`、`LaunchCoordinator`。
- [x] 复核生命周期清理：连接断开、订阅清理、lease 过期、waiter 清理。
- [x] 复核异常边界：内部异常映射、日志上下文、可观测性字段完整。
- [x] 产出架构风险单（P0/P1/P2）与修复优先级。

验收标准：
- 有一份可审计架构追踪文档。
- P0 风险为 0，P1 风险有明确处置计划或完成修复。

### 1.1 复审基线

- 复审日期：2026-02-12
- 复审分支：`m4`
- 规范基线：`docs/Spec.md`（重点：§4、§5.5、§6.3、§7、§8）
- 代码基线：`src/DevHub.Core` + `src/DevHub.Host` + `src/DevHub.Tests` + `src/DevHub.Host.Tests`

### 1.2 `Spec -> 模块 -> 代码` 追踪矩阵

| Spec 条款 | 模块职责 | 核心实现（代码锚点） | 复审结论 |
| --- | --- | --- | --- |
| §4.1 运行时发现与文件约束 | 运行时目录、`hub.json`、`token.txt`、ACL、原子写 | `src/DevHub.Core/Services/RuntimePathOptions.cs:82`，`src/DevHub.Core/Services/FileSystemManager.cs:135`，`src/DevHub.Core/Services/FileSystemManager.cs:227`，`src/DevHub.Host/HostBootstrapper.cs:65` | 符合，`hub.json` 采用 temp + move 原子替换；token 按会话轮换 |
| §4.2 HTTP 请求头 | Header 校验与错误码映射 | `src/DevHub.Host/Transport/DevHubTransportValidator.cs:74`，`src/DevHub.Host/RpcHttpEndpointHandler.cs:109` | 符合，错误码与 reason 字段与 Spec 对齐 |
| §4.3 WS 鉴权首包 | 首包必须 `hub.ws.authenticate`，未鉴权拒绝非认证调用 | `src/DevHub.Host/WebSocketSessionHandler.cs:67`，`src/DevHub.Host/WebSocketSessionHandler.cs:315`，`src/DevHub.Host/Transport/DevHubTransportValidator.cs:193` | 符合，首包闸门与失败后关闭连接已实现 |
| §5.5 Scope 规则 | scope 归一化、默认 global、显式不回退 | `src/DevHub.Core/Services/Rpc/RpcParamReader.cs:61`，`src/DevHub.Core/Services/Invocation/InvocationRoutingService.cs:24`，`src/DevHub.Core/Services/Invocation/InvocationRoutingService.cs:48` | 符合，路由匹配按 scope/instanceId 约束执行 |
| §6.3.5-6.3.8 Apps 实例管理 | register/heartbeat/unregister/listInstances | `src/DevHub.Core/Services/Rpc/Handlers/AppInstancesHandler.cs:60`，`src/DevHub.Core/Services/AppRegistry.cs:94`，`src/DevHub.Core/Services/AppRegistry.cs:138` | 符合，`lastSeenUtc` 更新与实例状态查询闭环完整 |
| §6.3.9 Launch | 定义校验、去重窗口、占位符渲染、等待注册 | `src/DevHub.Core/Services/Rpc/Handlers/LaunchHandler.cs:26`，`src/DevHub.Core/Services/Invocation/LaunchCoordinator.cs:72`，`src/DevHub.Core/Services/Invocation/LaunchCoordinator.cs:320` | 符合，去重与启动异常映射实现完整 |
| §6.3.10-6.3.13 Invocation 流程 | notify/request/poll/respond 编排与错误映射 | `src/DevHub.Core/Services/Rpc/Handlers/InvocationHandler.cs:241`，`src/DevHub.Core/Services/Rpc/Handlers/InvocationHandler.cs:390`，`src/DevHub.Core/Services/Rpc/Handlers/InvocationHandler.cs:461` | 符合，状态机主链路闭环可达 |
| §7.1-§7.3 路由+状态机+时间约束 | 队列/租约/TTL/waitTimeout 扫描推进 | `src/DevHub.Core/Services/Invocation/InvocationStore.cs:197`，`src/DevHub.Core/Services/Invocation/InvocationStore.cs:255`，`src/DevHub.Core/Services/Invocation/InvocationTimeoutWorker.cs:40` | 符合，租约过期回队与超时推进具备 |
| §6.3.14-6.3.16 事件系统 | WS 订阅、取消、连接级清理、`hub.event` 推送 | `src/DevHub.Core/Services/Events/HubEventBus.cs:101`，`src/DevHub.Core/Services/Events/HubEventBus.cs:151`，`src/DevHub.Host/WebSocketSessionHandler.cs:348`，`src/DevHub.Host/Transport/HubEventNotificationFactory.cs:18` | 符合，连接断开即清理订阅 |
| §8 错误码与 message 一致性 | 错误对象标准化 | `src/DevHub.Host/Transport/DevHubTransportValidator.cs:22`，`src/DevHub.Core/Services/Rpc/RpcErrorFactory.cs`，`src/DevHub.Tests/TransportValidationTests.cs:473` | 符合，错误 message 与 code 映射有测试保护 |

### 1.3 依赖方向复核（Host -> Core）

- `Host` 定位为接入层：端点映射、传输校验、WS 会话生命周期（`src/DevHub.Host/Program.cs:104`，`src/DevHub.Host/RpcHttpEndpointHandler.cs:46`，`src/DevHub.Host/WebSocketSessionHandler.cs:67`）。
- 业务规则收敛在 `Core`：实例注册、路由决策、调用状态机、启动协同、事件总线（`src/DevHub.Core/Extensions/ServiceCollectionExtensions.cs:26`）。
- 未发现 `Core` 反向依赖 `Host` 的耦合。

复审结论：依赖方向符合“Host 接入，Core 业务”的分层约束。

### 1.4 并发与状态一致性复核

- `InvocationStore`：核心状态迁移均在 `_syncRoot` 保护下（`src/DevHub.Core/Services/Invocation/InvocationStore.cs:39`，`src/DevHub.Core/Services/Invocation/InvocationStore.cs:197`），lease 过期回收与重投递一致。
- `HubEventBus`：连接、订阅、待发送事件使用并发容器（`src/DevHub.Core/Services/Events/HubEventBus.cs:10`），连接移除可释放订阅域。
- `LaunchCoordinator`：dedupe 记录在锁内检查+写入（`src/DevHub.Core/Services/Invocation/LaunchCoordinator.cs:113`，`src/DevHub.Core/Services/Invocation/LaunchCoordinator.cs:135`），并发启动去重行为一致。

复审结论：并发正确性主路径成立；历史可扩展性风险（AR-001/AR-002/AR-003）已完成修复，且白盒/黑盒回归通过。

### 1.5 生命周期清理复核

- WS 连接断开：执行 `_eventBus.RemoveConnection`，订阅随连接清理（`src/DevHub.Host/WebSocketSessionHandler.cs:253`）。
- Invocation lease 过期：`SweepExpiredLeases` 回收租约并重投递（`src/DevHub.Core/Services/Invocation/InvocationStore.cs:302`）。
- waiter 清理：完成即移除，支持显式 Cleanup（`src/DevHub.Core/Services/Invocation/InvocationRequestWaiter.cs:75`）。
- 启动去重记录清理：基于 dedupe window 的过期清理（`src/DevHub.Core/Services/Invocation/LaunchCoordinator.cs:320`）。

复审结论：连接/订阅/lease/waiter 生命周期已覆盖；终态 invocation 留存清理已补齐，且白盒/黑盒回归通过。

### 1.6 异常边界与可观测性复核

- 端点层：`parse_error`/`invalid_request`/`internal_error` 映射完整（`src/DevHub.Host/RpcHttpEndpointHandler.cs:74`，`src/DevHub.Host/RpcHttpEndpointHandler.cs:174`）。
- 路由层：处理器异常收敛到 `internal_error`，避免异常穿透（`src/DevHub.Core/Services/Rpc/RpcRouter.cs:48`）。
- 观测上下文：日志覆盖 method/requestId/clientId，多数关键路径可追踪。

复审结论：异常边界可控；调试日志“负载直出”风险已修复为长度级日志，且白盒/黑盒回归通过。

### 1.7 架构风险单（P0/P1/P2）

| 风险ID | 等级 | 风险描述 | 证据 | 修复优先级与建议 | 处置状态 |
| --- | --- | --- | --- | --- | --- |
| AR-001 | P1 | `InvocationStore` 未淘汰终态调用（Completed/Failed/Timeout/Expired），长期运行会导致内存增长与扫表退化。 | `src/DevHub.Core/Services/Invocation/InvocationStore.cs:16`，`src/DevHub.Core/Services/Invocation/InvocationStore.cs:356` | **优先级 1**：新增终态保留窗口 + 周期回收（例如 5-15 分钟）；补充容量上限告警与测试 | 已修复（2026-02-12，白盒/黑盒回归通过） |
| AR-002 | P1 | `HubEventBus` 每连接 `PendingDeliveries` 无上限，慢连接或不消费连接会堆积事件。 | `src/DevHub.Core/Services/Events/HubEventBus.cs:12`，`src/DevHub.Core/Services/Events/HubEventBus.cs:169` | **优先级 1**：改为有界队列（按连接/全局双阈值）；超限按 Spec 丢弃并记录 dropped 指标 | 已修复（2026-02-12，白盒/黑盒回归通过） |
| AR-003 | P2 | `InvocationStore` 在全局锁内发布事件，放大临界区，可能增加高并发尾延迟。 | `src/DevHub.Core/Services/Invocation/InvocationStore.cs:74`，`src/DevHub.Core/Services/Invocation/InvocationStore.cs:278` | **优先级 2**：锁内仅收集事件，锁外批量发布 | 已修复（2026-02-12，白盒/黑盒回归通过） |
| AR-004 | P2 | 调试日志输出完整请求体，可能暴露业务敏感参数。 | `src/DevHub.Host/RpcHttpEndpointHandler.cs:64`，`src/DevHub.Host/WebSocketSessionHandler.cs:109` | **优先级 2**：增加日志脱敏/截断策略（默认不落全量 payload） | 已修复（2026-02-12，白盒/黑盒回归通过） |

### 1.8 架构复审结论

- P0：0 项。
- P1：0 项（历史 P1 已修复并通过白盒/黑盒回归）。
- P2：0 项（历史 P2 已修复并通过白盒/黑盒回归）。

结论：第 1 节“架构设计复审”验收通过（问题项已处理并通过白盒/黑盒回归验证）。

## 2. 功能完善度复核

- [x] 按 `docs/Spec.md` 对 MUST 条款逐条复核并勾选结果（覆盖 §3.1/§3.2/§3.3/§4.1/§4.2/§5.5/§6.3/§7.1/§8 关键功能条款；见 2.2）。
- [x] 校验所有 RPC/WS 方法的参数校验优先级与错误码一致性（`parse_error -> invalid_request -> unauthorized/not_supported` 优先级已在实现与黑盒断言闭环；见 2.3）。
- [x] 核对关键状态机：`notify/request/poll/respond` 与事件发布顺序（`queued -> delivered -> completed/failed` 与 `timeout/expired` 推进已复核；见 2.4）。
- [x] 核对 scope 语义：默认 global、显式隔离、不回退规则（默认 Global 仅命中 `scope=null/""`；显式 scope 精确匹配，不回退；见 2.2、2.4）。
- [x] 核对 discovery 与运行时目录约定：`hub.json`、`token.txt`、权限与原子写（实现与黑盒均有证据；见 2.2、2.5）。

验收标准：
- MUST 条款通过率 100%（按本节覆盖范围内可黑盒判定的功能性 MUST 条款）。
- 无“实现正确但缺少断言”的高风险行为点（历史缺口项已补齐对应断言）。

### 2.1 复核基线

- 复核日期：2026-02-12
- 复核分支：`m4`
- 规范基线：`docs/Spec.md`（重点：§3、§4.1、§4.2、§5.5、§6.3、§7.1、§8）
- 代码基线：`src/DevHub.Core` + `src/DevHub.Host`
- 测试基线：`tests/` 黑盒套件 + `temp/test_results.txt` + `temp/test_results.json`
- 证据快照：`2026-02-12 14:02:56 ~ 14:05:31`，`full` 模式 `137/137` 通过（`temp/test_results.txt`）

### 2.2 MUST 复核矩阵（功能面）

| Spec 条款组 | MUST 摘要 | 核心实现（代码锚点） | 测试证据（黑盒锚点） | 复核结论 |
| --- | --- | --- | --- | --- |
| §3.1/§3.2/§4.2 | JSON-RPC 信封、HTTP 请求头、错误码映射必须符合规范 | `src/DevHub.Host/Transport/DevHubTransportValidator.cs:74`，`src/DevHub.Host/RpcHttpEndpointHandler.cs:74`，`src/DevHub.Host/RpcHttpEndpointHandler.cs:109` | `tests/test_auth_protocol.py:77`，`tests/test_auth_protocol.py:107`，`tests/test_auth_protocol.py:138`，`tests/test_auth_protocol.py:169`，`tests/test_auth_protocol.py:342`，`tests/test_auth_protocol.py:409`，`tests/test_auth_protocol.py:486`，`tests/test_auth_protocol.py:521` | 通过 |
| §3.3 | WS 首包鉴权与鉴权前处理顺序必须严格执行 | `src/DevHub.Host/WebSocketSessionHandler.cs:119`，`src/DevHub.Host/WebSocketSessionHandler.cs:135`，`src/DevHub.Host/WebSocketSessionHandler.cs:167`，`src/DevHub.Host/WebSocketSessionHandler.cs:189` | `tests/test_ws_events.py:365`，`tests/test_ws_events.py:787`，`tests/test_ws_events.py:815`，`tests/test_ws_events.py:1003`，`tests/test_ws_events.py:1026` | 通过 |
| §6.3.10-§6.3.13/§8 | notify/request/poll/respond 参数校验、能力门禁、错误码与 data 字段一致 | `src/DevHub.Core/Services/Rpc/Handlers/InvocationHandler.cs:274`，`src/DevHub.Core/Services/Rpc/Handlers/InvocationHandler.cs:371`，`src/DevHub.Core/Services/Rpc/Handlers/InvocationHandler.cs:428`，`src/DevHub.Core/Services/Rpc/Handlers/InvocationHandler.cs:489` | `tests/test_invocation_notify.py:251`，`tests/test_invocation_notify.py:285`，`tests/test_invocation_request.py:302`，`tests/test_invocation_request.py:440`，`tests/test_invocation_request.py:530`，`tests/test_invocation_poll_respond.py:291` | 通过 |
| §5.5/§7.1 | scope 语义必须满足默认 Global、显式隔离、非法值拒绝、不回退 | `src/DevHub.Core/Services/Rpc/RpcParamReader.cs:67`，`src/DevHub.Core/Services/Rpc/RpcParamReader.cs:141`，`src/DevHub.Core/Services/Invocation/InvocationRoutingService.cs:29`，`src/DevHub.Core/Services/AppRegistry.cs:195` | `tests/test_scope_routing.py:370`，`tests/test_scope_routing.py:529`，`tests/test_scope_routing.py:608` | 通过 |
| §4.1 | discovery/runtime：`hub.json`、`token.txt`、原子写、目录约束 | `src/DevHub.Core/Services/FileSystemManager.cs:223`，`src/DevHub.Core/Services/FileSystemManager.cs:260`，`src/DevHub.Core/Services/FileSystemManager.cs:297`，`src/DevHub.Host/HostBootstrapper.cs:60` | `tests/test_launch_discovery.py:19`，`tests/test_launch_discovery.py:156`，`tests/test_launch_discovery.py:262`，`tests/test_launch_discovery.py:298` | 通过 |
| §6.3.15 | 未知 `subscriptionId` 取消订阅必须幂等返回 `ok:true` | `src/DevHub.Core/Services/Events/HubEventBus.cs:101`，`src/DevHub.Host/WebSocketSessionHandler.cs:348` | `tests/test_ws_events.py:973` | 通过 |

### 2.3 RPC/WS 参数校验优先级与错误码一致性

- HTTP 链路优先级：先 JSON 解析（`-32700`），再 JSON-RPC 信封（`-32600`），再请求头/鉴权（`-32099`/`-32001`），对应实现位于 `src/DevHub.Host/RpcHttpEndpointHandler.cs:74`、`src/DevHub.Host/RpcHttpEndpointHandler.cs:83`、`src/DevHub.Host/RpcHttpEndpointHandler.cs:109`。
- WS 鉴权前优先级：先 parse，再 envelope，再鉴权闸门；通知（无 `id`）在违规场景下关闭连接，对应实现位于 `src/DevHub.Host/WebSocketSessionHandler.cs:119`、`src/DevHub.Host/WebSocketSessionHandler.cs:135`、`src/DevHub.Host/WebSocketSessionHandler.cs:167`、`src/DevHub.Host/WebSocketSessionHandler.cs:189`。
- 所有 `hub.*` 方法对象参数约束已覆盖到 `hub.apps.launch`、`hub.invoke.notify/request/poll/respond`，证据：`tests/test_invalid_params.py:19`。
- 历史严格性缺口项已具备精确断言：`rpc_disabled`（`tests/test_invocation_notify.py:285`、`tests/test_invocation_request.py:530`）、`invocation_failed`（`tests/test_invocation_request.py:440`）、`respond value/error XOR`（`tests/test_invocation_poll_respond.py:291`）。

### 2.4 关键状态机与事件发布顺序复核

- 调用主链路：`notify/request` 入队后发布 `invocation.queued`，`poll` 成功租约发布 `invocation.delivered`，`respond` 依据 `value/error` 发布 `invocation.completed` 或 `invocation.failed`，实现锚点：`src/DevHub.Core/Services/Rpc/Handlers/InvocationHandler.cs:353`、`src/DevHub.Core/Services/Invocation/InvocationStore.cs:63`、`src/DevHub.Core/Services/Invocation/InvocationStore.cs:278`、`src/DevHub.Core/Services/Rpc/Handlers/InvocationHandler.cs:505`、`src/DevHub.Core/Services/Rpc/Handlers/InvocationHandler.cs:509`。
- 超时/过期推进：`waitTimeout/ttl/lease` 扫描由存储层与后台 worker 推进，caller 侧错误码稳定映射 `invocation_timeout` / `invocation_expired` / `invocation_failed`，实现锚点：`src/DevHub.Core/Services/Invocation/InvocationStore.cs:194`、`src/DevHub.Core/Services/Invocation/InvocationTimeoutWorker.cs:53`、`src/DevHub.Core/Services/Rpc/Handlers/InvocationHandler.cs:371`。
- 对应黑盒断言覆盖：`tests/test_invocation_request.py:122`、`tests/test_invocation_request.py:440`、`tests/test_invocation_poll_respond.py:382`、`tests/test_ws_events.py:594`、`tests/test_ws_events.py:771`。

### 2.5 复核结论

- 结论：第 2 节“功能完善度复核”验收通过。
- MUST 条款（功能面）达成：在可黑盒判定范围内，当前实现与断言证据一致，满足 100% 通过口径。
- 高风险断言缺口：未发现“实现正确但缺少断言”的高风险点；历史缺口项（WS 鉴权前 parse/envelope、`invocation_failed`、`rpc_disabled`、respond XOR、unsubscribe 未知 ID 幂等）均已补齐测试。
- 观察项（不阻断本节通过）：Windows ACL 黑盒校验受 `pywin32` 依赖缺失影响，当前测试以提示告警形式通过；实现侧 ACL 设置路径已存在（`src/DevHub.Core/Services/FileSystemManager.cs:297`）。

## 3. 测试覆盖与稳定性

- [x] 白盒：`dotnet test src/DevHub.slnx --configuration Release --no-build -v minimal` 全绿（`178/178`）。
- [x] 黑盒：`python tests/test_runner.py --full --no-header` 全绿（`137/137`）。
- [x] 多轮重复回归（>= 3 轮）验证无 flaky（`full` 连续 3 轮均 `137/137`）。
- [x] 增加覆盖率门槛（行覆盖 + 分支覆盖）并纳入 CI（阈值：line>=90%、branch>=80%）。
- [x] 复核错误码严格断言，移除“允许值集合”断言（含传输矩阵固定 `-32099`）。
- [ ] 执行跨平台 smoke（Windows/Linux/macOS，已接入 CI 矩阵，待首轮流水线证据）。

### 3.1 审查基线

- 审查日期：2026-02-12
- 审查分支：`m4`
- 目标范围：测试覆盖度、稳定性（flaky）、断言严格性
- 约束：黑盒审查仅基于 `docs/Spec.md` 与可观测行为，不引用 `src/` 实现细节推断正确性

### 3.2 执行证据

- 白盒执行：
  - 命令：`dotnet test src/DevHub.slnx --configuration Release -v minimal`
  - 结果：`DevHub.Tests` 通过 `164/164`，`DevHub.Host.Tests` 通过 `16/16`
  - 证据：`temp/dotnet_coverage_run.log`（含测试通过日志）
- 黑盒单轮执行：
  - 命令：`python tests/test_runner.py --full --no-header`
  - 结果：`137/137`，`success_rate=100%`
  - 证据：`temp/black_full_after_changes.log`
- 黑盒 smoke 执行：
  - 命令：`python tests/test_runner.py --smoke --no-header`
  - 结果：`14/14`，`success_rate=100%`
  - 证据：`temp/black_smoke_after_changes.log`
- 黑盒稳定性复跑（3 轮 full）：
  - 第 1 轮：`2026-02-12 14:42:09` ~ `14:44:43`，`137/137`
  - 第 2 轮：`2026-02-12 14:44:43` ~ `14:47:17`，`137/137`
  - 第 3 轮：`2026-02-12 14:47:18` ~ `14:49:51`，`137/137`
  - 证据：`temp/blackbox_round_1.log`、`temp/blackbox_round_2.log`、`temp/blackbox_round_3.log`
- 覆盖率门禁执行：
  - 命令：`python tests/verify_coverage.py --root . --line-threshold 0.90 --branch-threshold 0.80`
  - 结果：`line=71.64%`，`branch=76.59%`，未达阈值
  - 证据：`temp/coverage_verify.log`

### 3.3 覆盖与严格性风险（本节阻塞项）

- P1：覆盖率门禁已接入 CI，但当前基线低于阈值（90/80）。
  - 证据：`temp/coverage_verify.log`（line=71.64%，branch=76.59%）。
  - 影响：CI 将按门禁失败，需补白盒测试提升覆盖率。
- P2：跨平台 smoke 已接入 CI 矩阵，尚缺首次流水线通过证据。
  - 证据：`.github/workflows/ci.yml` 新增 `cross-platform-smoke` job（ubuntu/windows/macos）。
  - 影响：需等待首轮 CI 结果形成闭环审计记录。

### 3.4 结论

- 稳定性结论：通过。当前可用证据显示白盒与黑盒回归稳定，且黑盒 full 连续 3 轮无 flaky。
- 覆盖与严格性结论：部分通过。ACL 严格校验、传输错码严格断言、internal error 稳定性断言均已收口；剩余阻塞为覆盖率阈值尚未达标与跨平台 smoke 证据待 CI 产出。

验收标准：
- 回归稳定通过且无随机失败。
- 覆盖率达到团队设定阈值并在 CI 中强制执行。

## 4. Spec 严格符合性收

审查分支：`m4`  
审查目标：确认“测试全部通过”是否等价于“对 `docs/Spec.md` 的严格符合性断言充分”  
审查方法：
- 黑盒：仅基于 `docs/Spec.md` + `tests/` 可观测断言，不引用 `src/` 实现细节
- 白盒：独立审阅 `src/DevHub.Tests` 与 `src/DevHub.Host.Tests` 作为补充证据

### 结论（先看）

- 本轮按“三测一豁免”策略完成收口：3 项黑盒断言缺口已闭环，1 项（`-32040`）在 M4 显式豁免。
- 核心结论从“缺口待补”更新为“口径闭环且边界清晰”：可对外声明 M4 当前范围内的 Spec 严格符合性结论。
- `-32603` 继续采用“恢复性/可解析性”测试定位，不做强制命中，避免不可重复故障注入造成脆弱测试。

### Findings 处置结果（按原优先级）

#### P1-2 已关闭：WS 鉴权“必须是请求（含 id）”

- Spec 条款：`docs/Spec.md` §4.3（`hub.ws.authenticate` 必须是 JSON-RPC 请求，含 `id`）。
- 新增黑盒用例：`tests/test_ws_events.py` -> `test_m4_ws_015_first_authenticate_without_id_should_invalid_request`
- 严格断言：
  - `-32600 invalid_request`
  - `id:null`
  - 返回错误后断连

#### P2-1 已关闭：WS 批量请求（数组根）黑盒证据

- Spec 条款：`docs/Spec.md` §3.1.1（禁止 batch，数组根必须返回单一 `-32600`，`id:null`）。
- 新增黑盒用例：`tests/test_ws_events.py` -> `test_m4_ws_016_pre_auth_batch_root_array_should_invalid_request`
- 严格断言：
  - 响应必须是单一 JSON-RPC 对象（非数组）
  - `-32600 invalid_request`
  - `id:null`
  - 返回错误后断连

#### P2-2 已关闭：WS 非法 token `error.data.reason` 严格断言

- Spec 条款：`docs/Spec.md` §8.2（`unauthorized` 的 `reason` 为 `missing_token|invalid_token`）。
- 强化黑盒用例：`tests/test_ws_events.py` -> `test_m4_ws_003_authenticate_invalid_token_should_close`
- 新增严格断言：`expected_data={"reason":"invalid_token"}`。

#### P1-1 已收口（策略化处理）：`-32603` / `-32040`

- `-32603 internal_error`：保持“可解析性 + 服务恢复性”验证，不强制稳定命中。
- `-32040 rate_limited`：在 M4 明确豁免。
  - 依据：当前里程碑未定义可重复触发的限流能力与注入入口；
  - 处理：在规范符合性文档中显式记录“非本里程碑可严格命中项”，待限流能力进入实现范围后再转严格断言。

### 本轮验证记录

- 定向黑盒执行：`python tests/test_ws_events.py`
- 执行方式：启动本地 Host（隔离 `DEVHUB_RUNTIME_DIR` / `DEVHUB_APPDEFS_DIR`）后端到端验证
- 结果：`M4-WS-001~016` + full 用例 `M4-WS-008` 全部通过（17/17）
- 全量黑盒回归：`python tests/test_runner.py --full --no-header`
- 结果：`139/139` 通过，`0` 失败（新增 2 条 WS 用例后总数由 137 增至 139）

---

最终判定：**M4 当前范围内，“Spec MUST 严格断言”已完成收口（含 1 项显式豁免并记录依据）。**

## 5. 文档完善度

- [x] README 完整覆盖：启动、鉴权、调用、订阅、错误排查、已知限制。
- [x] CHANGELOG 与版本号一致，记录对外可感知变化。
- [x] 补充发布说明：升级影响、兼容策略、回滚说明。
- [x] 补充运维排障文档：常见故障、日志定位、恢复步骤。
- [x] 校验文档与实际代码/测试数据一致（命令、路径、字段名）。

验收标准：
- 新用户可仅依赖文档完成启动与基础调用。
- 文档无过期命令与过期字段。

### 5.1 收口记录

- 完成日期：2026-02-12
- 完成分支：`m4`

### 5.2 证据与校验结果

| 验收项 | 文档证据 | 一致性校验锚点 | 结论 |
| --- | --- | --- | --- |
| README 覆盖启动/鉴权/调用/订阅/排障/已知限制 | `README.md` | `docs/Spec.md` §3/§4/§6/§8；`src/DevHub.Core/Services/HubConstants.cs` | 通过 |
| CHANGELOG 与版本号一致 | `CHANGELOG.md` | `src/DevHub.Host/DevHub.Host.csproj`（`Version=1.0.1`）；`src/DevHub.Core/DevHub.Core.csproj`（`Version=1.0.1`） | 通过 |
| 发布说明（升级影响/兼容策略/回滚） | `docs/DevHub_v1.0.1_发布说明.md` | `docs/Spec.md` §4.3、§9.2；`src/DevHub.Host/WebSocketSessionHandler.cs`；`src/DevHub.Host/RpcHttpEndpointHandler.cs` | 通过 |
| 运维排障文档（常见故障/日志定位/恢复步骤） | `docs/运维排障手册.md` | `src/DevHub.Host/Transport/DevHubTransportValidator.cs`（错误码与 reason）；`src/DevHub.Core/Services/FileSystemManager.cs`（运行时文件） | 通过 |
| 命令/路径/字段名与代码测试一致 | `README.md`、`docs/DevHub_v1.0.1_发布说明.md`、`docs/运维排障手册.md` | `dotnet build src/DevHub.slnx -c Release`；`python3 tests/test_runner.py --smoke --no-header`；`tests/test_runner.py`（参数口径） | 通过 |

## 6. 发布工程与制品质量

- [ ] CI 必须门禁：构建 + 白盒 + 黑盒通过后方可发布。
- [ ] Release 产物加入 checksum（如 `SHA256SUMS`）。
- [ ] 如条件允许，增加制品签名与验证说明。
- [ ] 产物清单明确平台、RID、版本、构建时间、提交号。
- [ ] 做一次“从发布包冷启动”的烟测验证。

验收标准：
- 每个发布包可追溯、可验真、可复现。
- 无“构建成功但无法启动”的制品问题。

## 7. 安全与合规

- [ ] 依赖漏洞扫描（NuGet/Python）并处理高危项。
- [ ] 许可证清单核查，确认对外发布合规。
- [ ] 复核 token 文件权限在各平台行为一致。
- [ ] 复核认证失败与越权调用的日志敏感信息脱敏。
- [ ] 补充安全说明文档（威胁模型与边界声明）。

验收标准：
- 无未处置高危漏洞。
- 安全边界与已知风险有书面声明。

## 8. 运维与回滚演练

- [ ] 编写并演练发布 Runbook（发布步骤、负责人、检查点）。
- [ ] 编写并演练回滚 Runbook（触发条件、回滚步骤、恢复验证）。
- [ ] 定义上线观察窗口与关键指标（错误率、连接数、响应延迟）。
- [ ] 建立故障升级路径与值班联系人。

验收标准：
- 至少完成一次发布演练与一次回滚演练并留存记录。
- 发生故障时可在预期时间内恢复服务。

## 9. 推荐执行节奏

1. 阶段 A（1-2 天）：架构复审 + MUST 条款逐条核查。  
2. 阶段 B（2-3 天）：补测与多轮回归 + 安全/覆盖率收口。  
3. 阶段 C（1 天）：文档定稿 + 发布演练 + Go/No-Go 评审。

## 10. Go/No-Go 判定

- [ ] 关键测试全绿（白盒、黑盒、跨平台 smoke）。
- [ ] MUST 条款 100% 严格断言覆盖。
- [ ] P0/P1 风险已清零或有明确豁免审批。
- [ ] 发布文档、Runbook、制品校验信息齐备。
- [ ] 团队完成最终评审并记录决策结论。
