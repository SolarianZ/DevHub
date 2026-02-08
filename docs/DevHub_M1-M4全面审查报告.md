# DevHub M1~M4 全面审查报告（逐条审计版）

> 审查日期：2026-02-08  
> 审查分支：`m4`  
> 审查范围：`src/DevHub.Host`、`src/DevHub.Core`、`src/DevHub.Tests`、`tests`  
> 结论粒度：验收项级（逐条）

---

## 1. 审查基线与方法

### 1.1 基线优先级
1. `docs/Spec.md`（协议权威）
2. `docs/DevHub协议与开发规划.md`（M1~M4 里程碑边界）
3. `docs/DevHub_M1细化任务文档.md` ~ `docs/DevHub_M4细化任务文档.md`
4. `docs/DevHub_M2测试任务拆分文档.md` ~ `docs/DevHub_M4测试任务拆分文档.md`

### 1.2 实测命令快照
- 白盒：`dotnet test src/DevHub.slnx -c Release`
  - 结果：`90 passed / 0 failed / 0 skipped`
- 黑盒：`python3 tests/test_runner.py --full --no-header`
  - 结果：`109 passed / 0 failed`
  - 报告：`temp/test_results.txt`、`temp/test_results.json`

### 1.3 风险等级定义
- `P0`：阻断里程碑验收/协议 MUST 违规
- `P1`：存在明确行为风险，建议优先修复
- `P2`：非阻断问题或覆盖增强项

---

## 2. 总体结论

- **M1：完成**（HTTP 基础能力、发现、鉴权、实例管理闭环成立）
- **M2：完成（附 1 条 P1 工程风险）**（Invocation 主链路、离线矩阵、lease/TTL 规则已落地）
- **M3：完成**（scope 语义一致性、隔离与不回退规则符合 Spec）
- **M4：完成（附 1 条 P2 覆盖增强项）**（WS 认证 + 订阅 + 事件推送主链路成立）

测试覆盖判定：**充分（可验收）**，并存在少量可补强项（见第 4 章）。

---

## 3. 逐条审计矩阵

## 3.1 M1（Hub HTTP 基础能力）

| 检查项ID | 验收项 | 实现状态 | 代码证据 | 测试证据 | 风险 | 结论与建议 |
| --- | --- | --- | --- | --- | --- | --- |
| M1-CHK-001 | 启动生成 `runtime/hub.json` 与 `runtime/token.txt` | 完成 | `FileSystemManager.GetToken`、`WriteHubJson`；`Program.Main` 启动流程 | `tests/test_launch_discovery.py`（发现文件存在） | P0 | 满足里程碑要求。 |
| M1-CHK-002 | `/rpc` JSON-RPC 接入，错误仍 HTTP 200 | 完成 | `Program.cs` `MapPost("/rpc")`，统一 `Results.Json` | `tests/test_auth_protocol.py::test_http_status_code_always_200` | P0 | 满足 Spec 与里程碑要求。 |
| M1-CHK-003 | Header 校验：`Authorization`、`X-DevHub-Protocol`、客户端标识 | 完成 | `Program.ValidateHeaders` | `tests/test_auth_protocol.py` 缺 token/协议/客户端头用例 | P0 | 语义符合 Spec。 |
| M1-CHK-004 | `hub.ping` 可用 | 完成 | `HubPingHandler` | `tests/test_auth_protocol.py::test_ping_with_valid_credentials` | P0 | 正常。 |
| M1-CHK-005 | `hub.apps.listDefinitions/getDefinition` 可读定义目录 | 完成 | `AppDefinitionsHandler` + `DefinitionLoader.Load()` | `tests/test_app_definitions.py` | P0 | 正常。 |
| M1-CHK-006 | `register/heartbeat/unregister/listInstances` 基础实例管理 | 完成 | `AppInstancesHandler` + `AppRegistry` | `tests/test_app_instances.py` | P0 | 正常。 |
| M1-CHK-007 | `lastSeenUtc` 在 register/heartbeat 更新，30s 在线判定 | 完成 | `AppRegistry.RegisterInstance`、`Heartbeat`、`ListInstances` | `tests/test_app_instances.py::test_heartbeat_updates_last_seen`、`test_instance_offline_after_30s_no_heartbeat` | P0 | 正常。 |
| M1-CHK-008 | Batch 不支持（数组根） | 完成 | `Program.cs` 对根数组返回 `invalid_request` | `tests/test_auth_protocol.py::test_batch_request_rejected` | P0 | 正常。 |
| M1-CHK-009 | 动态端口 + `hub.json` discovery（含 `wsUrl`） | 完成 | `Program` 监听端口后写入 `hub.json` | `tests/test_launch_discovery.py` | P0 | 正常。 |
| M1-CHK-010 | `hub.json` 原子写 + token/hub 文件权限 | 完成 | `FileSystemManager.WriteHubJson`（tmp+move）、Unix chmod 0600 | `tests/test_launch_discovery.py::test_hub_json_atomic_write`、`test_token_file_permissions` | P0 | 正常。 |
| M1-CHK-011 | 单实例互斥 | 完成（基础） | `Program` 中 `MutexName = Local\\DevHub_SingleInstance` | 手工行为 + 启动流程 | P2 | 功能存在；建议后续按建议文档引入 user-sid 维度命名，降低跨会话冲突概率。 |

## 3.2 M2（Invocation 闭环）

| 检查项ID | 验收项 | 实现状态 | 代码证据 | 测试证据 | 风险 | 结论与建议 |
| --- | --- | --- | --- | --- | --- | --- |
| M2-CHK-001 | `invoke.request` 成功闭环（caller->poll->respond->value） | 完成 | `InvocationHandler.RequestAsync/PollAsync/RespondAsync` | `tests/test_invocation_request.py::test_request_roundtrip_success`；`InvocationRequestFlowTests` | P0 | 满足里程碑主链路。 |
| M2-CHK-002 | `invoke.notify` 在线投递与离线入队后可投递 | 完成 | `InvocationHandler.NotifyAsync` + `InvocationStore.CreateInvocation/PollAsync` | `tests/test_invocation_notify.py` | P0 | 正常。 |
| M2-CHK-003 | `queueIfOffline + autoLaunch` 离线矩阵 | 完成 | `NotifyAsync/RequestAsync` 离线分支 + `LaunchCoordinator.LaunchAsync` | `tests/test_launch_invocation.py`、`tests/test_scope_routing.py::M3-SCOPE-010` | P0 | 正常。 |
| M2-CHK-004 | 无 `AppDefinition` 时不得入队，返回 `instance_not_found` | 完成 | `NotifyAsync/RequestAsync` 中 `definition is null` 分支 | `tests/test_invocation_notify.py::test_notify_target_instance_missing_should_return_specific_reason` 等 | P0 | 满足 Spec。 |
| M2-CHK-005 | launch dedupe 窗口（30s） | 完成 | `LaunchCoordinator` `_dedupeRecords` + `CleanupExpiredDedupeRecords` | `tests/test_launch_invocation.py::test_launch_dedupe_concurrent_should_return_already_running`、`LaunchCoordinatorTests` | P0 | 正常。 |
| M2-CHK-006 | poll 租约分配（lease）与 delivery 信息 | 完成 | `InvocationStore.TryLease` | `tests/test_invocation_notify.py`、`tests/test_invocation_poll_respond.py` | P0 | 正常。 |
| M2-CHK-007 | lease 到期重投递，`attempt++` | 完成 | `InvocationStore.SweepExpiredLeases` | `tests/test_invocation_poll_respond.py::test_lease_expired_should_redeliver_with_attempt_incremented`、`InvocationLeaseTests` | P0 | 正常。 |
| M2-CHK-008 | waitTimeout/TTL 错误语义（timeout/expired） | 完成 | `RequestAsync` 超时分支 + `InvocationStore.MarkTimeout/MarkExpired` | `tests/test_invocation_request.py::test_request_timeout_then_late_respond_expired`、`InvocationRequestFlowTests` | P0 | 行为正确。 |
| M2-CHK-009 | respond 冲突语义（重复/非租约持有者） | 完成 | `InvocationStore.Respond` 返回 `DeliveryConflict` | `tests/test_invocation_poll_respond.py`、`InvocationLeaseTests` | P0 | 正常。 |
| M2-CHK-010 | poll/respond 驱动 `lastSeenUtc` 更新 | 完成 | `PollAsync`/`RespondAsync` 中 `_appRegistry.Heartbeat` | `tests/test_app_instances.py` + invocation 集成链路间接覆盖 | P1 | 行为已实现，但建议补一条显式断言用例（poll/respond 后 lastSeen 变化）增强可回归性。 |
| M2-CHK-011 | request 取消（客户端断开）及时清理 waiter | 完成 | `RequestAsync` 改为 `Task.Delay(timeoutWindowMs, cancellationToken)` 并在取消路径显式 `Cleanup` | 白盒：`InvocationRequestFlowTests::Request_WhenCallerCanceled_ShouldReturnTimeout_AndLateRespondShouldBeExpired`；黑盒：`tests/test_invocation_request.py::test_request_client_cancel_then_late_respond_expired` | P0 | 已补齐取消路径与回归覆盖。 |

## 3.3 M3（Scope 一致性与隔离）

| 检查项ID | 验收项 | 实现状态 | 代码证据 | 测试证据 | 风险 | 结论与建议 |
| --- | --- | --- | --- | --- | --- | --- |
| M3-CHK-001 | `scope` 非法值（`""` / `"global"`）拒绝 | 完成 | `RpcParamReader.TryGetOptionalScope` | `tests/test_scope_routing.py::M3-SCOPE-003/004`、`ScopeParsingTests` | P0 | 正常。 |
| M3-CHK-002 | `target.scope` 非法值拒绝 | 完成 | `RpcParamReader.TryParseInvocationTarget` | `tests/test_scope_routing.py::M3-SCOPE-007` | P0 | 正常。 |
| M3-CHK-003 | 默认 `target.scope` 仅命中 Global | 完成 | `InvocationRoutingService.GetOnlineCandidates` (`target.Scope is null`) | `tests/test_scope_routing.py::M3-SCOPE-005` | P0 | 正常。 |
| M3-CHK-004 | 显式 scope 不回退 Global | 完成 | `InvocationRoutingService.GetOnlineCandidates` 显式分支 | `tests/test_scope_routing.py::M3-SCOPE-006` | P0 | 正常。 |
| M3-CHK-005 | 大小写敏感精确匹配 | 完成 | `StringComparison.Ordinal` 路由比较 | `tests/test_scope_routing.py::M3-SCOPE-008` | P0 | 正常。 |
| M3-CHK-006 | `target.instanceId` 优先于 scope | 完成 | `InvocationRoutingService` 先判 `target.InstanceId` | `tests/test_scope_routing.py::M3-SCOPE-009`、`InvocationScopeRoutingTests` | P0 | 正常。 |
| M3-CHK-007 | launch dedupe 按 scope 隔离 | 完成 | `LaunchCoordinator.ResolveDedupeKey` 使用 `{scopeOrGlobal}` | `tests/test_launch_invocation.py::M3-SCOPE-011`、`LaunchScopeTests` | P0 | 正常。 |
| M3-CHK-008 | poll 投递不跨 scope 泄漏（含 lease 重投递后） | 完成 | `InvocationStore.TryLease` + `_routingService.IsEligibleForInstance` | `tests/test_scope_routing.py::M3-SCOPE-012/M3-SCOPE-FULL`、`InvocationScopeRoutingTests` | P0 | 正常。 |
| M3-CHK-009 | 离线矩阵在 global/scoped 下一致 | 完成 | `NotifyAsync/RequestAsync` 统一离线分支 | `tests/test_scope_routing.py::M3-SCOPE-010` | P0 | 正常。 |

## 3.4 M4（WebSocket 认证与事件）

| 检查项ID | 验收项 | 实现状态 | 代码证据 | 测试证据 | 风险 | 结论与建议 |
| --- | --- | --- | --- | --- | --- | --- |
| M4-CHK-001 | `/ws` 端点与生命周期管理 | 完成 | `Program.Map("/ws")` + `HandleWebSocketConnectionAsync` | `tests/test_ws_events.py` 全模块 | P0 | 正常。 |
| M4-CHK-002 | 首条消息必须 `hub.ws.authenticate` 且需请求 `id` | 完成 | `firstMessageProcessed` + `method`/`id` 判定逻辑 | `M4-WS-001`、`M4-WS-002` | P0 | 正常。 |
| M4-CHK-003 | 未鉴权门禁：请求返回 unauthorized，通知断连 | 完成 | 未鉴权分支返回 `-32001`/关闭连接 | `M4-WS-001/002/009/010` | P0 | 正常。 |
| M4-CHK-004 | 鉴权参数校验与错误映射（token/protocol/client） | 完成 | `HandleWsAuthenticate` | `M4-WS-003/004` + `test_auth_protocol.py` | P0 | 正常。 |
| M4-CHK-005 | 鉴权成功响应 | 完成 | `HandleWsAuthenticate` 返回 `{ok:true, protocolVersion:1}` | `M4-WS-005` | P0 | 正常。 |
| M4-CHK-006 | subscribe/unsubscribe 可用且幂等 | 完成 | `HubEventBus.TrySubscribe` + `Unsubscribe` | `M4-WS-005`、`HubEventBusTests` | P0 | 正常。 |
| M4-CHK-007 | 断连自动清理订阅/待投递 | 完成 | `finally -> eventBus.RemoveConnection` | `M4-WS-007`、`HubEventBusTests::RemoveConnection` | P0 | 正常。 |
| M4-CHK-008 | 事件过滤与未知类型校验 | 完成 | `TryReadSubscriptionTypes` + `HubEventBus.IsSupportedEventType` | `HubEventBusTests`（过滤）；黑盒：`M4-WS-011` | P0 | 已补黑盒 unknown type 门禁断言。 |
| M4-CHK-009 | `hub.event` 推送字段完整 | 完成 | `SendPendingHubEventsAsync` 填充 `subscriptionId/type/timeUtc/payload` | `M4-WS-006/007/008` | P0 | 正常。 |
| M4-CHK-010 | 事件类型覆盖（registered/unregistered/queued/delivered/completed/failed） | 完成（白盒全量，黑盒补齐） | `AppInstancesHandler`、`InvocationHandler`、`InvocationStore` 发布事件 | 白盒：`AppInstanceEventTests` + `InvocationEventFlowTests`；黑盒：`M4-WS-006/008/012` | P0 | 已补黑盒 `app.instance.unregistered` 单列断言。 |

---

## 4. 缺口处理状态（2026-02-08 已处理）

### 4.1 P1：request 取消清理 waiter
- 处理：`RequestAsync` 等待阶段绑定 `cancellationToken`，并在取消路径显式清理 waiter。
- 回归：
  1. 白盒 `InvocationRequestFlowTests::Request_WhenCallerCanceled_ShouldReturnTimeout_AndLateRespondShouldBeExpired`；
  2. 黑盒 `M2-REQ-004`（`tests/test_invocation_request.py::test_request_client_cancel_then_late_respond_expired`）。

### 4.2 P1：WS unknown event type 黑盒断言
- 处理：新增 `M4-WS-011`。
- 回归：`types=["unknown.type"]` 断言 `-32602 invalid_params` + `reason=unsupported_event_type` + `type=unknown.type`。

### 4.3 P2：`app.instance.unregistered` 黑盒单列
- 处理：新增 `M4-WS-012`。
- 回归：注册 -> 注销 -> 接收 `app.instance.unregistered`，并校验 `payload.appId/instanceId/scope`。

### 4.4 P2：单实例互斥命名改进
- 处理：互斥量命名从固定 `Local\\DevHub_SingleInstance` 调整为用户维度命名 `Local\\DevHub_{userKey}`。
- 规则：Windows 优先 SID（失败回退用户名），非 Windows 使用用户名并归一化。

---

## 5. 文档偏差与判定说明

- 发现一处规范口径差异：`M1 细化文档`中部分位置将缺少客户端头描述为 `-32602`；`Spec.md`与当前实现/测试采用 `-32600 invalid_request + missing_header`。
- 本报告按“Spec 优先”判定，因此该处不计为实现缺陷。

---

## 6. 公共接口与类型变更说明

- 本次为审查活动，**未变更任何公共 API / 协议字段 / 类型定义**。
- 审计对象保持现状，仅输出评估结论与补强建议。

---

## 7. 最终判定

- **里程碑完成度**：M1~M4 均可判定为“已完成”。
- **测试覆盖充分性**：可判定为“充分（可验收）”。
- **后续动作优先级**：本次缺口项已处理完成；后续维持回归执行并继续按里程碑增量审计。
