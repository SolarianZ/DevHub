# DevHub M2测试任务拆分文档

---

> 参考文档：
> - [Spec.md](./Spec.md)
> - [DevHub协议与开发规划.md](./DevHub协议与开发规划.md)
> - [DevHub_M2细化任务文档.md](./DevHub_M2细化任务文档.md)

## 当前状态（截至 2026-02-07）

- M2 测试任务总体状态：**开发中**。
- 本批次已完成：
  - Python：`test_invocation_notify.py`、`test_invocation_poll_respond.py` 已新增并接入 `test_runner.py`
  - Python 基础能力：`test_base.py` 已补 invocation 相关 helper
  - C#：`InvocationRoutingTests.cs`、`InvocationStoreTests.cs`、`InvocationLeaseTests.cs` 已新增并通过
- 本批次仍在开发：
  - `test_launch_invocation.py`（本轮完成 `LAUNCH-001` 与关键负例）
  - `LaunchCoordinatorTests.cs`（本轮完成最小闭环用例，dedupe 模板类用例待补）
  - lease 到期重投递（30s）长耗时场景（已落地，full-only）

## 0. 文档目标与使用方式

### 0.1 目标
- 将 M2 的验收条目拆分为可直接执行的测试开发任务。
- 明确 Python 黑盒与 C# 白盒的边界、文件落点、用例编号与完成标准。
- 保证测试用例命名、报告输出、执行入口与当前仓库风格一致。

### 0.2 使用方式
- 本文档用于指导测试实现，不替代协议规范。
- 若与 `Spec.md` 冲突，以 `Spec.md` 为准。
- 任务执行顺序建议：先白盒关键路径，再黑盒闭环，最后全量回归。

---

## 1. 覆盖总览（M2 验收 -> 测试任务映射）

| 验收编号        | 目标                       | 测试类型              | 责任文件（建议）                                                                     |
| --------------- | -------------------------- | --------------------- | ------------------------------------------------------------------------------------ |
| `M2-REQ-001`    | request 成功往返           | Python 黑盒           | `tests/test_invocation_request.py`                                                   |
| `M2-REQ-002`    | request 超时               | Python 黑盒           | `tests/test_invocation_request.py`                                                   |
| `M2-REQ-003`    | TTL 过期                   | Python 黑盒           | `tests/test_invocation_request.py`                                                   |
| `M2-NOTIFY-001` | notify 在线投递            | Python 黑盒           | `tests/test_invocation_notify.py`                                                    |
| `M2-NOTIFY-002` | notify 离线入队后投递      | Python 黑盒           | `tests/test_invocation_notify.py`                                                    |
| `M2-LAUNCH-001` | autoLaunch 触发            | Python 黑盒           | `tests/test_launch_invocation.py`                                                    |
| `M2-LAUNCH-002` | dedupe 窗口去重            | Python 黑盒           | `tests/test_launch_invocation.py`                                                    |
| `M2-POLL-001`   | 未注册实例 poll 拒绝       | Python 黑盒           | `tests/test_invocation_poll_respond.py`                                              |
| `M2-RESP-001`   | 重复/越权 respond 冲突     | Python 黑盒           | `tests/test_invocation_poll_respond.py`                                              |
| `M2-LEASE-001`  | lease 到期重投递 attempt++ | Python 黑盒 + C# 白盒 | `tests/test_invocation_poll_respond.py` + `src/DevHub.Tests/InvocationLeaseTests.cs` |

---

## 2. Python 集成测试任务拆分（黑盒）

### 2.1 文件规划（新增）
- [x] 新增 `tests/test_invocation_notify.py`
- [x] 新增 `tests/test_invocation_request.py`
- [x] 新增 `tests/test_invocation_poll_respond.py`
- [x] 新增 `tests/test_launch_invocation.py`

> 命名风格与现有文件保持一致：`test_xxx.py`，类名 `TestXxx(unittest.TestCase)`。

### 2.2 基础能力扩展（复用 `tests/test_base.py`）
- [x] 增加 `RpcClient.call_with_timeout(method, params, timeout_sec)`，支持 request 超时场景。
- [x] 增加公共方法：`register_instance(instance_id, app_id, scope, poll, respond)`。
- [x] 增加公共方法：`heartbeat_instance(instance_id)`、`unregister_instance(instance_id)`。
- [x] 增加公共方法：`poll_once(instance_id, max_count=10, wait_ms=25000)`。
- [x] 增加公共方法：`respond_value(instance_id, invocation_id, value)` 与 `respond_error(...)`。
- [x] 增加公共方法：`invoke_notify(...)` 与 `invoke_request(...)` 的参数构造器（包含默认值填充）。

### 2.3 用例任务：`tests/test_invocation_notify.py`

#### `M2-NOTIFY-001` notify 在线投递
- [x] 前置：注册 1 个 `invoke.poll=true` 实例。
- [x] 步骤：caller 发 `hub.invoke.notify`；callee 调 `hub.invoke.poll` 拉取。
- [x] 断言：
  - notify 返回 `ok=true` 与 `invocationId`。
  - poll 返回 `items` 至少 1 条，且 `invocationId/method/target` 匹配。
  - `delivery.leaseSeconds=30`，`attempt=1`。

#### `M2-NOTIFY-002` notify 离线入队 + 上线投递
- [x] 前置：不注册实例，准备有效 AppDefinition。
- [x] 步骤：caller 发 notify（`queueIfOffline=true`）；稍后注册实例并 poll。
- [x] 断言：
  - notify 返回成功（入队）。
  - 实例上线后 poll 可取到该 invocation。

#### 参数校验补充
- [x] 指定 `target.instanceId` 且显式 `autoLaunch=true` -> `-32602`。
- [ ] `autoLaunch=true` 且 `queueIfOffline=false` -> `-32602`。
- [ ] `ttlMs<1000` -> `-32602`。

### 2.4 用例任务：`tests/test_invocation_request.py`

#### `M2-REQ-001` request 成功往返
- [x] 前置：注册支持 `poll/respond` 的实例。
- [x] 步骤：
  - 线程 A 发 `hub.invoke.request`。
  - 线程 B（callee）poll 获取 invocation 后 `respond.value`。
- [x] 断言：
  - caller 返回 `ok=true`、`invocationId`、`value`。
  - callee respond 返回 `ok=true`。

#### `M2-REQ-002` request 超时
- [x] 前置：无 callee respond。
- [x] 步骤：发 request，设置较短 `waitTimeoutMs`（如 2000）。
- [x] 断言：返回 `-32012 invocation_timeout`，`error.data.elapsedMs` 存在。

#### `M2-REQ-003` TTL 过期
- [x] 前置：构造较短超时窗口并在 timeout 后不立即 respond。
- [x] 步骤：发 request 超时后，再尝试迟到 respond。
- [x] 断言：迟到 respond 返回 `-32011 invocation_expired`。

#### request 参数边界
- [x] `waitTimeoutMs > ttlMs` -> `-32602 invalid_params`。
- [x] 指定 `target.instanceId` 且 `autoLaunch=true` -> `-32602`。
- [x] `queueIfOffline=false` 且无在线实例 -> `-32010 instance_not_found`。

### 2.5 用例任务：`tests/test_invocation_poll_respond.py`

#### `M2-POLL-001` 未注册实例 poll
- [x] 步骤：直接调用 `hub.invoke.poll`（随机 instanceId）。
- [x] 断言：`-32010 instance_not_found`，`error.message` 精确匹配。

#### `M2-RESP-001` 重复/越权 respond
- [x] 重复响应场景：同 invocation 连续 respond 两次。
- [x] 越权响应场景：非 lease holder 的实例 respond。
- [x] 断言：均返回 `-32030 delivery_conflict`。

#### `M2-LEASE-001` lease 到期重投递
- [x] 前置：生成 invocation 并由实例 A poll 获取（不 respond）。
- [x] 步骤：等待超过 30s lease，再由实例 B poll。
- [x] 断言：
  - 实例 B 可重新拿到同一 `invocationId`。
  - `delivery.attempt` 相比首次递增（至少 +1）。
  - 该场景仅在 `--full` 模式运行。

#### poll/resp 参数边界
- [ ] `maxCount=0` 或 `maxCount>100` -> `-32602`。
- [ ] `invoke.poll=false` 的实例调用 poll -> `-32002 forbidden` + `reason=poll_not_enabled`。
- [ ] `invoke.respond=false` 的实例调用 respond -> `-32002 forbidden` + `reason=respond_not_enabled`。

### 2.6 用例任务：`tests/test_launch_invocation.py`

#### `M2-LAUNCH-001` autoLaunch 成功链路
- [x] 前置：定义有效 `launch.exePath`，并可在测试环境启动。
- [x] 步骤：调用 notify/request（`autoLaunch=true`，无在线实例）。
- [x] 断言：
  - 调用不会因“无在线实例”直接返回 `instance_not_found`。
  - 新实例注册后可成功 `poll/respond` 并完成调用闭环。

#### `M2-LAUNCH-002` dedupe 窗口并发去重
- [ ] 步骤：并发触发同 appId/scope 启动请求。
- [ ] 断言：
  - 仅首个请求进入启动；其余返回 `status=already_running`。
  - 窗口内 `launchId` 复用。

#### launch 参数校验
- [x] `waitForRegisterMs < 0` -> `-32602`。
- [x] 缺失 `launch` 配置 -> `-32020 launch_failed` + `reason=launch_config_missing`。

### 2.7 `tests/test_runner.py` 集成任务
- [x] 增加新模块导入：`TestInvocationNotify`、`TestInvocationRequest`、`TestInvocationPollRespond`、`TestLaunchInvocation`。
- [ ] 运行顺序建议：launch_discovery -> auth -> app_def -> app_instance -> invocation -> invalid_params -> internal_errors。
- [ ] `--fast` 模式跳过 `lease(30s)` 与超时长场景。
- [ ] `--full` 模式增加并发与压力场景（dedupe 并发、批量 poll）。
- [x] `--full` 模式增加 lease 重投递长场景（30s，default/fast 不执行）。

---

## 3. C# 单元测试任务拆分（白盒）

### 3.1 文件规划（新增）
- [x] `src/DevHub.Tests/InvocationRoutingTests.cs`
- [x] `src/DevHub.Tests/InvocationStoreTests.cs`
- [x] `src/DevHub.Tests/InvocationLeaseTests.cs`
- [x] `src/DevHub.Tests/InvocationRequestWaiterTests.cs`
- [x] `src/DevHub.Tests/LaunchCoordinatorTests.cs`

### 3.2 `InvocationRoutingTests.cs`
- [x] 指定 `target.instanceId` 仅命中对应实例，不发生 scope/global 回退。
- [x] 指定 `target.scope` 仅命中对应 scope，不回退 global。
- [ ] 无定义且 `queueIfOffline=true` 时，拒绝入 Pending（返回 instance_not_found 路径）。

### 3.3 `InvocationStoreTests.cs`
- [x] 状态迁移：Created -> Queued/Pending -> Delivered -> Completed。
- [ ] 失败迁移：Delivered -> Failed / Expired / Timeout。
- [ ] 请求取消后，后续 respond 必然拒绝（expired/conflict 路径之一，按实现约定断言）。

### 3.4 `InvocationLeaseTests.cs`
- [x] poll 分配 lease（30s）并记录 holder。
- [x] lease 未过期时非 holder respond 返回 conflict。
- [x] lease 到期回收后可重投递且 `attempt++`。

### 3.5 `InvocationRequestWaiterTests.cs`
- [x] waiter 在成功响应时完成并移除。
- [x] waiter 在超时/取消时完成异常并移除。
- [x] waiter 清理后不残留内存引用（避免泄漏）。

### 3.6 `LaunchCoordinatorTests.cs`
- [ ] `dedupeKeyTemplate` 占位符替换正确：`{appId}`、`{scope}`、`{scopeOrGlobal}`、`{httpBaseUrl}`。
- [ ] dedupe 窗口内重复 launch 返回 already_running。
- [x] `waitForRegisterMs` 超时后返回 `starting`。
- [x] 缺失配置时返回 `launch_failed` 语义对象。

---

## 4. 测试数据与环境任务

### 4.1 AppDefinition 样例准备
- [ ] 增加 M2 用 definition：支持 `capabilities.rpc=true` 与 `launch` 配置。
- [ ] 增加负例 definition：`capabilities.rpc=false`。

### 4.2 实例 ID 与隔离策略
- [ ] 每个用例使用唯一 `instanceId`（带时间戳/uuid）。
- [ ] 每个用例结束执行 unregister（失败也要清理）。

### 4.3 长耗时场景隔离
- [x] lease 到期类用例单独分组，避免拖慢默认回归（full-only）。
- [ ] 默认回归优先 2~5 秒可完成用例。

---

## 5. 执行顺序与排期建议

### 5.1 迭代顺序
1. 先完成 C# 白盒（确保状态机、lease、waiter 内核稳定）。
2. 再完成 Python request/notify/poll/respond 黑盒主链路。
3. 最后补 launch + dedupe + 超时慢用例。

### 5.2 日程建议（测试维度）
- Day 1：白盒 `InvocationStore/Lease/Waiter` + 黑盒 `M2-REQ-001`。
- Day 2：黑盒 request 负例 + poll/respond 冲突类。
- Day 3：launch + dedupe + lease 重投递长用例 + 全回归。

---

## 6. 完成定义（DoD）

### 6.1 功能覆盖
- [ ] M2 验收 10 个编号用例全部落地并可重复执行。
- [ ] 默认回归模式稳定通过；`fast/full` 模式行为符合预期。

### 6.2 协议一致性
- [ ] 错误码与 `error.message` 与 Spec 字符串完全一致。
- [ ] 关键 `error.data.reason` 字段在对应场景出现。

### 6.3 可维护性
- [ ] 新增测试按模块分文件，不将 M2 用例混入旧 M1 文件。
- [ ] 公共逻辑抽到 `test_base.py`，避免重复复制请求构造代码。

---

## 7. 风险与应对

### 7.1 时间相关测试不稳定
- 风险：lease/timeout 场景受机器负载影响。
- 应对：统一增加时间缓冲（如 +300ms），并使用轮询等待代替固定睡眠。

### 7.2 launch 场景环境差异
- 风险：不同系统下启动命令与可执行路径不一致。
- 应对：测试 definition 中使用可跨平台最小命令，必要时按平台分支。

### 7.3 并发场景偶发失败
- 风险：dedupe 并发验证在低并发下不稳定复现。
- 应对：固定并发数（如 5~10）并记录每次返回 `launchId/status` 进行断言。

---

## 8. 交付清单（测试阶段）

- [x] Python 新增 4 个 M2 测试模块并接入 `test_runner.py`（`test_launch_invocation.py` 已接入）。
- [x] C# 新增 5 个白盒测试文件并通过 `dotnet test`。
- [ ] 产出测试报告：`temp/test_results.txt`、`temp/test_results.json`、`temp/test_log.txt`。
- [ ] 与 `DevHub_M2细化任务文档.md` 的用例编号一一对应。
