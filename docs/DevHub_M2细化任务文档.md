# DevHub M2细化任务文档

---

> 参考文档：
> - [Spec.md](./Spec.md)
> - [DevHub协议与开发规划.md](./DevHub协议与开发规划.md)
> - [DevHub_M1细化任务文档.md](./DevHub_M1细化任务文档.md)

## 当前状态（截至 2026-02-07）

- M2 总体状态：**开发中**（按“测试先行 + 小步迭代”推进）。
- 第一迭代已完成（本批次）：
  - `hub.invoke.notify`（默认值、校验、Queued/Pending 入队）
  - `hub.invoke.poll`（门禁、长轮询、lease 返回、`lastSeenUtc` 更新）
  - `hub.invoke.respond`（`value/error` 互斥、实例能力门禁、重复响应冲突）
  - `Invocation` 模型升级（`target/options/delivery/caller/kind/state`）
  - `hub.invoke.request` / `hub.apps.launch` 显式 `-32099 not_supported`（`reason=request_deferred/launch_deferred`）
- 本批次后续仍在开发：
  - `hub.invoke.request` 完整闭环（waiter、timeout、failed 映射）**（本轮完成）**
  - `hub.apps.launch` 真正启动与 dedupe
  - lease 到期重投递（`attempt++`）与超时扫描 **（本轮完成：poll/respond 驱动回收）**

## 0. 目标与验收对齐（必须满足）

### 0.1 M2 必须实现（对齐里程碑验收清单）
- `hub.invoke.request` 实现闭环：caller -> hub -> callee `poll` -> callee `respond` -> caller 收到结果。**（开发中）**
- `hub.invoke.notify` 支持入队并被 `hub.invoke.poll` 正确拉取。**（已完成：第一迭代）**
- `queueIfOffline + autoLaunch` 在无在线实例时可触发 `hub.apps.launch`，实例注册后可投递。**（开发中）**
- Lease 到期支持重投递；TTL 到期返回 `invocation_expired (-32011)`。**（本轮完成：重投递 + attempt 递增）**

### 0.2 协议输出约束（M2 继续沿用）
- **HTTP 状态码始终返回 `200`**，业务错误通过 JSON-RPC `error` 返回。
- JSON-RPC 必须符合 2.0，`jsonrpc` 固定为 `"2.0"`。
- `hub.*` 方法参数必须为对象（具名参数）；数组参数必须返回 `-32602 invalid_params`。
- `error.message` 必须使用 Spec 中标准化字符串：
  - `parse_error` / `invalid_request` / `invalid_params` / `method_not_found` / `internal_error`
  - `unauthorized` / `forbidden` / `instance_not_found` / `invocation_expired` / `invocation_timeout` / `app_definition_not_found` / `launch_failed` / `delivery_conflict` / `invocation_failed` / `not_supported`

### 0.3 非 M2 范围（必须明确）
- **M4 范围**：`/ws`、`hub.ws.authenticate`、`hub.events.subscribe/unsubscribe`、`hub.event` 推送。
- **M3 范围**：`scopePolicy` 严格隔离能力的全链路完善（M2 仅保持边界约束，不扩展为完整 M3 方案）。
- **v2 范围**：Invocation 持久化、事件重放、Hub 重启后的 pending 恢复。

---

## 1. M2 架构落点与模块拆分

### 1.1 新增服务职责（建议落地形态）
- `LaunchCoordinator`：负责 `hub.apps.launch` 的配置解析、进程启动、`dedupeKey` 窗口管理。
- `InvocationStore`：维护 Invocation 内存态（Queued/Pending/Delivered/Completed/Failed/Expired/Timeout）。
- `InvocationLeaseManager`：在 `poll` 时分配租约，跟踪 lease 到期与重投递。
- `InvocationRequestWaiter`：维护 `invoke.request` 的等待句柄（`TaskCompletionSource`）与取消清理。
- `InvocationTimeoutWorker`：统一处理 TTL 到期、waitTimeout 到期、lease 到期扫描。

### 1.2 与现有模块对接点（必须明确）
- `AppRegistry`：
  - 提供在线判定输入（`lastSeenUtc` + 30s 阈值）。
  - `poll/respond` 成功到达 Hub 时更新 `lastSeenUtc`。
- `DefinitionLoader`：
  - 为 `launch` 提供 `launch.exePath/argsTemplate/dedupeKeyTemplate`。
  - 为 `invoke.notify/request` 提供 `capabilities.rpc` 校验依据。
- `RpcRouter`：
  - 新增 `hub.apps.launch`、`hub.invoke.*` 对应 handler。
  - 继续复用统一错误包装与日志语义。

### 1.3 公共接口/类型变化（M2 文档约束）
- 新增/强化 RPC 契约：
  - `hub.apps.launch`
  - `hub.invoke.notify`
  - `hub.invoke.request`
  - `hub.invoke.poll`
  - `hub.invoke.respond`
- Invocation 类型语义升级（实现建议）：
  - `target` 必须是对象（非字符串）。
  - 增加 `caller`、`options`、`delivery`、`state`、`attempt` 语义。
  - `kind` 明确区分 `request` / `notify`。

---

## 2. Invocation 数据模型与状态机（M2 核心）

### 2.1 字段形态（对齐 Spec）
建议以如下结构作为 M2 内部/传输统一语义：

```json
{
  "invocationId": "invk-...",
  "appId": "asset.indexer",
  "target": {
    "scope": null,
    "instanceId": null
  },
  "method": "asset.index",
  "args": {},
  "kind": "request",
  "createdAtUtc": "2026-02-07T00:00:00Z",
  "options": {
    "ttlMs": 300000,
    "waitTimeoutMs": 120000,
    "queueIfOffline": true,
    "autoLaunch": true
  },
  "delivery": {
    "leaseSeconds": 30,
    "attempt": 1
  },
  "caller": {
    "clientId": "DevHubUI",
    "clientSessionId": "00000000-0000-0000-0000-000000000000"
  }
}
```

### 2.2 状态流转（必须实现）
- `Created`：请求进入 Hub 并完成参数校验。
- `Queued`：可被目标实例拉取。
- `Pending`：当前无在线可投递实例，等待实例上线。
- `Delivered`：已被某实例通过 `poll` 认领，lease 生效。
- `Requeued`：lease 到期未响应，且 TTL 未过期，回队并 `attempt++`。
- `Completed`：`respond.value` 成功完成（request 返回 value；notify 仅记录完成）。
- `Failed`：`respond.error` 导致调用失败（request 需映射 `invocation_failed`）。
- `Expired`：超过 TTL。
- `Timeout`：request 超过 `waitTimeoutMs`（并取消调用）。

### 2.3 时间与边界约束（M2 固化）

| 参数            | notify 默认值 | request 默认值 | 约束                       |
| --------------- | ------------- | -------------- | -------------------------- |
| `ttlMs`         | 60000         | 300000         | 必须 `>= 1000`             |
| `waitTimeoutMs` | N/A           | 120000         | 必须 `<= ttlMs`            |
| `leaseSeconds`  | 30            | 30             | 固定 30（由 Hub 分配）     |
| 在线阈值        | 30s           | 30s            | `now - lastSeenUtc <= 30s` |
| `maxCount`      | 10            | 10             | 必须在 `1..100`            |

- `waitMs` 为 `hub.invoke.poll` 长轮询参数，文档中推荐值 `25000ms`（实现可按 Spec 约束调整）。

### 2.4 语义约束（M2 需声明）
- 投递语义为 **at-least-once**，业务处理方应保证幂等。
- Hub 内存态重启不保留；调用方需容忍重启期间请求失败/中断。

---

## 3. RPC：`hub.apps.launch`（M2 必做）

### 3.1 请求与默认值

```json
{
  "appId": "asset.indexer",
  "scope": null,
  "dedupeKey": "optional-key",
  "waitForRegisterMs": 3000
}
```

- `waitForRegisterMs` 省略默认 `0`，必须为 `>= 0` 整数。
- 若 `dedupeKey` 省略：
  - 优先使用 `AppDefinition.launch.dedupeKeyTemplate` 渲染。
  - 若模板缺失，使用默认模板：`{appId}:{scopeOrGlobal}`。

### 3.2 返回语义

```json
{
  "ok": true,
  "status": "started",
  "pid": 12345,
  "launchId": "launch-..."
}
```

- `status` 必须为：`started` / `starting` / `already_running`。

### 3.3 去重与模板替换（必须）
- 去重窗口固定 30 秒。
- 占位符支持：`{appId}`、`{scope}`、`{scopeOrGlobal}`、`{httpBaseUrl}`。
- 窗口内同 `dedupeKey` 并发启动请求必须返回 `already_running`。
- 若窗口内检测到匹配实例注册，则启动状态转入运行态。

### 3.4 错误映射
- `-32014 app_definition_not_found`：缺失定义。
- `-32020 launch_failed`：启动配置缺失/进程启动失败。
  - `error.data.reason` 至少支持：`launch_config_missing`、`process_start_failed`。
- `-32602 invalid_params`：参数非法（如 `waitForRegisterMs < 0`）。

---

## 4. 路由与离线矩阵（`queueIfOffline + autoLaunch`）

### 4.1 严格路由规则（M2 必须）
- 指定 `target.instanceId`：仅允许路由到该实例，不允许回退。
- 指定 `target.scope`（非空字符串）：仅允许路由到该 scope，不允许回退到 global。
- 目标 `scope` 为 `""` 或 `"global"` 字符串应视为非法参数。

### 4.2 无在线实例时行为矩阵（必须）

| `queueIfOffline` | `autoLaunch` | `AppDefinition` | 行为                                                                             |
| ---------------- | ------------ | --------------- | -------------------------------------------------------------------------------- |
| false            | true         | 任意            | 返回 `-32602 invalid_params`（`autoLaunch=true` 必须要求 `queueIfOffline=true`） |
| false            | false        | 任意            | 直接返回 `-32010 instance_not_found`                                             |
| true             | false        | 不存在          | 返回 `-32010 instance_not_found`（禁止入队）                                     |
| true             | false        | 存在            | 入 `Pending` 队列                                                                |
| true             | true         | 不存在          | 返回 `-32010 instance_not_found`（禁止入队）                                     |
| true             | true         | 存在            | 触发 `launch`；成功后入队等待投递，失败返回 `-32020 launch_failed`               |

> 挂起队列约束：若 `appId` 无定义，即使 `queueIfOffline=true` 也不得入队。

---

## 5. RPC：`hub.invoke.notify`

### 5.1 参数与默认值

```json
{
  "appId": "asset.indexer",
  "target": { "scope": null, "instanceId": null },
  "method": "asset.rebuild",
  "args": {},
  "options": {
    "ttlMs": 60000,
    "queueIfOffline": true,
    "autoLaunch": true
  }
}
```

- 默认值：`ttlMs=60000`、`queueIfOffline=true`。
- `autoLaunch` 默认：
  - 未指定 `target.instanceId` 时默认 `true`。
  - 指定 `target.instanceId` 时默认 `false`。

### 5.2 校验规则（必须）
- `options.autoLaunch=true` 且 `queueIfOffline=false` -> `-32602 invalid_params`。
- 指定 `target.instanceId` 时显式 `autoLaunch=true` -> `-32602 invalid_params`。
- `ttlMs < 1000` -> `-32602 invalid_params`。
- 若定义存在且 `capabilities.rpc=false` -> `-32002 forbidden`，`error.data.reason="rpc_disabled"`。

### 5.3 结果

```json
{ "ok": true, "invocationId": "invk-..." }
```

---

## 6. RPC：`hub.invoke.request`

### 6.1 参数与默认值
- 结构同 `notify`，额外包含 `waitTimeoutMs`。
- 默认值：
  - `ttlMs=300000`
  - `waitTimeoutMs=120000`
  - `queueIfOffline=true`
  - `autoLaunch` 默认规则与 `notify` 一致。

### 6.2 校验规则（必须）
- `waitTimeoutMs <= ttlMs`，否则 `-32602 invalid_params`。
- 其余校验与 `notify` 保持一致。

### 6.3 同步等待与返回（必须）
- Hub 为每个 request 建立 waiter（`TaskCompletionSource` 或等价结构）。
- 成功时返回：

```json
{ "ok": true, "invocationId": "invk-...", "value": {} }
```

### 6.4 异常分流（必须）
- `-32012 invocation_timeout`：`waitTimeoutMs` 耗尽。
- `-32011 invocation_expired`：TTL 耗尽。
- `-32050 invocation_failed`：callee 使用 `respond.error` 返回业务错误。
  - `error.data` 必须包含 `invocationId` 与 `calleeError`。

---

## 7. RPC：`hub.invoke.poll`

### 7.1 请求参数

```json
{
  "instanceId": "inst-123",
  "maxCount": 10,
  "waitMs": 25000
}
```

- `maxCount` 默认 `10`，必须在 `1..100`。
- `waitMs` 推荐默认 `25000ms`。

### 7.2 门禁与校验（必须）
- 实例未注册 -> `-32010 instance_not_found`。
- 实例 `invoke.poll != true` -> `-32002 forbidden`，`error.data.reason="poll_not_enabled"`。

### 7.3 长轮询与投递（必须）
- 无可用项时执行长轮询，最多等待 `waitMs` 后返回空数组。
- 返回项必须包含 `delivery.leaseSeconds=30` 与当前 `attempt`。
- 成功到达 Hub 的 `poll`（即使返回空列表）必须刷新实例 `lastSeenUtc`。

---

## 8. RPC：`hub.invoke.respond`

### 8.1 请求约束（必须）

```json
{
  "instanceId": "inst-123",
  "invocationId": "invk-...",
  "value": {}
}
```

或

```json
{
  "instanceId": "inst-123",
  "invocationId": "invk-...",
  "error": { "code": 1001, "message": "app_error", "data": {} }
}
```

- `value` 与 `error` 必须且只能存在一个。
- 实例未注册 -> `-32010 instance_not_found`。
- 实例 `invoke.respond != true` -> `-32002 forbidden`，`error.data.reason="respond_not_enabled"`。

### 8.2 冲突与过期处理
- 以下场景必须返回 `-32030 delivery_conflict`：
  - 非 lease 持有者响应。
  - lease 已过期后仍响应。
  - 同一 invocation 重复响应。
- 以下场景返回 `-32011 invocation_expired`：
  - invocation 已超 TTL。
  - invocation 已因 request timeout 被取消。
- 成功响应后必须更新实例 `lastSeenUtc`。

### 8.3 request 错误映射
- 对 `kind=request`：callee 返回 `error` 时，Hub 向 caller 返回 `-32050 invocation_failed`，并携带 `calleeError`。
- 对 `kind=notify`：callee 返回 `error` 仅用于记录 invocation 失败，不回传给原调用方。

---

## 9. 错误码与返回策略（M2 新增部分）

### 9.1 M2 必须覆盖错误码
- `-32011 invocation_expired`
- `-32012 invocation_timeout`
- `-32020 launch_failed`
- `-32030 delivery_conflict`
- `-32050 invocation_failed`

### 9.2 `error.data.reason` 规范字段（M2 最低要求）
- `launch_failed`：`launch_config_missing` / `process_start_failed`
- `forbidden`：`rpc_disabled` / `poll_not_enabled` / `respond_not_enabled`
- `instance_not_found`：`offline_no_queue` / `unknown_instance` / `target_instance_missing`

### 9.3 返回一致性
- 所有错误统一通过 JSON-RPC `error` 返回，`result` 必须为空。
- `error.message` 必须与 Spec 名称逐字一致，禁止自定义变体。

---

## 10. 开发排期（1 人可执行）

> 说明（2026-02-07）：以下 Day 计划来自原始拆分建议。本轮按“测试先行 + 小步切片”执行，
> 优先落地 `notify/poll/respond` 最小闭环，因此任务完成顺序与 Day 编号不完全一致。
> Day 编号在当前阶段主要表示工作包分组，不等同于严格的日历先后。

### Day 0.5：模型/存储与队列骨架
- [x] 定义 Invocation 内存模型与状态枚举
- [x] 建立 Queued/Pending/Delivered 三类集合与索引
- [ ] 建立 request waiter 生命周期管理（创建/完成/取消/清理）

### Day 1：`launch` + dedupe
- [ ] 实现 `hub.apps.launch` 参数校验与返回模型（开发中：当前为 deferred `not_supported`）
- [ ] 实现 dedupe 窗口（30s）与模板渲染
- [ ] 接入 `AppDefinition.launch` 配置并完成进程启动封装

### Day 1.5：`notify/request`
- [x] 实现 `hub.invoke.notify`（默认值、校验、入队）
- [x] 实现 `hub.invoke.request`（waiter 绑定、成功返回、超时/失败映射）
- [ ] 完成离线矩阵路由与 autoLaunch 触发（开发中：`queueIfOffline + autoLaunch=false` 已支持，`autoLaunch=true` deferred）

### Day 2：`poll/respond` + lease
- [x] 实现 `hub.invoke.poll` 长轮询与租约分配
- [x] 实现 `hub.invoke.respond`（value/error 互斥、租约校验）
- [x] 完成 `poll/respond` 的 `lastSeenUtc` 更新时间

### Day 3：超时/重投递 + 回归
- [ ] 实现 TTL / waitTimeout / lease 到期扫描
- [x] 实现 lease 到期重投递与 `attempt++`（poll/respond 驱动回收）
- [ ] 完成 M2 回归测试并修复关键缺陷

---

## 11. M2 验收用例（最小可验收）

### 11.1 Python 集成测试（黑盒）
- [x] `M2-REQ-001`：request 成功往返，caller 收到 `value`
- [x] `M2-REQ-002`：request 超时返回 `-32012 invocation_timeout`
- [x] `M2-REQ-003`：TTL 到期/取消后迟到响应返回 `-32011 invocation_expired`
- [x] `M2-NOTIFY-001`：notify 在线投递并被 poll 取走
- [x] `M2-NOTIFY-002`：离线入队，实例上线后可 poll 拉取
- [ ] `M2-LAUNCH-001`：autoLaunch 触发成功，实例注册后完成投递
- [ ] `M2-LAUNCH-002`：dedupe 窗口内重复启动返回 `already_running`
- [x] `M2-POLL-001`：未注册实例 poll 返回 `-32010 instance_not_found`
- [x] `M2-RESP-001`：重复响应/越权响应返回 `-32030 delivery_conflict`
- [x] `M2-LEASE-001`：lease 到期触发重投递且 `attempt` 递增（长耗时场景放入 full 模式）

### 11.2 C# 单元测试（白盒）
- [x] 路由选择：`instanceId`/`scope` 精确匹配且不回退
- [x] 队列状态机：Created -> Queued/Pending -> Delivered -> Completed/Failed
- [x] waiter 清理：超时、取消、异常分支均可释放
- [x] lease 回收：到期后回队并更新 `attempt`
- [ ] 模板渲染：`dedupeKeyTemplate/argsTemplate` 占位符替换正确

---

## 12. 边界与风险提示

### 12.1 与 M3/M4 边界
- M2 不引入 WS 事件订阅与推送（M4）。
- M2 不将 `scopePolicy` 扩展为完整 M3 级严格隔离体系。

### 12.2 调用语义与幂等建议
- Hub 提供 at-least-once 语义，业务方应使用 `invocationId` 做幂等去重。
- 对外部副作用操作（写文件、发请求、发消息）必须防重复执行。

### 12.3 内存态限制与重启影响
- Hub 重启后 pending invocations 与 request waiter 不保留。
- 调用方需容忍重启导致的请求失败并具备重试策略。

### 12.4 默认值与假设（本阶段固化）
- 范围策略：里程碑优先，M2 聚焦 Invocation 闭环与 launch/离线矩阵。
- `waitMs` 推荐默认 `25000ms`（如实现层新增约束，以 Spec 最终条文为准）。
- 文档术语、错误码字符串、行为描述以 Spec 为准，不做语义改写。
